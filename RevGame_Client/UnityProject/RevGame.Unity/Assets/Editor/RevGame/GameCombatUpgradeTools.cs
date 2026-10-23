// 项目专用的离线资源接线入口：只处理固定四种动作，不执行传入的代码/方法名，不修改框架快照。
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>
    /// Unity MCP断连时，项目CLI仍能给已打开的编辑器发出明确请求。
    /// 请求/结果均位于可再生成的Library内；不保存场景、不启动第二个编辑器，也不暴露任意执行能力。
    /// </summary>
    [InitializeOnLoad]
    public static class GameCombatUpgradeTools
    {
        private const string Root = "Library/RevGameCombatUpgrade";
        private static double _nextPoll;
        static GameCombatUpgradeTools() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) { return; }
            _nextPoll = EditorApplication.timeSinceStartup + .5;
            string request = Root + "/request.txt";
            if (!File.Exists(request)) { return; }
            string action = File.ReadAllText(request).Trim();
            File.Delete(request);
            try
            {
                string result;
                switch (action)
                {
                    case "prepare": result = Prepare(); break;
                    case "build-pc": result = GameResourceBuild.BuildAndCopy(BuildTarget.StandaloneWindows64); break;
                    case "verify-rules": result = GameRuleChecks.Run(); break;
                    case "verify-play": GameCombatUpgradeChecks.Begin(); result = "started"; break;
                    case "check-result":
                        result = GameCombatUpgradeChecks.LastResult;
                        if (result.StartsWith("失败", StringComparison.Ordinal)) { throw new InvalidOperationException(result); }
                        break;
                    default: throw new ArgumentException("未知动作，仅支持prepare/build-pc/verify-rules/verify-play/check-result");
                }
                WriteResult(true, result);
            }
            catch (Exception error) { WriteResult(false, error.ToString()); Debug.LogException(error); }
        }
        [Serializable]
        private sealed class Result
        {
            public bool success;
            public string message;
        }
        private static void WriteResult(bool success, string message)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Root + "/result.json", JsonUtility.ToJson(new Result { success = success, message = message }, true));
        }
        /// <summary>把已入库PNG导入成图集适用的Sprite，并赋给真实特效预制体；重复执行结果相同。</summary>
        [MenuItem("RevGame/资源/配置战斗节奏与Boss扩展素材")]
        public static void PrepareMenu() { Debug.Log(Prepare()); }
        public static string Prepare()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("请先退出Play再配置资源"); }
            Sprite Import(string name)
            {
                string path = "Assets/GameRes/Art/Combat/" + name + ".png";
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) { throw new InvalidOperationException("缺少素材：" + path); }
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100;
                importer.spritePivot = new Vector2(.5f, .5f);
                importer.spriteBorder = Vector4.zero;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings); settings.spriteMeshType = SpriteMeshType.FullRect; importer.SetTextureSettings(settings);
                importer.mipmapEnabled = false; importer.isReadable = false; importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp; importer.maxTextureSize = 1024;
                importer.SaveAndReimport();
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) { throw new InvalidOperationException("Sprite导入失败：" + path); }
                return sprite;
            }
            const string prefabPath = "Assets/GameRes/Effects/Combat/BossSkillFx.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var view = root.GetComponent<RevGame.View.BossSkillFxView>();
                if (view == null) { throw new InvalidOperationException("Boss特效预制体缺少脚本"); }
                var serialized = new SerializedObject(view);
                serialized.FindProperty("energyBomb").objectReferenceValue = Import("EnergyBomb");
                SerializedProperty sprites = serialized.FindProperty("extendedSprites"); sprites.arraySize = 9;
                for (int i = 0; i < 9; i++) { sprites.GetArrayElementAtIndex(i).objectReferenceValue = Import("BossExt_" + (9 + i)); }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            const string cellPath = "Assets/GameRes/Board/Tile/BoardCell.prefab";
            root = PrefabUtility.LoadPrefabContents(cellPath);
            try
            {
                var cell = root.GetComponent<RevGame.View.BoardCellView>();
                var serialized = new SerializedObject(cell);
                serialized.FindProperty("hazardSprite").objectReferenceValue = Import("RegionHazard");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, cellPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            string configured = GameResourceBuild.Configure();
            return "11张Sprite、Boss特效与格子预制体已接线；" + configured;
        }
    }
}

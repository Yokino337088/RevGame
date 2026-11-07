using System;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        /// <summary>固定配方经RevMCP调用，复用项目Sprite及九宫格，不直接修改Prefab YAML。</summary>
        public static object BuildAgentAssets()
        {
            const string logoPath = "Assets/GameRes/Art/UI/Pvp/DeepSeekLogo.png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(logoPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            _font = GameAssetBuilder.Font();
            Menu("PvpAgentSettingsPanel", typeof(PvpAgentSettingsPanel), root =>
            {
                Label("heading", root, "DeepSeek Agent 设置", .22f, .79f, .78f, .9f, 38, true);
                Label("disclosure", root, "启用后人机对手和落子建议可调用DeepSeek并消耗API额度。\nKey在本机加密保存；本地人机你只手动落子。", .18f, .62f, .82f, .77f, 24, true);
                Label("keyLabel", root, "API Key（本机加密保存）", .22f, .59f, .78f, .625f, 18, true);
                Label("modelLabel", root, "DeepSeek V4.1 Flash · 1017（10月17日版）", .29f, .47f, .78f, .505f, 18, true);
                var logo = Rect("deepseekLogo", root, .22f, .395f, .285f, .465f).gameObject.AddComponent<Image>();
                logo.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(logoPath);
                logo.preserveAspect = true;
                logo.raycastTarget = false;
                Input("key", root, "", .22f, .51f, .78f, .59f, 512);
                Input("model", root, "deepseek-flash", .30f, .39f, .78f, .47f, 128);
                Label("state", root, "填写API Key与模型", .18f, .26f, .82f, .37f, 22, true);
                Button("enable", root, "保存并授权云端调用", .31f, .15f, .7f, .23f, 26);
                Button("disable", root, "关闭云端并清除Key", .31f, .06f, .7f, .14f, 25);
            });
            string path = "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab";
            GameObject panel = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform controls = panel.transform.Find("controls");
                if (controls.Find("agent") == null)
                {
                    Button("agent", controls, "Agent托管", .025f, .32f, .20f, .40f, 24);
                    Button("agentSettings", controls, "Agent设置", .025f, .22f, .20f, .30f, 24);
                }
                PrefabUtility.SaveAsPrefabAsset(panel, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(panel);
            }
            string entryPath = "Assets/GameRes/UI/Panel/Pvp/Menu/PvpEntryPanel.prefab";
            GameObject entry = PrefabUtility.LoadPrefabContents(entryPath);
            try
            {
                if (entry.transform.Find("agentSettings") == null)
                {
                    Button("agentSettings", entry.transform, "Agent设置", .73f, .07f, .93f, .14f, 25);
                }
                PrefabUtility.SaveAsPrefabAsset(entry, entryPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(entry);
            }
            AssetDatabase.SaveAssets();
            return new { success = true, text = "Agent设置与托管入口已制作" };
        }
    }
}

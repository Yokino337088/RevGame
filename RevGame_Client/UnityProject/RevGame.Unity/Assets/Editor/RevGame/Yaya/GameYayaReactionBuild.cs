// 局内互动用统一正式差分和已有帮手对话框，场景只保存表现根；图片、Prefab和图集全部走现有AB链。
using System;
using System.IO;
using System.Linq;
using RevGame.Data;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.Editor
{
    /// <summary>固定项目资源配方，保留已有图片GUID，不复制一套战斗低清图。源原图保持1600×2400供以后选用。</summary>
    public static class GameYayaReactionBuild
    {
        /// <summary>选用1/8/3夸奖，2/14鞭策，23/26/13嘲讽；重复的1、3、13、23沿用正式资源。</summary>
        private static readonly int[] UsedExpressions = { 1, 8, 3, 2, 14, 23, 26, 13 };
        /// <summary>编辑态固定制作：只补缺少图片，保存场景/预制体，不启动Play或另开batch Unity。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("互动制作要求编辑态");
            }
            const string folder = "Assets/GameRes/Art/Characters/Yaya/Expressions";
            foreach (int id in UsedExpressions)
            {
                string path = folder + "/YayaExpression_" + id.ToString("D2") + ".png";
                if (!File.Exists(path))
                {
                    string original = Path.GetFullPath("../../美术/AI生成/新版/表情差分/鸦鸦表情差分_透明/" + id + ".png");
                    File.Copy(original, path);
                    AssetDatabase.ImportAsset(path);
                }
                TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = 1024;
                importer.spritePixelsPerUnit = 100;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.DXT5 });
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.ETC2_RGBA8 });
                importer.SaveAndReimport();
            }
            EnsureFolder("Assets/GameRes/Config/Yaya");
            EnsureFolder("Assets/GameRes/Characters/Yaya/Reaction");
            const string catalogPath = "Assets/GameRes/Config/Yaya/YayaExpressionCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<YayaExpressionCatalog>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<YayaExpressionCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            if (catalog.Entries.Length == 0)
            {
                catalog.Entries = UsedExpressions.Select(id => new YayaExpressionCatalog.Entry { Id = id, Sprite = AssetDatabase.LoadAssetAtPath<Sprite>(folder + "/YayaExpression_" + id.ToString("D2") + ".png") }).ToArray();
                EditorUtility.SetDirty(catalog);
            }
            catalog.AtlasNames = new[] { "YayaExpressions", "YayaCombatExpressions" };
            EditorUtility.SetDirty(catalog);
            // Excel可增换表情，但必须先在图片目录配好对应编号；构建阶段失败比真机临时白图更容易排查。
            var lines = new YayaReactionLineTable();
            lines.LoadText(File.ReadAllText("Assets/GameRes/Data/YayaReactionLine.txt"), out int errors);
            if (errors != 0)
            {
                throw new InvalidOperationException("互动台词数据解析失败，请重新导表");
            }
            foreach (YayaReactionLine line in lines.All)
            {
                catalog.Find(line.Expression);
            }
            BuildPrefab();
            ReserveCampaignCorner();
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Any(s => s.isLoaded && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(s.path).isDirty))
            {
                throw new InvalidOperationException("场景未保存，互动制作保留现场");
            }
            try
            {
                foreach (string path in new[] { "Assets/Scenes/GameScene.unity", "Assets/Scenes/EndlessScene.unity" })
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    var presenter = scene.GetRootGameObjects().Select(g => g.GetComponent<YayaReactionPresenter>()).FirstOrDefault(p => p != null);
                    if (presenter == null)
                    {
                        presenter = new GameObject("YayaReactionScene", typeof(YayaReactionPresenter)).GetComponent<YayaReactionPresenter>();
                    }
                    SetCampaignArea(presenter);
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            AssetDatabase.SaveAssets();
            return "8张统一差分（含4张新增）已压缩配置；场景互动根、图片目录和无输入对话框已制作";
        }
        /// <summary>战斗时手牌面板已关闭，仅将底部说明移到右侧现有空隙，积分移到左上；准备期六牌维持原布局。</summary>
        private static void ReserveCampaignCorner()
        {
            const string hudPath = "Assets/GameRes/UI/Panel/BattleHudPanel.prefab";
            GameObject hud = PrefabUtility.LoadPrefabContents(hudPath);
            try
            {
                foreach (string node in new[] { "assistPlate", "assistText" })
                {
                    var rect = hud.transform.Find(node) as RectTransform;
                    if (rect != null)
                    {
                        rect.anchorMin = new Vector2(.76f, .33f);
                        rect.anchorMax = new Vector2(.98f, .42f);
                        rect.offsetMin = rect.offsetMax = Vector2.zero;
                    }
                }
                RectTransform score = hud.transform.Find("scoreFrame") as RectTransform;
                if (score != null)
                {
                    GameScoreBuilder.ApplyReadableLayout(score);
                }
                PrefabUtility.SaveAsPrefabAsset(hud, hudPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(hud);
            }
        }
        /// <summary>局部更新既有HUD和六章立绘区域，不重导图片、不重建台词Prefab；有未保存场景时保留现场并拒绝。</summary>
        public static string ConfigureReadability()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("可读性制作要求编辑态");
            }
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Any(s => s.isLoaded && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(s.path).isDirty))
            {
                throw new InvalidOperationException("请先保存场景，可读性制作未改变现场");
            }
            ReserveCampaignCorner();
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/GameScene.unity", OpenSceneMode.Single);
                var presenter = scene.GetRootGameObjects().Select(g => g.GetComponent<YayaReactionPresenter>()).First(p => p != null);
                SetCampaignArea(presenter);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            AssetDatabase.SaveAssets();
            return "六章立绘区域高度翻倍，积分复用弹幕九宫格并放大三行文字；等待真实AB画面验收";
        }
        /// <summary>区域高度由0.39提升到0.78，人物仍占72%，得到原来的两倍高度；宽度容纳全身并避开中央棋盘。</summary>
        private static void SetCampaignArea(YayaReactionPresenter presenter)
        {
            var so = new SerializedObject(presenter);
            so.FindProperty("campaignViewport").rectValue = new Rect(.01f, .028f, .24f, .78f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        /// <summary>SpriteRenderer身体+世界Canvas台词，Prefab不引用全部立绘，目录只有发生互动才加载。</summary>
        private static void BuildPrefab()
        {
            var root = new GameObject("YayaReaction");
            try
            {
                YayaReactionView view = root.AddComponent<YayaReactionView>();
                var body = new GameObject("Portrait", typeof(SpriteRenderer)).GetComponent<SpriteRenderer>();
                body.transform.SetParent(root.transform, false);
                body.sortingOrder = 70;
                body.enabled = false;
                var bubble = new GameObject("Speech", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
                bubble.transform.SetParent(root.transform, false);
                bubble.renderMode = RenderMode.WorldSpace;
                bubble.sortingOrder = 80;
                ((RectTransform)bubble.transform).sizeDelta = new Vector2(360, 110);
                var background = new GameObject("Background", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                background.transform.SetParent(bubble.transform, false);
                background.rectTransform.anchorMin = Vector2.zero;
                background.rectTransform.anchorMax = Vector2.one;
                background.rectTransform.offsetMin = background.rectTransform.offsetMax = Vector2.zero;
                background.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_Tip_imgBK.png");
                background.type = Image.Type.Sliced;
                background.raycastTarget = false;
                var label = new GameObject("SpeechText", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
                label.transform.SetParent(bubble.transform, false);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(14, 12);
                label.rectTransform.offsetMax = new Vector2(-14, -12);
                label.font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf");
                label.fontSize = 24;
                label.color = new Color(1, .96f, .8f);
                label.alignment = TextAnchor.MiddleCenter;
                label.supportRichText = false;
                label.raycastTarget = false;
                var so = new SerializedObject(view);
                so.FindProperty("portrait").objectReferenceValue = body;
                so.FindProperty("bubble").objectReferenceValue = bubble;
                so.FindProperty("speech").objectReferenceValue = label;
                so.ApplyModifiedPropertiesWithoutUndo();
                bubble.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Characters/Yaya/Reaction/YayaReaction.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        /// <summary>递归补齐GameRes内功能目录，AssetDatabase创建.meta，不文本拼GUID。</summary>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}

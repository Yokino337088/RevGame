using System;
using System.Collections.Generic;
using System.Linq;
using Revolution.Editor;
using Revolution.Gal;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>装配鸦鸦续篇的导入规格、主题与大厅入口，包和图集继续交给项目正式构建。</summary>
    public static class GameGalArtBuild
    {
        private const string ArtRoot = "Assets/GameRes/Art/";
        private const string ControlsPath = ArtRoot + "UI/Gal/GalControlsSheet.png";
        private const string ThemePath = "Assets/GameRes/UI/Gal/RevGalPanelTheme.asset";
        private const string PanelPath = "Assets/GameRes/UI/Gal/RevGalPanel.prefab";
        private const string FontPath = "Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf";
        private const string HomePath = "Assets/GameRes/UI/Panel/HomePanel.prefab";

        [RevAutomationAction("game.gal-art.import", Environment = "edit", Keys = new string[0], Description = "导入鸦鸦续篇背景、透明立绘和控件切片")]
        public static object Import(RevAutomationRequest request)
        {
            var roots = new[] { ArtRoot + "Background/Gal", ArtRoot + "Gal", ArtRoot + "UI/Gal" };
            var paths = AssetDatabase.FindAssets("t:Texture2D", roots).Select(AssetDatabase.GUIDToAssetPath)
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (!paths.Contains(ControlsPath))
            {
                throw new InvalidOperationException("请先放入 Gal 控件源图：" + ControlsPath);
            }
            foreach (string path in paths)
            {
                ImportSprite(path);
            }
            return new Dictionary<string, object> { ["sprites"] = paths.Length };
        }

        private static void ImportSprite(string path)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            bool controls = path == ControlsPath;
            bool background = path.StartsWith(ArtRoot + "Background/Gal/", StringComparison.Ordinal);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = controls ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            importer.alphaSource = background ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = !background;
            importer.sRGBTexture = true;
            importer.isReadable = false;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = background || controls ? 2048 : 1024;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.spritePixelsPerUnit = 100f;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
            if (controls)
            {
                // 裁切坐标使用源图的像素，Unity 原点在左下。空白间距不参与绘制。
#pragma warning disable CS0618
                importer.spritesheet = new[]
                {
                    MakeSlice("GalDialogueWood", new Rect(38, 792, 558, 184), new Vector4(45, 40, 45, 40)),
                    MakeSlice("GalButton", new Rect(672, 817, 546, 135), new Vector4(30, 28, 30, 28)),
                    MakeSlice("GalSettings", new Rect(43, 181, 545, 289), new Vector4(30, 30, 30, 30)),
                    MakeSlice("GalHeader", new Rect(666, 249, 546, 147), new Vector4(30, 30, 30, 30))
                };
#pragma warning restore CS0618
            }
            importer.SaveAndReimport();
        }

        private static SpriteMetaData MakeSlice(string name, Rect rect, Vector4 border)
        {
            return new SpriteMetaData { name = name, rect = rect, border = border, alignment = (int)SpriteAlignment.Center };
        }

        [RevAutomationAction("game.gal-art.assemble", Environment = "edit", Keys = new string[0], Description = "绑定 Gal 九宫格主题、左侧说话人立绘和大厅入口")]
        public static object Assemble(RevAutomationRequest request)
        {
            var theme = AssetDatabase.LoadAssetAtPath<RevGalPanelTheme>(ThemePath);
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (theme == null || font == null)
            {
                throw new InvalidOperationException("缺少 Gal 面板主题或项目中文字库：" + ThemePath);
            }
            var sprites = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<Sprite>().ToDictionary(sprite => sprite.name);
            var serializedTheme = new SerializedObject(theme);
            serializedTheme.FindProperty("font").objectReferenceValue = font;
            serializedTheme.ApplyModifiedPropertiesWithoutUndo();
            ConfigureTheme(theme, sprites["GalDialogueWood"], sprites["GalButton"], sprites["GalSettings"]);
            ConfigureSpeakerLayer();
            AddHomeEntry(font);
            AssetDatabase.SaveAssets();
            return new Dictionary<string, object>
            {
                ["theme"] = ThemePath, ["entry"] = HomePath, ["controlSprites"] = sprites.Count
            };
        }

        private static void ConfigureTheme(RevGalPanelTheme theme, Sprite dialogue, Sprite button, Sprite settings)
        {
            foreach (var style in theme.Parts)
            {
                switch (style.Part)
                {
                    case RevGalThemePart.DialogueBox:
                        ApplySkin(style, dialogue);
                        Place(style, .035f, .035f, .965f, .327f);
                        break;
                    case RevGalThemePart.Header:
                        style.Sprite = null;
                        style.AtlasSprite = string.Empty;
                        style.OverrideColor = true;
                        style.Tint = Color.clear;
                        Place(style, .035f, .915f, .965f, .992f);
                        break;
                    case RevGalThemePart.SettingsPanel:
                        ApplySkin(style, settings);
                        break;
                    case RevGalThemePart.Close:
                    case RevGalThemePart.OpenSettings:
                    case RevGalThemePart.Advance:
                    case RevGalThemePart.ChoiceButton:
                    case RevGalThemePart.CloseSettings:
                    case RevGalThemePart.Auto:
                    case RevGalThemePart.ReadSkip:
                    case RevGalThemePart.Rollback:
                    case RevGalThemePart.History:
                    case RevGalThemePart.QuickSave:
                    case RevGalThemePart.QuickLoad:
                    case RevGalThemePart.HistoryClose:
                    case RevGalThemePart.HistoryOlder:
                    case RevGalThemePart.HistoryNewer:
                        ApplySkin(style, button);
                        break;
                    case RevGalThemePart.Speaker:
                        style.OverrideColor = true;
                        style.Tint = new Color(1f, .79f, .46f);
                        Place(style, .24f, .71f, .85f, .98f);
                        break;
                    case RevGalThemePart.Dialogue:
                        Place(style, .24f, .08f, .86f, .71f);
                        break;
                    case RevGalThemePart.LeftPortrait:
                        Place(style, .021f, .040f, .247f, .895f);
                        break;
                    case RevGalThemePart.CenterPortrait:
                        // 冒险家半身像使用独立的左栏范围，肩线压在对白框左侧。
                        Place(style, .042f, .070f, .228f, .425f);
                        break;
                }
            }
            EditorUtility.SetDirty(theme);
        }

        /// <summary>说话人盖在对白框左栏上，始终位于设置和回看的输入层下方。</summary>
        private static void ConfigureSpeakerLayer()
        {
            var root = PrefabUtility.LoadPrefabContents(PanelPath);
            try
            {
                var portrait = root.transform.Find("View/Story/Left");
                var bust = root.transform.Find("View/Story/Center");
                var title = root.transform.Find("View/Story/Header/Title");
                if (portrait == null || bust == null || title == null)
                {
                    throw new InvalidOperationException("Gal 面板缺少左侧立绘或标题节点：" + PanelPath);
                }
                portrait.SetAsLastSibling();
                bust.SetAsLastSibling();
                portrait.GetComponent<UnityEngine.UI.Image>().preserveAspect = true;
                bust.GetComponent<UnityEngine.UI.Image>().preserveAspect = true;
                title.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, PanelPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ApplySkin(RevGalPartStyle style, Sprite sprite)
        {
            style.Sprite = sprite;
            style.Sliced = true;
        }

        private static void Place(RevGalPartStyle style, float left, float bottom, float right, float top)
        {
            style.Layout.Override = true;
            style.Layout.AnchorMin = new Vector2(left, bottom);
            style.Layout.AnchorMax = new Vector2(right, top);
            style.Layout.OffsetMin = Vector2.zero;
            style.Layout.OffsetMax = Vector2.zero;
        }

        /// <summary>大厅入口复用同屏点滴按钮的底图，点击才真正加载续篇的 UI 分包。</summary>
        private static void AddHomeEntry(Font font)
        {
            var root = PrefabUtility.LoadPrefabContents(HomePath);
            try
            {
                if (root.transform.Find("btnCottageStory") != null)
                {
                    return;
                }
                var memory = root.transform.Find("btnMemories");
                var memoryImage = memory.GetComponent<UnityEngine.UI.Image>();
                var button = new GameObject("btnCottageStory", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                button.layer = memory.gameObject.layer;
                var rect = (RectTransform)button.transform;
                rect.SetParent(root.transform, false);
                rect.anchorMin = rect.anchorMax = new Vector2(.125f, .836f);
                rect.sizeDelta = new Vector2(312f, 68f);
                var image = button.GetComponent<UnityEngine.UI.Image>();
                image.sprite = memoryImage.sprite;
                image.type = memoryImage.type;
                image.color = memoryImage.color;
                button.GetComponent<UnityEngine.UI.Button>().targetGraphic = image;
                var label = new GameObject("label", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                label.layer = button.layer;
                var labelRect = (RectTransform)label.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(10f, 0f);
                labelRect.offsetMax = new Vector2(-10f, 0f);
                var text = label.GetComponent<UnityEngine.UI.Text>();
                text.font = font;
                text.text = "和鸦鸦一起过日子";
                text.fontSize = 30;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(.28f, .16f, .09f);
                text.raycastTarget = false;
                button.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, HomePath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}

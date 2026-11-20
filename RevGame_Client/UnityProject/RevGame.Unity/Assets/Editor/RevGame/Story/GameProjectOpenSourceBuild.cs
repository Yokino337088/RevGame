using System;
using System.IO;
using RevGame.Data;
using RevGame.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>装配项目书及大厅入口。重装配会保留作者配置资产与预制体 GUID。</summary>
    public static class GameProjectOpenSourceBuild
    {
        private const string Root = "Assets/GameRes/";
        private const string Panel = Root + "UI/Panel/ProjectOpenSource/";
        private const string Book = Root + "UI/Panel/YayaMemories/Book/";
        private const string Materials = Root + "Material/UI/YayaMemories/";
        private const string Art = Root + "Art/UI/ProjectOpenSource/";
        private const string MemoryArt = Root + "Art/UI/YayaMemories/";
        private static readonly Color Ink = new Color(.27f, .14f, .065f);
        private static readonly Color NoteInk = new Color(.37f, .22f, .12f);
        private static readonly Color Cream = new Color(1f, .94f, .77f);

        [MenuItem("RevGame/资源/装配项目开源界面")]
        private static void ConfigureFromMenu()
        {
            Debug.Log(Configure());
        }

        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("项目书只在编辑态装配");
            }
            Directory.CreateDirectory(Panel);
            AssetDatabase.Refresh();
            Sprite primary = GameYayaMemoryBookBuild.ImportPlate(Art + "ProjectBookPrimary.png", .14f);
            Sprite secondary = GameYayaMemoryBookBuild.ImportPlate(Art + "ProjectBookSecondary.png", .14f);
            var heading = Require<Sprite>(MemoryArt + "MemoryBookTitle.png");
            var config = GetOrCreateConfig();
            BuildBook(config, heading, primary, secondary);
            PatchHome();
            AssetDatabase.SaveAssets();
            return "已装配大厅项目开源入口、两页书本、按钮和作者配置";
        }

        private static ProjectOpenSourceConfig GetOrCreateConfig()
        {
            const string path = Panel + "ProjectOpenSourceConfig.asset";
            var config = AssetDatabase.LoadAssetAtPath<ProjectOpenSourceConfig>(path);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<ProjectOpenSourceConfig>();
                AssetDatabase.CreateAsset(config, path);
            }
            return config;
        }

        /// <summary>补齐项目开源入口，沿用大厅行距并装配专用书本插画，修改结束后卸载预制体内容。</summary>
        private static void PatchHome()
        {
            const string path = Root + "UI/Panel/HomePanel.prefab";
            GameObject home = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var quit = (RectTransform)home.transform.Find("btnQuit");
                var endless = home.transform.Find("btnEndless");
                var entry = home.transform.Find("btnProjectOpenSource") as RectTransform;
                if (entry == null)
                {
                    entry = (RectTransform)Object.Instantiate(endless.gameObject, home.transform, false).transform;
                    entry.name = "btnProjectOpenSource";
                }
                // 沿用右侧菜单的宽高和行距，书本插画与其它入口使用同一大厅图集。
                Vector2 row = new Vector2(0f, .115f);
                entry.anchorMin = quit.anchorMin - row;
                entry.anchorMax = quit.anchorMax - row;
                entry.offsetMin = quit.offsetMin;
                entry.offsetMax = quit.offsetMax;
                entry.SetSiblingIndex(quit.GetSiblingIndex() + 1);
                entry.Find("label").GetComponent<Text>().text = "项目开源";
                GameLobbyButtonBuild.Apply(entry.GetComponent<Button>());
                if (PrefabUtility.SaveAsPrefabAsset(home, path) == null)
                {
                    throw new InvalidOperationException("大厅项目开源入口保存失败");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(home);
            }
        }

        private static void BuildBook(ProjectOpenSourceConfig config, Sprite heading, Sprite primary, Sprite secondary)
        {
            var root = new GameObject("ProjectOpenSourcePanel", typeof(RectTransform), typeof(CanvasGroup), typeof(ProjectOpenSourcePanel));
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 1080);
                var panel = new SerializedObject(root.GetComponent<ProjectOpenSourcePanel>());
                panel.FindProperty("configuration").objectReferenceValue = config;
                panel.ApplyModifiedPropertiesWithoutUndo();
                var coverArt = Require<Texture2D>(Book + "BookCover.png");
                var openArt = Require<Texture2D>(Book + "OpenBook.png");
                var backdrop = Raw(root.transform, "Backdrop", coverArt, null, true);
                backdrop.color = new Color(.37f, .28f, .24f);
                Raw(root.transform, "Paper", openArt, Require<Material>(Materials + "BookPaper.mat"), true);

                Transform pages = Stretch("Pages", root.transform);
                Transform overview = Page("Overview", pages).transform;
                BuildOverview(overview, heading, primary, secondary);
                Transform author = Page("Author", pages).transform;
                BuildAuthor(author, heading);
                author.gameObject.SetActive(false);
                BuildNavigation(Stretch("Navigation", root.transform), primary, secondary);

                Raw(root.transform, "Cover", coverArt, Require<Material>(Materials + "BookCoverFace.mat"), true);
                Transform coverTitle = Page("CoverTitle", root.transform).transform;
                Text stamp = Label(coverTitle, "CoverStamp", "项目开源", 0, 102, 960, 140, 100, Cream);
                var shadow = stamp.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(.21f, .08f, .034f, .9f);
                shadow.effectDistance = new Vector2(2, -5);

                if (PrefabUtility.SaveAsPrefabAsset(root, Panel + "ProjectOpenSourcePanel.prefab") == null)
                {
                    throw new InvalidOperationException("项目开源预制体保存失败");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildOverview(Transform page, Sprite heading, Sprite primary, Sprite secondary)
        {
            Heading(page, "OverviewHeading", "项目开源", heading);
            Label(page, "TechnicalLead", "技术负责人：想陶朴智妍的吉吉", 0, 279, 1300, 72, 56, Ink);
            Label(page, "ArtWorkflow", "美术工作流：BitMotor", 0, 203, 1300, 72, 56, Ink);
            Label(page, "Design", "策划：星尘", 0, 127, 1300, 72, 56, Ink);

            Label(page, "RepositoryAddress", ProjectOpenSourcePanel.RepositoryAddress, 0, 20, 1140, 58, 42, Ink);
            ActionButton(page, "btnRepository", "前往仓库", primary, -148, -60, 264, Cream);
            ActionButton(page, "btnCopyRepository", "复制地址", secondary, 148, -60, 264, Ink);

            Label(page, "GroupNumber", "游戏开发技术交流群 QQ：" + ProjectOpenSourcePanel.GroupNumber, 0, -159, 1250, 57, 41, Ink);
            ActionButton(page, "btnJoinGroup", "点击加群", primary, -148, -237, 264, Cream);
            ActionButton(page, "btnCopyGroup", "复制群号", secondary, 148, -237, 264, Ink);
            Label(page, "Notice", "", 0, -305, 1020, 48, 29, NoteInk);
            Label(page, "NextHint", "下一页是作者的话 · 点击下一页或拖动右侧书角", 0, -379, 1280, 46, 30, NoteInk);
        }

        private static void BuildAuthor(Transform page, Sprite heading)
        {
            Heading(page, "AuthorHeading", "作者的话", heading);
            GameObject message = GameAssetBuilder.Rect("Message", page, new Vector2(1180, 688), new Vector2(0, -26));
            message.AddComponent<RectMask2D>();
            var scroll = message.AddComponent<ScrollRect>();
            scroll.viewport = (RectTransform)message.transform;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            Text body = Label(message.transform, "Body", "", 0, 0, 1180, 0, 42, Ink);
            body.alignment = TextAnchor.UpperLeft;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            body.lineSpacing = 1.18f;
            body.raycastTarget = true;
            RectTransform content = body.rectTransform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(.5f, 1f);
            content.sizeDelta = content.anchoredPosition = Vector2.zero;
            body.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;
        }

        private static void BuildNavigation(Transform navigation, Sprite primary, Sprite secondary)
        {
            ActionButton(navigation, "btnClose", "合上书", secondary, 720, 410, 202, Ink);
            ActionButton(navigation, "btnPrevious", "上一页", secondary, -704, -433, 208, Ink).gameObject.SetActive(false);
            ActionButton(navigation, "btnNext", "下一页", primary, 704, -433, 208, Cream);
            Label(navigation, "PageNumber", "1 / 2", 0, -433, 148, 46, 30, NoteInk);
        }

        private static void Heading(Transform page, string name, string text, Sprite heading)
        {
            Image plate = GameAssetBuilder.Picture(name + "Plate", page, heading, new Vector2(642, 104), new Vector2(0, 410));
            plate.raycastTarget = false;
            Label(page, name, text, 0, 410, 586, 76, 58, Ink);
        }

        private static Button ActionButton(Transform page, string name, string text, Sprite skin, float x, float y, float width, Color textColor)
        {
            var size = new Vector2(width, 68);
            Image buttonImage = GameAssetBuilder.Picture(name, page, skin, size, new Vector2(x, y));
            buttonImage.raycastTarget = true;
            var button = buttonImage.gameObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1f, .95f, .78f);
            colors.pressedColor = new Color(.85f, .75f, .62f);
            button.colors = colors;
            Label(button.transform, name + "Label", text, 0, 0, width - 20, 54, width >= 240 ? 34 : 30, textColor);
            return button;
        }

        private static Text Label(Transform page, string name, string text, float x, float y, float width, float height, int size, Color color)
        {
            Text label = GameAssetBuilder.Label(name, page, text, new Vector2(width, height), new Vector2(x, y), size);
            label.color = color;
            label.fontStyle = FontStyle.Normal;
            // 粗体黑体的行高大于文字字号，允许越过布置框才能画全标题与署名。
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.supportRichText = false;
            return label;
        }

        private static CanvasGroup Page(string name, Transform parent)
        {
            var page = Stretch(name, parent).gameObject.AddComponent<CanvasGroup>();
            page.blocksRaycasts = page.interactable = false;
            return page;
        }

        private static RawImage Raw(Transform parent, string name, Texture texture, Material material, bool raycast)
        {
            var image = Stretch(name, parent).gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.material = material;
            image.raycastTarget = raycast;
            return image;
        }

        private static RectTransform Stretch(string name, Transform parent)
        {
            var rect = (RectTransform)GameAssetBuilder.Rect(name, parent, Vector2.zero, Vector2.zero).transform;
            GameAssetBuilder.Stretch(rect);
            return rect;
        }

        private static T Require<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new FileNotFoundException("项目书资源缺失", path);
            }
            return asset;
        }
    }
}

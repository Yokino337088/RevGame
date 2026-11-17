using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution.Gal.Editor
{
    /// <summary>生成可直接编辑的 UGUI 预制体。布局、蒙版和按钮随 Prefab 打包。</summary>
    internal static class RevGalPanelBuilder
    {
        private static readonly Color Backdrop = new Color(.055f, .078f, .115f);
        private static readonly Color PanelColor = new Color(.09f, .13f, .19f, .97f);
        private static readonly Color ButtonColor = new Color(.22f, .34f, .46f);
        private static readonly Color Accent = new Color(.55f, .83f, .94f);
        private static readonly Color Ink = new Color(.94f, .96f, 1f);

        // 部位映射属于默认 Prefab 的生成配方。生成时存成组件引用，播放时不走层级搜索。
        private static readonly (RevGalThemePart Part, string Path)[] ThemeTargets =
        {
            (RevGalThemePart.Backdrop, ""),
            (RevGalThemePart.Background, "Story/Background"),
            (RevGalThemePart.LeftPortrait, "Story/Left"),
            (RevGalThemePart.CenterPortrait, "Story/Center"),
            (RevGalThemePart.RightPortrait, "Story/Right"),
            (RevGalThemePart.Header, "Story/Header"),
            (RevGalThemePart.Title, "Story/Header/Title"),
            (RevGalThemePart.Mode, "Story/Header/Mode"),
            (RevGalThemePart.Close, "Story/Header/Close"),
            (RevGalThemePart.OpenSettings, "Story/Header/OpenSettings"),
            (RevGalThemePart.DialogueBox, "Story/DialogueDock"),
            (RevGalThemePart.Speaker, "Story/DialogueDock/Speaker"),
            (RevGalThemePart.Dialogue, "Story/DialogueDock/Line"),
            (RevGalThemePart.Advance, "Story/DialogueDock/Advance"),
            (RevGalThemePart.Notice, "Story/Notice"),
            (RevGalThemePart.Choices, "Story/Choices"),
            (RevGalThemePart.ChoiceButton, "Story/Choices/ChoiceTemplate"),
            (RevGalThemePart.SettingsShade, "SettingsOverlay"),
            (RevGalThemePart.SettingsPanel, "SettingsOverlay/SettingsPanel"),
            (RevGalThemePart.SettingsTitle, "SettingsOverlay/SettingsPanel/Title"),
            (RevGalThemePart.SettingsMode, "SettingsOverlay/SettingsPanel/Mode"),
            (RevGalThemePart.SettingsNotice, "SettingsOverlay/SettingsPanel/Notice"),
            (RevGalThemePart.CloseSettings, "SettingsOverlay/SettingsPanel/CloseSettings"),
            (RevGalThemePart.Auto, "SettingsOverlay/SettingsPanel/Auto"),
            (RevGalThemePart.ReadSkip, "SettingsOverlay/SettingsPanel/ReadSkip"),
            (RevGalThemePart.Rollback, "SettingsOverlay/SettingsPanel/Rollback"),
            (RevGalThemePart.History, "SettingsOverlay/SettingsPanel/History"),
            (RevGalThemePart.QuickSave, "SettingsOverlay/SettingsPanel/QuickSave"),
            (RevGalThemePart.QuickLoad, "SettingsOverlay/SettingsPanel/QuickLoad"),
            (RevGalThemePart.HistoryShade, "Backlog"),
            (RevGalThemePart.HistoryTitle, "Backlog/BacklogTitle"),
            (RevGalThemePart.HistoryText, "Backlog/HistoryScroll"),
            (RevGalThemePart.HistoryPage, "Backlog/HistoryPage"),
            (RevGalThemePart.HistoryOlder, "Backlog/OlderHistory"),
            (RevGalThemePart.HistoryNewer, "Backlog/NewerHistory"),
            (RevGalThemePart.HistoryClose, "Backlog/CloseHistory")
        };

        private static string PanelDirectory => RevGalAssets.ResourceRoot() + "/" + RevGal.PanelRoot;

        /// <summary>仅建立用于测量的默认层级。对象带隐藏标志，调用方结束测量后销毁。</summary>
        internal static GameObject BuildLayoutPreview(RevGalPanelTheme theme)
        {
            var root = new GameObject("GalLayoutPreview", typeof(RectTransform));
            root.hideFlags = HideFlags.HideAndDontSave;
            // 设计器也可能在 Play 中刷新，先停用层级再添加 View，避免触发运行时 Awake 和资源逻辑。
            root.SetActive(false);
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(1920, 1080);
            try
            {
                var fields = BuildView(rect, theme.Font != null ? theme.Font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
                fields.ApplyModifiedPropertiesWithoutUndo();
                foreach (var part in theme.Parts.Where(style => style != null))
                {
                    var target = FindPreviewTarget(root, part.Part);
                    if (target != null && RevGalPartStyle.UsesRectLayout(part.Part) && part.Layout != null && part.Layout.Override)
                    {
                        part.Layout.ApplyTo(target);
                    }
                }
                rect.ForceUpdateRectTransforms();
                return root;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw;
            }
        }

        /// <summary>从默认配方中找到控件，CLI 和设计器都通过稳定部位名称定位。</summary>
        internal static RectTransform FindPreviewTarget(GameObject root, RevGalThemePart part)
        {
            string path = ThemeTargets.First(target => target.Part == part).Path;
            return root.transform.Find(path.Length == 0 ? "View" : "View/" + path) as RectTransform;
        }

        /// <summary>从工作台打开当前默认面板实际绑定的主题。</summary>
        public static RevGalPanelTheme FindTheme()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelDirectory + "/RevGalPanel.prefab");
            return prefab == null ? null : prefab.GetComponentInChildren<RevGalView>(true)?.Theme;
        }

        /// <summary>生成默认面板及主题绑定，已有主题可跨剧情复用，重建时保留该主题 SO。</summary>
        public static (string Path, string ThemePath) Create(string fontPath, string themePath, bool overwrite, string bundle)
        {
            string folder = PanelDirectory;
            string path = folder + "/RevGalPanel.prefab";
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            var previousPrefab = existing as GameObject;
            if ((existing != null || File.Exists(path))
                && (!overwrite || previousPrefab == null || previousPrefab.GetComponent<RevGalPanel>() == null))
            {
                throw new InvalidOperationException("默认面板已存在。重建需要允许覆盖 RevGalPanel：" + path);
            }
            Font previousFont = previousPrefab == null ? null : previousPrefab.GetComponentInChildren<Text>(true)?.font;
            Font font = string.IsNullOrEmpty(fontPath) ? previousFont ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                : AssetDatabase.LoadAssetAtPath<Font>(RevGalAssets.AssetPath(fontPath, null));
            if (font == null)
            {
                throw new ArgumentException("无法读取字体：" + fontPath);
            }
            var previousTheme = previousPrefab == null ? null : previousPrefab.GetComponentInChildren<RevGalView>(true)?.Theme;
            bool explicitTheme = !string.IsNullOrEmpty(themePath);
            themePath = explicitTheme ? RevGalAssets.AssetPath(themePath, ".asset") : folder + "/RevGalPanelTheme.asset";
            var theme = !explicitTheme && previousTheme != null
                ? previousTheme : AssetDatabase.LoadAssetAtPath<RevGalPanelTheme>(themePath);
            if (theme == null && (explicitTheme || File.Exists(themePath)))
            {
                throw new ArgumentException("无法读取剧情面板主题：" + themePath);
            }

            RevGalAssets.EnsureFolder(folder);
            bool createTheme = theme == null;
            bool saved = false;
            var root = new GameObject("RevGalPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(RevGalPanel));
            try
            {
                var rect = (RectTransform)root.transform;
                rect.sizeDelta = new Vector2(1920, 1080);
                Sound(rect, "Music", true);
                Sound(rect, "Voice", false);
                Sound(rect, "Sound", false);
                var fields = BuildView(rect, font);
                if (createTheme)
                {
                    theme = ScriptableObject.CreateInstance<RevGalPanelTheme>();
                }
                BindTheme(fields, theme, createTheme);
                if (createTheme)
                {
                    AssetDatabase.CreateAsset(theme, themePath);
                    RevGalAuthoring.MarkBundle(themePath, bundle);
                }
                Bind(fields, "theme", theme);
                fields.ApplyModifiedPropertiesWithoutUndo();

                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                {
                    throw new InvalidOperationException("默认剧情面板保存失败：" + path);
                }
                saved = true;
                RevGalAuthoring.MarkBundle(path, bundle);
                return (path, AssetDatabase.GetAssetPath(theme));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (!saved && createTheme && theme != null)
                {
                    if (AssetDatabase.Contains(theme))
                    {
                        AssetDatabase.DeleteAsset(themePath);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(theme);
                    }
                }
            }
        }

        private static SerializedObject BuildView(RectTransform root, Font font)
        {
            var view = Node(root, "View", 0, 0, 1, 1);
            Plate(view, Backdrop, true);
            var fields = new SerializedObject(view.gameObject.AddComponent<RevGalView>());
            var story = Node(view, "Story", 0, 0, 1, 1);
            Bind(fields, "storyInput", story.gameObject.AddComponent<CanvasGroup>());
            Bind(fields, "background", Art(story, "Background", 0, 0, 1, 1));
            Bind(fields, "left", Art(story, "Left", .03f, .24f, .37f, .89f));
            Bind(fields, "center", Art(story, "Center", .33f, .24f, .67f, .89f));
            Bind(fields, "right", Art(story, "Right", .63f, .24f, .97f, .89f));

            var header = Node(story, "Header", 0, .91f, 1, 1);
            Plate(header, PanelColor, false);
            Bind(fields, "title", Label(Node(header, "Title", .04f, 0, .53f, 1), font, "", 40, TextAnchor.MiddleLeft, Ink));
            Bind(fields, "mode", Label(Node(header, "Mode", .55f, 0, .77f, 1), font, "", 26, TextAnchor.MiddleRight, Accent));
            Bind(fields, "openSettings", Button(Node(header, "OpenSettings", .79f, .17f, .878f, .83f), font, "设置", 28));
            Bind(fields, "close", Button(Node(header, "Close", .89f, .17f, .972f, .83f), font, "关闭", 28));

            var dock = Node(story, "DialogueDock", .036f, .055f, .964f, .344f);
            Plate(dock, PanelColor, true);
            Bind(fields, "speaker", Label(Node(dock, "Speaker", .036f, .71f, .962f, .98f), font, "", 36, TextAnchor.MiddleLeft, Accent));
            Bind(fields, "dialogue", ScrollingText(Node(dock, "Line", .036f, .08f, .86f, .71f), font, 37, out ScrollRect dialogueScroll));
            Bind(fields, "dialogueScroll", dialogueScroll);
            Bind(fields, "advance", Button(Node(dock, "Advance", .879f, .10f, .974f, .36f), font, "继续", 28));

            Bind(fields, "notice", Label(Node(story, "Notice", .036f, .345f, .964f, .407f), font, "", 25, TextAnchor.MiddleLeft, Accent));

            var choices = Node(story, "Choices", .18f, .436f, .82f, .84f);
            var choiceContent = ScrollContent(choices, out _);
            var column = choiceContent.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 12f;
            column.padding = new RectOffset(12, 12, 12, 12);
            column.childControlHeight = column.childControlWidth = true;
            column.childForceExpandHeight = false;
            FitHeight(choiceContent);
            var choiceTemplate = Node(choices, "ChoiceTemplate", 0, 0, 1, 1);
            choiceTemplate.gameObject.AddComponent<LayoutElement>().preferredHeight = 65f;
            Bind(fields, "choiceTemplate", Button(choiceTemplate, font, "", 32));
            choiceTemplate.gameObject.SetActive(false);
            Bind(fields, "choiceRoot", choiceContent);
            Bind(fields, "choiceOverlay", choices.gameObject);
            choices.gameObject.SetActive(false);

            BuildSettings(view, fields, font);
            var backlog = Node(view, "Backlog", 0, 0, 1, 1);
            Plate(backlog, new Color(.035f, .054f, .086f, .98f), true);
            Label(Node(backlog, "BacklogTitle", .109f, .84f, .75f, .93f), font, "对话回看", 43, TextAnchor.MiddleLeft, Ink);
            Bind(fields, "closeHistory", Button(Node(backlog, "CloseHistory", .795f, .846f, .905f, .918f), font, "返回", 28));
            Bind(fields, "historyText", ScrollingText(Node(backlog, "HistoryScroll", .109f, .105f, .9f, .81f), font, 34, out ScrollRect historyScroll));
            Bind(fields, "historyScroll", historyScroll);
            Bind(fields, "historyOverlay", backlog.gameObject);
            Bind(fields, "olderHistory", Button(Node(backlog, "OlderHistory", .30f, .028f, .405f, .084f), font, "更早", 25));
            Bind(fields, "newerHistory", Button(Node(backlog, "NewerHistory", .595f, .028f, .7f, .084f), font, "更新", 25));
            Bind(fields, "historyPage", Label(Node(backlog, "HistoryPage", .425f, .028f, .575f, .084f), font, "", 24, TextAnchor.MiddleCenter, Accent));
            backlog.gameObject.SetActive(false);
            return fields;
        }

        /// <summary>播放工具共用独立弹层。遮罩置于剧情层上方，按钮位置可各自覆盖。</summary>
        private static void BuildSettings(RectTransform view, SerializedObject fields, Font font)
        {
            var overlay = Node(view, "SettingsOverlay", 0, 0, 1, 1);
            Plate(overlay, new Color(.025f, .043f, .071f, .77f), true);
            Bind(fields, "settingsOverlay", overlay.gameObject);
            var card = Node(overlay, "SettingsPanel", .25f, .22f, .75f, .8f);
            Plate(card, PanelColor, true);
            Label(Node(card, "Title", .08f, .82f, .68f, .96f), font, "剧情设置", 42, TextAnchor.MiddleLeft, Ink);
            Bind(fields, "closeSettings", Button(Node(card, "CloseSettings", .76f, .83f, .92f, .94f), font, "返回", 28));
            Bind(fields, "settingsMode", Label(Node(card, "Mode", .08f, .705f, .92f, .81f), font, "手动播放", 26,
                TextAnchor.MiddleLeft, Accent));
            Bind(fields, "settingsNotice", Label(Node(card, "Notice", .08f, .058f, .92f, .157f), font, "", 25,
                TextAnchor.MiddleLeft, Accent));
            Bind(fields, "auto", Button(Node(card, "Auto", .08f, .549f, .475f, .677f), font, "自动", 30));
            Bind(fields, "skip", Button(Node(card, "ReadSkip", .525f, .549f, .92f, .677f), font, "跳过已读", 30));
            Bind(fields, "rollback", Button(Node(card, "Rollback", .08f, .371f, .475f, .499f), font, "回退", 30));
            Bind(fields, "history", Button(Node(card, "History", .525f, .371f, .92f, .499f), font, "回看", 30));
            Bind(fields, "save", Button(Node(card, "QuickSave", .08f, .193f, .475f, .321f), font, "快速保存", 30));
            Bind(fields, "load", Button(Node(card, "QuickLoad", .525f, .193f, .92f, .321f), font, "快速读取", 30));
            overlay.gameObject.SetActive(false);
        }

        /// <summary>默认主题记录锚点与偏移初值，已有主题只重新绑定。</summary>
        private static void BindTheme(SerializedObject fields, RevGalPanelTheme theme, bool seed)
        {
            var view = (RevGalView)fields.targetObject;
            var targets = fields.FindProperty("themeTargets");
            targets.arraySize = ThemeTargets.Length;
            for (int index = 0; index < ThemeTargets.Length; index++)
            {
                var (part, path) = ThemeTargets[index];
                var rect = (RectTransform)(path.Length == 0 ? view.transform : view.transform.Find(path));
                var binding = targets.GetArrayElementAtIndex(index);
                binding.FindPropertyRelative("part").intValue = (int)part;
                binding.FindPropertyRelative("rect").objectReferenceValue = rect;
                if (seed)
                {
                    theme.Parts[(int)part].Layout.ReadFrom(rect);
                }
            }
        }

        private static Text ScrollingText(RectTransform frame, Font font, int size, out ScrollRect scroll)
        {
            var content = ScrollContent(frame, out scroll);
            var text = Label(content, font, "", size, TextAnchor.UpperLeft, Ink);
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = true;
            text.lineSpacing = 1.12f;
            FitHeight(content);
            return text;
        }

        private static RectTransform ScrollContent(RectTransform root, out ScrollRect scroll)
        {
            // 透明图形只负责滚动命中，Viewport 裁剪超过安全阅读区的内容。
            Plate(root, Color.clear, true);
            root.gameObject.AddComponent<RectMask2D>();
            scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = root;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var content = Node(root, "Content", 0, 1, 1, 1);
            content.pivot = new Vector2(.5f, 1f);
            scroll.content = content;
            return content;
        }

        private static void FitHeight(RectTransform content)
        {
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static Image Art(Transform parent, string name, float x0, float y0, float x1, float y1)
        {
            var image = Plate(Node(parent, name, x0, y0, x1, y1), Color.white, false);
            image.preserveAspect = true;
            image.enabled = false;
            return image;
        }

        private static Image Plate(RectTransform rect, Color color, bool raycast)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        private static Text Label(RectTransform rect, Font font, string value, int size, TextAnchor anchor, Color color)
        {
            var label = rect.gameObject.AddComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.alignment = anchor;
            label.text = value;
            label.color = color;
            label.supportRichText = false;
            label.raycastTarget = false;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private static Button Button(RectTransform rect, Font font, string value, int size)
        {
            var image = Plate(rect, ButtonColor, true);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(.85f, .95f, 1f);
            colors.pressedColor = new Color(.57f, .76f, .97f);
            button.colors = colors;
            Label(Node(rect, "Label", .045f, 0, .955f, 1), font, value, size, TextAnchor.MiddleCenter, Ink);
            return button;
        }

        private static RectTransform Node(Transform parent, string name, float x0, float y0, float x1, float y1)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(x0, y0);
            rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static void Sound(Transform root, string name, bool loop)
        {
            var source = Node(root, name, 0, 0, 0, 0).gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0;
        }

        private static void Bind(SerializedObject view, string name, UnityEngine.Object reference)
        {
            view.FindProperty(name).objectReferenceValue = reference;
        }
    }
}

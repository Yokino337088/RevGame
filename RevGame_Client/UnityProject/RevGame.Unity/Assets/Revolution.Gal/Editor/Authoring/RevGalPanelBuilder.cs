using System;
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

        public static string Create(string fontPath, bool overwrite, string bundle)
        {
            string path = RevGalAssets.ResourceRoot() + "/" + RevGal.PanelRoot + "/RevGalPanel.prefab";
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && (!overwrite || !(existing is GameObject rootPrefab) || rootPrefab.GetComponent<RevGalPanel>() == null))
            {
                throw new InvalidOperationException("默认面板已存在。重建需要允许覆盖 RevGalPanel：" + path);
            }
            Font font = string.IsNullOrEmpty(fontPath) ? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                : AssetDatabase.LoadAssetAtPath<Font>(RevGalAssets.AssetPath(fontPath, null));
            if (font == null)
            {
                throw new ArgumentException("无法读取字体：" + fontPath);
            }
            RevGalAssets.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));

            var root = new GameObject("RevGalPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(RevGalPanel));
            try
            {
                var rect = (RectTransform)root.transform;
                rect.sizeDelta = new Vector2(1920, 1080);
                Sound(rect, "Music", true);
                Sound(rect, "Voice", false);
                Sound(rect, "Sound", false);
                BuildView(rect, font);

                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                {
                    throw new InvalidOperationException("默认剧情面板保存失败");
                }
                RevGalAuthoring.MarkBundle(path, bundle);
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void BuildView(RectTransform root, Font font)
        {
            var view = Node(root, "View", 0, 0, 1, 1);
            Plate(view, Backdrop, true);
            var fields = new SerializedObject(view.gameObject.AddComponent<RevGalView>());
            Bind(fields, "background", Art(view, "Background", 0, 0, 1, 1));
            Bind(fields, "left", Art(view, "Left", .03f, .24f, .37f, .89f));
            Bind(fields, "center", Art(view, "Center", .33f, .24f, .67f, .89f));
            Bind(fields, "right", Art(view, "Right", .63f, .24f, .97f, .89f));

            var header = Node(view, "Header", 0, .91f, 1, 1);
            Plate(header, PanelColor, false);
            Bind(fields, "title", Label(Node(header, "Title", .04f, 0, .68f, 1), font, "", 40, TextAnchor.MiddleLeft, Ink));
            Bind(fields, "mode", Label(Node(header, "Mode", .7f, 0, .86f, 1), font, "", 26, TextAnchor.MiddleRight, Accent));
            Bind(fields, "close", Button(Node(header, "Close", .89f, .17f, .972f, .83f), font, "关闭", 28));

            var dock = Node(view, "DialogueDock", .036f, .104f, .964f, .375f);
            Plate(dock, PanelColor, true);
            Bind(fields, "speaker", Label(Node(dock, "Speaker", .036f, .71f, .962f, .98f), font, "", 36, TextAnchor.MiddleLeft, Accent));
            Bind(fields, "dialogue", ScrollingText(Node(dock, "Line", .036f, .08f, .86f, .71f), font, 37, out ScrollRect dialogueScroll));
            Bind(fields, "dialogueScroll", dialogueScroll);
            Bind(fields, "advance", Button(Node(dock, "Advance", .879f, .10f, .974f, .36f), font, "继续", 28));

            var controls = Node(view, "Controls", .038f, .031f, .962f, .087f);
            var row = controls.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 11f;
            row.childControlWidth = row.childForceExpandWidth = true;
            row.childControlHeight = row.childForceExpandHeight = true;
            foreach (var control in new[]
            {
                (field: "auto", name: "Auto", label: "自动"), (field: "skip", name: "ReadSkip", label: "跳过已读"),
                (field: "rollback", name: "Rollback", label: "回退"), (field: "history", name: "History", label: "回看"),
                (field: "save", name: "QuickSave", label: "快速保存"), (field: "load", name: "QuickLoad", label: "快速读取")
            })
            {
                Bind(fields, control.field, Button(Node(controls, control.name, 0, 0, 1, 1), font, control.label, 26));
            }
            Bind(fields, "notice", Label(Node(view, "Notice", .036f, .377f, .964f, .422f), font, "", 25, TextAnchor.MiddleLeft, Accent));

            var choices = Node(view, "Choices", .18f, .436f, .82f, .84f);
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
            fields.ApplyModifiedPropertiesWithoutUndo();
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

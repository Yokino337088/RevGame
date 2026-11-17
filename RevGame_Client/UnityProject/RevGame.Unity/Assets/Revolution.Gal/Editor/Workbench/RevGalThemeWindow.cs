using System;
using System.Collections.Generic;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>按可见控件选中并调整面板尺寸。所有保存调用与 RevCLI 相同的预检入口。</summary>
    public sealed class RevGalThemeWindow : EditorWindow
    {
        [SerializeField] private RevGalPanelTheme _theme;
        [SerializeField] private string _part = "DialogueBox";
        private VisualElement _list;
        private VisualElement _preview;
        private VisualElement _details;
        private Label _status;
        private TextField _search;

        /// <summary>工作台和主题 Inspector 都能打开当前主题。</summary>
        public static void Open(RevGalPanelTheme theme)
        {
            var window = GetWindow<RevGalThemeWindow>();
            window.titleContent = new GUIContent("Gal 面板设计");
            window.minSize = new Vector2(1040, 580);
            window._theme = theme;
            window.CreateGUI();
            window.Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            RevGalEditorStyles.Apply(root);
            var header = new VisualElement();
            header.AddToClassList("gal-header");
            var title = new Label("面板设计");
            title.AddToClassList("gal-brand");
            header.Add(title);
            var asset = new ObjectField { objectType = typeof(RevGalPanelTheme), value = _theme, allowSceneObjects = false };
            asset.style.width = 240;
            asset.RegisterValueChangedCallback(evt => { _theme = evt.newValue as RevGalPanelTheme; Refresh(); });
            header.Add(asset);
            header.Add(new Button(() =>
            {
                if (_theme != null)
                {
                    AssetDatabase.SaveAssetIfDirty(_theme);
                    Refresh();
                }
            }) { text = "保存主题" });
            _status = new Label("1920 × 1080 · 坐标从父控件左上角开始");
            _status.AddToClassList("gal-status");
            header.Add(_status);
            root.Add(header);
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1;
            root.Add(body);
            var left = new VisualElement();
            left.AddToClassList("gal-sidebar");
            left.style.width = 170;
            _search = new TextField { tooltip = "搜索控件名" };
            _search.RegisterValueChangedCallback(_ => BuildList());
            left.Add(_search);
            _list = new ScrollView();
            _list.AddToClassList("gal-outline");
            left.Add(_list);
            body.Add(left);
            var canvas = new VisualElement();
            canvas.AddToClassList("gal-preview-area");
            _preview = new VisualElement();
            _preview.AddToClassList("gal-preview-frame");
            canvas.Add(_preview);
            canvas.RegisterCallback<GeometryChangedEvent>(_ => _preview.style.height = Mathf.Max(160, _preview.resolvedStyle.width * 9f / 16f));
            body.Add(canvas);
            _details = new ScrollView();
            _details.AddToClassList("gal-inspector");
            _details.style.width = 290;
            body.Add(_details);
            Undo.undoRedoPerformed -= Refresh;
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Refresh;
        }

        private void BuildList()
        {
            _list.Clear();
            foreach (RevGalThemePart role in Enum.GetValues(typeof(RevGalThemePart)))
            {
                string label = RevGalLayoutAuthoring.Label(role);
                string query = _search.value;
                if (!string.IsNullOrWhiteSpace(query) && !label.Contains(query) && role.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var button = new Button(() => { _part = role.ToString(); Refresh(); }) { text = label };
                if (_part == role.ToString())
                {
                    button.AddToClassList("gal-selected");
                }
                _list.Add(button);
            }
        }

        /// <summary>预览使用生成器的实际矩形，切换控件即可查看其所属的主界面、设置或回看。</summary>
        private void Refresh()
        {
            if (_details == null)
            {
                return;
            }
            _details.Unbind();
            _details.Clear();
            _preview.Clear();
            BuildList();
            if (_theme == null)
            {
                return;
            }
            var report = RevGalLayoutAuthoring.Describe(_theme, null);
            var items = (List<Dictionary<string, object>>)report["items"];
            var selected = items.FirstOrDefault(item => (string)item["part"] == _part);
            if (selected == null)
            {
                return;
            }
            int role = (int)Enum.Parse(typeof(RevGalThemePart), _part);
            int mode = role >= (int)RevGalThemePart.HistoryShade ? 2 : role >= (int)RevGalThemePart.SettingsShade ? 1 : 0;
            foreach (var item in items)
            {
                int itemRole = (int)Enum.Parse(typeof(RevGalThemePart), (string)item["part"]);
                int group = itemRole >= (int)RevGalThemePart.HistoryShade ? 2 : itemRole >= (int)RevGalThemePart.SettingsShade ? 1 : 0;
                if (group != mode || !((bool)item["editable"]))
                {
                    continue;
                }
                var rect = (Dictionary<string, object>)item["canvasRect"];
                var box = new Label((string)item["label"]);
                box.AddToClassList("gal-preview-part");
                if ((string)item["part"] == _part)
                {
                    box.AddToClassList("gal-picked");
                }
                box.style.left = Length.Percent(Convert.ToSingle(rect["x"]) / 19.2f);
                box.style.top = Length.Percent(Convert.ToSingle(rect["y"]) / 10.8f);
                box.style.width = Length.Percent(Convert.ToSingle(rect["width"]) / 19.2f);
                box.style.height = Length.Percent(Convert.ToSingle(rect["height"]) / 10.8f);
                string part = (string)item["part"];
                box.RegisterCallback<ClickEvent>(_ => { _part = part; Refresh(); });
                _preview.Add(box);
            }
            var heading = new Label((string)selected["label"]);
            heading.AddToClassList("gal-section-title");
            _details.Add(heading);
            var rectangle = (Dictionary<string, object>)selected["rect"];
            var position = new Vector2Field("左上位置") { value = new Vector2(Convert.ToSingle(rectangle["x"]), Convert.ToSingle(rectangle["y"])) };
            var size = new Vector2Field("控件大小") { value = new Vector2(Convert.ToSingle(rectangle["width"]), Convert.ToSingle(rectangle["height"])) };
            position.SetEnabled((bool)selected["editable"]);
            size.SetEnabled((bool)selected["editable"]);
            _details.Add(position);
            _details.Add(size);
            if ((bool)selected["editable"])
            {
                _details.Add(new Button(() => Commit(new Dictionary<string, object> {
                    ["part"] = _part, ["rect"] = new Dictionary<string, object> {
                        ["x"] = position.value.x, ["y"] = position.value.y, ["width"] = size.value.x, ["height"] = size.value.y
                    }
                }, (string)report["hash"])) { text = "应用位置与大小" });
                _details.Add(new Button(() => Commit(new Dictionary<string, object> { ["part"] = _part, ["override"] = false }, (string)report["hash"]))
                    { text = "使用 Prefab 原布局" });
            }
            var serialized = new SerializedObject(_theme);
            var parts = serialized.FindProperty("parts");
            for (int index = 0; index < parts.arraySize; index++)
            {
                var style = parts.GetArrayElementAtIndex(index);
                if (style.FindPropertyRelative("Part").intValue != role)
                {
                    continue;
                }
                if (role == (int)RevGalThemePart.ChoiceButton)
                {
                    _details.Add(new PropertyField(style.FindPropertyRelative("ChoiceHeight"), "选项高度"));
                }
                _details.Add(new PropertyField(style.FindPropertyRelative("Layout"), "锚点与边距"));
                _details.Add(new PropertyField(style.FindPropertyRelative("Sprite"), "控件图片"));
                _details.Add(new PropertyField(style.FindPropertyRelative("AtlasSprite"), "图集切片"));
                _details.Add(new PropertyField(style.FindPropertyRelative("Sliced"), "九宫格"));
                _details.Add(new PropertyField(style.FindPropertyRelative("OverrideColor"), "覆盖颜色"));
                _details.Add(new PropertyField(style.FindPropertyRelative("Tint"), "颜色"));
                break;
            }
            _details.Bind(serialized);
        }

        /// <summary>用与 CLI 一致的批量文档保存，错误保留在状态栏以便纠正。</summary>
        private void Commit(Dictionary<string, object> edit, string hash)
        {
            try
            {
                var args = new Dictionary<string, object> {
                    ["path"] = AssetDatabase.GetAssetPath(_theme), ["expectedHash"] = hash,
                    ["document"] = RevAutomationJson.Write(new Dictionary<string, object> { ["parts"] = new[] { edit } })
                };
                RevGalLayoutAuthoring.Set(new RevAutomationRequest { payloadJson = RevAutomationJson.Write(args) });
                Refresh();
                _status.text = "布局已保存 · 下次打开剧情时应用";
            }
            catch (Exception error)
            {
                _status.text = error.Message;
            }
        }
    }
}

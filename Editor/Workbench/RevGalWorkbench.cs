using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>同一 SO 的写作、剧情图、问题列表和试读入口。</summary>
    public sealed class RevGalWorkbench : EditorWindow
    {
        [SerializeField] private RevGalStory _story;
        [SerializeField] private string _selected;
        private RevGalGraphView _graph;
        private ScrollView _outline;
        private ScrollView _inspector;
        private ScrollView _issues;
        private ObjectField _asset;
        private TextField _search;
        private DropdownField _kind;
        private static readonly RevGalNodeKind[] NodeKinds = (RevGalNodeKind[])Enum.GetValues(typeof(RevGalNodeKind));
        private Label _status;
        private IVisualElementScheduledItem _scheduled;
        private bool _redrawInspector;

        [MenuItem("Revolution.Tools/Gal/剧情工作台")]
        private static void OpenMenu()
        {
            Open(Selection.activeObject as RevGalStory);
        }

        internal static void Open(RevGalStory story)
        {
            var window = GetWindow<RevGalWorkbench>();
            window.titleContent = new GUIContent("Gal 剧情工作台");
            window.minSize = new Vector2(960, 520);
            if (story != null)
            {
                window.SetStory(story);
            }
            window.Show();
            window.Focus();
        }

        /// <summary>自动化写回当前剧情后，刷新已打开工作台的连线与详情。</summary>
        internal static void RefreshSavedStory(RevGalStory story)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<RevGalWorkbench>())
            {
                if (window._story == story)
                {
                    window.QueueRefresh(true);
                }
            }
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += UndoChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= UndoChanged;
            _scheduled?.Pause();
            _scheduled = null;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            var toolbar = new Toolbar();
            rootVisualElement.Add(toolbar);
            _asset = new ObjectField { objectType = typeof(RevGalStory), allowSceneObjects = false };
            _asset.style.width = 235;
            _asset.SetValueWithoutNotify(_story);
            _asset.RegisterValueChangedCallback(change => SetStory(change.newValue as RevGalStory));
            toolbar.Add(_asset);
            ToolbarAction(toolbar, "新建剧情", NewStory);
            ToolbarAction(toolbar, "保存", Save);
            ToolbarAction(toolbar, "导入 JSON", Import);
            ToolbarAction(toolbar, "导出 JSON", Export);
            ToolbarAction(toolbar, "从起点试读", () => Preview(null));
            ToolbarAction(toolbar, "定位全图", () => _graph.FrameAll());
            ToolbarAction(toolbar, "自动排版", Arrange);
            ToolbarAction(toolbar, "生成游戏面板", CreatePanel);
            ToolbarAction(toolbar, "编辑面板主题", EditPanelTheme);
            _status = new Label();
            _status.style.marginLeft = 12;
            _status.style.unityTextAlign = TextAnchor.MiddleLeft;
            toolbar.Add(_status);

            var body = new TwoPaneSplitView(0, 208, TwoPaneSplitViewOrientation.Horizontal);
            body.style.flexGrow = 1;
            rootVisualElement.Add(body);
            var left = new VisualElement();
            left.style.minWidth = 165;
            left.style.paddingLeft = 7;
            left.style.paddingRight = 7;
            left.Add(Header("剧情节点"));
            _search = new TextField { tooltip = "按标题、节点 ID、说话人或正文搜索" };
            _search.RegisterValueChangedCallback(_ => BuildOutline());
            left.Add(_search);
            left.Add(new Button(() => Select(null, false)) { text = "剧情设置 / 变量" });
            _kind = new DropdownField(NodeKinds.Select(RevGalEditorLabels.Kind).ToList(), 0);
            _kind.tooltip = "选择要添加的节点类型";
            left.Add(_kind);
            left.Add(new Button(() => AddNode(NodeKinds[_kind.index], null)) { text = "添加节点" });
            _outline = new ScrollView(ScrollViewMode.Vertical);
            _outline.style.flexGrow = 1;
            left.Add(_outline);
            body.Add(left);

            var details = new TwoPaneSplitView(1, 360, TwoPaneSplitViewOrientation.Horizontal);
            details.style.flexGrow = 1;
            body.Add(details);
            _graph = new RevGalGraphView(id => Select(id, false), structural => QueueRefresh(structural),
                (kind, position) => AddNode(kind, position));
            details.Add(_graph);
            _inspector = new ScrollView(ScrollViewMode.Vertical);
            _inspector.style.minWidth = 245;
            _inspector.style.paddingLeft = 11;
            _inspector.style.paddingRight = 11;
            details.Add(_inspector);

            var footer = new VisualElement();
            footer.style.flexShrink = 0;
            footer.style.maxHeight = 118;
            footer.style.paddingLeft = 10;
            footer.style.paddingRight = 10;
            footer.Add(Header("剧情校验 · 点击问题定位节点"));
            _issues = new ScrollView(ScrollViewMode.Vertical);
            _issues.style.maxHeight = 85;
            footer.Add(_issues);
            rootVisualElement.Add(footer);
            Refresh(true);
        }

        private void SetStory(RevGalStory story)
        {
            if (_story != story)
            {
                _story = story;
                _selected = story?.Definition?.Start;
            }
            _asset?.SetValueWithoutNotify(_story);
            Refresh(true);
        }

        private void UndoChanged()
        {
            QueueRefresh(true);
        }

        private void QueueRefresh(bool inspector)
        {
            _redrawInspector |= inspector;
            if (_scheduled != null || _graph == null)
            {
                return;
            }
            _scheduled = rootVisualElement.schedule.Execute(() =>
            {
                bool redraw = _redrawInspector;
                _redrawInspector = false;
                _scheduled = null;
                Refresh(redraw);
            }).StartingIn(120);
        }

        private void Refresh(bool inspector)
        {
            if (_graph == null)
            {
                return;
            }
            if (_selected != null && Selected() == null)
            {
                _selected = null;
                inspector = true;
            }
            _graph.Draw(_story, _selected);
            BuildOutline();
            BuildIssues();
            if (inspector)
            {
                BuildInspector();
            }
        }

        private void BuildOutline()
        {
            _outline.Clear();
            if (_story?.Definition?.Nodes == null)
            {
                return;
            }
            string query = _search.value?.Trim() ?? "";
            foreach (var node in _story.Definition.Nodes.Where(item => item != null))
            {
                if (query.Length > 0 && !new[] { node.Id, node.Title, node.Speaker, node.Text }.Any(part =>
                    part != null && part.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }
                string name = string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title;
                var button = new Button(() => Select(node.Id, true))
                {
                    text = (node.Id == _story.Definition.Start ? "● " : "") + name,
                    tooltip = RevGalEditorLabels.Kind(node.Kind) + " · " + node.Id
                };
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.minHeight = 25;
                if (node.Id == _selected)
                {
                    button.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
                _outline.Add(button);
            }
        }

        private void Select(string id, bool highlight)
        {
            _selected = id;
            if (highlight)
            {
                _graph.Highlight(id);
            }
            BuildOutline();
            BuildInspector();
        }

        private void BuildInspector()
        {
            _inspector.Unbind();
            _inspector.Clear();
            if (_story == null)
            {
                _inspector.Add(new HelpBox("先新建或选择一个剧情 SO，再写对白和连接节点。", HelpBoxMessageType.Info));
                return;
            }

            var serialized = new SerializedObject(_story);
            var definition = serialized.FindProperty("definition");
            RevGalNode node = Selected();
            if (node == null)
            {
                _inspector.Add(Header("剧情设置"));
                Property(_inspector, definition.FindPropertyRelative("Title"), "剧情名称");
                Property(_inspector, definition.FindPropertyRelative("Id"), "剧情 ID");
                Property(_inspector, definition.FindPropertyRelative("Revision"), "存档兼容修订");
                TargetPicker(_inspector, "起点", _story.Definition.Start, value => Change("设置剧情起点", () => _story.Definition.Start = value));
                Property(_inspector, definition.FindPropertyRelative("Variables"), "变量 / 旗标");
                return;
            }

            var fields = definition.FindPropertyRelative("Nodes").GetArrayElementAtIndex(_story.Definition.Nodes.IndexOf(node));
            _inspector.Add(Header(RevGalEditorLabels.Kind(node.Kind)));
            _inspector.Add(new Label(node.Id) { tooltip = "节点 ID 用于连线、已读以及存档定位" });
            _inspector.Add(new Button(() => Change("设为起点", () => _story.Definition.Start = node.Id)) { text = "设为剧情起点" });
            _inspector.Add(new Button(() => Preview(node.Id)) { text = "从此节点试读" });
            _inspector.Add(new Button(DuplicateSelected) { text = "复制节点" });
            Property(_inspector, fields.FindPropertyRelative("Title"), "节点名称");

            if (node.Kind == RevGalNodeKind.Line || node.Kind == RevGalNodeKind.Choice || node.Kind == RevGalNodeKind.End)
            {
                Property(_inspector, fields.FindPropertyRelative("Speaker"), "说话人 · 留空为旁白");
                Property(_inspector, fields.FindPropertyRelative("Text"), "正文");
                var localized = new Foldout { text = "本地化键", value = false };
                Property(localized, fields.FindPropertyRelative("SpeakerKey"), "说话人键");
                Property(localized, fields.FindPropertyRelative("TextKey"), "正文键");
                _inspector.Add(localized);
            }
            if (node.Kind == RevGalNodeKind.Signal)
            {
                Property(_inspector, fields.FindPropertyRelative("Signal"), "信号名");
                Property(_inspector, fields.FindPropertyRelative("Argument"), "传给宿主的参数");
            }
            if (node.Kind != RevGalNodeKind.End)
            {
                TargetPicker(_inspector, node.Kind == RevGalNodeKind.Choice || node.Kind == RevGalNodeKind.Branch ? "保底出口" : "下一节点",
                    node.Next, value => Change("设置下一节点", () => node.Next = value));
            }
            if (node.Kind == RevGalNodeKind.Choice || node.Kind == RevGalNodeKind.Branch)
            {
                BuildExits(node, fields.FindPropertyRelative("Exits"));
            }
            var stage = new Foldout { text = "舞台、音频与收藏", value = false };
            stage.Add(new HelpBox("舞台留空时沿用上次内容，填单个减号时清空。选资源会自动填逻辑路径。", HelpBoxMessageType.Info));
            var stageFields = fields.FindPropertyRelative("Stage");
            foreach (var track in new[]
            {
                (name: "Background", label: "背景"), (name: "Left", label: "左侧立绘"),
                (name: "Center", label: "中间立绘"), (name: "Right", label: "右侧立绘")
            })
            {
                Resource(stage, stageFields.FindPropertyRelative(track.name), track.label, typeof(Sprite));
            }
            Resource(stage, stageFields.FindPropertyRelative("Music"), "背景音乐", typeof(AudioClip));
            Resource(stage, fields.FindPropertyRelative("Voice"), "本句语音", typeof(AudioClip));
            Resource(stage, fields.FindPropertyRelative("Sound"), "本节点音效", typeof(AudioClip));
            Property(stage, fields.FindPropertyRelative("GalleryId"), "插画收藏 ID");
            _inspector.Add(stage);
            var effects = new Foldout { text = "进入节点时修改变量", value = false };
            Property(effects, fields.FindPropertyRelative("Effects"), "变量修改");
            _inspector.Add(effects);
        }

        private void BuildExits(RevGalNode node, SerializedProperty fields)
        {
            _inspector.Add(Header(node.Kind == RevGalNodeKind.Choice ? "玩家选项" : "按顺序匹配的条件出口"));
            for (int index = 0; index < (node.Exits?.Count ?? 0); index++)
            {
                var exit = node.Exits[index];
                if (exit == null)
                {
                    continue;
                }
                int captured = index;
                var group = new Foldout { text = string.IsNullOrEmpty(exit.Label) ? "出口 " + (index + 1) : exit.Label, value = true };
                var field = fields.GetArrayElementAtIndex(index);
                Property(group, field.FindPropertyRelative("Label"), node.Kind == RevGalNodeKind.Choice ? "按钮文字" : "出口说明");
                TargetPicker(group, "目标", exit.Target, value => Change("设置出口目标", () => exit.Target = value));
                var advanced = new Foldout { text = "条件、效果与文案键", value = false };
                Property(advanced, field.FindPropertyRelative("Conditions"), "同时满足的条件");
                Property(advanced, field.FindPropertyRelative("Effects"), "选择后修改变量");
                if (node.Kind == RevGalNodeKind.Choice)
                {
                    Property(advanced, field.FindPropertyRelative("TextKey"), "选项文案键");
                    Property(advanced, field.FindPropertyRelative("ShowWhenLocked"), "未满足时仍显示");
                }
                group.Add(advanced);
                var actions = new VisualElement();
                actions.style.flexDirection = FlexDirection.Row;
                var up = new Button(() => Change("调整出口顺序", () =>
                {
                    node.Exits.RemoveAt(captured);
                    node.Exits.Insert(captured - 1, exit);
                })) { text = "上移" };
                up.SetEnabled(index > 0);
                actions.Add(up);
                actions.Add(new Button(() => Change("删除出口", () => node.Exits.RemoveAt(captured))) { text = "删除出口" });
                group.Add(actions);
                _inspector.Add(group);
            }
            _inspector.Add(new Button(() => Change("添加出口", () =>
            {
                node.Exits ??= new List<RevGalEdge>();
                node.Exits.Add(new RevGalEdge { Id = NewId("exit"), Label = node.Kind == RevGalNodeKind.Choice ? "新选项" : "" });
            })) { text = "添加出口" });
        }

        private void TargetPicker(VisualElement parent, string label, string value, Action<string> change)
        {
            var ids = new List<string> { "" };
            var labels = new List<string> { "未连接" };
            foreach (var node in _story.Definition.Nodes.Where(item => item != null && !string.IsNullOrEmpty(item.Id)))
            {
                ids.Add(node.Id);
                labels.Add(string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title + " [" + node.Id + "]");
            }
            int selected = Math.Max(0, ids.IndexOf(value ?? ""));
            var field = new DropdownField(label, labels, selected);
            field.RegisterValueChangedCallback(e =>
            {
                int index = labels.IndexOf(e.newValue);
                if (index >= 0)
                {
                    change(ids[index]);
                }
            });
            parent.Add(field);
        }

        private void Property(VisualElement parent, SerializedProperty property, string label)
        {
            if (property == null)
            {
                return;
            }
            var field = new PropertyField(property, label);
            field.BindProperty(property);
            field.RegisterValueChangeCallback(_ => QueueRefresh(false));
            parent.Add(field);
        }

        private void Resource(VisualElement parent, SerializedProperty property, string label, Type type)
        {
            if (property == null)
            {
                return;
            }
            Property(parent, property, label);
            var picker = new ObjectField("选资源") { objectType = type, allowSceneObjects = false };
            picker.RegisterValueChangedCallback(change =>
            {
                if (change.newValue == null)
                {
                    return;
                }
                Guard(() =>
                {
                    property.serializedObject.Update();
                    property.stringValue = RevGalAssets.LogicalPath(change.newValue);
                    property.serializedObject.ApplyModifiedProperties();
                    QueueRefresh(false);
                });
                picker.SetValueWithoutNotify(null);
            });
            parent.Add(picker);
        }

        private void BuildIssues()
        {
            _issues.Clear();
            if (_story == null)
            {
                _status.text = "请选择剧情";
                return;
            }
            var issues = RevGalValidation.Validate(_story.Definition);
            _status.text = (EditorUtility.IsDirty(_story) ? "未保存 · " : "已保存 · ") +
                issues.Count(i => !i.Warning) + " 错误 / " + issues.Count(i => i.Warning) + " 提示";
            foreach (var issue in issues)
            {
                var button = new Button(() => Select(issue.Node, true)) { text = (issue.Warning ? "提示 · " : "错误 · ") + issue.Message };
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.tooltip = issue.Node;
                _issues.Add(button);
            }
        }

        private void AddNode(RevGalNodeKind kind, Vector2? position)
        {
            if (_story == null)
            {
                Guard(() => throw new InvalidOperationException("先选择或新建剧情"));
                return;
            }
            var previous = Selected();
            var node = new RevGalNode
            {
                Id = NewId("node"), Kind = kind, Title = RevGalEditorLabels.Kind(kind),
                Position = position ?? (previous?.Position + new Vector2(280, 120) ?? new Vector2(30, 40)),
                Text = kind == RevGalNodeKind.Line ? "在这里写对白。" : ""
            };
            if (kind == RevGalNodeKind.Choice)
            {
                node.Exits.Add(new RevGalEdge { Id = "first", Label = "选项一" });
                node.Exits.Add(new RevGalEdge { Id = "second", Label = "选项二" });
            }
            Change("添加 Gal 节点", () =>
            {
                _story.Definition.Nodes.Add(node);
                if (string.IsNullOrEmpty(_story.Definition.Start))
                {
                    _story.Definition.Start = node.Id;
                }
                if (!position.HasValue && previous != null && previous.Kind == RevGalNodeKind.Line && string.IsNullOrEmpty(previous.Next))
                {
                    previous.Next = node.Id;
                }
                _selected = node.Id;
            });
        }

        private void DuplicateSelected()
        {
            var source = Selected();
            if (source == null)
            {
                return;
            }
            var copy = JsonUtility.FromJson<RevGalNode>(JsonUtility.ToJson(source));
            copy.Id = NewId("node");
            copy.Position += new Vector2(32, 54);
            Change("复制 Gal 节点", () =>
            {
                _story.Definition.Nodes.Add(copy);
                _selected = copy.Id;
            });
        }

        private void Arrange()
        {
            RequireStory();
            var nodes = _story.Definition.Nodes.Where(node => node != null && !string.IsNullOrEmpty(node.Id))
                .GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            if (nodes.Count == 0)
            {
                return;
            }
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            string start = _story.Definition.Start;
            if (!string.IsNullOrEmpty(start) && nodes.ContainsKey(start))
            {
                ranks.Add(start, 0);
                queue.Enqueue(start);
            }
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                var node = nodes[id];
                var targets = new[] { node.Next }.Concat((node.Exits ?? new List<RevGalEdge>()).Select(exit => exit?.Target));
                foreach (string target in targets)
                {
                    if (!string.IsNullOrEmpty(target) && nodes.ContainsKey(target) && !ranks.ContainsKey(target))
                    {
                        ranks.Add(target, ranks[id] + 1);
                        queue.Enqueue(target);
                    }
                }
            }
            int unusedColumn = ranks.Count == 0 ? 0 : ranks.Values.Max() + 1;
            Change("自动排版 Gal 剧情图", () =>
            {
                foreach (var lane in nodes.Values.GroupBy(node => ranks.TryGetValue(node.Id, out int rank) ? rank : unusedColumn))
                {
                    int next = 0;
                    foreach (var node in lane)
                    {
                        node.Position = new Vector2(32 + lane.Key * 300, 50 + next);
                        next += 148 + Math.Max(0, node.Exits?.Count ?? 0) * 22;
                    }
                }
            });
            _graph.schedule.Execute(() => _graph.FrameAll()).StartingIn(60);
        }

        private RevGalNode Selected()
        {
            return _story?.Definition?.Nodes?.FirstOrDefault(x => x != null && x.Id == _selected);
        }

        private void Change(string undo, Action mutation)
        {
            Undo.RecordObject(_story, undo);
            mutation();
            EditorUtility.SetDirty(_story);
            Refresh(true);
        }

        private void NewStory()
        {
            string path = EditorUtility.SaveFilePanelInProject("新建 Gal 剧情", "GalStory", "asset", "请选择剧情 SO 的目录");
            if (!string.IsNullOrEmpty(path))
            {
                string title = Path.GetFileNameWithoutExtension(path);
                SetStory(RevGalAssets.Write(path, RevGalAssets.Template(RevGalValidation.IsId(title) ? title : NewId("story"), title), false));
            }
        }

        private void Save()
        {
            RequireStory();
            AssetDatabase.SaveAssetIfDirty(_story);
            BuildIssues();
        }

        private void Export()
        {
            RequireStory();
            string path = EditorUtility.SaveFilePanel("导出 Gal 剧情", "", _story.name, "json");
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, RevGalJson.Export(_story.Definition), new System.Text.UTF8Encoding(false));
            }
        }

        private void Import()
        {
            string source = EditorUtility.OpenFilePanel("导入 Gal 剧情", "", "json");
            if (string.IsNullOrEmpty(source))
            {
                return;
            }
            var definition = RevGalJson.Import(File.ReadAllText(source));
            bool replace = _story != null;
            if (replace && !EditorUtility.DisplayDialog("替换剧情内容", "把 JSON 导入到 " + _story.name + "，保留资产 GUID？", "导入", "取消"))
            {
                return;
            }
            string destination = replace ? AssetDatabase.GetAssetPath(_story)
                : EditorUtility.SaveFilePanelInProject("保存导入的剧情", "GalStory", "asset", "请选择剧情 SO 的目录");
            if (!string.IsNullOrEmpty(destination))
            {
                SetStory(RevGalAssets.Write(destination, definition, replace));
            }
        }

        private void Preview(string entry)
        {
            RequireStory();
            RevGalPreviewWindow.Open(_story, entry);
        }

        private void CreatePanel()
        {
            var created = RevGalPanelBuilder.Create(null, null, false, null);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<RevGalPanelTheme>(created.ThemePath);
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        /// <summary>选择当前默认面板引用的共享配置，直接在 Inspector 中编辑。</summary>
        private static void EditPanelTheme()
        {
            var theme = RevGalPanelBuilder.FindTheme();
            if (theme == null)
            {
                throw new InvalidOperationException("请先生成绑定了主题的 Gal 游戏面板");
            }
            Selection.activeObject = theme;
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        private void RequireStory()
        {
            if (_story == null)
            {
                throw new InvalidOperationException("请先选择一个剧情 SO");
            }
        }

        private void ToolbarAction(VisualElement toolbar, string name, Action action)
        {
            toolbar.Add(new ToolbarButton(() => Guard(action)) { text = name });
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                ShowNotification(new GUIContent(error.Message));
                _status.text = error.Message;
            }
        }

        private static Label Header(string name)
        {
            var title = new Label(name);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 5;
            title.style.marginBottom = 5;
            return title;
        }

        private static string NewId(string prefix)
        {
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }

    [CustomEditor(typeof(RevGalStory))]
    internal sealed class RevGalStoryInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (GUILayout.Button("打开 Gal 剧情工作台"))
            {
                RevGalWorkbench.Open((RevGalStory)target);
            }
            DrawDefaultInspector();
        }
    }
}

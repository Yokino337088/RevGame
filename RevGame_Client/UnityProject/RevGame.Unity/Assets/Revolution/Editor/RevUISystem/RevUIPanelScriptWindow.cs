// ============================================================
// RevUIPanelScriptWindow.cs —— 按预制体生成 RevUI 面板脚本（只读预制体、绝不覆盖业务代码）
//
// 操作：选位于 AB 资源根目录内的面板预制体 → 勾选需要的控件和事件 →
//       选 Assets 下的脚本目录 → 预览 → 生成唯一的 .cs 文件 → 编译后自行挂到预制体根节点。
// 注意：事件按节点【名字】分发，不能用路径消歧；绑定字段才使用精确路径。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution.Editor
{
    public sealed class RevUIPanelScriptWindow : EditorWindow
    {
        private GameObject _prefab;
        private DefaultAsset _outputFolder;
        private string _className = "";
        private string _namespace = "";
        private RevUILayer _layer = RevUILayer.Normal;
        private Vector2 _scroll;
        private Vector2 _codeScroll;

        private const string PrefNamespace = "Revolution.RevUIGen.Namespace";
        private const string PrefLayer = "Revolution.RevUIGen.Layer";
        private const string PrefAttributes = "Revolution.RevUIGen.UseAttributes";
        private const string PrefFolder = "Revolution.RevUIGen.Folder";
        private const string PrefPrefab = "Revolution.RevUIGen.PrefabPath";
        private bool _showCode;
        private string _message;
        private MessageType _messageType;
        private readonly List<PanelControl> _controls = new List<PanelControl>();

        [MenuItem("Revolution.Tools/UI/面板代码生成器", false, 30)]
        public static void Open()
        {
            var window = GetWindow<RevUIPanelScriptWindow>("UI 面板代码生成");
            window.minSize = new Vector2(620, 580);
            window.Show();
        }

        private void OnEnable()
        {
            // 记住上次的输出目录 / 命名空间 / 监听方式：新手连续生成多个面板时不用反复填同样的值。
            string savedFolder = EditorPrefs.GetString(PrefFolder, "Assets");
            _outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(savedFolder)
                            ?? AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets");
            _namespace = EditorPrefs.GetString(PrefNamespace, "");
            _layer = (RevUILayer)EditorPrefs.GetInt(PrefLayer, (int)RevUILayer.Normal);
            _useAttributes = EditorPrefs.GetBool(PrefAttributes, true);

            // 恢复上次所选的预制体并重扫控件：脚本重编译（Domain Reload）会清空窗口状态，
            // 不恢复的话用户在旧窗口上直接点生成，会得到一份没有任何绑定和事件的空壳。
            string savedPrefab = EditorPrefs.GetString(PrefPrefab, "");
            if (!string.IsNullOrEmpty(savedPrefab))
            {
                _prefab = AssetDatabase.LoadAssetAtPath<GameObject>(savedPrefab);
                if (_prefab != null)
                {
                    if (string.IsNullOrEmpty(_className))
                        _className = _prefab.name.EndsWith("Panel", StringComparison.Ordinal) ? _prefab.name : _prefab.name + "Panel";
                    ScanPrefab();
                }
            }
        }

        private void OnDisable() => SavePrefs();

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefNamespace, _namespace);
            EditorPrefs.SetInt(PrefLayer, (int)_layer);
            EditorPrefs.SetBool(PrefAttributes, _useAttributes);
            string folder = OutputFolder();
            if (folder != null) EditorPrefs.SetString(PrefFolder, folder);
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("从预制体生成面板代码", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("选资源 · 选控件 · 选监听方式 · 预览并生成。默认使用事件特性，不修改预制体。", EditorStyles.wordWrappedMiniLabel);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSource();
            EditorGUILayout.Space(8);
            DrawOutput();
            EditorGUILayout.Space(8);
            DrawControls();
            EditorGUILayout.Space(8);
            DrawPreview();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSource()
        {
            GUILayout.Label("01  面板来源", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var picked = (GameObject)EditorGUILayout.ObjectField("面板预制体", _prefab, typeof(GameObject), false);
                if (picked != _prefab)
                {
                    _prefab = picked;
                    _className = picked == null ? "" : (picked.name.EndsWith("Panel", StringComparison.Ordinal) ? picked.name : picked.name + "Panel");
                    ScanPrefab();
                }
                if (_prefab == null)
                {
                    EditorGUILayout.HelpBox("在 Project 中将预制体拖到这里。请先在 RevAB 打包工具里设置资源根目录。", MessageType.Info);
                    return;
                }
                if (GUILayout.Button("预制体层级已修改？重新扫描控件", GUILayout.Width(220))) ScanPrefab();
                string prefabPath = AssetDatabase.GetAssetPath(_prefab);
                string root = ResourceRoot(prefabPath);
                EditorGUILayout.LabelField("资源位置", prefabPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("生成的[RevUIPanel]根路径", root.Length == 0 ? "（无法推导：需位于资源根目录的子文件夹）" : root, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("预制体资源名", Path.GetFileNameWithoutExtension(prefabPath), EditorStyles.miniLabel);
                if (root.Length == 0)
                    EditorGUILayout.HelpBox("所选预制体必须位于打包工具配置的资源根目录下的子文件夹中，才能得到可用的逻辑路径。", MessageType.Error);
            }
        }

        private void DrawOutput()
        {
            GUILayout.Label("02  脚本设置", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _className = EditorGUILayout.TextField(new GUIContent("面板类名", "需是合法 C# 标识符；不会重命名预制体"), _className);
                _namespace = EditorGUILayout.TextField("命名空间", _namespace);
                _layer = (RevUILayer)EditorGUILayout.EnumPopup("UI 层级", _layer);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("脚本输出文件夹", _outputFolder, typeof(DefaultAsset), false);
                    if (GUILayout.Button("选择…", GUILayout.Width(62)))
                    {
                        string picked = EditorUtility.OpenFolderPanel("选择脚本输出目录", Application.dataPath, "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            string folder = ToAssetFolder(picked);
                            if (folder == null) SetMessage("请选择当前工程 Assets 目录下、且不位于 Editor 文件夹中的目录。", MessageType.Error);
                            else _outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder);
                        }
                    }
                }
                EditorGUILayout.LabelField("监听写法", EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox("推荐：方法特性（[RevButtonClick] 等）最简洁；重写钩子（OnClick 等）适合高频输入/滚动或集中分发。\n同一控件的同一事件只生成一种写法，不会重复挂 AddListener。", MessageType.None);
                _useAttributes = GUILayout.Toolbar(_useAttributes ? 0 : 1, new[] { "方法特性（推荐）", "重写钩子函数" }) == 0;
            }
        }

        private bool _useAttributes = true;

        private void DrawControls()
        {
            GUILayout.Label($"03  控件与监听  ·  {_controls.Count} 个可用控件", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_prefab == null) return;
                EditorGUILayout.LabelField("绑定字段使用精确层级路径；事件使用节点名（同名交互节点必须先改名）。", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("勾选全部事件", EditorStyles.miniButton, GUILayout.Width(100)))
                        foreach (PanelControl c in _controls) foreach (PanelEvent e in c.Events) e.Enabled = true;
                    if (GUILayout.Button("清空全部事件", EditorStyles.miniButton, GUILayout.Width(100)))
                        foreach (PanelControl c in _controls) foreach (PanelEvent e in c.Events) e.Enabled = false;
                    GUILayout.Label("仅勾选确实要处理的控件，事件方法会生成空方法体待填。", EditorStyles.miniLabel);
                }
                if (_controls.Count == 0)
                    EditorGUILayout.HelpBox("没有识别到 UGUI 控件。仅生成空的面板骨架；TMP 输入框等自定义交互需手动注册事件。", MessageType.Info);
                foreach (PanelControl control in _controls)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        control.Bind = GUILayout.Toggle(control.Bind, "绑定", GUILayout.Width(52));
                        GUILayout.Label(control.Path + "  [" + control.ComponentName + "]", EditorStyles.miniLabel, GUILayout.MinWidth(180), GUILayout.ExpandWidth(true));
                        foreach (PanelEvent evt in control.Events)
                            evt.Enabled = GUILayout.Toggle(evt.Enabled, evt.Label, GUILayout.Width(evt.Label.Length > 4 ? 70 : 54));
                    }
                }
            }
        }

        private void DrawPreview()
        {
            GUILayout.Label("04  检查并生成", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string output = OutputFolder();
                string dest = output == null || string.IsNullOrWhiteSpace(_className) ? "（先选择有效输出目录与类名）" : output + "/" + _className + ".cs";
                EditorGUILayout.LabelField("目标文件", dest, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(
                    $"已选：{_controls.Count(c => c.Bind)} 个绑定字段 · {_controls.Count(c => c.Events.Any(e => e.Enabled))} 个事件控件" +
                    "（两者都没有时无法生成）", EditorStyles.miniLabel);
                if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, _messageType);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("预览代码", GUILayout.Height(30), GUILayout.Width(110)))
                    {
                        _showCode = true;
                        if (TryGenerate(out _, out string problem)) SetMessage("预览已更新。", MessageType.Info);
                        else SetMessage(problem, MessageType.Error);
                    }
                    if (GUILayout.Button("生成面板脚本", GUILayout.Height(30), GUILayout.Width(130))) Generate();
                }
                if (!_showCode) return;
                if (TryGenerate(out string source, out string error))
                {
                    _codeScroll = EditorGUILayout.BeginScrollView(_codeScroll, GUILayout.Height(200));
                    EditorGUILayout.TextArea(source, EditorStyles.textArea, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                }
                else EditorGUILayout.HelpBox(error, MessageType.Warning);
            }
        }

        /// <summary>工具生成骨架的头部标记：带它才是"可以放心覆盖"的文件。</summary>
        private const string SkeletonMarker = "面板代码骨架：由 Revolution.Tools/UI/面板代码生成器生成";

        private void Generate()
        {
            if (!TryGenerate(out string source, out string error)) { SetMessage(error, MessageType.Error); return; }
            string destination = OutputFolder() + "/" + _className + ".cs";

            // 已存在时的策略：
            //   · 本工具生成的骨架 → 弹窗确认后覆盖（改了预制体层级后"重新生成"是常规操作）；
            //   · 其它文件 → 一律不动（里面可能有手写业务逻辑，覆盖就是事故）。
            if (File.Exists(destination))
            {
                string firstLine = "";
                try { using var reader = new StreamReader(destination); firstLine = reader.ReadLine() ?? ""; }
                catch (IOException) { }

                if (!firstLine.Contains(SkeletonMarker))
                {
                    SetMessage("目标文件已存在，且不是本工具生成的骨架（可能包含手写逻辑），为保护代码未覆盖：" + destination +
                               "。如需更新控件，请复制预览中的字段/事件方法手动合入。", MessageType.Warning);
                    return;
                }

                if (!EditorUtility.DisplayDialog("覆盖已生成的骨架？",
                        destination + "\n\n这个文件是本工具之前生成的骨架，将被按当前勾选重新生成；" +
                        "你之前在骨架里手改过的内容会丢失。",
                        "覆盖", "取消"))
                { SetMessage("已取消覆盖。", MessageType.Info); return; }
            }
            try
            {
                File.WriteAllText(destination, source, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(destination);
                SetMessage("生成成功：" + destination + "。等待 Unity 编译后，将该脚本挂到预制体【根节点】；字段和事件在首次打开时由框架自动装配。", MessageType.Info);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<MonoScript>(destination));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetMessage("写入失败：" + ex.Message, MessageType.Error);
            }
        }

        private bool TryGenerate(out string source, out string error)
        {
            source = null;
            SavePrefs();
            if (_prefab == null || !PrefabUtility.IsPartOfPrefabAsset(_prefab)) { error = "请选择 Project 视图中的预制体资产，不能使用场景对象。"; return false; }
            if (_prefab.GetComponent<RectTransform>() == null) { error = "面板预制体根节点需要 RectTransform。"; return false; }
            if (_prefab.GetComponent<RevUIPanel>() != null) { error = "预制体根节点已有 RevUIPanel 脚本。请编辑现有面板，不要再生成第二个。"; return false; }
            string prefabPath = AssetDatabase.GetAssetPath(_prefab);
            string root = ResourceRoot(prefabPath);
            if (root.Length == 0) { error = "预制体必须位于打包配置中的资源根目录下的子文件夹，例如 Assets/GameRes/UI/BagPanel.prefab。"; return false; }
            if (!ValidIdentifier(_className) || !ValidNamespace(_namespace)) { error = "类名或命名空间不是合法 C# 标识符（不能包含空格、连字符或 C# 关键字）。"; return false; }
            string folder = OutputFolder();
            if (folder == null) { error = "脚本输出目录必须是当前工程 Assets 下的普通文件夹，且不能位于 Editor 目录或只读 Package 中。"; return false; }
            string fullTypeName = string.IsNullOrEmpty(_namespace) ? _className : _namespace + "." + _className;
            if (TypeCache.GetTypesDerivedFrom<RevUIPanel>().Any(t => t.FullName == fullTypeName))
            { error = "工程里已有同名面板类型：" + fullTypeName + "。请改类名或命名空间。"; return false; }

            var chosen = _controls.Where(c => c.Bind || c.Events.Any(e => e.Enabled)).ToList();
            // ★ 空勾选会生成一份"没有任何字段和监听"的空壳，而且编译通过、极难发现 —— 必须在这里拦下。
            if (chosen.Count == 0)
            { error = "没有勾选任何控件绑定或事件 —— 请在第 03 区至少勾选一项，或点「重新扫描控件」刷新列表。"; return false; }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PanelControl c in chosen)
            {
                // Transform.Find("A/B") 遇到同级同名只能找第一个；按名字分发同名控件会双触发。
                if (!paths.Add(c.Path)) { error = "重复控件路径：" + c.Path + "。请先给同级同名节点改名。"; return false; }
                if (c.Events.Any(e => e.Enabled))
                {
                    if (c.Name.Contains("/") || c.Name.Contains("\\"))
                    { error = "事件节点名不能包含路径分隔符：" + c.Name; return false; }
                    if (!eventNames.Add(c.Name) || _controls.Any(other => other != c && other.Name == c.Name &&
                        other.Events.Any(e => c.Events.Any(selectedEvent => selectedEvent.Enabled && selectedEvent.Hook == e.Hook))))
                    { error = "同名交互控件：" + c.Name + "。事件按节点名分发、路径无法消歧，请先重命名。"; return false; }
                }
                if (c.Path.Split('/').Any(p => p.Length == 0 || p == "." || p == "..") ||
                    (c.Bind && _prefab.transform.Find(c.Path) != c.Node))
                { error = "节点路径不唯一或无法用于绑定：" + c.Path + "。请先给同级同名节点改名。"; return false; }
            }
            source = RevUIPanelScriptBuilder.Build(_className, _namespace, _layer, root,
                Path.GetFileNameWithoutExtension(prefabPath), chosen, _useAttributes);
            error = null;
            return true;
        }

        private void ScanPrefab()
        {
            _controls.Clear();
            _showCode = false;
            _message = null;
            if (_prefab == null || !PrefabUtility.IsPartOfPrefabAsset(_prefab)) return;
            Transform root = _prefab.transform;
            // 内置 UGUI 类型在生成的代码里用短名（配合 using UnityEngine.UI）；其它（如 TMP）保留全名兜底。
            string TypeNameOf(Component component)
            {
                var type = component.GetType();
                return type.Namespace == "UnityEngine.UI" ? type.Name : "global::" + type.FullName.Replace('+', '.');
            }
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                string path = RelativePath(t, root);
                // 同一节点优先交互控件，避免 Button 的 Image 再生成一个重复绑定；
                // 图像/文本这类展示控件默认不勾选，可按需开启。
                Component component = t.GetComponent<Button>();
                if (component == null) component = t.GetComponent<Toggle>();
                if (component == null) component = t.GetComponent<Slider>();
                if (component == null) component = t.GetComponent<InputField>();
                if (component == null) component = t.GetComponent<Dropdown>();
                if (component == null) component = t.GetComponent<ScrollRect>();
                if (component == null) component = t.GetComponent<Text>();
                if (component == null) component = t.GetComponent<Image>();
                if (component == null)
                    component = t.GetComponents<Component>().FirstOrDefault(c => c != null &&
                        c.GetType().FullName == "TMPro.TextMeshProUGUI");
                if (component == null) continue;
                var row = new PanelControl { Path = path, Name = t.name, Node = t, ComponentName = component.GetType().Name,
                    TypeName = TypeNameOf(component), Bind = component is Selectable || component is ScrollRect };
                if (component is Button)
                {
                    row.Events.Add(new PanelEvent("点击", "RevButtonClick", "Click", "", true));
                    row.Events.Add(new PanelEvent("长按", "RevButtonLongPress", "LongPress", "", false));
                    row.Events.Add(new PanelEvent("松开", "RevButtonLoosen", "Loosen", "", false));
                }
                else if (component is Toggle) row.Events.Add(new PanelEvent("变化", "RevToggleChanged", "ToggleChanged", "bool value", true));
                else if (component is Slider) row.Events.Add(new PanelEvent("变化", "RevSliderChanged", "SliderChanged", "float value", true));
                else if (component is InputField)
                {
                    row.Events.Add(new PanelEvent("输入中", "RevInputChanged", "InputChanged", "string value", false));
                    row.Events.Add(new PanelEvent("结束编辑", "RevInputEndEdit", "InputEndEdit", "string value", true));
                }
                else if (component is Dropdown) row.Events.Add(new PanelEvent("变化", "RevDropdownChanged", "DropdownChanged", "int index", true));
                else if (component is ScrollRect) row.Events.Add(new PanelEvent("滚动", "RevScrollChanged", "ScrollChanged", "float x, float y", false));
                _controls.Add(row);
            }

            // 记住所选预制体，配合 OnEnable 的恢复逻辑（清空选择时一并清掉偏好）。
            if (_prefab != null) EditorPrefs.SetString(PrefPrefab, AssetDatabase.GetAssetPath(_prefab));
            else EditorPrefs.DeleteKey(PrefPrefab);
        }

        private static string RelativePath(Transform node, Transform root)
        {
            var parts = new List<string>();
            for (Transform current = node; current != null && current != root; current = current.parent) parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string ResourceRoot(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return "";
            string basePath = ABBuildConfig.Instance.GetResRoot().TrimEnd('/');
            string dir = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            if (string.IsNullOrEmpty(basePath) || !dir.StartsWith(basePath + "/", StringComparison.Ordinal)) return "";
            return dir.Substring(basePath.Length + 1);
        }

        private string OutputFolder()
        {
            string path = _outputFolder == null ? "" : AssetDatabase.GetAssetPath(_outputFolder);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path) ||
                (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))) return null;
            if (path.Split('/').Any(part => part.Equals("Editor", StringComparison.OrdinalIgnoreCase))) return null;
            return path;
        }

        private static string ToAssetFolder(string absolute)
        {
            string assets = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            string path = absolute.Replace('\\', '/').TrimEnd('/');
            if (string.Equals(path, assets, StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!path.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) return null;
            string result = "Assets" + path.Substring(assets.Length);
            return AssetDatabase.IsValidFolder(result) ? result : null;
        }

        internal static bool ValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) return false;
            return !Keywords.Contains(name);
        }

        private static bool ValidNamespace(string name) => string.IsNullOrEmpty(name) || name.Split('.').All(ValidIdentifier);
        private static readonly HashSet<string> Keywords = new HashSet<string>(new[] {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while" }, StringComparer.Ordinal);

        private void SetMessage(string text, MessageType type) { _message = text; _messageType = type; Repaint(); }
    }

    internal sealed class PanelControl
    {
        public string Path;
        public string Name;
        public Transform Node;
        public string ComponentName;
        public string TypeName;
        public bool Bind;
        public readonly List<PanelEvent> Events = new List<PanelEvent>();
    }

    internal sealed class PanelEvent
    {
        public readonly string Label, Attribute, Hook, Parameters;
        public bool Enabled;
        public PanelEvent(string label, string attribute, string hook, string parameters, bool enabled)
        { Label = label; Attribute = attribute; Hook = hook; Parameters = parameters; Enabled = enabled; }
    }

    internal static class RevUIPanelScriptBuilder
    {
        internal static string Build(string name, string ns, RevUILayer layer, string root, string prefabName,
            List<PanelControl> controls, bool attributes)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("// 面板代码骨架：由 Revolution.Tools/UI/面板代码生成器生成。再次生成时会确认覆盖；手写逻辑请放在别的文件或生成后及时修改。");
            sb.AppendLine("using Revolution;");
            // 有内置 UGUI 绑定字段时补上对应 using，让生成的字段类型用短名、可读性更好。
            if (controls.Any(c => c.Bind && !c.TypeName.StartsWith("global::", StringComparison.Ordinal)))
            {
                sb.AppendLine("using UnityEngine;");
                sb.AppendLine("using UnityEngine.UI;");
            }
            if (!string.IsNullOrEmpty(ns)) { sb.Append("namespace ").Append(ns).AppendLine("\n{"); }
            string indent = string.IsNullOrEmpty(ns) ? "" : "    ";
            sb.Append(indent).Append("[RevUIPanel(\"").Append(Escape(root)).Append("\", RevUILayer.").Append(layer)
                .Append(", \"").Append(Escape(prefabName)).AppendLine("\")]");
            sb.Append(indent).Append("public sealed class ").Append(name).AppendLine(" : RevUIPanel");
            sb.Append(indent).AppendLine("{");
            string tab = indent + "    ";
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PanelControl control in controls.Where(c => c.Bind))
            {
                string stem = SafeName(control.Name);
                string field = "_" + char.ToLowerInvariant(stem[0]) + stem.Substring(1);
                string unique = field;
                int suffix = 2;
                while (!fieldNames.Add(unique)) unique = field + suffix++;
                sb.Append(tab).Append("[RevBind(\"").Append(Escape(control.Path)).Append("\")] private ")
                    .Append(control.TypeName).Append(' ').Append(unique).AppendLine(";");
            }
            if (fieldNames.Count > 0) sb.AppendLine();
            sb.Append(tab).AppendLine("protected override void OnBindView()");
            sb.Append(tab).AppendLine("{");
            sb.Append(tab).AppendLine("    // 这里的 [RevBind] 字段已赋值；只做首次装配，不在此处重复订阅自动事件。");
            sb.Append(tab).AppendLine("}");
            sb.AppendLine();
            sb.Append(tab).AppendLine("protected override void OnOpen()");
            sb.Append(tab).AppendLine("{");
            sb.Append(tab).AppendLine("    // 每次打开面板都会调用（包括从池里复用）。");
            sb.Append(tab).AppendLine("}");
            sb.AppendLine();
            sb.Append(tab).AppendLine("protected override void OnRefreshView()");
            sb.Append(tab).AppendLine("{");
            sb.Append(tab).AppendLine("    // 在这里把数据绘制到已绑定的控件上。");
            sb.Append(tab).AppendLine("}");
            var selected = controls.SelectMany(c => c.Events.Where(e => e.Enabled).Select(e => (control: c, evt: e))).ToList();
            if (attributes)
            {
                var methods = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in selected)
                {
                    sb.AppendLine();
                    string stem = SafeName(pair.control.Name);
                    string method = "On" + char.ToUpperInvariant(stem[0]) + stem.Substring(1) + pair.evt.Hook;
                    string unique = method; int suffix = 2;
                    while (!methods.Add(unique)) unique = method + suffix++;
                    sb.Append(tab).Append('[').Append(pair.evt.Attribute).Append("(\"").Append(Escape(pair.control.Name)).AppendLine("\")]");
                    sb.Append(tab).Append("private void ").Append(unique).Append('(').Append(pair.evt.Parameters).AppendLine(")");
                    sb.Append(tab).AppendLine("{");
                    sb.Append(tab).AppendLine("    // 在此处理该控件事件。");
                    sb.Append(tab).AppendLine("}");
                }
            }
            else
            {
                // 框架已修复：只有长按/松开（特性或重写钩子）也会自动安装按压继电器，无需任何绕行代码。
                foreach (var group in selected.GroupBy(p => p.evt.Hook))
                {
                    PanelEvent evt = group.First().evt;
                    sb.AppendLine();
                    string signature = evt.Hook == "Click" || evt.Hook == "LongPress" || evt.Hook == "Loosen"
                        ? "string nodeName" : "string nodeName, " + evt.Parameters;
                    sb.Append(tab).Append("protected override void On").Append(evt.Hook).Append('(').Append(signature).AppendLine(")");
                    sb.Append(tab).AppendLine("{");
                    sb.Append(tab).AppendLine("    switch (nodeName)");
                    sb.Append(tab).AppendLine("    {");
                    foreach (var pair in group)
                    {
                        sb.Append(tab).Append("        case \"").Append(Escape(pair.control.Name)).AppendLine("\":");
                        sb.Append(tab).AppendLine("            // 在此处理该控件事件。");
                        sb.Append(tab).AppendLine("            break;");
                    }
                    sb.Append(tab).AppendLine("    }");
                    sb.Append(tab).AppendLine("}");
                }
            }
            sb.Append(indent).AppendLine("}");
            if (!string.IsNullOrEmpty(ns)) sb.AppendLine("}");
            return sb.ToString();
        }

        private static string SafeName(string text)
        {
            string result = Regex.Replace(text, @"[^A-Za-z0-9_]", "_");
            if (result.Length == 0 || char.IsDigit(result[0])) result = "Control_" + result;
            return result;
        }

        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}

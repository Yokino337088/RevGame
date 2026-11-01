// RevMCPWindow —— 面向新手的连接控制台与实时活动日志。
// 默认先看 Agent 在做什么；Python/端口/凭据等接线细节集中在“连接设置”。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public sealed partial class RevMCPWindow : EditorWindow
    {
        [SerializeField] private int _port = 18766;
        [SerializeField] private int _page;
        [SerializeField] private string _script, _python = "python";
        [SerializeField] private bool _advanced;
        [NonSerialized] private string _message;
        [NonSerialized] private bool _messageError;
        [NonSerialized] private Vector2 _settingsScroll;
        [NonSerialized] private double _refresh;
        [NonSerialized] private int _editorState = -1;
        [NonSerialized] private string _logError;
        [NonSerialized] private bool _clearActivity, _runtimeEndpointExists;
        [NonSerialized] private RevMCPActivityReader _reader;
        [NonSerialized] private GUIStyle _titleStyle, _cardStyle;
        private static string EndpointPath => Path.Combine(RevAutomationBridge.Root, "runtime.json");
        internal static string ActivityPath => Path.Combine(RevAutomationBridge.Root, "activity");

        [MenuItem("Revolution.Tools/AI/RevMCP 控制台", false, 50)]
        public static void Open()
        {
            var window = GetWindow<RevMCPWindow>("RevMCP");
            window.minSize = new Vector2(480, 360);
            window.Show();
        }

        /// <summary>专用自动化入口可切换页签/筛选，浮动预览供截图验收使用；仍只操作当前工程窗口。</summary>
        internal static RevMCPWindow ShowForAutomation(bool floating, int page, string query, int target, int state)
        {
            var window = floating ? CreateInstance<RevMCPWindow>() : GetWindow<RevMCPWindow>("RevMCP");
            window.titleContent = new GUIContent("RevMCP");
            window._page = page; window._query = query ?? ""; window._targetFilter = target; window._stateFilter = state;
            if (floating)
            {
                window.position = new Rect(180, 100, 720, 650);
                window.ShowUtility();
            }
            else { window.Show(); }
            window.Repaint();
            return window;
        }

        private void OnEnable()
        {
            minSize = new Vector2(480, 360);
            _reader = new RevMCPActivityReader(ActivityPath);
            _viewVersion = -1;
            _script = EditorPrefs.GetString(Preference("Script"), _script ?? "");
            _python = EditorPrefs.GetString(Preference("Python"), _python ?? "python");
            ResolveScript();
            EditorApplication.update += UpdateActivity;
        }
        private void OnDisable() { EditorApplication.update -= UpdateActivity; }

        /// <summary>仅窗口打开时每 250ms 刷新一次；不通过 MCP 自我查询，所以不会制造日志循环。</summary>
        private void UpdateActivity()
        {
            if (EditorApplication.timeSinceStartup < _refresh) { return; }
            _refresh = EditorApplication.timeSinceStartup + .25;
            _reader ??= new RevMCPActivityReader(ActivityPath);
            bool cleared = _clearActivity;
            if (cleared) { _reader.ClearView(); _clearActivity = false; }
            bool changed = _reader.Poll() || cleared;
            if (changed && _follow) { _activityScroll.y = float.MaxValue; }
            RefreshActivityRows();
            int state = (EditorApplication.isCompiling ? 1 : 0) | (EditorApplication.isPlaying ? 2 : 0)
                | (EditorApplication.isPaused ? 4 : 0) | (EditorApplication.isUpdating ? 8 : 0);
            string error = (_reader.LastError ?? "") + (RevAutomationActivity.LastError ?? "");
            bool endpointExists = File.Exists(EndpointPath);
            if (changed || state != _editorState || error != _logError || endpointExists != _runtimeEndpointExists) { Repaint(); }
            _editorState = state; _logError = error; _runtimeEndpointExists = endpointExists;
        }

        private static string Preference(string suffix) => "Revolution.RevMCP." + Path.GetFullPath(".") + "." + suffix;
        private void EnsureStyles()
        {
            _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 20 };
            _cardStyle ??= new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(12, 12, 10, 10) };
        }

        private void OnGUI()
        {
            EnsureStyles();
            GUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12);
                GUILayout.Label("RevMCP", _titleStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(EditorApplication.isCompiling ? "● 编译中" : EditorApplication.isPlaying
                    ? EditorApplication.isPaused ? "● 游戏暂停" : "● 游戏运行中" : "● 编辑器就绪", EditorStyles.miniBoldLabel);
                GUILayout.Space(12);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12);
                GUILayout.Label(new GUIContent(Path.GetFileName(Path.GetFullPath(".")), Path.GetFullPath(".")), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("复制 Agent 配置", EditorStyles.miniButton, GUILayout.Width(114))) { CopyConfiguration(false); }
                GUILayout.Space(12);
            }
            GUILayout.Space(8);
            _page = GUILayout.Toolbar(_page, new[] { "活动日志", "连接设置" }, GUILayout.Height(25));
            if (!string.IsNullOrEmpty(_message)) { EditorGUILayout.HelpBox(_message, _messageError ? MessageType.Error : MessageType.Info); }
            if (_page == 0) { DrawActivity(); }
            else { DrawConnection(); }
        }

        private void DrawConnection()
        {
            _settingsScroll = EditorGUILayout.BeginScrollView(_settingsScroll);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                GUILayout.Label("1  把当前工程连接给 Agent", EditorStyles.boldLabel);
                GUILayout.Label("编辑器操作、Play 中的 GM 与 UI 都可走默认连接。复制配置后添加到 AI 客户端即可。", EditorStyles.wordWrappedMiniLabel);
                DrawPath("当前工程", Path.GetFullPath("."));
                DrawPath("MCP 脚本", _script);
                if (GUILayout.Button("复制默认连接配置", GUILayout.Height(28))) { CopyConfiguration(false); }
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                GUILayout.Label("2  在日志里观察它做了什么", EditorStyles.boldLabel);
                GUILayout.Label("发出一条测试请求，再打开“活动日志”。点击记录查看参数、结果、计划步骤与对应工单。", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("打开活动日志", GUILayout.Height(26))) { _page = 0; }
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                _advanced = EditorGUILayout.Foldout(_advanced, "高级连接 · Python / 独立游戏 HTTP", true);
                if (_advanced)
                {
                    GUILayout.Label("只在脚本位置不同，或需要连接独立开发版游戏时设置。", EditorStyles.wordWrappedMiniLabel);
                    string python = EditorGUILayout.TextField("Python 命令或路径", _python);
                    if (python != _python) { _python = python; EditorPrefs.SetString(Preference("Python"), _python); }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string script = EditorGUILayout.TextField("rev_mcp.py 路径", _script);
                        if (script != _script) { SaveScript(script); }
                        if (GUILayout.Button("选择", EditorStyles.miniButton, GUILayout.Width(44)))
                        {
                            string chosen = EditorUtility.OpenFilePanel("选择 rev_mcp.py", "", "py");
                            if (!string.IsNullOrEmpty(chosen)) { SaveScript(chosen); }
                        }
                    }
                    GUILayout.Space(8);
                    bool running = _runtimeEndpointExists;
                    GUILayout.Label("运行时 HTTP：" + (running ? "凭据已生成；实际连接由 Agent 校验" : "未启动"), EditorStyles.miniBoldLabel);
                    _port = EditorGUILayout.IntField("本机端口", _port);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || EditorApplication.isPaused || running))
                        {
                            if (GUILayout.Button("启动服务")) { Try(() => { RevRuntimeAutomation.Start(_port); _message = "运行时服务已启动。"; }); }
                        }
                        // 暂停时也可停止服务，避免旧界面暂停后无法收尾。
                        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || !running))
                        {
                            if (GUILayout.Button("停止服务")) { Try(() => { RevRuntimeAutomation.Stop(); _message = "运行时服务已停止。"; }); }
                        }
                    }
                    if (GUILayout.Button("复制 HTTP 连接配置")) { CopyConfiguration(true); }
                    GUILayout.Label("游戏服务需要 Play；独立开发版 Player 使用 -revAutomation。凭据 token 不显示、不复制到日志。", EditorStyles.wordWrappedMiniLabel);
                    DrawPath("本机凭据文件", EndpointPath);
                }
            }
            GUILayout.Space(8);
            if (GUILayout.Button("打开工单与证据目录", EditorStyles.miniButton)) { OpenDirectory(RevAutomationBridge.Root); }
            GUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawPath(string label, string path)
        {
            GUILayout.Label(label, EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(path) ? "尚未找到，请在高级连接中选择" : path,
                EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(28));
        }
        private void SaveScript(string script) { _script = script; EditorPrefs.SetString(Preference("Script"), script); }
        private void ResolveScript()
        {
            if (!string.IsNullOrEmpty(_script) && File.Exists(_script)) { return; }
            // 框架仓库和游戏工程嵌套深度不同，逐级找明确命名的 MCP 目录，最多六级；不扫描任意 .py。
            DirectoryInfo folder = new DirectoryInfo(Path.GetFullPath("."));
            for (int depth = 0; folder != null && depth < 6; depth++, folder = folder.Parent)
            {
                string candidate = Path.Combine(folder.FullName, "Revolution.RevMCP", "rev_mcp.py");
                if (File.Exists(candidate)) { SaveScript(candidate); return; }
                candidate = Path.Combine(folder.FullName, "Revolution", "Revolution.RevMCP", "rev_mcp.py");
                if (File.Exists(candidate)) { SaveScript(candidate); return; }
            }
        }
        private void Try(Action action)
        {
            _messageError = false;
            try { action(); }
            catch (Exception e) { _message = e.Message; _messageError = true; }
        }
        private void OpenDirectory(string path) { Try(() => { Directory.CreateDirectory(path); EditorUtility.RevealInFinder(path); }); }
        private void CopyConfiguration(bool runtime)
        {
            Try(() =>
            {
                ResolveScript();
                if (string.IsNullOrWhiteSpace(_script) || !File.Exists(_script))
                { _page = 1; _advanced = true; throw new FileNotFoundException("请在高级连接中选择实际 rev_mcp.py 文件，再复制配置。"); }
                if (string.IsNullOrWhiteSpace(_python)) { throw new ArgumentException("请填写 Python 命令或可执行文件路径。"); }
                var args = new List<string> { Path.GetFullPath(_script), "--project", Path.GetFullPath(".") };
                if (runtime) { args.Add("--runtime-endpoint"); args.Add(EndpointPath); }
                args.Add("serve");
                GUIUtility.systemCopyBuffer = RevAutomationJson.Write(new Dictionary<string, object>
                {
                    ["mcpServers"] = new Dictionary<string, object>
                    {
                        ["revolution"] = new Dictionary<string, object>
                        {
                            ["command"] = _python, ["args"] = args,
                            ["env"] = new Dictionary<string, object> { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" }
                        }
                    }
                });
                _message = "配置已复制。添加到 AI 客户端后，让 Agent 先调用 rev_discover 确认当前工程。";
            });
        }
    }
}

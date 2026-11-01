// 活动页：摘要 → 可筛选的实时列表 → 一条记录的参数/结果。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public sealed partial class RevMCPWindow
    {
        [SerializeField] private string _query = "";
        [SerializeField] private int _targetFilter, _stateFilter;
        [SerializeField] private bool _follow = true;
        [NonSerialized] private string _selectedId;
        [NonSerialized] private Vector2 _activityScroll, _detailScroll;
        [NonSerialized] private GUIStyle _rowStyle;
        private static readonly string[] Targets = { "全部来源", "Agent / MCP", "编辑器", "游戏运行时", "GM 指令", "外部工具", "计划步骤" };
        private static readonly string[] States = { "全部状态", "执行中", "成功", "失败", "取消" };
        private static readonly string[] TargetKeys = { "", "mcp", "editor", "game", "gm", "cli", "plan" };
        private static readonly string[] StateKeys = { "", "Running", "Succeeded", "Failed", "Cancelled" };
        private sealed class ActivityRow
        {
            public RevAutomationActivityEntry Entry;
            public string Title, Purpose, Action, Source, Time, GMCommand, Search;
            public bool RuntimeGM;
            public GUIContent Content;
            public float Height, Width;
        }
        [NonSerialized] private readonly Dictionary<string, ActivityRow> _rows = new Dictionary<string, ActivityRow>(StringComparer.Ordinal);
        [NonSerialized] private readonly List<ActivityRow> _orderedRows = new List<ActivityRow>();
        [NonSerialized] private readonly List<ActivityRow> _filteredRows = new List<ActivityRow>();
        [NonSerialized] private readonly List<float> _rowEnds = new List<float>();
        [NonSerialized] private int _viewVersion = -1, _runningCount, _failedCount, _cachedTarget = -1, _cachedState = -1;
        [NonSerialized] private string _cachedQuery;
        [NonSerialized] private bool _filterDirty = true;

        // 字符串与 JSON 解释只在 Update 收到新 entry 时构建。开始/结束替换 entry 自动失效，GUI 不读盘。
        private void RefreshActivityRows()
        {
            if (_reader == null || _viewVersion == _reader.Version) { return; }
            _orderedRows.Clear(); _runningCount = 0; _failedCount = 0;
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (RevAutomationActivityEntry entry in _reader.Entries)
            {
                active.Add(entry.id);
                if (!_rows.TryGetValue(entry.id, out ActivityRow row) || !ReferenceEquals(row.Entry, entry))
                {
                    row = new ActivityRow { Entry = entry, Title = RevMCPActivityDescription.Title(entry), Purpose = RevMCPActivityDescription.Purpose(entry),
                        Action = DisplayAction(entry), Source = SourceLabel(entry), Time = LocalTime(entry.utc) };
                    row.RuntimeGM = RevMCPActivityDescription.TryGetRuntimeGM(entry, out row.GMCommand);
                    row.Search = row.Title + " " + row.Action + " " + entry.argumentsJson + " " + entry.message + " " + entry.plan + " " + entry.intent;
                    string purpose = string.IsNullOrWhiteSpace(entry.intent) ? row.Purpose : "AI 说明的目的：" + entry.intent;
                    string subtitle = row.Time + "  ·  " + row.Source + "  ·  " + StateLabel(entry.state)
                        + (entry.state == "Running" ? "" : $"  ·  {entry.elapsedMs:F0} ms")
                        + (string.IsNullOrEmpty(entry.plan) ? "" : "  ·  " + entry.plan + (entry.phase == "finally" ? " / 清理" : ""));
                    row.Content = new GUIContent(row.Title + "\n" + purpose + "\n" + subtitle, "原始命令：" + entry.action + "\n" + entry.message);
                    _rows[entry.id] = row;
                }
                _orderedRows.Add(row);
                if (entry.state == "Running") { _runningCount++; }
                if (entry.state == "Failed") { _failedCount++; }
            }
            foreach (string id in _rows.Keys.Where(id => !active.Contains(id)).ToArray()) { _rows.Remove(id); }
            _viewVersion = _reader.Version; _filterDirty = true;
        }

        private void RefreshActivityFilter()
        {
            string query = (_query ?? "").Trim();
            if (!_filterDirty && query == _cachedQuery && _targetFilter == _cachedTarget && _stateFilter == _cachedState) { return; }
            _filteredRows.Clear();
            foreach (ActivityRow row in _orderedRows) { if (MatchesFilter(row, query)) { _filteredRows.Add(row); } }
            if (_selectedId != null && !_filteredRows.Any(row => row.Entry.id == _selectedId))
            { _selectedId = null; _detailScroll = Vector2.zero; }
            _cachedQuery = query; _cachedTarget = _targetFilter; _cachedState = _stateFilter; _filterDirty = false;
        }

        private void DrawActivity()
        {
            int entryCount = _orderedRows.Count;
            GUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                GUILayout.Label($"最近 {entryCount} 条", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"执行中 {_runningCount}   失败 {_failedCount}", EditorStyles.miniLabel);
                GUILayout.Space(10);
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _query = GUILayout.TextField(_query, EditorStyles.toolbarSearchField, GUILayout.MinWidth(80));
                _targetFilter = EditorGUILayout.Popup(_targetFilter, Targets, EditorStyles.toolbarPopup, GUILayout.Width(100));
                _stateFilter = EditorGUILayout.Popup(_stateFilter, States, EditorStyles.toolbarPopup, GUILayout.Width(78));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool follow = GUILayout.Toggle(_follow, "自动跟随最新", EditorStyles.miniButton, GUILayout.Width(100));
                if (follow && !_follow) { _activityScroll.y = float.MaxValue; }
                _follow = follow;
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("复制可见日志", EditorStyles.miniButton, GUILayout.Width(90))) { CopyVisible(); }
                if (GUILayout.Button("清空视图", EditorStyles.miniButton, GUILayout.Width(66)))
                { _clearActivity = true; _selectedId = null; _message = "视图已清空，工单与磁盘证据保留。"; _messageError = false; }
            }

            _rowStyle ??= new GUIStyle(EditorStyles.label) { richText = false, padding = new RectOffset(6, 6, 4, 4), wordWrap = true };
            RefreshActivityFilter();
            Rect viewport = GUILayoutUtility.GetRect(0, 100000, 80, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            // Layout 事件的 viewport 是占位矩形，用稳定窗宽避免 Layout/Repaint 来回失效所有行高。
            float width = Mathf.Max(240, position.width - 30), height = 0;
            _rowEnds.Clear();
            foreach (ActivityRow row in _filteredRows)
            {
                if (row.Width != width || row.Height <= 0)
                { row.Width = width; row.Height = Mathf.Max(76, _rowStyle.CalcHeight(row.Content, width) + 8); }
                height += row.Height; _rowEnds.Add(height);
            }
            _activityScroll.y = Mathf.Clamp(_activityScroll.y, 0, Mathf.Max(0, height - viewport.height));
            _activityScroll = GUI.BeginScrollView(viewport, _activityScroll, new Rect(0, 0, width, Mathf.Max(height, viewport.height)));
            if (_filteredRows.Count == 0)
            {
                GUI.Label(new Rect(12, 20, width - 24, 65), entryCount == 0
                    ? "等待 Agent 的第一条命令\n先复制 Agent 配置，再让它检查当前编辑器状态。"
                    : "没有符合筛选条件的记录\n尝试清空搜索，或把来源与状态改回全部。", EditorStyles.wordWrappedLabel);
            }
            // 用累计高度二分定位视口；屏幕外行不创建控件、不算文字高度、不解析 JSON。
            int first = FirstVisibleRow(_rowEnds, _activityScroll.y);
            for (int i = first; i < _filteredRows.Count; i++)
            {
                float top = i == 0 ? 0 : _rowEnds[i - 1];
                if (top >= _activityScroll.y + viewport.height) { break; }
                ActivityRow row = _filteredRows[i];
                RevAutomationActivityEntry entry = row.Entry;
                Rect rect = new Rect(0, top, width, row.Height);
                if (Event.current.type == EventType.Repaint)
                {
                    Color background = entry.id == _selectedId ? new Color(.18f, .40f, .45f, .45f)
                        : EditorGUIUtility.isProSkin ? new Color(1, 1, 1, .025f) : new Color(0, 0, 0, .025f);
                    EditorGUI.DrawRect(rect, background);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3, 3, rect.height - 6), StateColor(entry.state));
                }
                if (GUI.Button(rect, row.Content, _rowStyle))
                { _selectedId = entry.id; _follow = false; _detailScroll = Vector2.zero; Repaint(); }
            }
            GUI.EndScrollView();

            ActivityRow selected = null;
            if (_selectedId != null) { _rows.TryGetValue(_selectedId, out selected); }
            if (selected != null) { DrawActivityDetail(selected); }
            else { GUILayout.Label("点击记录查看参数与结果。Agent 请求和 Unity 实际执行分开显示，成功仍需业务断言确认。", EditorStyles.wordWrappedMiniLabel); }
            if (!string.IsNullOrEmpty(_reader.LastError) || !string.IsNullOrEmpty(RevAutomationActivity.LastError))
            { EditorGUILayout.HelpBox("日志读取/写入异常：" + (_reader.LastError ?? RevAutomationActivity.LastError), MessageType.Warning); }
            GUILayout.Label("最近最多显示 500 条 · 250ms 刷新 · 常见凭据字段已隐藏", EditorStyles.centeredGreyMiniLabel);
        }

        private bool MatchesFilter(ActivityRow row, string query)
        {
            RevAutomationActivityEntry entry = row.Entry;
            if (_targetFilter == 4 && !row.RuntimeGM) { return false; }
            if (_targetFilter == 3 && entry.target != "game" && !row.RuntimeGM) { return false; }
            if (_targetFilter > 0 && _targetFilter != 3 && _targetFilter != 4 && entry.target != TargetKeys[_targetFilter]) { return false; }
            if (_stateFilter > 0 && entry.state != StateKeys[_stateFilter]) { return false; }
            return query.Length == 0 || row.Search.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>GM 行显示完整指令；runtime.call 显示实际业务能力，避免用户只看到适配器名字。</summary>
        internal static string DisplayAction(RevAutomationActivityEntry entry)
        {
            if (RevMCPActivityDescription.TryGetRuntimeGM(entry, out string command)) { return "游戏运行时 · GM 指令：" + command; }
            try
            {
                var args = RevAutomationJson.Object(RevAutomationJson.Parse(entry.argumentsJson ?? "{}"));
                if (entry.action == "gm.exec" && args.TryGetValue("command", out object line)) { return "GM · " + Convert.ToString(line); }
                if (entry.action == "runtime.call" && args.TryGetValue("runtimeAction", out object action)) { return "游戏 · " + Convert.ToString(action); }
            }
            catch (Exception) { }
            return entry.action;
        }

        private static int FirstVisibleRow(IReadOnlyList<float> ends, float scroll)
        {
            int low = 0, high = ends.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (ends[middle] <= scroll) { low = middle + 1; } else { high = middle; }
            }
            return low;
        }

        private void DrawActivityDetail(ActivityRow row)
        {
            RevAutomationActivityEntry entry = row.Entry;
            using (new EditorGUILayout.VerticalScope(_cardStyle, GUILayout.Height(Mathf.Clamp(position.height * .32f, 110, 250))))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(row.Title, EditorStyles.wordWrappedLabel, GUILayout.MinWidth(80));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("复制详情", EditorStyles.miniButton, GUILayout.Width(64))) { GUIUtility.systemCopyBuffer = RevAutomationJson.Write(entry); }
                    if (ValidJobId(entry.jobId) && GUILayout.Button("工单", EditorStyles.miniButton, GUILayout.Width(42))) { OpenDirectory(RevAutomationBridge.JobDirectory(entry.jobId)); }
                }
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
                GUILayout.Label(StateLabel(entry.state) + " · " + row.Source + " · " + row.Time, EditorStyles.miniLabel);
                DetailText("在做什么", row.Title);
                DetailText("有什么用", row.Purpose);
                DetailText("AI 说明的目的", string.IsNullOrWhiteSpace(entry.intent) ? "Agent 没有提供目的说明；以上用途来自接口契约。" : entry.intent);
                DetailText("原始命令", row.Action);
                if (row.RuntimeGM)
                {
                    DetailText("执行环境", "游戏运行时 · GM 指令（传输来源：" + entry.source + "）");
                    DetailText("具体 GM 指令与参数", row.GMCommand);
                }
                if (!string.IsNullOrEmpty(entry.plan))
                { GUILayout.Label("计划：" + entry.plan + " / " + (entry.phase == "finally" ? "收尾清理" : "执行") + " / " + entry.step, EditorStyles.wordWrappedMiniLabel); }
                DetailText("参数", entry.argumentsJson);
                if (!string.IsNullOrEmpty(entry.message)) { DetailText("说明", entry.message); }
                if (!string.IsNullOrEmpty(entry.error)) { DetailText("错误", entry.error); }
                if (!string.IsNullOrEmpty(entry.resultJson)) { DetailText("结果摘要", entry.resultJson); }
                EditorGUILayout.EndScrollView();
            }
        }

        private static void DetailText(string label, string text) { GUILayout.Label(label, EditorStyles.miniBoldLabel); GUILayout.Label(text ?? "—", EditorStyles.wordWrappedMiniLabel); }
        private void CopyVisible()
        {
            RefreshActivityFilter();
            GUIUtility.systemCopyBuffer = string.Join("\n", _filteredRows.Select(row => RevAutomationJson.Write(row.Entry)));
            _message = "当前筛选下的日志已复制。"; _messageError = false;
        }
        private static bool ValidJobId(string id) => id != null && id.Length == 32 && id.All(Uri.IsHexDigit);
        private static string LocalTime(string utc) => DateTimeOffset.TryParse(utc, out DateTimeOffset value) ? value.LocalDateTime.ToString("HH:mm:ss") : "—";
        private static string SourceLabel(RevAutomationActivityEntry entry) => RevMCPActivityDescription.TryGetRuntimeGM(entry, out _)
            ? "游戏运行时 · GM 指令 · " + entry.source : entry.target == "mcp" ? "Agent / MCP"
            : entry.target == "plan" ? "计划断言" : entry.target == "cli" ? "外部工具"
            : entry.target == "gm" ? "GM 指令 · " + entry.source : entry.target == "game" ? "运行时 · " + entry.source : "编辑器 · " + entry.source;
        private static string StateLabel(string state) => state == "Running" ? "执行中" : state == "Succeeded" ? "成功" : state == "Failed" ? "失败" : state == "Cancelled" ? "取消" : state;
        private static Color StateColor(string state) => state == "Running" ? new Color(.35f, .65f, .95f) : state == "Succeeded" ? new Color(.35f, .72f, .58f)
            : state == "Failed" ? new Color(.92f, .42f, .38f) : new Color(.7f, .65f, .4f);
    }
}

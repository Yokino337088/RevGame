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

        private void DrawActivity()
        {
            _reader ??= new RevMCPActivityReader(ActivityPath);
            IReadOnlyList<RevAutomationActivityEntry> entries = _reader.Entries;
            GUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                GUILayout.Label($"最近 {entries.Count} 条", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"执行中 {entries.Count(e => e.state == "Running")}   失败 {entries.Count(e => e.state == "Failed")}", EditorStyles.miniLabel);
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
                _follow = GUILayout.Toggle(_follow, "自动跟随最新", EditorStyles.miniButton, GUILayout.Width(100));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("复制可见日志", EditorStyles.miniButton, GUILayout.Width(90))) { CopyVisible(); }
                if (GUILayout.Button("清空视图", EditorStyles.miniButton, GUILayout.Width(66)))
                { _reader.ClearView(); _selectedId = null; _message = "视图已清空，工单与磁盘证据保留。"; _messageError = false; }
            }

            _rowStyle ??= new GUIStyle(EditorStyles.label) { richText = false, padding = new RectOffset(6, 6, 4, 4), wordWrap = true };
            var filtered = entries.Where(MatchesFilter).ToArray();
            _activityScroll = EditorGUILayout.BeginScrollView(_activityScroll, GUILayout.ExpandHeight(true));
            if (filtered.Length == 0)
            {
                GUILayout.Space(20);
                using (new EditorGUILayout.VerticalScope(_cardStyle))
                {
                    GUILayout.Label(entries.Count == 0 ? "等待 Agent 的第一条命令" : "没有符合筛选条件的记录", EditorStyles.boldLabel);
                    GUILayout.Label(entries.Count == 0
                        ? "先复制 Agent 配置，再让它“检查当前编辑器状态”。新版 Python MCP 和 Unity 桥会把调用写到这里。"
                        : "尝试清空搜索，或把来源与状态改回“全部”。", EditorStyles.wordWrappedMiniLabel);
                }
            }
            foreach (RevAutomationActivityEntry entry in filtered)
            {
                string purpose = string.IsNullOrWhiteSpace(entry.intent) ? RevMCPActivityDescription.Purpose(entry) : "AI 说明的目的：" + entry.intent;
                string summary = RevMCPActivityDescription.Title(entry) + "\n" + purpose;
                float contentWidth = Mathf.Max(240, position.width - 30);
                float rowHeight = Mathf.Max(76, _rowStyle.CalcHeight(new GUIContent(summary), contentWidth) + 24);
                Rect rect = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                {
                    Color background = entry.id == _selectedId ? new Color(.18f, .40f, .45f, .45f)
                        : EditorGUIUtility.isProSkin ? new Color(1, 1, 1, .025f) : new Color(0, 0, 0, .025f);
                    EditorGUI.DrawRect(rect, background);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3, 3, rect.height - 6), StateColor(entry.state));
                }
                string subtitle = LocalTime(entry.utc) + "  ·  " + SourceLabel(entry) + "  ·  " + StateLabel(entry.state)
                    + (entry.state == "Running" ? "" : $"  ·  {entry.elapsedMs:F0} ms")
                    + (string.IsNullOrEmpty(entry.plan) ? "" : "  ·  " + entry.plan + (entry.phase == "finally" ? " / 清理" : ""));
                if (GUI.Button(rect, new GUIContent(summary + "\n" + subtitle,
                        "原始命令：" + entry.action + "\n" + entry.message), _rowStyle))
                { _selectedId = entry.id; _follow = false; _detailScroll = Vector2.zero; }
            }
            EditorGUILayout.EndScrollView();

            RevAutomationActivityEntry selected = entries.FirstOrDefault(e => e.id == _selectedId);
            if (selected != null) { DrawActivityDetail(selected); }
            else { GUILayout.Label("点击记录查看参数与结果。Agent 请求和 Unity 实际执行分开显示，成功仍需业务断言确认。", EditorStyles.wordWrappedMiniLabel); }
            if (!string.IsNullOrEmpty(_reader.LastError) || !string.IsNullOrEmpty(RevAutomationActivity.LastError))
            { EditorGUILayout.HelpBox("日志读取/写入异常：" + (_reader.LastError ?? RevAutomationActivity.LastError), MessageType.Warning); }
            GUILayout.Label("最近最多显示 500 条 · 250ms 刷新 · 常见凭据字段已隐藏", EditorStyles.centeredGreyMiniLabel);
        }

        private bool MatchesFilter(RevAutomationActivityEntry entry)
        {
            string[] targetKeys = { "", "mcp", "editor", "game", "gm", "cli", "plan" };
            string[] stateKeys = { "", "Running", "Succeeded", "Failed", "Cancelled" };
            bool runtimeGM = RevMCPActivityDescription.TryGetRuntimeGM(entry, out _);
            if (_targetFilter == 4 && !runtimeGM) { return false; }
            if (_targetFilter == 3 && entry.target != "game" && !runtimeGM) { return false; }
            if (_targetFilter > 0 && _targetFilter != 3 && _targetFilter != 4 && entry.target != targetKeys[_targetFilter]) { return false; }
            if (_stateFilter > 0 && entry.state != stateKeys[_stateFilter]) { return false; }
            string query = (_query ?? "").Trim();
            return query.Length == 0 || (RevMCPActivityDescription.Title(entry) + " " + DisplayAction(entry) + " " + entry.argumentsJson + " " + entry.message + " " + entry.plan + " " + entry.intent)
                .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
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

        private void DrawActivityDetail(RevAutomationActivityEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle, GUILayout.Height(Mathf.Clamp(position.height * .32f, 110, 250))))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(RevMCPActivityDescription.Title(entry), EditorStyles.wordWrappedLabel, GUILayout.MinWidth(80));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("复制详情", EditorStyles.miniButton, GUILayout.Width(64))) { GUIUtility.systemCopyBuffer = RevAutomationJson.Write(entry); }
                    if (ValidJobId(entry.jobId) && GUILayout.Button("工单", EditorStyles.miniButton, GUILayout.Width(42))) { OpenDirectory(RevAutomationBridge.JobDirectory(entry.jobId)); }
                }
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
                GUILayout.Label(StateLabel(entry.state) + " · " + SourceLabel(entry) + " · " + LocalTime(entry.utc), EditorStyles.miniLabel);
                DetailText("在做什么", RevMCPActivityDescription.Title(entry));
                DetailText("有什么用", RevMCPActivityDescription.Purpose(entry));
                DetailText("AI 说明的目的", string.IsNullOrWhiteSpace(entry.intent) ? "Agent 没有提供目的说明；以上用途来自接口契约。" : entry.intent);
                DetailText("原始命令", DisplayAction(entry));
                if (RevMCPActivityDescription.TryGetRuntimeGM(entry, out string gmCommand))
                {
                    DetailText("执行环境", "游戏运行时 · GM 指令（传输来源：" + entry.source + "）");
                    DetailText("具体 GM 指令与参数", gmCommand);
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
            GUIUtility.systemCopyBuffer = string.Join("\n", _reader.Entries.Where(MatchesFilter).Select(RevAutomationJson.Write));
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

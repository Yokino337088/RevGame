// RevMCPDiagnostics —— 窗口能力的明确 CLI 入口，避免 Agent 依赖鼠标位置；只操作当前工程。
// 日志返回实际账本，summary 只是用途解释，不能把文字当作待执行指令或业务验收结论。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>日志的只读命令行入口。与窗口共用读取器，Agent/CI 可以核对实际记账结果。</summary>
    public static class RevMCPDiagnostics
    {
        [Serializable] private sealed class Options { public string query, target, state; public int offset; public int limit = 50; }
        [Serializable] private sealed class WindowOptions { public bool floating; public int page, targetFilter, stateFilter; public string query; }
        [RevAutomationAction("editor.activity-window", Environment = "live", Keys = new[] { "floating", "page", "query", "targetFilter", "stateFilter" },
            Description = "打开 RevMCP 日志窗口并设置页签/筛选；floating 可使用独立预览窗口")]
        public static object Window(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<WindowOptions>(request.payloadJson ?? "{}");
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            foreach (string key in new[] { "page", "targetFilter", "stateFilter" })
            {
                if (values.TryGetValue(key, out object value) && (!(value is double number) || number != Math.Truncate(number)))
                { throw new ArgumentException(key + " 必须是整数"); }
            }
            if (values.TryGetValue("floating", out object floating) && !(floating is bool)) { throw new ArgumentException("floating 必须是 bool"); }
            if (options.page < 0 || options.page > 1 || options.targetFilter < 0 || options.targetFilter > 6 || options.stateFilter < 0 || options.stateFilter > 4)
            { throw new ArgumentException("page 为 0日志/1连接；targetFilter 为 0..6，stateFilter 为 0..4，与窗口筛选顺序一致。"); }
            RevMCPWindow.ShowForAutomation(options.floating, options.page, options.query, options.targetFilter, options.stateFilter);
            return new Dictionary<string, object> { ["opened"] = true, ["page"] = options.page };
        }
        [RevAutomationAction("editor.activity", Environment = "either", Keys = new[] { "query", "target", "state", "offset", "limit" },
            Description = "分页读取 RevMCP 活动记录：Agent 请求、Editor、运行时 GM、外部工具与计划断言")]
        public static object Activity(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<Options>(request.payloadJson ?? "{}");
            if (options.offset < 0 || options.limit < 1 || options.limit > 500) { throw new ArgumentException("offset >= 0，limit 为 1..500"); }
            var reader = new RevMCPActivityReader(RevMCPWindow.ActivityPath);
            reader.Poll();
            var rows = reader.Entries.Where(row =>
                (string.IsNullOrEmpty(options.target) || row.target == options.target)
                && (string.IsNullOrEmpty(options.state) || row.state == options.state)
                && (string.IsNullOrEmpty(options.query) || (RevMCPActivityDescription.Title(row) + " " + RevMCPWindow.DisplayAction(row) + " " + row.argumentsJson + " " + row.plan + " " + row.intent)
                    .IndexOf(options.query, StringComparison.OrdinalIgnoreCase) >= 0)).Reverse().ToArray();
            return new Dictionary<string, object>
            {
                ["total"] = rows.Length, ["offset"] = options.offset, ["capacity"] = RevMCPActivityReader.Capacity,
                ["entries"] = rows.Skip(options.offset).Take(options.limit).ToArray(), ["error"] = reader.LastError,
                ["summaries"] = rows.Skip(options.offset).Take(options.limit).Select(row => new Dictionary<string, object>
                {
                    ["id"] = row.id, ["operation"] = RevMCPActivityDescription.Title(row), ["purpose"] = RevMCPActivityDescription.Purpose(row),
                    ["agentIntent"] = row.intent, ["intentProvided"] = !string.IsNullOrWhiteSpace(row.intent)
                }).ToArray(),
                ["note"] = "只读活动记录；Agent 调用成功不等于计划断言或业务验收通过。"
            };
        }
    }
}

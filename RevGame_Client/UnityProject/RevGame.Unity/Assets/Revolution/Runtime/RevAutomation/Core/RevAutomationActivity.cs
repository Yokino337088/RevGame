// RevAutomationActivity —— 自动化命令的旁路账本，Editor 文件桥与游戏 HTTP 共用。
// 不向 Unity Console 写日志：否则监视动作自身会污染测试的 Error/Exception 统计。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Revolution
{
    /// <summary>
    /// 一条活动记录。开始与完成事件共用 id，窗口据此更新同一行，避免把一次调用显示成两次。
    /// utc 为 UTC ISO 时间；elapsedMs 单位毫秒；参数和结果是经过脱敏、限长的 JSON 摘要。
    /// </summary>
    [Serializable]
    public sealed class RevAutomationActivityEntry
    {
        public int schema = 1;
        public string id, utc, source, target, action, state, argumentsJson, resultJson, message, error;
        public string jobId, plan, phase, step;
        // intent 是 Agent 自述的任务目的；description 是注册表的能力说明，两者不可混为一谈。
        public string intent, description;
        public double elapsedMs;
    }

    /// <summary>
    /// 开发态命令日志。先在主线程 Configure 设置活动目录，再开始记录；纯数据写入不访问 Unity API。
    /// 每进程使用独立分段文件，日志最多保留四个 2 MiB 分段，避免持续测试无限增长。
    /// 日志失败只更新 LastError，业务结果、取消与回执完全沿用原桥接语义。
    /// </summary>
    public static class RevAutomationActivity
    {
        public sealed class Ticket
        {
            internal RevAutomationActivityEntry Entry;
            internal long Started;
            internal bool Finished;
            public string Id => Entry.id;
        }

        private static readonly object Gate = new object();
        private static readonly string StreamId = Guid.NewGuid().ToString("N");
        private static string _directory;
        private static int _segment;
        private static readonly Regex SecretKey = new Regex("token|password|passwd|secret|credential|authorization|api.?key", RegexOptions.IgnoreCase);
        private static readonly Regex InlineSecret = new Regex(@"(?i)(Bearer\s+)[^\s""\\]+|((?:token|password|secret|api[_-]?key)\s*[=:]\s*)[^\s,;""\\]+");
        public static string LastError { get; private set; }

        /// <summary>目录由桥接层提供：Editor 在 Library，Player 在 persistentDataPath；不写入 Assets。</summary>
        public static void Configure(string directory)
        {
            lock (Gate)
            {
                string absolute = Path.GetFullPath(directory);
                if (_directory != absolute) { _directory = absolute; _segment = 0; }
            }
        }

        /// <summary>开始记录真实执行；id 可用已有工单 id，jobId 供窗口定位对应证据目录。</summary>
        public static Ticket Begin(string source, string target, string action, string argumentsJson, string id = null, string jobId = null, string description = null)
        {
            var ticket = new Ticket
            {
                Started = Stopwatch.GetTimestamp(),
                Entry = new RevAutomationActivityEntry
                {
                    id = id ?? Guid.NewGuid().ToString("N"), source = source, target = target,
                    action = action, argumentsJson = SafeJson(argumentsJson), state = "Running", jobId = jobId,
                    description = SafeText(description, 512)
                }
            };
            Write(ticket.Entry);
            return ticket;
        }

        /// <summary>完成一次执行；重复收尾不重复记账。结果摘要省略截图 base64 和常见凭据。</summary>
        public static void Finish(Ticket ticket, bool success, string message, object result = null, string error = null)
        {
            if (ticket == null || ticket.Finished) { return; }
            ticket.Finished = true;
            ticket.Entry.state = success ? "Succeeded" : error != null &&
                (error.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "Cancelled" : "Failed";
            ticket.Entry.message = SafeText(message, 512);
            ticket.Entry.error = SafeText(error, 128);
            ticket.Entry.elapsedMs = (Stopwatch.GetTimestamp() - ticket.Started) * 1000d / Stopwatch.Frequency;
            try { ticket.Entry.resultJson = SafeJson(RevAutomationJson.Write(result)); }
            catch (Exception) { ticket.Entry.resultJson = "[结果摘要不可序列化]"; }
            Write(ticket.Entry);
        }

        /// <summary>识别 JSON 对象/数组中的敏感字段；解析失败时不保留原始输入，防止误写凭据。</summary>
        public static string SafeJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { return "{}"; }
            try { return SafeText(RevAutomationJson.Write(Sanitize(RevAutomationJson.Parse(json), 0)), 8192); }
            catch (Exception) { return "[参数或结果不是可记录的 JSON]"; }
        }

        private static object Sanitize(object value, int depth)
        {
            if (depth > 12) { return "[嵌套已截断]"; }
            if (value is IDictionary map)
            {
                var safe = new Dictionary<string, object>();
                foreach (DictionaryEntry item in map)
                {
                    if (safe.Count == 48) { break; }
                    string key = Convert.ToString(item.Key);
                    safe[key] = SecretKey.IsMatch(key) ? "[已隐藏]" : string.Equals(key, "base64", StringComparison.OrdinalIgnoreCase)
                        ? "[图像数据省略]" : Sanitize(item.Value, depth + 1);
                }
                return safe;
            }
            if (value is string text)
            {
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    try { return Sanitize(RevAutomationJson.Parse(text), depth + 1); }
                    catch (Exception) { return "[嵌套 JSON 无效]"; }
                }
                return SafeText(text, 4096);
            }
            if (value is IEnumerable sequence)
            {
                var safe = new List<object>();
                foreach (object item in sequence)
                {
                    if (safe.Count == 24) { safe.Add("…其余项已省略"); break; }
                    safe.Add(Sanitize(item, depth + 1));
                }
                return safe;
            }
            return value;
        }

        public static string SafeText(string value, int limit = 4096)
        {
            string safe = InlineSecret.Replace(value ?? "", match =>
                (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + "[已隐藏]");
            return safe.Length <= limit ? safe : safe.Substring(0, limit) + "…[已截断]";
        }

        private static void Write(RevAutomationActivityEntry entry)
        {
            try
            {
                lock (Gate)
                {
                    if (_directory == null) { return; }
                    Directory.CreateDirectory(_directory);
                    string prefix = "unity-" + StreamId + "-";
                    string path = Path.Combine(_directory, prefix + _segment.ToString("D4") + ".jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                    { _segment++; path = Path.Combine(_directory, prefix + _segment.ToString("D4") + ".jsonl"); }
                    entry.utc = DateTime.UtcNow.ToString("O");
                    File.AppendAllText(path, RevAutomationJson.Write(entry) + "\n", new UTF8Encoding(false));
                    string old = Path.Combine(_directory, prefix + (_segment - 4).ToString("D4") + ".jsonl");
                    if (File.Exists(old)) { File.Delete(old); }
                    LastError = null;
                }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        /// <summary>包装跨帧动作：完成、取消、异常与 Dispose 仍传给原操作，不改变其业务状态。</summary>
        public static object TraceRuntime(string action, string payload, Func<object> invoke, string description = null)
        {
            string target = action == "gm.exec" ? "gm" : "game";
            var ticket = Begin("Unity Runtime", target, action, payload, description: description);
            try
            {
                object result = invoke();
                if (result is RevRuntimeOperation operation) { return new TracedOperation(operation, ticket); }
                Finish(ticket, true, "完成", result);
                return result;
            }
            catch (Exception e) { Finish(ticket, false, e.Message, null, e.GetType().Name); throw; }
        }

        private sealed class TracedOperation : RevRuntimeOperation
        {
            private readonly RevRuntimeOperation _inner;
            private readonly Ticket _ticket;
            private bool _done;
            public TracedOperation(RevRuntimeOperation inner, Ticket ticket) { _inner = inner; _ticket = ticket; }
            public override bool Tick()
            {
                try
                {
                    _done = _inner.Tick(); Success = _inner.Success; Message = _inner.Message; Result = _inner.Result;
                    return _done;
                }
                catch (Exception e) { Finish(_ticket, false, e.Message, null, e.GetType().Name); throw; }
            }
            public override void Cancel()
            {
                try { _inner.Cancel(); }
                finally { Success = false; Message = _inner.Message; Result = _inner.Result; Finish(_ticket, false, Message, Result, "Cancelled"); }
            }
            public override void Dispose()
            {
                try { _inner.Dispose(); Finish(_ticket, _done && _inner.Success, _inner.Message, _inner.Result, _done ? null : "Interrupted"); }
                catch (Exception e) { Finish(_ticket, false, e.Message, null, "DisposeFailed"); throw; }
            }
        }
    }
}
#endif

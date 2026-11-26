// 活动诊断仅在编辑器实时窗口订阅期间发布临时事件。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// 活动窗口打开后才记录。生产者最多暂存 64 条事件，由窗口读取后立即删除。
    /// 默认无订阅时直接返回，Player 与无人值守的调用不创建活动文件。
    /// </summary>
    public static class RevAutomationActivity
    {
        public sealed class Ticket
        {
            internal RevAutomationActivityEntry Entry;
            internal long Started;
            /// <summary>创建时的窗口订阅编号，关闭后丢弃晚到的终态。</summary>
            internal int Generation;
            internal bool Finished;
            public string Id => Entry.id;
        }

        /// <summary>窗口来不及消费时最多暂存 64 个诊断事件，满了覆盖最早的一个。</summary>
        private const int Slots = 64;
        /// <summary>限制单个活动文件的 UTF-8 字节数，整个待消费队列最多 4 MiB。</summary>
        private const int MaxEventBytes = 64 * 1024;
        /// <summary>串行化窗体、操作主线程和外部活动上报对临时文件的访问。</summary>
        private static readonly object Gate = new object();
        /// <summary>当前窗口的暂存位置，空值表示没有采集者。</summary>
        private static volatile string _directory;
        /// <summary>窗口订阅时递增，旧订阅完成的工单不能写进新窗口。</summary>
        private static int _generation;
        /// <summary>当前订阅中下一事件的插入顺序。</summary>
        private static int _nextSlot;
        /// <summary>隐藏 JSON 字段中的常见令牌、密码和授权头。</summary>
        private static readonly Regex SecretKey = new Regex("token|password|passwd|secret|credential|authorization|api.?key", RegexOptions.IgnoreCase);
        /// <summary>隐藏自由文本中出现的常见凭据形式。</summary>
        private static readonly Regex InlineSecret = new Regex(@"(?i)(Bearer\s+)[^\s""\\]+|((?:token|password|secret|api[_-]?key)\s*[=:]\s*)[^\s,;""\\]+");

        /// <summary>当前窗口是否接收活动，上报方用它跳过无人查看的事件。</summary>
        public static bool IsRecording => _directory != null;

        /// <summary>最近一次活动文件 IO 错误，供实时窗口提示，不影响工单结果。</summary>
        public static string LastError { get; private set; }

        /// <summary>首个窗口开启时接管临时目录，先回收同一项目上次崩溃留下的事件。</summary>
        public static void StartRecording(string directory)
        {
            lock (Gate)
            {
                _directory = Path.GetFullPath(directory);
                _generation++;
                _nextSlot = 0;
                RemovePending(_directory);
            }
        }

        /// <summary>最后一个窗口关闭或域重载前停采集，并清除未被读取的临时事件。</summary>
        public static void StopRecording()
        {
            lock (Gate)
            {
                string directory = _directory;
                _directory = null;
                RemovePending(directory);
            }
        }

        /// <summary>编辑器启动或维护时回收崩溃遗留，当前订阅拥有的事件仍由窗口消费。</summary>
        public static void RecoverAbandoned(string directory)
        {
            lock (Gate)
            {
                if (_directory == null)
                {
                    RemovePending(directory);
                }
            }
        }

        /// <summary>单个实时视图读取临时事件。最后一次读取之后在同一锁中删除文件。</summary>
        public static int Drain(int limit, Action<string> accept)
        {
            lock (Gate)
            {
                string directory = _directory;
                if (directory == null || !Directory.Exists(directory))
                {
                    return 0;
                }

                int count = 0;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("活动暂存目录不能是链接");
                    }
                    // 从最旧槽位读取。正在写的事件和读取后的删除在同一锁中，避免截断或重复消费。
                    for (int offset = 0; offset < Slots && count < limit; offset++)
                    {
                        string path = SlotPath(directory, (_nextSlot + offset) % Slots);
                        if (!File.Exists(path))
                        {
                            continue;
                        }
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                            {
                                accept(File.ReadAllText(path, Encoding.UTF8));
                            }
                        }
                        finally
                        {
                            File.Delete(path);
                        }
                        count++;
                    }
                    LastError = null;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    LastError = error.Message;
                }
                return count;
            }
        }

        /// <summary>只回收工具固定槽位，清理时保留同目录的其他文件。</summary>
        private static void RemovePending(string directory)
        {
            if (directory == null || !Directory.Exists(directory))
            {
                return;
            }
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("活动暂存目录不能是链接");
                }
                for (int slot = 0; slot < Slots; slot++)
                {
                    File.Delete(SlotPath(directory, slot));
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
                LastError = null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                LastError = error.Message;
            }
        }

        /// <summary>稳定的槽位文件名令多次 Editor 重启后的异常残留仍有固定上限。</summary>
        private static string SlotPath(string directory, int slot)
        {
            return Path.Combine(directory, "event-" + slot.ToString("D2") + ".json");
        }

        /// <summary>由认证后的 Editor HTTP 入口发布 Agent 活动，限制字段并重新执行凭据脱敏。</summary>
        public static void PublishAgent(RevAutomationActivityEntry entry)
        {
            if (!IsRecording)
            {
                return;
            }
            if (entry == null || !Guid.TryParseExact(entry.id, "N", out _) ||
                (entry.state != "Running" && entry.state != "Succeeded" && entry.state != "Failed" && entry.state != "Cancelled"))
            {
                throw new ArgumentException("无效的 Agent 活动事件");
            }
            entry.source = "Agent";
            entry.target = SafeText(entry.target, 32);
            entry.action = SafeText(entry.action, 256);
            entry.state = SafeText(entry.state, 16);
            entry.argumentsJson = SafeJson(entry.argumentsJson);
            entry.resultJson = SafeJson(entry.resultJson);
            entry.message = SafeText(entry.message, 512);
            entry.error = SafeText(entry.error, 128);
            entry.jobId = Guid.TryParseExact(entry.jobId, "N", out _) ? entry.jobId : null;
            entry.plan = SafeText(entry.plan, 512);
            entry.phase = SafeText(entry.phase, 128);
            entry.step = SafeText(entry.step, 512);
            entry.intent = SafeText(entry.intent, 512);
            entry.description = SafeText(entry.description, 512);
            Write(entry, _generation);
        }

        /// <summary>开始记录真实执行；id 可用已有工单 id，jobId 供窗口定位对应证据目录。</summary>
        public static Ticket Begin(string source, string target, string action, string argumentsJson, string id = null, string jobId = null, string description = null)
        {
            if (!IsRecording || action == "editor.activity")
            {
                return null;
            }
            var ticket = new Ticket
            {
                Started = Stopwatch.GetTimestamp(),
                Generation = _generation,
                Entry = new RevAutomationActivityEntry
                {
                    id = id ?? Guid.NewGuid().ToString("N"), source = source, target = target,
                    action = action, argumentsJson = SafeJson(argumentsJson), state = "Running", jobId = jobId,
                    description = SafeText(description, 512)
                }
            };
            Write(ticket.Entry, ticket.Generation);
            return ticket;
        }

        /// <summary>完成一次执行；重复收尾不重复记账。结果摘要省略截图 base64 和常见凭据。</summary>
        public static void Finish(Ticket ticket, bool success, string message, object result = null, string error = null)
        {
            if (!IsRecording || ticket == null || ticket.Finished || ticket.Generation != _generation)
            {
                return;
            }
            ticket.Finished = true;
            ticket.Entry.state = success ? "Succeeded" : error != null &&
                (error.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "Cancelled" : "Failed";
            ticket.Entry.message = SafeText(message, 512);
            ticket.Entry.error = SafeText(error, 128);
            ticket.Entry.elapsedMs = (Stopwatch.GetTimestamp() - ticket.Started) * 1000d / Stopwatch.Frequency;
            // 截图字典先剔除大图再序列化摘要，避免第二次编码整份 PNG 响应。
            try
            {
                object summary = result is IDictionary ? Sanitize(result, 0) : result;
                ticket.Entry.resultJson = SafeJson(RevAutomationJson.Write(summary));
            }
            catch (Exception) { ticket.Entry.resultJson = "[结果摘要不可序列化]"; }
            Write(ticket.Entry, ticket.Generation);
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

        /// <summary>仅在订阅存续时写入固定槽位。队列拥塞时覆盖最旧事件，避免堆积。</summary>
        private static void Write(RevAutomationActivityEntry entry, int generation)
        {
            lock (Gate)
            {
                if (_directory == null || generation != _generation)
                {
                    return;
                }
                try
                {
                    entry.utc = DateTime.UtcNow.ToString("O");
                    string json = RevAutomationJson.Write(entry);
                    if (Encoding.UTF8.GetByteCount(json) > MaxEventBytes)
                    {
                        return;
                    }
                    Directory.CreateDirectory(_directory);
                    string path = SlotPath(_directory, _nextSlot);
                    if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0 ||
                        (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    {
                        throw new IOException("活动暂存路径不能是链接");
                    }
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    _nextSlot = (_nextSlot + 1) % Slots;
                    LastError = null;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    LastError = error.Message;
                }
            }
        }

        /// <summary>包装跨帧动作：完成、取消、异常与 Dispose 仍传给原操作，不改变其业务状态。</summary>
        public static object TraceRuntime(string action, string payload, Func<object> invoke, string description = null)
        {
            string target = action == "gm.exec" ? "gm" : "game";
            var ticket = Begin("Unity Runtime", target, action, payload, description: description);
            try
            {
                object result = invoke();
                if (ticket != null && result is RevRuntimeOperation operation)
                {
                    return new TracedOperation(operation, ticket);
                }
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

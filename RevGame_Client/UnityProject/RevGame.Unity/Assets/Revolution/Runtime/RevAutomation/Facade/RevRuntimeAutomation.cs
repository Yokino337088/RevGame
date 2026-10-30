// 开发态显式注册的游戏能力。MCP、HTTP 和 Editor 复用同一主线程业务入口，不扫描任意方法。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 一次需要多帧才能完成的动作，例如等待截屏文件写完。
    /// 处理函数返回此对象后，桥每帧调用 Tick；返回 false 表示继续等，true 表示读取结果并结束。
    /// 不要在 Tick 中 while 忙等，否则 Unity 无法渲染下一帧，取消请求也无法被处理。
    /// </summary>
    public abstract class RevRuntimeOperation : IDisposable
    {
        public bool Success { get; protected set; } = true;
        public string Message { get; protected set; } = "完成";
        public object Result { get; protected set; }
        /// <summary>只在 Unity 主线程调用。完成前设置 Result、Success 和 Message。</summary>
        public abstract bool Tick();
        // 取消不是数据库回滚：已经加过的道具、发出的网络请求需要业务自己的 finally 接口清理。
        public virtual void Cancel() { Success = false; Message = "操作已取消；已发生的业务副作用不会回滚"; }
        public virtual void Dispose() { }
    }

    /// <summary>
    /// 游戏提供给 AI 的公开门面。只注册愿意开放的业务入口，不反射执行任意 C# 方法。
    /// Editor 文件桥和开发版 Player HTTP 桥都调用此门面，因此业务只接线一次。
    /// 整个文件只在 Editor、Development Build 或明确开启 REV_AUTOMATION 的构建中存在；
    /// 项目调用处也要使用相同的 #if，避免正式构建引用被裁掉的类型。
    /// </summary>
    public static partial class RevRuntimeAutomation
    {
        private sealed class Entry
        {
            public string Name, Description;
            public Dictionary<string, object> Schema;
            public bool ReadOnly, Confirm;
            public object Owner;
            public Func<string, object> Handler;
        }
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static bool _builtins;
        private static int _thread;
#if UNITY_EDITOR
        // Editor 文件桥与 HTTP 桥共用主线程，却可能来自两个 Agent 进程。
        // 共享忙标记避免 Test Runner/场景变更与跨帧游戏动作交叉执行。
        public static bool EditorBusy { get; set; }
#endif
        public static bool HasPendingWork => RevRuntimeAutomationHost.HasPendingWork;
        private static readonly object LogGate = new object();
        private static readonly Queue<object> Logs = new Queue<object>();
        private static int _errors;
        public static int ErrorCount => System.Threading.Volatile.Read(ref _errors);
        internal static void MainThread()
        {
            if (_thread == 0 || _thread != System.Threading.Thread.CurrentThread.ManagedThreadId)
            { throw new InvalidOperationException("RevRuntimeAutomation 只能在 Unity 主线程注册/调用"); }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            // 即使关闭 Enter Play Mode 的 Domain Reload，此回调仍会执行。
            // 清掉上一局注册表，避免旧场景对象被静态委托持有，也避免重复注册。
            Entries.Clear(); _builtins = false; _thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
#if UNITY_EDITOR
            EditorBusy = false;
#endif
            lock (LogGate) { Logs.Clear(); _errors = 0; }
            Application.logMessageReceivedThreaded -= CaptureLog;
            Application.logMessageReceivedThreaded += CaptureLog;
        }
        private static void CaptureLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) { System.Threading.Interlocked.Increment(ref _errors); }
            lock (LogGate)
            {
                if (Logs.Count == 128) { Logs.Dequeue(); }
                Logs.Enqueue(new Dictionary<string, object> { ["message"] = message.Length > 4096 ? message.Substring(0, 4096) : message,
                    ["type"] = type.ToString(), ["utc"] = DateTime.UtcNow.ToString("O"), ["stack"] = stack.Length > 4096 ? stack.Substring(0, 4096) : stack });
            }
        }
        internal static object RecentLogs()
        {
            lock (LogGate) { return new Dictionary<string, object> { ["errorCount"] = ErrorCount, ["capacity"] = 128, ["entries"] = Logs.ToArray() }; }
        }
        /// <summary>
        /// 在场景 Awake 或业务启动入口登记一个能力，随后 Agent 的 discover 就能看到它。
        /// </summary>
        /// <param name="name">稳定的业务名，例如 inventory.add；同名重复注册会直接报错。</param>
        /// <param name="description">给 Agent 的说明，应讲清前置条件、实际效果和失败原因。</param>
        /// <param name="inputSchemaJson">根为 object 的 JSON Schema；调用前先校验类型、必填和范围。</param>
        /// <param name="handler">接收已校验的 JSON，在主线程调用玩家共用入口；返回可序列化数据或跨帧操作。</param>
        /// <param name="readOnly">仅无业务副作用的查询设 true；测试计划只会重复轮询这类能力。</param>
        /// <param name="confirm">true 时调用方必须显式传 confirm；它不替代用户对业务动作的授权。</param>
        /// <param name="owner">通常传 this，并在 OnDestroy/Dispose 调用 UnregisterAllOf(this)。</param>
        public static void Register(string name, string description, string inputSchemaJson, Func<string, object> handler,
            bool readOnly = false, bool confirm = false, object owner = null)
        {
            MainThread();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 96 || name.StartsWith("gm.", StringComparison.Ordinal)
                || name.Any(c => !(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')))
            { throw new ArgumentException("能力名需要 1..96 位字母/数字/._-，gm.* 为保留前缀"); }
            if (handler == null) { throw new ArgumentNullException(nameof(handler)); }
            var schema = RevAutomationJson.Object(RevAutomationJson.Parse(inputSchemaJson)); RevAutomationJson.CheckSchema(schema);
            if ((string)schema["type"] != "object") { throw new ArgumentException("能力参数 schema 根必须是 object"); }
            Entries.Add(name, new Entry { Name = name, Description = description ?? name, Schema = schema,
                Handler = handler, ReadOnly = readOnly, Confirm = confirm, Owner = owner });
        }
        /// <summary>按完整名称移除；返回 false 表示本来就没有登记。</summary>
        public static bool Unregister(string name) { MainThread(); return Entries.Remove(name); }
        /// <summary>清除同一个 owner 注册的全部能力；比较对象身份，不调用业务 Dispose。</summary>
        public static void UnregisterAllOf(object owner)
        {
            MainThread(); if (owner == null) { throw new ArgumentNullException(nameof(owner)); }
            foreach (string name in Entries.Where(p => ReferenceEquals(p.Value.Owner, owner)).Select(p => p.Key).ToArray()) { Entries.Remove(name); }
        }
        private static void EnsureBuiltins() { if (!_builtins) { _builtins = true; RevRuntimeTools.Register(); } }
        /// <summary>返回此刻真实注册表与 GM 参数；场景切换后应重新发现，不复用旧对象 id。</summary>
        public static object Capabilities()
        {
            MainThread(); EnsureBuiltins();
            return new Dictionary<string, object> { ["playing"] = Application.isPlaying, ["gmEnabled"] = RevGM.Enabled,
                ["actions"] = Entries.Values.OrderBy(e => e.Name).Select(e => new Dictionary<string, object> {
                    ["id"] = e.Name, ["description"] = e.Description, ["inputSchema"] = e.Schema, ["readOnly"] = e.ReadOnly, ["confirm"] = e.Confirm }).ToArray(),
                ["commands"] = RevGM.Commands.Select(GMInfo).ToArray() };
        }
        public static object GMInfo(RevGMCommand command)
        {
            var props = new Dictionary<string, object>(); var required = new List<string>();
            for (int i = 0; i < command.Args.Length; i++)
            {
                RevGMArg arg = command.Args[i]; string key = "arg" + i;
                var property = new Dictionary<string, object> { ["type"] = arg.Type == RevGMArgType.Int ? "integer" : arg.Type == RevGMArgType.Float ? "number" : arg.Type == RevGMArgType.Bool ? "boolean" : "string", ["description"] = arg.Name + ": " + arg.Describe() };
                if (arg.Candidates != null && arg.Candidates.Length > 0) { property["enum"] = arg.Candidates; }
                if (arg.Type == RevGMArgType.Int) { property["minimum"] = int.MinValue; property["maximum"] = int.MaxValue; }
                props[key] = property; if (arg.IsRequired) { required.Add(key); }
            }
            // 没有参数说明的旧 GM 仍可用原始参数数组；不虚构业务参数。
            if (command.Args.Length == 0) { props["args"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" } }; }
            return new Dictionary<string, object> { ["name"] = command.Name, ["description"] = command.Description,
                ["signature"] = command.Signature(), ["highRisk"] = command.IsHighRisk, ["hidden"] = command.IsHidden,
                ["inputSchema"] = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false } };
        }
        /// <summary>桥接层调用入口：先检查运行态与 Schema，再调用处理函数；异常交给工单记录。</summary>
        public static object Invoke(string action, string payloadJson, bool confirm = false)
        {
            return RevAutomationActivity.TraceRuntime(action, payloadJson, () => InvokeCore(action, payloadJson, confirm),
                Entries.TryGetValue(action, out Entry entry) ? entry.Description : null);
        }

        private static object InvokeCore(string action, string payloadJson, bool confirm)
        {
            MainThread(); if (!Application.isPlaying) { throw new InvalidOperationException("游戏能力需要 Play/Player"); }
            EnsureBuiltins();
            if (action == "runtime.capabilities") { return Capabilities(); }
            if (!Entries.TryGetValue(action, out Entry entry)) { throw new ArgumentException("未注册的游戏能力：" + action); }
            RevAutomationJson.Validate(RevAutomationJson.Parse(payloadJson), entry.Schema);
            if (entry.Confirm && !confirm) { throw new InvalidOperationException("该能力要求显式 confirm=true"); }
            return entry.Handler(payloadJson);
        }
        public static object ExecuteGM(string commandLine, bool confirm)
        {
            return RevAutomationActivity.TraceRuntime("gm.exec", RevAutomationJson.Write(new Dictionary<string, object> { ["command"] = commandLine }),
                () => ExecuteGMCore(commandLine, confirm), GMDescription(commandLine));
        }

        private static string GMDescription(string line)
        {
            try { RevGMParser.Split(line, out string name, out _); return RevGM.TryGet(name, out RevGMCommand command) ? command.Description : null; }
            catch (Exception) { return null; }
        }

        private static object ExecuteGMCore(string commandLine, bool confirm)
        {
            MainThread(); RevGMParser.Split(commandLine, out string name, out _);
            if (!RevGM.TryGet(name, out RevGMCommand command) || command.Name != name) { throw new ArgumentException("GM 必须使用已注册的完整命令名"); }
            if (command.IsHighRisk && !confirm) { throw new InvalidOperationException("高危 GM 要求 confirm=true"); }
            RevGMResult result = RevGM.Execute(commandLine);
            if (!result.Success) { throw new InvalidOperationException(result.Message); }
            return new Dictionary<string, object> { ["success"] = true, ["message"] = result.Message, ["elapsedMs"] = result.ElapsedMs };
        }
        /// <summary>
        /// 显式开启本机 HTTP，返回凭据 JSON 的路径。通常 Editor 直接走文件桥即可；
        /// 独立 Player 才需要此服务。token=null 时生成随机令牌，Agent 从本机文件读取。
        /// </summary>
        public static string Start(int port = 18766, string token = null)
        {
            MainThread(); if (!Application.isPlaying) { throw new InvalidOperationException("运行时服务只允许在游戏运行时启动"); }
            EnsureBuiltins(); return RevRuntimeAutomationHost.StartServer(port, token);
        }
        /// <summary>停止监听、取消未完成工单并移除本会话凭据。已完成的业务副作用不撤销。</summary>
        public static void Stop() { MainThread(); RevRuntimeAutomationHost.StopServer(); }
    }
}
#endif

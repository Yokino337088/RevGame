// ============================================================
// RevLog.cs —— 日志唯一入口（小白只需要读这一个文件）
//
// 位置：Runtime\RevLog\Facade\
//
// 【三条铁律】
//   ① **级别就是成本契约**：`Debug` 在正式包里不存在（编译期删除，连字符串拼接都不发生）；
//      `Info/Warn` 便宜（不采堆栈）；`Error/Exception` 才采堆栈（微秒级，只在真出错时付）。
//   ② **循环计时器…（不，日志版）循环里别拼字符串**：`RevLog.Debug($"x={x}")` 在正式包里被删除、零成本 ✓，
//      但 `RevLog.Info($"x={x}")` 会真的拼 —— 热循环里请先 `if (RevLog.IsEnabled(RevLogLevel.Info))`。
//   ③ **不打日志时也不该有代价**：没有 Sink 时只走兜底出口；有 Sink 时逐个 try/catch，单个通道坏掉不影响别的。
//
// 【最短上手】
// <code>
// RevLog.Info("登录成功");                                  // 普通信息
// RevLog.Warn("配置缺失，用默认值", "Config");               // 带 tag（第二个参数就是 tag，不会写反）
// RevLog.Error("数据库连不上", "Network");
// RevLog.Exception(e, "战斗开始失败", "Battle");             // 带异常对象（控制台可点击跳转）
// RevLog.Debug("仅编辑器可见，正式包零成本");
// </code>
//
// 【零配置】Unity 里由 Support\RevLogUnityHooks 自动装好控制台通道 + 帧号；纯 C# 环境走标准错误兜底。
//
// 【要落盘？】一行：`RevLog.EnableFileLog(Application.persistentDataPath + "/revlog");` —— 异步线程写，不卡帧。
//
// 【要上报？】订阅 `RevLog.OnReport += (e, msg) => 你的崩溃平台.Report(e, msg);`（默认不接 = 不上报）。
//
// 【要看现场？】`RevLog.Dump(200)` 拿到最近 200 行（环形缓冲常驻，不用提前开文件）。
// ============================================================
using System;
using System.Diagnostics;

namespace Revolution
{
    /// <summary>日志门面。门面是静态的（业务调用最省事），内核 <see cref="RevLogCore"/> 是实例。</summary>
    public static class RevLog
    {
        internal static readonly RevLogCore Core = new RevLogCore();

        // ==================== 写日志（唯一入口，参数都在一行里）====================

        /// <summary>
        /// 调试日志：<c>[Conditional]</c> → **正式包里调用点被编译器完全删除**（连参数求值都不发生）。
        /// ★ 业务工程想要它，请在自己工程的 Scripting Define Symbols 里加 <c>REVLOG_DEBUG</c>。
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        [Conditional("REVLOG_DEBUG")]
        public static void Debug(string message, string tag = null) => Core.Log(RevLogLevel.Debug, tag, message, null);

        /// <summary>普通信息（正式包保留；不采堆栈）。</summary>
        public static void Info(string message, string tag = null) => Core.Log(RevLogLevel.Info, tag, message, null);

        /// <summary>告警：用法可疑但还能跑。</summary>
        public static void Warn(string message, string tag = null) => Core.Log(RevLogLevel.Warn, tag, message, null);

        /// <summary>错误：必须修（默认采堆栈）。</summary>
        public static void Error(string message, string tag = null) => Core.Log(RevLogLevel.Error, tag, message, null);

        /// <summary>异常：带异常对象（堆栈总是采，控制台可点击跳转；可触发上报）。</summary>
        public static void Exception(Exception e, string message = null, string tag = null) => Core.Log(RevLogLevel.Exception, tag, message, e);

        /// <summary>完整入口：自己指定级别（其余方法都是它的快捷方式）。</summary>
        public static void Log(RevLogLevel level, string message, string tag = null) => Core.Log(level, tag, message, null);

        // ==================== 开关 ====================

        /// <summary>最低输出级别（默认 Info）。调高 = 少打；调低 = 多打（Debug 由编译期管，不受这里影响）。</summary>
        public static RevLogLevel MinLevel { get => Core.MinLevel; set => Core.MinLevel = value; }

        /// <summary>这个级别 + tag 现在会输出吗（热循环里先问它，省掉字符串拼接）。</summary>
        public static bool IsEnabled(RevLogLevel level, string tag = null) => Core.IsEnabled(level, tag);

        /// <summary>静音某个 tag（按模块开关：`RevLog.MuteTag("Network")`）。</summary>
        public static void MuteTag(string tag, bool muted = true) => Core.MuteTag(tag, muted);

        /// <summary>这个 tag 被静音了吗。</summary>
        public static bool IsTagMuted(string tag) => Core.IsTagMuted(tag);

        // ==================== 输出通道 ====================

        /// <summary>加一个输出通道（控制台/文件/上报/自定义面板）。同一个实例不会重复加。</summary>
        public static void AddSink(IRevLogSink sink) => Core.AddSink(sink);

        /// <summary>移除并收尾一个通道（会调它的 Dispose）。</summary>
        public static bool RemoveSink(IRevLogSink sink) => Core.RemoveSink(sink);

        /// <summary>有叫这个名字的通道吗（默认的控制台通道就叫 "Console"）。</summary>
        public static bool HasSink(string name)
        {
            IRevLogSink[] sinks = Core.SnapshotSinks();
            for (int i = 0; i < sinks.Length; i++)
                if (sinks[i] != null && sinks[i].Name == name) return true;

            return false;
        }

        /// <summary>一行启用文件日志（异步写，不卡帧；目录不可写会自动禁用并报到 SinkErrors）。</summary>
        public static RevFileSink EnableFileLog(string directory, string prefix = "revlog")
        {
            var sink = new RevFileSink(directory, prefix);
            AddSink(sink);
            return sink;
        }

        /// <summary>把还在路上的日志落下去（切场景/退到后台/出包前调）。</summary>
        public static void Flush() => Core.Flush();

        /// <summary>清空历史（环形缓冲），通道不动。</summary>
        public static void Clear() => Core.Clear();

        /// <summary>输出通道自身出错的累计次数（0 = 一切正常；&gt;0 说明某个通道坏了，日志可能在丢）。</summary>
        public static int SinkErrors => Core.SinkErrors;

        // ==================== 看现场 ====================

        /// <summary>最近 N 条日志（旧的在前；count &lt;= 0 = 全部）。</summary>
        public static RevLogEntry[] Recent(int count = 50) => Core.Recent(count);

        /// <summary>最近 N 条拼成多行文本（一键复制现场用）。</summary>
        public static string Dump(int count = 200) => Core.Dump(count);

        /// <summary>最近日志条数。</summary>
        public static int Count => Core.RingCount;

        // ==================== 出口（可替换）====================

        /// <summary>异常上报出口（默认不接 = 不上报）：接你的崩溃平台只需一行。</summary>
        public static event Action<Exception, string> OnReport
        {
            add => Core.OnReport += value;
            remove => Core.OnReport -= value;
        }

        /// <summary>兜底输出（没有 Sink 时用；Unity 下由钩子接到 Debug，纯 C# 下是标准错误）。</summary>
        public static Action<string> Fallback
        {
            get => Core.Fallback;
            set => Core.Fallback = value;
        }

        /// <summary>帧号来源（Unity 下由钩子接到 Time.frameCount；没有 → -1，不用 0 冒充）。</summary>
        public static Func<int> FrameProvider
        {
            get => Core.FrameProvider;
            set => Core.FrameProvider = value;
        }

        // ==================== 框架内部 ====================

        /// <summary>某个 Sink 出错时，内核用它报到兜底出口（供 Sink 实现内部调用）。</summary>
        internal static void ReportSinkError(string message) => Core.ReportSinkError(message);

        /// <summary>进 Play / 换账号时复位（由 <c>Support\RevLogUnityHooks</c> 调用，业务不用管）。</summary>
        internal static void ResetForNewSession() => Core.ResetForNewSession();
    }
}

// ============================================================
// RevInputLog.cs —— 输入模块的日志出口（框架不打日志，这里只做"出口"）
//
// 位置：Runtime\RevInput\Implementation\
//
// 【要解决的问题】
//   输入是全游戏最高频的系统，**绝不能**在正常路径打日志（一条 Info 就是一次分配 + 一次 IO 排队）。
//   但失败又必须看得见 —— 于是这里的规则是：
//     · 正常路径：一条都不打；
//     · `<see cref="Verbose"/>` 打开时：诊断信息走 Log.Info；
//     · 失败路径：走 Warn（只在下述少数几种"业务配置错了"的情况）；
//     · 业务异常（你自己的监听者抛的）：走 OnException 隔离，不让它打断整帧输入。
//
// 【接线方式】（由 Support\RevInputUnityHooks.cs 完成）
//   <code>
//   RevInputLog.Log = m => RevLog.Info(m, "Input");           // 诊断（Verbose 时）
//   RevInputLog.Warn = m => RevLog.Warn(m, "Input");          // 失败
//   RevInputLog.OnException = (e, m) => RevLog.Exception(e, m, "Input");
//   </code>
// ============================================================

using System;

namespace Revolution
{
    /// <summary>模块内日志出口（<c>internal</c>：业务不直接调它，用 <c>RevInput.VerboseLog</c> 开关）。</summary>
    internal static class RevInputLog
    {
        /// <summary>诊断输出（仅 <see cref="Verbose"/> 为真时调用）。</summary>
        internal static Action<string> Log;

        /// <summary>失败输出（配置类错误，例如键位不认识、动作超上限）。</summary>
        internal static Action<string> Warn;

        /// <summary>异常出口（业务监听者抛异常时，隔离后送到这里）。</summary>
        internal static Action<Exception, string> OnException;

        /// <summary>是否输出诊断（业务用 <c>RevInput.VerboseLog</c> 开关）。</summary>
        internal static bool Verbose;

        /// <summary>诊断信息（Verbose 关闭时连参数求值都省了：调用方用 <see cref="IsVerbose"/> 先判断）。</summary>
        internal static bool IsVerbose => Verbose && Log != null;

        /// <summary>打一条诊断。</summary>
        internal static void V(string message)
        {
            if (Verbose) Log?.Invoke(message);
        }

        /// <summary>打一条失败（配置错误；不做去重，因为这类错误本来就该只有几条）。</summary>
        internal static void W(string message) => Warn?.Invoke(message);

        /// <summary>
        /// 把一次业务回调包起来执行：**任何监听者抛异常都不许打断整帧输入**。
        /// 这是"事件系统里一个订阅者写坏了，别的订阅者照常收"的防线。
        /// </summary>
        internal static void Guard(string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                OnException?.Invoke(e, what);
            }
        }
    }
}

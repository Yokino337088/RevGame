// ============================================================
// RevABLog.cs —— RevAB 打包工具的日志出口（统一走框架日志系统）
//
// 位置：Editor\RevResourceSystem\ABTool\Core\
//
// 【为什么工具里不再直接 Debug.Log】
//   打包工具是这个仓库里"说话最多"的地方（校验失败、命名冲突、生成跳过、CI 打包结果…），
//   以前它直接打 UnityEngine.Debug —— 结果是：
//     · 框架有了统一日志系统后，它成了唯一的例外（"两套日志"）；
//     · 打出来的东西不会进 RevLog 的环形缓冲，出问题时"现场"是断的；
//     · 没法按模块静音（例如不想看生成器的告警）。
//
// 【为什么单独一个类，而不是每处都写 RevLog.Info(msg, "RevAB")】
//   和 RevPoolLog / RevHeavyFSMLog 同一个惯例：**模块自己的出口类** ——
//   标签只写一遍、以后想改出口（比如同时写进工具的窗口）也只改这一处。
//
// 【级别映射】与 Unity 控制台一一对应，行为不变：
//   Debug.Log → Info（工具里的"进度信息"，用 Info 而不是 Debug：编辑器里 Debug 也能出，
//                      但 Info 不会被"只留 Warn 以上"的阈值顺手滤掉）
//   Debug.LogWarning → Warn
//   Debug.LogError   → Error（自动带堆栈）
//
// 【更名说明】本工具原名 LiteAB，已正式更名为 RevAB；日志 tag 同步改为 "RevAB"
//   （以前静音过 "LiteAB" 的，改成 RevLog.MuteTag("RevAB")）。
// ============================================================
namespace Revolution.Editor
{
    /// <summary>RevAB 打包工具的日志出口（tag = "RevAB"，统一进 <see cref="RevLog"/>）。</summary>
    internal static class RevABLog
    {
        /// <summary>工具的统一 tag（要按模块静音时就静音它：RevLog.MuteTag("RevAB")）。</summary>
        internal const string Tag = "RevAB";

        /// <summary>进度/结果信息（原来是 Debug.Log）。</summary>
        internal static void Info(string message) => RevLog.Info(message, Tag);

        /// <summary>告警：可疑但没拦住流程（原来是 Debug.LogWarning）。</summary>
        internal static void Warn(string message) => RevLog.Warn(message, Tag);

        /// <summary>错误：必须修（原来是 Debug.LogError；RevLog 会带上堆栈）。</summary>
        internal static void Error(string message) => RevLog.Error(message, Tag);

        /// <summary>
        /// 诊断细节（注册表内容、扫描结果这类"只在排查时要看"的东西）。
        /// 走 <see cref="RevLog.Debug"/> 意味着：正式包里编译期删除，编辑器里照常可见。
        /// </summary>
        internal static void Debug(string message) => RevLog.Debug(message, Tag);
    }
}

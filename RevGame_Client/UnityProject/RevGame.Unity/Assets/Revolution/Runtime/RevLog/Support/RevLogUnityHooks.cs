// ============================================================
// RevLogUnityHooks.cs —— 日志系统的 Unity 生命周期钩子（★ 全框架日志统一的接入点）
//
// 位置：Runtime\RevLog\Support\
//
// 【为什么需要它】内核是**纯 C#**（不引用 UnityEngine），所以"兜底输出往哪打、帧号从哪来、
//   退出前要把文件落盘"这类引擎相关的事，由这个薄文件补上。
//
// 【它做四件事】
//   ① 装控制台通道（叫 "Console"；已有同名通道就不重复装）；
//   ② 接兜底出口与帧号来源（内核默认写标准错误、帧号 -1；这里接到 Debug 与 Time.frameCount）；
//   ③ 退出前 Flush（异步写文件的通道靠它把队列排空，否则崩溃/关服时可能丢最后几条）；
//   ④ **接管没人管的出口**：状态机 / UI 的日志出口以前是自己直接打 Debug（业务改不了，因为是 internal），
//      这里统一改成走 RevLog —— 至此框架只有一个日志出口（"不制造第二套日志"）。
//
// 【已经有自己 Install() 的模块（事件 / 对象池 / 计时器 / 音效）】它们各自的钩子文件里
//   直接把出口指向 RevLog（见各模块 Support\*UnityHooks.cs），不在这个文件里重复接管。
//
// 【进 Play 复位】关掉 Domain Reload 时静态字段不消失 —— 上一局的文件句柄、后台线程、
//   环形缓冲都要收掉（ResetForNewSession 会 Dispose 所有通道）。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把日志系统接到 Unity 生命周期上（控制台通道 + 兜底出口 + 帧号 + 退出落盘 + 收口未接管出口）。</summary>
    internal static class RevLogUnityHooks
    {
#if UNITY_EDITOR
        // 编辑器里（没运行游戏时）也要能用，所以额外接一次
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            RevLog.ResetForNewSession();       // ★ 先收上一局（文件句柄/后台线程），再装新的
            Install();
        }

        private static void Install()
        {
            // ① 出口与帧号（内核是纯 C#，这些只能由这里提供）
            RevLog.Fallback = message => Debug.LogWarning(message);
            RevLog.FrameProvider = () => Time.frameCount;

            // ② 控制台通道（默认通道；业务自己装过同名的不重复装）
            if (!RevLog.HasSink("Console")) RevLog.AddSink(new RevConsoleSink());

            // ③ 退出前把队列里的日志落盘（文件通道；其它自定义通道同理）
            Application.quitting -= FlushOnQuit;
            Application.quitting += FlushOnQuit;

            // ④ 哪些模块**不需要**在这里接管（避免重复接线，也避免"两套出口"）：
            //    · 事件 / 对象池 / 计时器：各自的 Support\*UnityHooks 已经把出口指向 RevLog；
            //    · UI / 状态机：默认实现本身就走 RevLog（见 RevUISetting / RevHeavyFSM）；
            //    · 音效 / 资源加载：它们的设计是"失败带原因码 + Failed 事件 / handle.ErrorReason"，
            //      业务订阅即可 —— 框架不额外打日志（这是它们的契约，不是遗漏）。
        }

        private static void FlushOnQuit() => RevLog.Flush();
    }
}

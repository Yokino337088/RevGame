// ============================================================
// RevTimerUnityHooks.cs —— 计时器的 Unity 生命周期钩子
//
// 位置：Runtime\RevTimer\Support\
//
// 【为什么需要它】内核（RevTimerCore / 句柄 / 槽位表 / 服务器时钟 / 秒表）是**纯 C#**，
//   不引用 UnityEngine —— 好处是能在工程外跑断言（本模块 40+ 条行为断言就是这么验的）。
//   代价是"日志往哪打、进 Play 要清什么"没人做，就由这个薄薄的文件补上。
//
// 【它做两件事】
//   ① 装上出口：日志/异常接到 Debug（★ 只在自己没被设过时接管，业务自定义了就不覆盖）。
//   ② 进 Play 复位：关掉 Domain Reload 时静态数据不消失 —— 上一局没走完的计时器会继续跑
//      （表现为"我明明重开了，怎么还有倒计时在动"）。SubsystemRegistration 阶段清一次。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把计时器接到 Unity 生命周期上（驱动安装 + 出口接管 + 进 Play 复位）。</summary>
    internal static class RevTimerUnityHooks
    {
#if UNITY_EDITOR
        // 编辑器里（没运行游戏时）也要能用，所以额外接一次
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            Install();

            RevTimer.ResetForNewSession();       // 清掉上一次运行留下的计时器与服务器时间锚点
        }

        private static void Install()
        {
            RevTimerDriver.Install();

            // 计时器的告警/异常统一走框架日志系统（tag = Timer）
            // 文件头承诺"只在自己没被设过时接管"，但以前是无条件覆盖：业务先设置了自己的日志出口，
            // 随后 Install 再跑一次（编辑器与运行时各调一次）就把它冲掉了。这里只补默认值，不覆盖已有设置。
            if (RevTimer.Log == null) RevTimer.Log = message => RevLog.Warn(message, "Timer");
            if (RevTimer.OnException == null) RevTimer.OnException = (e, message) => RevLog.Exception(e, message, "Timer");
        }
    }
}

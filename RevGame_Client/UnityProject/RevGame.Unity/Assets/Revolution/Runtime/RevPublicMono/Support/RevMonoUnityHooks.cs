// ============================================================
// RevMonoUnityHooks.cs —— 公共 Mono 模块的 Unity 生命周期钩子
//
// 位置：Runtime\RevPublicMono\Support\
//
// 【为什么需要它】监听列表的内核是纯 C#（可工程外断言），所以"进 Play 要清什么、
//   异常往哪报"这类引擎相关的事由这个薄文件补上。
//
// 【它做两件事】
//   ① 装上驱动安装点与异常出口（默认接到框架日志系统 RevLog，tag = Mono）；
//   ② **进 Play 复位**：关掉 Domain Reload 时静态数据不会清空 —— 上一次运行留下的监听者
//      会继续活着（表现为"我明明重开了，怎么每帧还在跑上一局的逻辑"）。这里清一次。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>把公共 Mono 模块接到 Unity 生命周期上（驱动安装 + 出口接管 + 进 Play 复位）。</summary>
    internal static class RevMonoUnityHooks
    {
#if UNITY_EDITOR
        // 编辑器里（没运行游戏时）也要能用，所以额外接一次
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            RevMono.ResetForNewSession();       // 清掉上一局残留的监听者（鬼故事防线）
            RevMonoDriver.ResetForNewSession();  // Domain Reload 关闭时复用宿主但停止上一局协程，并恢复 IsRunning
            Install();
        }

        private static void Install()
        {
            RevMonoDriver.Install();

            if (RevMono.OnException == null)
            {
                RevMono.OnException = (e, message) => RevLog.Exception(e, message, "Mono");
            }
        }
    }
}

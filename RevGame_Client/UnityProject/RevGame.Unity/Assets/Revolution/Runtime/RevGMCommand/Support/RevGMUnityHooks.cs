// ============================================================
// RevGMUnityHooks.cs —— GM 命令注册表的 Unity 生命周期钩子
//
// 位置：Runtime\RevGMCommand\Support\
//
// 【为什么需要这个文件】
//   命令表是静态的（RevGM 是静态类），而"关闭 Domain Reload 的快速进入 Play 模式"下
//   静态字段**不会**被清空 —— 上一次 Play 注册过的命令会原封不动活到这一次。
//   后果不是"多几条命令"那么轻：RevGM.Register 对重名是**直接报错**的（刻意不静默覆盖），
//   于是第二次进 Play 时，注册入口会在第一条命令上抛异常，整批注册中断 ——
//   表现为"这次 Play 里 GM 面板一条命令都没有"，而报错信息（"已经注册过了"）还指向错误的方向。
//
// 【做法】进入 Play 的最早阶段（SubsystemRegistration：早于场景加载、早于任何业务注册）
//   清一次注册表，保证每次 Play 都从空表开始注册。与 RevEventSystem / RevLog 的钩子同一套做法。
//
// 【注意】本文件依赖 UnityEngine，所以不参与工程外的纯 C# 断言（其余 9 个文件都不依赖 UnityEngine）。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把 GM 命令注册表接到 Unity 的生命周期上（进 Play 清一次，避免跨局残留）。</summary>
    internal static class RevGMUnityHooks
    {
        // SubsystemRegistration：进入 Play 后最先执行的阶段之一（早于场景加载、早于任何业务代码）
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnEnterPlay()
        {
            // 只清命令表，不动 Enabled 总开关（业务可能在启动期按包类型把它置 false，框架不该覆盖它的决定）
            RevGM.Clear();
        }
    }
}

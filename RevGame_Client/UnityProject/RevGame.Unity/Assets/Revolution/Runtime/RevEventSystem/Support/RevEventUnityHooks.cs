// ============================================================
// RevEventUnityHooks.cs —— 事件系统的 Unity 生命周期钩子
//
// 位置：Runtime\RevEventSystem\Support\
//
// 【为什么需要这个文件】
//   事件系统的核心（RevEvent / RevEventCenter / RevEventHandlerGroup / RevEventListener）
//   是**纯 C#**，不引用 UnityEngine —— 好处是它能在工程外跑单元测试（本书的事件断言就是这么验的）。
//   代价是"日志往哪打、进 Play 要清什么"这类 Unity 相关的事没人做，就由这个薄薄的文件补上。
//
// 【它做两件事】
//   ① 装上日志出口：没接日志的话，事件系统的告警/异常会输出到标准错误，
//      在 Unity 里不够显眼。这里接到 Debug.LogWarning / Debug.LogError + Debug.LogException。
//      ★ 只在自己没被设置过的时候接管 —— 业务自定义了出口就不覆盖。
//
//   ② 重置静态数据：静态字段在"关闭 Domain Reload 的快速进入 Play 模式"下**不会**被清空，
//      上一次运行留下的监听者会原封不动地活到这一次（表现为"我明明重新开始了，怎么还有监听"）。
//      SubsystemRegistration 阶段重置一次，彻底避免这种鬼故事。
//
// 【注意】本文件依赖 UnityEngine / UnityEditor，所以不参与工程外的单元测试。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把事件系统接到 Unity 的生命周期上（日志出口 + 进 Play 重置）。</summary>
    internal static class RevEventUnityHooks
    {
#if UNITY_EDITOR
        // 编辑器里（没运行游戏时）也要能用事件系统，所以额外接一次
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor()
        {
            Install();
        }
#endif

        // SubsystemRegistration：进入 Play 后最先执行的阶段之一（早于场景加载、早于任何业务代码）
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            Install();

            // 关闭 Domain Reload 时静态字段会残留 → 必须手动清一次
            RevEvent.ResetAll();
        }

        /// <summary>
        /// 装上默认日志出口：统一走框架日志系统 <see cref="RevLog"/>（事件系统不再自己打 Debug）。
        /// ★ 只在启动阶段（SubsystemRegistration / InitializeOnLoad）执行 —— 那时业务还没机会设出口，
        ///   所以这里直接赋值是安全的；业务之后想换成自己的日志系统，再赋值覆盖即可。
        /// </summary>
        private static void Install()
        {
            if (RevEvent.Log == null)
                RevEvent.Log = message => RevLog.Warn(message, "Event");
            if (RevEvent.OnException == null)
                RevEvent.OnException = (e, message) => RevLog.Exception(e, message, "Event");
        }
    }
}

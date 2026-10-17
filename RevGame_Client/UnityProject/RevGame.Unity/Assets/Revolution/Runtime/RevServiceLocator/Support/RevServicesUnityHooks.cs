// ============================================================
// RevServicesUnityHooks.cs —— 服务定位器的 Unity 生命周期钩子（薄适配层）
//
// 位置：Runtime\RevServiceLocator\Support\
//
// 【为什么需要它】服务表的内核（RevServiceHub）是纯 C#（可以链接进工程外断言），不认识 Unity。
//   "进 Play 要清什么 / 每帧谁来驱动 / 异常往哪报"这些引擎相关的事，由这个薄文件补上。
//
// 【它做三件事】
//   ① 进 Play 复位：关闭 Domain Reload 时静态数据不会清空 —— 上一局注册的服务（以及它们引用的所有对象）
//      会继续活着。表现为"我明明重开了，怎么还拿到上一局的服务 / 上一局的内存没释放"。这里清一次。
//   ② 每帧驱动（按需、零配置）：只有当第一个"实现了 RevITickable"的服务被创建时，才向 RevMono 挂一个每帧回调；
//      没有可 Tick 的服务就一帧都不会多跑。业务不用写 Update、不用摆物体。
//   ③ 异常隔离：某个服务的 OnTick 抛异常，只记日志（tag = Services），不影响其它服务继续被驱动。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把服务定位器接到 Unity 生命周期上（进 Play 复位 + 按需挂每帧驱动 + 异常出口）。</summary>
    internal static class RevServicesUnityHooks
    {
        private static bool _tickHooked;

#if UNITY_EDITOR
        // 编辑器里没点 Play 时也可能注册服务（工具脚本），注册能力要就位
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            // ★ 先复位再装钩子：上一局的服务（连同它们引用的对象）在新一局开始前就被清掉
            _tickHooked = false;
            RevServices.ResetForNewSession();
            Install();
        }

        private static void Install()
        {
            RevServicesBridge.EnsureInstalled = EnsureTickHook;
            RevServices.Hub.TickableCreated = EnsureTickHook;
        }

        /// <summary>有可 Tick 的服务出现时才挂每帧回调（用方法组而不是 lambda：关闭 Domain Reload 时不会持有失效闭包）。</summary>
        private static void EnsureTickHook()
        {
            if (_tickHooked)
            {
                return;
            }

            // 只有"真的存在可 Tick 的服务"才挂；EnsureInstalled 在每次 Register 时都会被喊一声，这里不能无脑挂
            if (!RevServices.Hub.HasTickable)
            {
                return;
            }

            // ★ 必须显式传 owner（随便一个"不会被任何域管到"的对象）：
            //   不传 owner 时，如果此刻恰好开着一个作用域（比如第一次 Register 发生在战斗域里），
            //   这条"框架自己的基础设施驱动"会被当成战斗域的东西 —— 战斗一结束它被摘掉，
            //   而 _tickHooked 还是 true，之后所有 RevITickable 服务再也不会被驱动，且毫无报错。
            _tickHooked = RevMono.AddInfrastructureUpdate(OnUpdate, typeof(RevServicesUnityHooks));
        }

        private static void OnUpdate()
        {
            RevServices.Hub.Tick(Time.deltaTime, OnTickError);
        }

        private static void OnTickError(System.Exception e)
        {
            RevLog.Exception(e, "服务 OnTick 抛异常（已隔离，不影响其它服务）", "Services");
        }
    }
}

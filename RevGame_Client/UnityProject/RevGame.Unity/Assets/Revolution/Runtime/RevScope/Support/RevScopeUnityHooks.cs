// ============================================================
// RevScopeUnityHooks.cs —— 作用域系统的 Unity 生命周期钩子（薄适配层）
//
// 位置：Runtime\RevScope\Support\
//
// 【为什么需要它】作用域的内核（RevScopeCore / RevLogicHost）是纯 C#（可以链接进工程外断言），
//   不认识 Unity。凡是"要用 Unity 类型才能做的事"都由这个薄文件补上：
//
//   ① 补装 4 个回收步骤 —— 它们要用 Unity 侧的模块：
//        第 2 步 界面    RevUIManager        关闭挂在这个域上的面板
//        第 3 步 每帧回调 RevMono             摘掉这个域（和它名下对象）的每帧回调
//        第 6 步 对象池   RevPool             DestroyGroup(域的回收分组)
//        第 7 步 资源     RevResBootstrap     Shutdown(域的回收分组)
//      ★ 6 必须在 7 之前（池手里端着 prefab 的资源引用，先还实例、再卸资源）—— 这个顺序由内核的步骤编号保证，
//        本文件只管"每一步具体怎么做"，不决定顺序。
//   ② 每帧驱动（按需、零配置）：只有当第一个"重写了 OnTick 的 RevLogic"启动时，才向 RevMono 挂一个每帧回调；
//      没有这种 Logic 就一帧都不会多跑。业务不用写 Update、不用摆物体。
//   ③ 进 Play 复位：关闭 Domain Reload 时静态数据不会清空 —— 上一局的域和 Logic 会一直活到下一局
//      （表现为"我明明重开了，怎么还在跑上一局的 Logic"）。这里清一次。
//
// 【为什么退出应用时不自动 ExitAll】
//   进程都要没了，再去跑回收只会和"正在被销毁的场景对象"打架、徒增报错；
//   编辑器里停止 Play 之后，下一次进 Play 会自动复位（见 ③）。
//   如果你的 Logic 需要"退出前上报"，请监听 RevAppLifecycle 的退出事件自己处理。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>把作用域系统接到 Unity 上（补装回收步骤 + 按需挂每帧驱动 + 进 Play 复位）。</summary>
    internal static class RevScopeUnityHooks
    {
        private static bool _tickHooked;

#if UNITY_EDITOR
        // 编辑器里没点 Play 时也可能用到作用域（工具脚本 / 测试），所以步骤要就位
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            // ★ 先复位再装钩子：上一局的域 / Logic 在新一局开始前就被安静地清掉
            _tickHooked = false;
            RevScope.ResetForNewSession();
            Install();
        }

        private static void Install()
        {
            RevScope.InstallStep(RevScopeStep.Ui, CloseUi);
            RevScope.InstallStep(RevScopeStep.Mono, ClearMono);
            RevScope.InstallStep(RevScopeStep.Pool, DestroyPools);
            RevScope.InstallStep(RevScopeStep.Res, ShutdownResources);

            RevScopeBridge.EnsureTickDriver = EnsureTickHook;
            RevScopeBridge.RemoveMonoOf = RemoveMonoOf;
        }

        // ============================================================
        // 回收步骤（第 2 / 3 / 6 / 7 步）
        // ============================================================

        /// <summary>第 2 步：关闭挂在这个域上的面板（界面系统在登记面板时已经把它们记到域上）。</summary>
        private static int CloseUi(RevScopeHandle node)
        {
            if (node.Owners.Count == 0) { return 0; }

            object[] owners = node.SnapshotOwners();
            int closed = 0;
            for (int i = 0; i < owners.Length; i++)
            {
                // 注意用 is 判类型：Unity 对象销毁后 == null，但类型判断仍然成立；Close 内部会自己处理"已销毁 / 已关闭"
                if (owners[i] is RevUIPanel panel && RevUIManager.Instance.Close(panel)) { closed++; }
            }

            return closed;
        }

        /// <summary>第 3 步：摘掉这个域名下的每帧回调（域自己 + 它登记过的每个对象）。</summary>
        private static int ClearMono(RevScopeHandle node)
        {
            return RevScopeCore.SumOwners(node, RevMono.RemoveAllOf);
        }

        /// <summary>第 6 步：销毁域配置的资源分组下的所有池（先还实例，才轮到第 7 步卸资源）。</summary>
        private static int DestroyPools(RevScopeHandle node)
        {
            if (node.Reclaim == RevResGroup.Unknown) { return RevScopeCore.Skipped; }

            return RevPool.DestroyGroup(node.Reclaim);
        }

        /// <summary>第 7 步：卸载域配置的资源分组。返回值 = 资源缓存里少了多少条（作为"回收了多少"的参考）。</summary>
        private static int ShutdownResources(RevScopeHandle node)
        {
            if (node.Reclaim == RevResGroup.Unknown) { return RevScopeCore.Skipped; }

            int before = RevResManager.CachedCount;
            RevResBootstrap.Instance.Shutdown(node.Reclaim);
            return Math.Max(0, before - RevResManager.CachedCount);
        }

        // ============================================================
        // 业务单元的每帧驱动
        // ============================================================

        /// <summary>有"重写了 OnTick 的 Logic"出现时才挂每帧回调（用方法组而不是 lambda：关闭 Domain Reload 时不会持有失效闭包）。</summary>
        private static void EnsureTickHook()
        {
            if (_tickHooked) { return; }

            // ★ 显式传 owner（一个不会被任何域管到的对象）：
            //   不传的话，如果这条"框架自己的基础设施驱动"恰好是在某个域里第一次挂上的，它会被当成那个域的东西，
            //   域一关就被摘掉，而 _tickHooked 仍是 true —— 之后所有 Logic 的 OnTick 都不再被驱动，且毫无报错。
            _tickHooked = RevMono.AddInfrastructureUpdate(OnUpdate, typeof(RevScopeUnityHooks));
        }

        private static void OnUpdate()
        {
            RevScope.Tick(Time.deltaTime);
        }

        /// <summary>Logic 停止时，摘它自己（owner = this）注册的每帧回调。</summary>
        private static void RemoveMonoOf(object owner)
        {
            RevMono.RemoveAllOf(owner);
        }
    }
}

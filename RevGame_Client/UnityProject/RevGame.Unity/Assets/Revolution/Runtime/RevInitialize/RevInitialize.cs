// ============================================================
// RevInitialize.cs —— Revolution 框架统一初始化门面
//
// 位置：Runtime\RevInitialize\
//
// 【最常用写法】在游戏启动入口（Boot / Bootstrap）的 Awake 或 Start 调一次：
//     RevInitialize.Initialize();
//
// 它会装配资源系统策略，之后 RevUI / RevPool / RevDataLoad 等才能按资源路径加载资源。
// 调用是幂等的：同一运行会话内重复调用不会重复重建资源缓存。
//
// 【为什么不是“把所有模块都 new / Init 一遍”】
//   RevEvent、RevInput、RevTimer、RevUI、RevPool、RevAppLifecycle 等模块，
//   已由各自的 RuntimeInitializeOnLoadMethod 钩子复位状态、安装宿主或按需启动；
//   重复手动启动反而可能清掉监听、取消加载或重复驱动。
//   服务（RevServices / RevServiceLocator）由业务自己在启动入口或各模块的 Awake 里 Register，框架不能替业务决定注册内容。
//
// 【扩展接缝】有资源系统扩展（例如热更新）时，可通过 Initialize 的回调先装配扩展钩子，
//   再由本门面初始化资源系统。回调只在本会话第一次成功初始化时执行。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// Revolution 框架的统一启动门面。
    /// <para>在游戏启动入口调用一次 <see cref="Initialize"/>；同一运行会话内重复调用安全无副作用。</para>
    /// <para>当前必须显式装配的框架级入口是资源系统；其它模块由 Unity 生命周期钩子或首次使用时启动。</para>
    /// </summary>
    public static class RevInitialize
    {
        private enum InitState : byte
        {
            NotStarted,
            Initializing,
            Initialized
        }

        private static InitState _state;

        /// <summary>本次 Unity 运行会话是否已完成框架初始化。</summary>
        public static bool IsInitialized => _state == InitState.Initialized;

        /// <summary>
        /// 一键初始化 Revolution。
        ///
        /// 在启动场景的 Boot / Bootstrap 脚本中调用一次即可，不需要再直接调用
        /// <c>RevResBootstrap.Instance.Init()</c>。
        ///
        /// <paramref name="beforeResourceInitialization">可选扩展装配回调：在资源策略注册前执行；
        /// 例如安装会覆盖 ResMap 的热更新钩子。普通项目保持默认 null。</paramref>
        ///
        /// 重复调用会直接返回。若扩展装配或资源初始化抛出异常，状态会恢复为未初始化并重新抛出，
        /// 调用方可修复原因后重试。
        /// </summary>
        /// <param name="beforeResourceInitialization">资源系统初始化前的可选扩展装配回调。</param>
        public static void Initialize(Action beforeResourceInitialization = null)
        {
            if (_state == InitState.Initialized) return;

            if (_state == InitState.Initializing)
            {
                throw new InvalidOperationException("RevInitialize.Initialize() 正在执行，不能在资源扩展装配回调中递归调用它。");
            }

            _state = InitState.Initializing;
            try
            {
                beforeResourceInitialization?.Invoke();
                RevResBootstrap.Instance.Init();
                _state = InitState.Initialized;
            }
            catch (Exception exception)
            {
                _state = InitState.NotStarted;
                Debug.LogException(exception);
                throw;
            }

            RevLog.Info("Revolution 框架初始化完成（资源系统策略已就绪）", "Initialize");
        }

        /// <summary>
        /// Unity 关闭 Domain Reload 时，静态字段会跨 Play 会话保留；每次新会话重置门面状态，
        /// 让启动入口可以重新装配资源策略。这里只复位本门面的标记，不在 Unity hook 中启动资源。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            _state = InitState.NotStarted;
        }
    }
}

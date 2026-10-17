// ============================================================
// RevILightState.cs —— 轻量级状态接口
//
// 位置：Runtime\RevStateMachine\Lightweight\Interfaces\
//
// 【设计来源】
//   对齐王者 tweenplayer 的 IState（OnEnter / OnExit / OnUpdate 三段式）。
//   王者四套状态机里真正"每套都有"的回调就这三个，所以它是轻量级状态机的最小契约。
//
// 【为什么接口要按能力拆开】
//   栈语义（被压住 / 重新露出）在 RevILightStackState 里扩展；
//   异步准备（等资源/动画就绪才交割）在 RevILightAsyncState 里扩展 ——
//   业务状态只需实现自己用到的能力，不为用不到的方法写空实现。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 轻量级状态接口：进入 / 退出 / 每帧更新。
    /// <para>任何想被 <see cref="RevLightFSM{T}"/> 驱动的状态，实现这个接口即可；
    /// 不想手写空方法的，继承 <see cref="RevLightStateBase"/>。</para>
    /// </summary>
    public interface RevILightState
    {
        /// <summary>进入状态（成为当前状态时调用一次）—— 典型用途：开 UI、订阅事件、重置计时</summary>
        void OnEnter();

        /// <summary>退出状态（被切走时调用一次）—— 典型用途：关 UI、退订事件、收尾清理</summary>
        void OnExit();

        /// <summary>每帧更新（仅"当前激活"状态会被调用）</summary>
        /// <param name="deltaTime">距上一帧的秒数（由驱动方传入，便于测试与暂停控制）</param>
        void OnUpdate(float deltaTime);
    }
}

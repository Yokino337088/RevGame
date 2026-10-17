// ============================================================
// RevLightStateBase.cs —— 轻量级状态空实现基类
//
// 位置：Runtime\RevStateMachine\Lightweight\Base\
//
// 【设计来源】
//   王者①的 BaseState / ③的 BaseStackState：全部回调给空实现，
//   业务状态继承后只 override 关心的（比如只管 Enter/Exit，不管 Suspend/Resume）。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 轻量级状态空实现基类：实现 <see cref="RevILightStackState"/> 的全部方法（全空），
    /// 业务状态继承它，只 override 关心的回调。
    /// <para>同时满足单状态机（<see cref="RevLightFSM{T}"/>）与栈状态机
    /// （<see cref="RevLightStackFSM{T}"/>）的约束 —— 同一个状态类两种机器都能用。</para>
    /// <para>带异步准备的状态请继承 <see cref="RevLightAsyncStateBase"/>。</para>
    /// </summary>
    public abstract class RevLightStateBase : RevILightStackState
    {
        /// <inheritdoc/>
        public virtual void OnEnter() { }

        /// <inheritdoc/>
        public virtual void OnExit() { }

        /// <inheritdoc/>
        public virtual void OnUpdate(float deltaTime) { }

        /// <inheritdoc/>
        public virtual void OnSuspend() { }

        /// <inheritdoc/>
        public virtual void OnResume() { }
    }
}

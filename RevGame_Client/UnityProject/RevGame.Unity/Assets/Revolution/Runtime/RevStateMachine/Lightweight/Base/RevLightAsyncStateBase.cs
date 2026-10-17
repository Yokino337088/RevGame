// ============================================================
// RevLightAsyncStateBase.cs —— 轻量级异步状态空实现基类
//
// 位置：Runtime\RevStateMachine\Lightweight\Base\
//
// 【它把两份空实现合到一个基类里】
//   RevLightStateBase 的五个生命周期回调（全空）
//     + RevILightAsyncState 的两个 Prepare（默认直接完成）
//   业务继承它之后，只要 override 真正要等的那一个 Prepare，其余全都免费：
//
//     class LoadingState : RevLightAsyncStateBase
//     {
//         public override async RevTask PrepareEnterAsync(RevCancellationToken token)
//         {
//             await RevResManager.LoadAsync&lt;GameObject&gt;("BattleScene", token);
//         }
//     }
//
// 【什么时候用它】
//   状态有"加载完才准进 / 收尾完才准走"这类异步准备时。
//   纯同步状态用 RevLightStateBase 即可（更轻，也不会让状态机走异步路径）。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 轻量级异步状态基类：继承 <see cref="RevLightStateBase"/>（五个生命周期回调全空），
    /// 再实现 <see cref="RevILightAsyncState"/>（两个 Prepare 默认直接完成）。
    /// <para>业务只需 override 真正要等的那一个，其余全都免费。</para>
    /// </summary>
    public abstract class RevLightAsyncStateBase : RevLightStateBase, RevILightAsyncState
    {
        /// <inheritdoc/>
        public virtual RevTask PrepareEnterAsync(RevCancellationToken token) => RevTask.Completed;

        /// <inheritdoc/>
        public virtual RevTask PrepareExitAsync(RevCancellationToken token) => RevTask.Completed;
    }
}

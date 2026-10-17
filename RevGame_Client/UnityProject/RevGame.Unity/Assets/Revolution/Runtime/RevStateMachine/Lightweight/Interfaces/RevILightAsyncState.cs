// ============================================================
// RevILightAsyncState.cs —— 轻量级异步状态接口（可选能力）
//
// 位置：Runtime\RevStateMachine\Lightweight\Interfaces\
//
// 【设计来源：王者④事务式 FSM<T>（HLOD）】
//   普通状态机"说切就切"；王者 HLOD 的状态机要求"双方都准备好才交割"：
//   旧状态准备好退出（资源卸完 / 收尾动画播完）+ 新状态准备好进入（资源加载好）——
//   这正是"异步"在状态机里的真实含义（切换可以横跨很多帧）。
//
// 【"就绪"怎么表达：直接用本框架的 RevTask】
//   不再自造一套轮询用的 IsReadyToExit / IsReadyToEnter，而是让状态把"准备过程"写成异步方法：
//     · 状态不实现本接口 → 同步切换，零成本（99% 的状态）；
//     · 状态实现本接口 → 状态机 await 它的 Prepare，真正完成后才交割
//       （RevTask 由 RevTaskScheduler 驱动，所以才敢"跨帧"）。
//
//   业务侧想怎么写就怎么写，加载、延时、等动画结束都能塞进去：
//     public async RevTask PrepareEnterAsync(RevCancellationToken token)
//     {
//         await RevResManager.LoadAsync&lt;GameObject&gt;("Boss", token);   // 资源加载
//         await RevTask.Delay(500);                                    // 再等 500ms
//     }
//
// 【token 的作用】
//   状态机每发起一次异步切换就新建一个取消源；当这次切换被"更新的请求"取代、
//   或被 CancelAsyncTransition() 主动取消时，token 会被取消 ——
//   业务可以 token.Register(...) 中断在途的加载，不做无用功。
//
// 【注意】
//   · 只有 RevLightFSM<T> 会 await 本接口；
//     重量级 RevHeavyFSM<TStateEnum, TOwner> 用的是自己那套
//     （RevHeavyFSMState.PrepareEnterAsync / PrepareExitAsync，签名一致）；
//   · 用了 RevTask 就别再混协程，两套时序混在一起很难推理；
//   · 不想手写空实现的，继承 Base 目录下的 RevLightAsyncStateBase。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 轻量级异步状态接口（可选能力）：实现它的状态，切换时要先 await 完"准备过程"才交割。
    /// <para>只被 <see cref="RevLightFSM{T}"/> 识别；只实现 <see cref="RevILightState"/>
    /// 的状态走同步切换（默认）。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// class LoadingState : RevLightAsyncStateBase
    /// {
    ///     public override async RevTask PrepareEnterAsync(RevCancellationToken token)
    ///     {
    ///         await RevResManager.LoadAsync&lt;GameObject&gt;("BattleScene", token);   // 加载完才允许切进来
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface RevILightAsyncState
    {
        /// <summary>
        /// 进入前的准备：await 完成之前，状态机<b>不会</b>交割（当前状态继续跑、继续被 Update 驱动）。
        /// <para>不需要准备的直接 <c>return RevTask.Completed;</c>。</para>
        /// </summary>
        /// <param name="token">取消令牌：本次切换被取代 / 被取消时会 Cancel，业务可用它中断在途操作</param>
        RevTask PrepareEnterAsync(RevCancellationToken token);

        /// <summary>
        /// 退出前的准备：await 完成之前，状态机<b>不会</b>交割（用于等收尾动画播完、等资源卸干净）。
        /// <para>不需要准备的直接 <c>return RevTask.Completed;</c>。</para>
        /// </summary>
        /// <param name="token">取消令牌：本次切换被取代 / 被取消时会 Cancel</param>
        RevTask PrepareExitAsync(RevCancellationToken token);
    }
}

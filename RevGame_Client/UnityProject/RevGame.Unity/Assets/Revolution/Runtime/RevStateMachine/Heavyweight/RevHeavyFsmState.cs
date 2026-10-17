// ============================================================
// RevHeavyFSMState.cs —— 重量级状态基类（泛型枚举 + 宿主行为接口）
//
// 位置：Runtime\RevStateMachine\Heavyweight\
//
// 【保留了原框架 BaseState 的什么】
//   · 泛型双参数：TStateEnum 状态枚举 + TOwner 宿主行为接口；
//   · 构造时传入所属状态机（状态里能拿到 Machine 与 Owner）；
//   · abstract StateType：每个状态声明自己对应哪个枚举值；
//   · ChangeState / CanChangeState / GetOtherState 等便捷方法（改名更短的 ChangeTo / CanChangeTo / GetState）。
//
// 【改了什么（对齐王者 + 去臃肿）】
//   · 生命周期命名：EnterState/QuitState/UpdateState → OnEnter/OnExit/OnUpdate
//     （对齐王者命名与 C# 事件惯例）；
//   · 三个回调从 abstract 改为 virtual 空实现 —— 只 override 关心的（王者 BaseState 的做法），
//     不用给每个状态硬写三个方法；
//   · 驱动回调带 deltaTime 参数（王者②③的做法）：不再让状态自己读 Time.deltaTime，
//     方便测试、也方便做暂停/慢动作；
//   · 砍掉分层状态机相关（ParentState / ChildStateMachine / GetRootStateMachine）；
//
// 【新增：事务式异步切换（吸收王者④，用 RevTask 表达）】
//   PrepareExitAsync / PrepareEnterAsync 默认直接返回 RevTask.Completed（= 同步切换，零成本，
//   连 await 挂起点都不会产生）。需要"等收尾动画播完才准走""等资源加载好才准进"的状态
//   override 它，把等待写成真正的异步流程即可：
//
//     public override async RevTask PrepareEnterAsync(RevCancellationToken token)
//     {
//         await RevResManager.LoadAsync<GameObject>("Boss", token);   // 加载完才允许交割
//     }
//
//   状态机 await 完双方才交割（详见 RevHeavyFSM 的文件头注释）；token 在本次切换被取代/取消时 Cancel。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 重量级状态基类：所有回调默认空实现，业务状态只 override 关心的。
    /// </summary>
    /// <typeparam name="TStateEnum">状态枚举</typeparam>
    /// <typeparam name="TOwner">宿主行为接口</typeparam>
    public abstract class RevHeavyFSMState<TStateEnum, TOwner>
        where TStateEnum : struct, Enum
        where TOwner : class, RevIHeavyFSMOwner
    {
        /// <summary>所属状态机（构造时传入）</summary>
        public RevHeavyFSM<TStateEnum, TOwner> Machine { get; }

        /// <summary>宿主对象（原框架的 AIObj）—— 状态通过它读写 AI 的行为与数据</summary>
        protected TOwner Owner => Machine.Owner;

        /// <summary>本状态对应的枚举值</summary>
        public abstract TStateEnum StateType { get; }

        /// <summary>当前是否激活（= 是状态机的当前状态）</summary>
        protected bool IsActive => ReferenceEquals(Machine.CurrentState, this);

        /// <summary>进入本状态多久了（秒，状态机交割时清零）—— 做超时/阶段计时用</summary>
        protected float StateTime => Machine.StateTime;

        protected RevHeavyFSMState(RevHeavyFSM<TStateEnum, TOwner> machine)
        {
            Machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        // ── 生命周期（默认空实现，按需 override）────────────────

        /// <summary>进入状态（交割完成时调用一次）—— 典型用途：播动画、订阅事件、重置计时</summary>
        public virtual void OnEnter() { }

        /// <summary>退出状态（交割开始时调用一次）—— 典型用途：停动画、退订事件、收尾清理</summary>
        public virtual void OnExit() { }

        /// <summary>每帧更新（仅当前激活状态被调）</summary>
        public virtual void OnUpdate(float deltaTime) { }

        /// <summary>固定步长更新（仅当前激活状态被调）—— 移动/寻路等物理逻辑放这里</summary>
        public virtual void OnFixedUpdate(float fixedDeltaTime) { }

        /// <summary>LateUpdate（仅当前激活状态被调）—— 相机跟随、表现纠偏放这里</summary>
        public virtual void OnLateUpdate(float deltaTime) { }

        // ── 事务式异步准备（吸收王者④；默认直接完成 = 同步切换）──

        /// <summary>
        /// 退出前的准备（默认直接完成）：await 完成之前，状态机不会交割、本状态仍是激活状态。
        /// <para>用于"等死亡动画播完""等特效回收完"这类必须等等再走的场合。</para>
        /// </summary>
        /// <param name="token">取消令牌：本次切换被更新的请求取代 / 被主动取消时会 Cancel</param>
        public virtual RevTask PrepareExitAsync(RevCancellationToken token) => RevTask.Completed;

        /// <summary>
        /// 进入前的准备（默认直接完成）：await 完成之前，状态机不会交割。
        /// <para>用于"等资源加载好""等前摇动作就绪"这类必须等等再进的场合。</para>
        /// </summary>
        /// <param name="token">取消令牌：本次切换被更新的请求取代 / 被主动取消时会 Cancel</param>
        public virtual RevTask PrepareEnterAsync(RevCancellationToken token) => RevTask.Completed;

        // ── 便捷方法 ──────────────────────────────────────────

        /// <summary>请求切换状态（同步、立即交割，等价于 Machine.ChangeState）</summary>
        protected bool ChangeTo(TStateEnum target, string reason = "") => Machine.ChangeState(target, reason);

        /// <summary>
        /// 请求异步切换（事务式）：await 双方 Prepare 完成后才交割，等价于 Machine.ChangeStateAsync。
        /// <code>protected override void OnUpdate(float dt) { ChangeToAsync(BossStateType.CastSkill, "进入射程").Forget(); }</code>
        /// </summary>
        protected RevTask ChangeToAsync(TStateEnum target, string reason = "") => Machine.ChangeStateAsync(target, reason);

        /// <summary>能否切到指定状态（已注册 且 不是当前状态）</summary>
        protected bool CanChangeTo(TStateEnum target)
        {
            return Machine.HasState(target) && !EqualityComparer<TStateEnum>.Default.Equals(StateType, target);
        }

        /// <summary>取另一个状态的实例（跨状态读数据用，如读"巡逻状态"的路径点）</summary>
        protected TState GetState<TState>(TStateEnum type) where TState : RevHeavyFSMState<TStateEnum, TOwner>
        {
            return Machine.GetState<TState>(type);
        }
    }
}

// ============================================================
// EnemyAI.cs —— 敌方重量级状态机的“装配与驱动”入口（不含具体AI决策）。
//
// 【它在敌方AI里的位置】
// 一个敌棋/敌方指挥的完整行为由三部分组成：
//   1. 本文件：按枚举注册状态、把固定模拟时间交给 RevHeavyFSM 驱动、负责释放；
//   2. EnemyStates.cs：每个状态“感知 → 切换 → 执行”的真实业务；
//   3. EnemyTactics.cs：选谁打、走哪格、补在哪的评分算法（纯计算，无状态）。
// 上层 BattleService 只负责创建/驱动/回收，不写 AI 决策。
//
// 【为什么每颗敌棋一台机器，而不是全队共用一台】
// 每颗棋都有自己的攻击进度、移动冷却、气眼和死亡状态；共用一台机器会让
// “A 棋接敌”影响“B 棋等待”，状态时间也不再可信。指挥（布阵/增援）与棋子
// 职责不同，另开一台，互不覆盖当前状态。
//
// 【用到的框架概念】
// · RevHeavyFSM：本文件创建的重量级状态机（类型名大写 FSM，文件名保留 Fsm 是框架约定）；
// · RevHeavyFSMState：EnemyStates.cs 里的状态都继承它；
// · RevIHeavyFSMOwner：宿主标记接口，本类不再直接实现，而是由服务适配器实现；
// · IDisposable：换回合/结算/退出时显式 Dispose，避免旧棋子被旧状态机继续驱动。
// ============================================================
using System;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>
    /// 敌方AI的全部状态枚举。棋子与指挥各用一半，靠 EnemyAI 构造时的 commander 参数区分：
    /// 棋子：Hold（等待冷却）/ Attack（有射界）/ Advance（无射界且冷却到）/ Locked（零气）/ Dead（死亡）；
    /// 指挥：Prepare（提交已规划好的布阵）/ Deploy（按冷却战术增援）/ Stopped（停援或战斗结束）。
    /// 用同一个枚举是为了共用一台泛型机器；混用状态会在 Tick 时被明确拒绝，而不是悄悄跑错。
    /// </summary>
    public enum EnemyAIState { Prepare, Hold, Attack, Advance, Locked, Dead, Deploy, Stopped }

    /// <summary>
    /// 一台敌方AI状态机的宿主包装。
    ///
    /// 【新手最容易误解的一点】
    /// 本类**不是** AI 业务本身。它不计算“打谁、走哪”，也不判断“该攻击还是该等待”；
    /// 那些在 EnemyStates.cs 的 OnUpdate 里。这里只做三件事：
    /// 装配状态 → 每步把时间交给框架 → 用完释放。
    ///
    /// 【为什么有两个 Tick】
    /// · Tick(float)：正常驱动。当前状态自己感知并切换，这是战斗中的唯一入口；
    /// · Tick(state, ...)：外部强制指定状态，只用于“死亡”“提交布阵”这类事实已确定、
    ///   不需要再感知的场合（服务在真实扣血后、布阵预检后调用）。它仍然只切一次，
    ///   不会让状态在 OnEnter 里再切换（框架明确禁止重入）。
    ///
    /// 【生命周期】
    /// 由 BattleService 持有：创建敌棋时 new，换回合/结算/战斗域退出时 Dispose。
    /// Dispose 后机器和宿主引用都失效，再次 Tick 会抛 ObjectDisposedException，
    /// 而不是静默复用旧棋子——这是防止“上一回合的棋在大厅里继续动”的最后一道闸。
    /// </summary>
    public sealed class EnemyAI : IDisposable
    {
        /// <summary>
        /// 状态机宿主（提供棋盘、当前棋子、原子动作）。Dispose 时置空，便于尽早释放引用。
        /// 声明成接口而不是具体服务，是为了让 AI 目录不反过来依赖 Services 目录。
        /// </summary>
        private IEnemyAIOwner _owner;
        /// <summary>框架的重量级状态机：负责注册、切换、状态时长与切换历史。</summary>
        private readonly RevHeavyFSM<EnemyAIState, IEnemyAIOwner> _machine;
        /// <summary>true = 敌方指挥（布阵/增援），false = 单颗棋子；决定注册哪一组状态。</summary>
        private readonly bool _commander;
        /// <summary>是否已释放；防止重复 Dispose 和释放后误用。</summary>
        private bool _disposed;
        /// <summary>当前状态枚举，供 HUD/GM 只读观察（不是让界面改 AI 的手段）。</summary>
        public EnemyAIState State => _machine.CurrentStateType;
        /// <summary>当前状态已持续的模拟秒数；框架在每次切换时清零，可用于调试节奏。</summary>
        public float StateTime => _machine.StateTime;
        /// <summary>切换历史（含原因），排查“AI 为什么突然不动了”时先看这里。</summary>
        public RevHeavyFSMTransition<EnemyAIState>[] History => _machine.GetHistory();

        /// <summary>
        /// 创建一台机器并按角色注册状态。
        /// 参数 owner 由调用方提供，机器本身不 new 服务、不查全局容器，保证 AI 目录可离线测试。
        /// </summary>
        /// <param name="commander">true 表示敌方指挥，只允许布阵/增援/停止三种状态。</param>
        /// <param name="owner">状态读取数据与提交原子动作的宿主适配器。</param>
        public EnemyAI(bool commander, IEnemyAIOwner owner)
        {
            _commander = commander;
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _machine = new RevHeavyFSM<EnemyAIState, IEnemyAIOwner>(_owner);
            // 显式注册而非反射：IL2CPP 能看到所有状态类型，也没有 Activator 的隐藏开销。
            // 只注册本角色需要的状态，误切到另一半会在 ChangeState 时直接失败，而不是跑进空状态。
            if (commander)
            {
                _machine.AddState(EnemyAIState.Prepare, new EnemyPrepareState(_machine));
                _machine.AddState(EnemyAIState.Deploy, new EnemyDeployState(_machine));
            }
            else
            {
                _machine.AddState(EnemyAIState.Hold, new EnemyHoldState(_machine));
                _machine.AddState(EnemyAIState.Attack, new EnemyAttackState(_machine));
                _machine.AddState(EnemyAIState.Advance, new EnemyAdvanceState(_machine));
                _machine.AddState(EnemyAIState.Locked, new EnemyLockedState(_machine));
                _machine.AddState(EnemyAIState.Dead, new EnemyDeadState(_machine));
            }
            _machine.AddState(EnemyAIState.Stopped, new EnemyStoppedState(_machine));
            // 初始状态：指挥先停着等布阵；棋子先等待，第一步 Update 会自行感知成 Attack/Hold。
            _machine.ChangeState(commander ? EnemyAIState.Stopped : EnemyAIState.Hold, "创建AI");
        }

        /// <summary>
        /// 正常驱动：把本小步的模拟秒数交给当前状态。
        /// 感知、切换、执行都在 EnemyStates.cs 里完成；这里只校验时间和释放状态，
        /// 保证“AI 什么时候动”由状态决定，而不是由调用方每次都猜一个枚举。
        /// </summary>
        /// <param name="seconds">固定模拟小步（通常 BattleSimulationStepSeconds），不是渲染帧时间。</param>
        public void Tick(float seconds)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EnemyAI));
            }
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }
            _machine.UpdateState(seconds);
        }

        /// <summary>
        /// 强制指定状态后再驱动一次，仅用于“事实已确定”的场合（死亡、提交布阵）。
        /// 与 Tick(float) 的区别：不经过状态自身的感知，调用方要为这个判断负责。
        /// 同一状态重复指定时不会再次 OnEnter，只继续 Update，因此不会重置状态时长。
        /// </summary>
        public void Tick(EnemyAIState next, float seconds, string reason)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EnemyAI));
            }
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }
            bool teamState = next == EnemyAIState.Prepare || next == EnemyAIState.Deploy;
            if ((_commander && !teamState && next != EnemyAIState.Stopped) || (!_commander && teamState))
            {
                throw new InvalidOperationException("棋子AI与指挥AI的状态不能混用：" + next);
            }
            if (State == EnemyAIState.Dead)
            {
                return;
            }
            if (State != next && !_machine.ChangeState(next, reason))
            {
                throw new InvalidOperationException("敌方AI状态切换被拒绝：" + next);
            }
            _machine.UpdateState(seconds);
        }

        /// <summary>
        /// 释放状态机与宿主引用。可重复调用（第二次直接返回）。
        /// 为什么用 Dispose 而不是“把机器禁用”：禁用只是不再驱动，状态对象和引用还在，
        /// 旧回合的 AI 仍会占内存，也更容易在误用时继续读到已移除的棋子。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _machine.Dispose();
            _owner = null;
            _disposed = true;
        }
    }
}

// ============================================================
// PlayerAI.cs —— 每颗玩家棋独占的 Revolution 重量级状态机。
// 状态负责感知和执行，服务宿主只提供试算/原子移动/攻击入口。固定步驱动，不创建逐棋计时器。
// 战斗服务创建并在死亡、结算、换轮、退出时释放，状态不持有Unity对象。
// ============================================================
using System;
using RevGame.Core;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>等待、守眼、外围封气、最后破眼、后撤、死亡、停止；锁死玩家仍能守阵攻击。</summary>
    public enum PlayerAIState
    {
        Hold, GuardEye, Surround, BreakEye, Retreat, Dead, Stopped
    }
    /// <summary>状态机宿主契约。计划在独立深快照中产生；提交必须再次走BoardModel.Move并刷新规则。</summary>
    public interface IPlayerAIOwner : RevIHeavyFSMOwner
    {
        Piece Actor
        {
            get;
        }
        GamePhase Phase
        {
            get;
        }
        /// <summary>到独立移动冷却才试算；输出本步落点，false表示继续守阵。</summary>
        bool PlanMove(out BoardPos position, out bool breaksEye);
        void Move(BoardPos position);
        void Attack(float seconds);
    }
    public sealed class PlayerAI : IDisposable
    {
        /// <summary>机器拥有全部状态，Dispose统一释放；历史由框架保留供开发态查询。</summary>
        private readonly RevHeavyFSM<PlayerAIState, IPlayerAIOwner> _machine;
        private bool _disposed;
        public PlayerAIState State => _machine.CurrentStateType;
        public RevHeavyFSMTransition<PlayerAIState>[] History => _machine.GetHistory();

        public PlayerAI(IPlayerAIOwner owner)
        {
            _machine = new RevHeavyFSM<PlayerAIState, IPlayerAIOwner>(owner);
            foreach (PlayerAIState state in Enum.GetValues(typeof(PlayerAIState)))
            {
                _machine.AddState(state, new PlayerState(_machine, state));
            }
            _machine.ChangeState(PlayerAIState.Hold, "玩家棋AI创建");
        }
        /// <summary>传入固定模拟秒数；不能用渲染帧时间额外推进同一颗棋。</summary>
        public void Tick(float seconds)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlayerAI));
            }
            if (seconds < 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }
            _machine.UpdateState(seconds);
        }
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _machine.Dispose();
        }
        /// <summary>共享感知入口防止各状态判断分叉，切换后只执行新状态一次，不在OnEnter/OnExit重入切换。</summary>
        private sealed class PlayerState : RevHeavyFSMState<PlayerAIState, IPlayerAIOwner>
        {
            private readonly PlayerAIState _type;
            public override PlayerAIState StateType => _type;
            public PlayerState(RevHeavyFSM<PlayerAIState, IPlayerAIOwner> machine, PlayerAIState type) : base(machine) { _type = type; }

            public override void OnUpdate(float seconds)
            {
                Piece actor = Owner.Actor;
                BoardPos position = actor.Pos;
                PlayerAIState next;

                if (!actor.IsAlive)
                {
                    next = PlayerAIState.Dead;
                }
                else

                    if (Owner.Phase != GamePhase.Battle)
                    {
                        next = PlayerAIState.Stopped;
                    }
                    else

                        if (actor.IsEyeGarrison || actor.EyeCount > 0)
                        {
                            next = PlayerAIState.GuardEye;
                        }
                        else

                            if (!actor.IsLocked && Owner.PlanMove(out position, out bool breaksEye))
                            {
                                next = actor.Def.IsRanged ? PlayerAIState.Retreat : (breaksEye ? PlayerAIState.BreakEye : PlayerAIState.Surround);
                            }
                            else
                            {
                                next = PlayerAIState.Hold;
                            }
                if (next != StateType)
                {
                    ChangeTo(next, Reason(next));
                }
                ((PlayerState)Machine.CurrentState).Act(seconds, position);
            }
            /// <summary>移动先提交，攻击后查询；刚移入本方眼时，本步就读取新的增益，不等下一帧。</summary>
            private void Act(float seconds, BoardPos position)
            {
                if (_type == PlayerAIState.Dead || _type == PlayerAIState.Stopped)
                {
                    return;
                }
                if (_type == PlayerAIState.Surround || _type == PlayerAIState.BreakEye || _type == PlayerAIState.Retreat)
                {
                    Owner.Move(position);
                }
                Owner.Attack(seconds);
            }
            private static string Reason(PlayerAIState state)
            {
                switch (state)
                {
                    case PlayerAIState.GuardEye:
                        return "守住己方眼位与骨架";
                    case PlayerAIState.Surround:
                        return "外围封气，禁止提前钻敌眼";
                    case PlayerAIState.BreakEye:
                        return "敌群外围已封完，只剩最后一眼";
                    case PlayerAIState.Retreat:
                        return "远程受近身威胁，安全后撤";
                    case PlayerAIState.Dead:
                        return "真实死亡";
                    case PlayerAIState.Stopped:
                        return "离开战斗阶段";
                    default:
                        return "等待移动冷却或原位反击";
                }
            }
        }
    }
}

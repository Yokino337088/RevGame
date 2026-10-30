using System;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>帮手重量级状态机：充能等待、爬行、弹幕、持续激光、停战。状态时钟由固定模拟步驱动。</summary>
    public enum HelperAIState
    {
        Charge, Crawl, Barrage, Beam, Storm, Rest
    }
    /// <summary>状态只读感知，并通过宿主的原子动作提交；不碰UI、占格或历史积分存储。</summary>
    public interface IHelperAIOwner : RevIHeavyFSMOwner
    {
        bool CanFight
        {
            get;
        }
        bool HasTarget
        {
            get;
        }
        bool BeamRunning
        {
            get;
        }
        bool CanBeam
        {
            get;
        }
        bool MoveDue
        {
            get;
        }
        bool ShotDue
        {
            get;
        }
        /// <summary>仅剩少量真实玩家棋子时启动；全灭和结算由CanFight优先阻止。</summary>
        bool StormActive
        {
            get;
        }
        void Crawl();
        void Barrage();
        void Beam(float seconds);
        void Storm(float seconds);
    }
    /// <summary>BattleService拥有唯一机器；换回合Dispose重建，退出后不会留旧目标引用。</summary>
    public sealed class HelperAI : IDisposable
    {
        private readonly RevHeavyFSM<HelperAIState, IHelperAIOwner> _machine;
        public HelperAIState State => _machine.CurrentStateType;
        public HelperAI(IHelperAIOwner owner)
        {
            _machine = new RevHeavyFSM<HelperAIState, IHelperAIOwner>(owner);
            foreach (HelperAIState state in Enum.GetValues(typeof(HelperAIState)))
            {
                _machine.AddState(state, new StateNode(_machine, state));
            }
            _machine.ChangeState(HelperAIState.Charge, "帮手进入本回合");
        }
        public void Tick(float seconds)
        {
            _machine.UpdateState(seconds);
        }
        public void Dispose()
        {
            _machine.Dispose();
        }
        /// <summary>只在OnUpdate切状态；OnEnter/OnExit不重入。每步最多一个动作，避免追帧时瞬间无限发射。</summary>
        private sealed class StateNode : RevHeavyFSMState<HelperAIState, IHelperAIOwner>
        {
            private readonly HelperAIState _kind;
            public override HelperAIState StateType => _kind;
            public StateNode(RevHeavyFSM<HelperAIState, IHelperAIOwner> machine, HelperAIState kind) : base(machine) { _kind = kind; }
            public override void OnUpdate(float seconds)
            {
                HelperAIState next = !Owner.CanFight ? HelperAIState.Rest
                    : !Owner.HasTarget ? HelperAIState.Charge
                    : Owner.StormActive ? HelperAIState.Storm
                    : Owner.BeamRunning || Owner.CanBeam ? HelperAIState.Beam
                    : Owner.MoveDue ? HelperAIState.Crawl
                    : Owner.ShotDue ? HelperAIState.Barrage : HelperAIState.Charge;

                if (next != StateType)
                {
                    ChangeTo(next, "感知目标、积分与动作冷却");
                }
                if (next == HelperAIState.Crawl)
                {
                    Owner.Crawl();
                }
                else

                    if (next == HelperAIState.Barrage)
                    {
                        Owner.Barrage();
                    }
                    else

                        if (next == HelperAIState.Beam)
                        {
                            Owner.Beam(seconds);
                        }
                        else

                            if (next == HelperAIState.Storm)
                            {
                                Owner.Storm(seconds);
                            }
            }
        }
    }
}

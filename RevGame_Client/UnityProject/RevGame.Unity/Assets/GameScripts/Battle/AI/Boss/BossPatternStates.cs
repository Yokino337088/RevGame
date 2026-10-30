// 新增九个独立技能状态；共用原有蓄力/多段/冷却/收招生命周期，不在状态外另开计时器。
using RevGame.Core;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>格子型技能的共同执行：在命中时重新查询固定预警区域，移动出去可以躲避。</summary>
    internal abstract class BossPatternState : BossSkillState
    {
        protected BossPatternState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        protected override void Hit(int index)
        {
            // 使用施法快照；即使外部GM挪动主将，命中也必须和玩家已经看到的预警一致。
            BossPatternGeometry.Collect(Owner.Board, Info.Origin, Info.OriginSize, Plan, Def.Radius, index, Victims);
            DamageAll(Victims);
        }
    }
    /// <summary>流星依次落在三个预先锁定的点，身体与范围相交才受伤。</summary>
    internal sealed class BossMeteorShowerState : BossPatternState
    {
        public BossMeteorShowerState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.MeteorShower;
        protected override BossSkillKind Kind => BossSkillKind.MeteorShower;
    }
    /// <summary>从主将中心发出横纵两条贯穿棋盘的光束。</summary>
    internal sealed class BossCrossLaserState : BossPatternState
    {
        public BossCrossLaserState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.CrossLaser;
        protected override BossSkillKind Kind => BossSkillKind.CrossLaser;
    }
    /// <summary>冰霜方环逐段扩散，每一段只命中该圈，不把整片区域重复扣血。</summary>
    internal sealed class BossFrostNovaState : BossPatternState
    {
        public BossFrostNovaState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.FrostNova;
        protected override BossSkillKind Kind => BossSkillKind.FrostNova;
    }
    /// <summary>雷电按顺序跳向锁定的格子，空格不造成伤害。</summary>
    internal sealed class BossChainLightningState : BossPatternState
    {
        public BossChainLightningState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.ChainLightning;
        protected override BossSkillKind Kind => BossSkillKind.ChainLightning;
    }
    /// <summary>固定毒池多段伤害，离开毒池就不再受后续伤害。</summary>
    internal sealed class BossPoisonMarshState : BossPatternState
    {
        public BossPoisonMarshState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.PoisonMarsh;
        protected override BossSkillKind Kind => BossSkillKind.PoisonMarsh;
    }
    /// <summary>一整纵列火墙，持续喷发多次。</summary>
    internal sealed class BossFlameWallState : BossPatternState
    {
        public BossFlameWallState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.FlameWall;
        protected override BossSkillKind Kind => BossSkillKind.FlameWall;
    }
    /// <summary>一整横排冰刺穿透，不会被第一颗受击棋阻挡。</summary>
    internal sealed class BossIceLanceState : BossPatternState
    {
        public BossIceLanceState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.IceLance;
        protected override BossSkillKind Kind => BossSkillKind.IceLance;
    }
    /// <summary>引力伤害区域逐段收缩，外围棋子可以提前脱离。</summary>
    internal sealed class BossGravityWellState : BossPatternState
    {
        public BossGravityWellState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.GravityWell;
        protected override BossSkillKind Kind => BossSkillKind.GravityWell;
    }
    /// <summary>先对角符文再中心爆发，两个阶段的范围在预警中同时标明。</summary>
    internal sealed class BossDoomSigilState : BossPatternState
    {
        public BossDoomSigilState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.DoomSigil;
        protected override BossSkillKind Kind => BossSkillKind.DoomSigil;
    }
}

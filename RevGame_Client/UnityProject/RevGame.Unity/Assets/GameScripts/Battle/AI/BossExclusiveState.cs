// 章节专属状态仍使用既有FSM的蓄力、多段、收招和冷却契约，不另开业务计时器。
using RevGame.Core;
using Revolution;
namespace RevGame.Battle
{
    /// <summary>十二个技能各自注册独立状态实例；几何、持续效果与资源表现分开维护。</summary>
    internal sealed class BossExclusiveState : BossSkillState
    {
        private readonly BossSkillKind _kind;
        public BossExclusiveState(RevHeavyFSM<BossAIState, IBossAIOwner> machine, BossSkillKind kind) : base(machine) { _kind = kind; }
        public override BossAIState StateType => BossAI.StateOf(_kind);
        protected override BossSkillKind Kind => _kind;
        protected override void OnPlanned()
        {
            if (_kind == BossSkillKind.GlacialAegis) { Owner.Boss.BossInvulnerable = true; }
        }
        protected override void Hit(int index)
        {
            // 爆炸之前先撤无敌；被GM打断、死亡或离场时OnExit同样兜底撤销。
            if (_kind == BossSkillKind.GlacialAegis) { Owner.Boss.BossInvulnerable = false; }
            Owner.ExclusiveHit(Info.WithIndex(index));
        }
        public override void OnExit()
        {
            if (_kind == BossSkillKind.GlacialAegis) { Owner.Boss.BossInvulnerable = false; }
            base.OnExit();
        }
    }
}

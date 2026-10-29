// 每个新增招式持有独立FSM状态实例，复用原有预警、多段、冷却和离场契约。
using RevGame.Core;
using RevGame.Data;
using Revolution;
namespace RevGame.Battle
{
    /// <summary>状态只负责时间推进；命中、控制、占格更新仍由战斗服务的唯一入口提交。</summary>
    internal sealed class BossExpandedState : BossSkillState
    {
        /// <summary>创建后不变的技能身份。状态由当前BossAI拥有，释放机器时一起释放。</summary>
        private readonly BossSkillKind _kind;
        public BossExpandedState(RevHeavyFSM<BossAIState, IBossAIOwner> machine, BossSkillKind kind) : base(machine)
        {
            _kind = kind;
        }
        public override BossAIState StateType => BossAI.StateOf(_kind);
        protected override BossSkillKind Kind => _kind;
        protected override void Hit(int index)
        {
            if (BossSkillCatalog.IsSpecial(_kind)) { Owner.ExclusiveHit(Info.WithIndex(index)); }
            else
            {
                BossExpandedGeometry.Collect(Owner.Board,Info.WithIndex(index),Victims);
                DamageAll(Victims);
            }
        }
    }
}

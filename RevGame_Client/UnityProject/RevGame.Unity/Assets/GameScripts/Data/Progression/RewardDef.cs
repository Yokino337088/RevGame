// ============================================================
// RewardDef.cs —— 三选一里已经抽好的奖励。
// 随机内容在弹窗出现前确定，点击时只消费这份结果，不能在点击后重新抽另一个奖励。
// 标题、说明、强化量都来自 Excel（配置表/关卡与奖励配置.xlsx 的 RewardConfig / RewardBoostConfig）。
// ============================================================
using RevGame.Core;

namespace RevGame.Data
{
    public enum RewardKind
    {
        TeamBoost,
        DuplicateCard,
        NewPiece,
        EyeStone
    }
    public enum BoostKind
    {
        Attack,
        Speed,
        Liberty
    }
    /// <summary>奖励抽取与强化量的读取入口（权重、解锁条件、永久加成量都在 Excel）。</summary>
    public static class RewardCatalog
    {
        internal static RewardRule RuleOf(RewardKind kind)
        {
            return GameConfig.Current.Rewards[kind];
        }
        /// <summary>全队强化的永久加成量：Attack/Speed 为比例（0.1=10%），Liberty 为气数。</summary>
        public static float BoostAmount(BoostKind boost)
        {
            return GameConfig.Current.Boosts[boost].Amount;
        }
        /// <summary>抽取权重：基础权重，人数落后时再加雪中送炭的额外权重。</summary>
        public static int WeightOf(RewardKind kind, bool outnumbered)
        {
            RewardRule rule = RuleOf(kind);
            return rule.Weight + (outnumbered ? rule.OutnumberedBonus : 0);
        }
        /// <summary>本次运行已开局数达到这个值之后，该奖励才会出现（0=一直可出现）。</summary>
        public static int UnlockAfterCampaigns(RewardKind kind)
        {
            return RuleOf(kind).UnlockAfterCampaigns;
        }
    }
    public sealed class RewardDef
    {
        public RewardKind Kind
        {
            get;
        }
        public PieceKind PieceKind
        {
            get;
        }
        public BoostKind Boost
        {
            get;
        }
        public string Title
        {
            get;
        }
        public string Description
        {
            get;
        }
        public RewardDef(RewardKind kind, PieceKind pieceKind, BoostKind boost)
        {
            Kind = kind;
            PieceKind = pieceKind;
            Boost = boost;
            RewardRule rule = RewardCatalog.RuleOf(kind);
            Title = rule.Title;

            if (kind == RewardKind.TeamBoost)
            {
                // 全队强化的说明取自强化表，{Amount} 已在装配阶段渲染好。
                Description = GameConfig.Current.Boosts[boost].Detail;
            }
            else
            {
                // 带 {PieceName} 的模板在这里才有具体棋子；装配阶段已校验模板语法，这里占位符缺失会直接抛出。
                string pieceName = PieceCatalog.Get(pieceKind, StarTier.One).DisplayName;
                Description = ConfigText.RenderOrThrow(rule.Detail, "RewardConfig[" + kind + "].Detail", token => token == "PieceName" ? pieceName : null);
            }
        }
    }
}

// ============================================================
// RewardDef.cs —— 三选一里已经抽好的奖励。
// 随机内容在弹窗出现前确定，点击时只消费这份结果，不能在点击后重新抽另一个奖励。
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

    public sealed class RewardDef
    {
        public RewardKind Kind { get; }
        public PieceKind PieceKind { get; }
        public BoostKind Boost { get; }
        public string Title { get; }
        public string Description { get; }

        public RewardDef(RewardKind kind, PieceKind pieceKind, BoostKind boost)
        {
            Kind = kind;
            PieceKind = pieceKind;
            Boost = boost;
            switch (kind)
            {
                case RewardKind.TeamBoost:
                    Title = "全队强化";
                    Description = boost == BoostKind.Attack ? "攻击永久 +10%" : (boost == BoostKind.Speed ? "攻速永久 +10%" : "全队永久 +1 气");
                    break;
                case RewardKind.DuplicateCard:
                    Title = "同名牌";
                    Description = "下章额外获得「" + PieceCatalog.Get(pieceKind, StarTier.One).DisplayName + "」";
                    break;
                case RewardKind.NewPiece:
                    Title = "新棋子";
                    Description = "解锁「" + PieceCatalog.Get(pieceKind, StarTier.One).DisplayName + "」加入手牌池";
                    break;
                default:
                    Title = "眼位石";
                    Description = "保留一颗眼位石；准备阶段点金色格子补成一个眼";
                    break;
            }
        }
    }
}

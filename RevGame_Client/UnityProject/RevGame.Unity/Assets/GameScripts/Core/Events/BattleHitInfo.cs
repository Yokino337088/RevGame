// ============================================================
// BattleHitInfo.cs —— 一次真实伤害的不可变表现快照。
// 为什么不让界面算血量差：连击可能在同一步扣几次血，死亡后目标还会回池；快照才能保留每一击。
// 只保存数值和棋盘坐标，不引用 Unity 对象，因此独立规则测试也能核对通知内容。
// ============================================================
namespace RevGame.Core
{
    public readonly struct BattleHitInfo
    {
        public readonly int TargetId;
        public readonly BoardPos Position;
        public readonly int TargetSize;
        public readonly GameSide TargetSide;
        public readonly int Damage;
        public readonly bool HasAttacker;
        public readonly BoardPos Source;

        public BattleHitInfo(int targetId, BoardPos position, int targetSize, GameSide targetSide, int damage, bool hasAttacker, BoardPos source)
        {
            TargetId = targetId;
            Position = position;
            TargetSize = targetSize;
            TargetSide = targetSide;
            Damage = damage;
            HasAttacker = hasAttacker;
            Source = source;
        }
    }
}

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
        /// <summary>
        /// 攻击者在本局的唯一编号；0表示环境伤害或旧的表现测试没有提供来源。
        /// 不能用坐标代替编号：玩家可以换位，而公平弹道队列仍需认出同一颗棋子，防止早落棋长期挤掉新棋。
        /// </summary>
        public readonly int SourceId;
        /// <summary>
        /// 是否来自 Boss 技能。技能有自己的爆炸/激光特效，棋盘只显示伤害数字，
        /// 不再额外画一条普通弹道——否则一次范围技能会同时飞出十几根黄色子弹，画面混乱。
        /// </summary>
        public readonly bool FromSkill;
        /// <summary>扣血当刻的职业、占格尺寸与远程标记；死亡或换位后仍使用这一份快照。</summary>
        public readonly PieceKind SourceKind;
        public readonly int SourceSize;
        public readonly bool SourceIsRanged;

        public BattleHitInfo(int targetId, BoardPos position, int targetSize, GameSide targetSide, int damage, bool hasAttacker, BoardPos source, int sourceId = 0, bool fromSkill = false, PieceKind sourceKind = PieceKind.Swordman, int sourceSize = 1, bool sourceIsRanged = false)
        {
            TargetId = targetId;
            Position = position;
            TargetSize = targetSize;
            TargetSide = targetSide;
            Damage = damage;
            HasAttacker = hasAttacker;
            Source = source;
            SourceId = sourceId;
            FromSkill = fromSkill;
            SourceKind = sourceKind;
            SourceSize = sourceSize;
            SourceIsRanged = sourceIsRanged;
        }
    }
}

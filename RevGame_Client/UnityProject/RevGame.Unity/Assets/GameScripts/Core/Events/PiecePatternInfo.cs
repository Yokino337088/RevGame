namespace RevGame.Core
{
    /// <summary>一次攻击形状的不可变表现事实；仅保存值，不持有可能死亡或回收的棋子引用。</summary>
    public readonly struct PiecePatternInfo
    {
        /// <summary>来源编号与棋种，供表现预算按来源公平调度。</summary>
        public readonly int SourceId;
        public readonly PieceKind Kind;
        /// <summary>出手与爆心棋盘坐标，范围视觉必须使用结算时的位置。</summary>
        public readonly BoardPos Source, Center;
        public readonly GameSide Side;
        /// <summary>配置的格子半径；不以粒子尺寸反推规则范围。</summary>
        public readonly int Radius;

        public PiecePatternInfo(int sourceId, PieceKind kind, BoardPos source, BoardPos center, GameSide side, int radius)
        {
            SourceId = sourceId;
            Kind = kind;
            Source = source;
            Center = center;
            Side = side;
            Radius = radius;
        }
    }
}

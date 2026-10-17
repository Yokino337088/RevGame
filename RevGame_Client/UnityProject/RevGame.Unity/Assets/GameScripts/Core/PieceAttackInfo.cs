// ============================================================
// PieceAttackInfo.cs —— 每次真实攻击的不可变快照。
// 只通知谁在什么位置发动了哪种攻击；文字、颜色和动画由 Excel 配置及 View 决定。
// ============================================================
namespace RevGame.Core
{
    public readonly struct PieceAttackInfo
    {
        /// <summary>攻击者唯一编号；同一棋子连击时可用它控制口号密度。</summary>
        public readonly int PieceId;
        /// <summary>攻击棋种，用于查找 Excel 的 Attack_棋种编号。</summary>
        public readonly PieceKind Kind;
        /// <summary>攻击当刻身体左下角；后续移动、死亡不改变这份快照。</summary>
        public readonly BoardPos Position;
        /// <summary>身体边长，大棋的口号应显示在整个身体中心，而非左下格。</summary>
        public readonly int Size;
        /// <summary>所属阵营；表现可按阵营区分攻击颜色与统计。</summary>
        public readonly GameSide Side;
        /// <summary>只保存值，不保存棋子对象，避免棋子已死时动画回调读到失效状态。</summary>
        public PieceAttackInfo(int pieceId, PieceKind kind, BoardPos position, int size, GameSide side)
        {
            PieceId = pieceId;
            Kind = kind;
            Position = position;
            Size = size;
            Side = side;
        }
    }
}

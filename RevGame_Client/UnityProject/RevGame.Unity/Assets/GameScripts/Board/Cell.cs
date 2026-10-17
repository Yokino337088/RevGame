// ============================================================
// Cell.cs
// 一格只保存占位与眼标记；眼属于哪一方必须一同记录，才可阻止对手落进自己的眼。
// ============================================================
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Board
{
    /// <summary>
    /// 棋盘上的一格。**只存"这一格上有什么"，不存规则** —— 规则在 <c>GoRules</c> 里，
    /// 这样格子就能被当成纯数据结构来读写，不会变成"什么都干"的万能类。
    /// </summary>
    public sealed class Cell
    {
        /// <summary>格子坐标（不可变）。</summary>
        public BoardPos Pos { get; }
        /// <summary>这一格上的棋子；空格为 null。</summary>
        public Piece Occupant { get; internal set; }
        /// <summary>是否是星位（9×9 棋盘有 5 个星位，纯美术用途）。</summary>
        public bool IsStarPoint { get; }
        /// <summary>是否被标记成"眼"（供 UI 显示金色眼标记；由规则层刷新）。</summary>
        public bool IsEyeMarked { get; internal set; }
        /// <summary>眼属于哪方。落子不能填进对方的眼，避免只显示眼却没有规则保护。</summary>
        public GameSide? EyeSide { get; internal set; }

        /// <summary>是否为空。</summary>
        public bool IsEmpty
        {
            get
            {
                return Occupant == null;
            }
        }

        public Cell(BoardPos pos, bool isStarPoint)
        {
            Pos = pos;
            IsStarPoint = isStarPoint;
        }

        public override string ToString()
        {
            return Pos + (IsEmpty ? " 空" : " " + Occupant.Def.DisplayName);
        }
    }
}

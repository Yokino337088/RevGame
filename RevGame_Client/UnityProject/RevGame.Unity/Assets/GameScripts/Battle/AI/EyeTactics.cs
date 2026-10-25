// ============================================================
// EyeTactics.cs —— 自动移动和补子的共同眼位门槛。
// 先判硬规则，再打分；高分不能覆盖“先封外围，最后才破眼”的限制。
// 只读已经刷新过的棋盘，不移动实战棋、不分配全局可变试盘。
// ============================================================
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    public static class EyeTactics
    {
        /// <summary>检查整个身体。只有近战能进入敌空眼，且相邻每个敌群都必须只剩这一口实际空格气和一个眼。</summary>
        public static bool CanEnter(BoardModel board, BoardPos pos, int size, GameSide side, bool ranged)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Cell cell = board.GetCell(new BoardPos(pos.X + x, pos.Y + y));
                    if (cell == null) { return false; }
                    if (!cell.EyeSide.HasValue || cell.EyeSide == side) { continue; }
                    if (ranged || !cell.IsEmpty) { return false; }
                    foreach (BoardPos neighbor in cell.Pos.GetOrthogonalNeighbors())
                    {
                        Piece guard = board.GetPiece(neighbor);
                        if (guard != null && (guard.Side == side || guard.OpenLiberties != 1 || guard.EyeCount != 1))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>一格眼只能容纳一格棋；返回稳定扫描顺序中的首个本方空眼，没有则明确返回false。</summary>
        public static bool TryFindOwnEye(BoardModel board, GameSide side, int size, out BoardPos position)
        {
            position = new BoardPos(-1, -1);
            if (size != 1) { return false; }
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    Cell cell = board.GetCell(new BoardPos(x, y));
                    if (cell.IsEmpty && cell.EyeSide == side)
                    {
                        position = cell.Pos;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}

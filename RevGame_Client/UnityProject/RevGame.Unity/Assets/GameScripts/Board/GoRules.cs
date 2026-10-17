// ============================================================
// GoRules.cs —— 连通块、气、眼的统一计算，兼容 1×1 / 2×2 / 3×3。
// 一次刷新先找眼，再找连通块。所有显示字段由此覆盖，避免破眼后仍保留「活棋」状态。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Board
{
    public static class GoRules
    {
        public static List<List<Piece>> BuildGroups(BoardModel board)
        {
            var groups = new List<List<Piece>>();
            var visited = new HashSet<Piece>();
            foreach (Piece piece in board.GetAllPieces())
            {
                if (!visited.Add(piece))
                {
                    continue;
                }

                var group = new List<Piece>();
                var queue = new Queue<Piece>();
                queue.Enqueue(piece);
                while (queue.Count > 0)
                {
                    Piece current = queue.Dequeue();
                    group.Add(current);
                    foreach (BoardPos next in BoardModel.Boundary(current))
                    {
                        Piece neighbor = board.GetPiece(next);
                        if (neighbor != null && neighbor.Side == current.Side && visited.Add(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                groups.Add(group);
            }

            return groups;
        }

        /// <summary>气是空格的并集，不是每颗棋子气数相加，共用的一口气只能算一次。</summary>
        public static int CountLiberties(BoardModel board, List<Piece> group)
        {
            var empty = new HashSet<BoardPos>();
            foreach (Piece piece in group)
            {
                foreach (BoardPos pos in BoardModel.Boundary(piece))
                {
                    Cell cell = board.GetCell(pos);
                    if (cell != null && cell.IsEmpty)
                    {
                        empty.Add(pos);
                    }
                }
            }

            return empty.Count;
        }

        /// <summary>v18：只检查棋盘内邻居；角上两颗、边上三颗也能围成眼。</summary>
        public static bool IsEye(BoardModel board, BoardPos pos, GameSide side)
        {
            if (!board.CanPlace(pos))
            {
                return false;
            }

            int neighbors = 0;
            foreach (BoardPos next in pos.GetOrthogonalNeighbors())
            {
                Cell cell = board.GetCell(next);
                if (cell == null)
                {
                    continue;
                }

                neighbors++;
                if (cell.IsEmpty || cell.Occupant.Side != side)
                {
                    return false;
                }
            }

            return neighbors >= 2;
        }

        private static int CountEyes(BoardModel board, List<Piece> group)
        {
            var eyes = new HashSet<BoardPos>();
            foreach (Piece piece in group)
            {
                foreach (BoardPos pos in BoardModel.Boundary(piece))
                {
                    if (IsEye(board, pos, piece.Side))
                    {
                        eyes.Add(pos);
                    }
                }
            }

            return eyes.Count;
        }

        public static bool HasTwoEyes(BoardModel board, List<Piece> group)
        {
            return CountEyes(board, group) >= 2;
        }

        public static void Evaluate(BoardModel board)
        {
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    Cell cell = board.GetCell(pos);
                    cell.EyeSide = IsEye(board, pos, GameSide.Player) ? GameSide.Player : (IsEye(board, pos, GameSide.Enemy) ? (GameSide? )GameSide.Enemy : null);
                    cell.IsEyeMarked = cell.EyeSide.HasValue;
                }
            }

            foreach (List<Piece> group in BuildGroups(board))
            {
                int liberties = CountLiberties(board, group);
                int eyes = CountEyes(board, group);
                int cells = 0;
                int bonus = int.MinValue;
                foreach (Piece piece in group)
                {
                    cells += piece.Size * piece.Size;
                    // 加气作用于整块而非每个占格，避免大棋把 +1 气重复计算九次。
                    bonus = System.Math.Max(bonus, piece.BonusLiberties);
                }

                liberties = System.Math.Max(0, liberties + bonus);
                GroupState state = eyes > 0 ? GroupState.Normal : (liberties == 0 ? GroupState.Locked : (liberties == 1 ? GroupState.Atari : GroupState.Normal));
                foreach (Piece piece in group)
                {
                    piece.Liberties = liberties;
                    piece.GoState = state;
                    piece.EyeCount = eyes;
                    piece.ConnectedCells = cells;
                }
            }
        }
    }
}

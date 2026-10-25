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
        /// <summary>
        /// 一张棋盘的计算草稿。Group同时充当广度优先搜索队列：读取下标向前走，新邻居追加末尾。
        /// Empty用格子编号去重，同一口气即使挨着三颗棋子也只计一次；每个棋群开始前清空。
        /// 容量由棋盘格数决定，重复Evaluate复用内存，不为每个棋群new队列/集合。
        /// </summary>
        internal sealed class Workspace
        {
            internal readonly List<Piece> Group;
            internal readonly HashSet<Piece> Visited = new HashSet<Piece>();
            internal readonly bool[] Empty;
            internal Workspace(int capacity)
            {
                Group = new List<Piece>(capacity);
                Empty = new bool[capacity];
            }
        }
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

        /// <summary>按当前完整占格判眼，不依赖落子顺序；角上两邻、边上三邻也有效。中心可为空或本方单格棋，大棋内部不算眼。</summary>
        public static bool IsEye(BoardModel board, BoardPos pos, GameSide side)
        {
            Cell center = board.GetCell(pos);
            // 不能要求中心棋曾经“落入已成型空眼”：中心先落、外围最后补齐也必须在本次全盘刷新获得驻眼状态。
            if (center == null || (!center.IsEmpty && (center.Occupant.Side != side || center.Occupant.Size != 1)))
            {
                return false;
            }

            int neighbors = 0;
            for (int direction = 0; direction < 4; direction++)
            {
                BoardPos next = Neighbor(pos, direction);
                Cell cell = board.GetCell(next);
                if (cell == null)
                {
                    continue;
                }

                neighbors++;
                if (cell.IsEmpty || cell.Occupant.Side != side || cell.Occupant == center.Occupant)
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

            Workspace scratch = board.RuleWorkspace;
            scratch.Visited.Clear();
            foreach (Piece root in board.Pieces)
            {
                if (!scratch.Visited.Add(root))
                {
                    continue;
                }
                scratch.Group.Clear();
                scratch.Group.Add(root);
                System.Array.Clear(scratch.Empty, 0, scratch.Empty.Length);
                int liberties = 0;
                int eyes = 0;
                int cells = 0;
                int bonus = int.MinValue;
                // ★ 只走棋子外边界，不走大棋身体内部。否则2×2的内部邻格会被误当作连接或气。
                for (int head = 0; head < scratch.Group.Count; head++)
                {
                    Piece piece = scratch.Group[head];
                    piece.IsEyeGarrison = false;
                    piece.HasEyeGuardAura = false;
                    // 驻眼仍是这组棋的眼，但不是空格气。按身体格计数且只处理一次，不能被四周邻棋重复计眼。
                    for (int y = 0; y < piece.Size; y++)
                    {
                        for (int x = 0; x < piece.Size; x++)
                        {
                            Cell own = board.GetCell(new BoardPos(piece.Pos.X + x, piece.Pos.Y + y));
                            if (own.EyeSide == piece.Side)
                            {
                                piece.IsEyeGarrison = true;
                                eyes++;
                            }
                        }
                    }
                    cells += piece.Size * piece.Size;
                    bonus = System.Math.Max(bonus, piece.BonusLiberties);
                    for (int edge = 0; edge < piece.Size; edge++)
                    {
                        for (int direction = 0; direction < 4; direction++)
                        {
                            BoardPos pos = BoundaryAt(piece, edge, direction);
                            Cell cell = board.GetCell(pos);
                            if (cell == null)
                            {
                                continue;
                            }
                            // 读首遍已经计算好的眼标记。只看身体外边界，覆盖多格或邻接多个眼也只设一个布尔值。
                            if (piece.Side == GameSide.Player && cell.EyeSide == GameSide.Player)
                            {
                                piece.HasEyeGuardAura = true;
                            }
                            if (cell.IsEmpty)
                            {
                                int index = pos.Y * board.Size + pos.X;
                                if (scratch.Empty[index])
                                {
                                    continue;
                                }
                                scratch.Empty[index] = true;
                                liberties++;
                                // 全盘第一遍已经判过眼，此处直接读结果，不对同一空格重复算四邻。
                                if (cell.EyeSide == root.Side)
                                {
                                    eyes++;
                                }
                            }
                            else if (cell.Occupant.Side == root.Side && scratch.Visited.Add(cell.Occupant))
                            {
                                scratch.Group.Add(cell.Occupant);
                            }
                        }
                    }
                }

                int openLiberties = liberties;
                liberties = System.Math.Max(0, liberties + bonus);
                GroupState state = eyes > 0 ? GroupState.Normal : (liberties == 0 ? GroupState.Locked : (liberties == 1 ? GroupState.Atari : GroupState.Normal));
                foreach (Piece piece in scratch.Group)
                {
                    piece.Liberties = liberties;
                    piece.OpenLiberties = openLiberties;
                    piece.GoState = state;
                    piece.EyeCount = eyes;
                    piece.ConnectedCells = cells;
                }
            }
            // 不让草稿区在棋子死亡后额外持有旧对象；Clear保留容量，下一次仍可复用。
            scratch.Group.Clear();
            scratch.Visited.Clear();
        }

        /// <summary>四邻查询不使用yield迭代器，避免整盘判眼时创建大量短命枚举对象。</summary>
        private static BoardPos Neighbor(BoardPos pos, int direction)
        {
            if (direction == 0)
            {
                return new BoardPos(pos.X - 1, pos.Y);
            }
            if (direction == 1)
            {
                return new BoardPos(pos.X + 1, pos.Y);
            }
            if (direction == 2)
            {
                return new BoardPos(pos.X, pos.Y - 1);
            }
            return new BoardPos(pos.X, pos.Y + 1);
        }

        /// <summary>按“边上的第edge格、四个方向”取得外边界；只创建值类型坐标。</summary>
        private static BoardPos BoundaryAt(Piece piece, int edge, int direction)
        {
            if (direction == 0)
            {
                return new BoardPos(piece.Pos.X - 1, piece.Pos.Y + edge);
            }
            if (direction == 1)
            {
                return new BoardPos(piece.Pos.X + piece.Size, piece.Pos.Y + edge);
            }
            if (direction == 2)
            {
                return new BoardPos(piece.Pos.X + edge, piece.Pos.Y - 1);
            }
            return new BoardPos(piece.Pos.X + edge, piece.Pos.Y + piece.Size);
        }
    }
}

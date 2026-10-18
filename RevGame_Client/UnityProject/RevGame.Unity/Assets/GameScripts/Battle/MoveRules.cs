// ============================================================
// MoveRules.cs
// 只计算合法目标，不搬动棋子；所有移动最终由棋盘整块校验，避免大棋越界或闯入敌眼。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>
    /// 走法与攻击判定（策划案 §07-D：借象棋，把"自动移动"从玄学变成可预判）。
    ///
    /// 四种走法：单步（Step）、滑步（Slide，车）、马步（Knight，有蹩马腿）、炮（Cannon，隔一子吃）。
    /// 为什么用枚举 + switch 而不是给每种棋子挂一个策略类：
    /// 这里只有 4 种、且都不带状态，**上策略模式是过度设计**；等真的出现第 5 种带特殊规则的走法再说。
    ///
    /// 约定：所有方法都只读棋盘，不改任何状态（想移动请调 <c>BoardModel.Move</c>）。
    /// </summary>
    public static class MoveRules
    {
        private static readonly int[] Dx =
        {
            1,
            -1,
            0,
            0
        };
        private static readonly int[] Dy =
        {
            0,
            0,
            1,
            -1
        };
        /// <summary>马步的 8 个落点（dx, dy）与对应的"蹩马腿"检查点。</summary>
        private static readonly int[, ] KnightMoves =
        {
            {
                1,
                2
            },
            {
                2,
                1
            },
            {
                2,
                -1
            },
            {
                1,
                -2
            },
            {
                -1,
                -2
            },
            {
                -2,
                -1
            },
            {
                -2,
                1
            },
            {
                -1,
                2
            },
        };
        /// <summary>
        /// 取这颗棋子**能走到的空格**（不含攻击格）。
        /// 被锁死、已死亡返回空列表（不是 null，调用方不用判空）。
        /// </summary>
        public static List<BoardPos> GetMoveTargets(BoardModel board, Piece piece)
        {
            List<BoardPos> result = new List<BoardPos>();
            if (board == null || piece == null)
            {
                return result;
            }

            if (!piece.IsAlive || piece.IsLocked || piece.EyeCount > 0)
            {
                return result;
            }

            if (piece.Size > 1)
            {
                CollectSlide(board, piece, result, false);
                return result;
            }

            switch (piece.Def.Pattern)
            {
                case MovePattern.Step:
                    CollectStep(board, piece, result);
                    break;
                case MovePattern.Slide:
                    CollectSlide(board, piece, result, false);
                    break;
                case MovePattern.Knight:
                    CollectKnight(board, piece, result);
                    break;
                case MovePattern.Cannon:
                    // 炮的"移动"和"吃子"分开：移动只能走到第一个挡子前的空位。
                    CollectSlide(board, piece, result, false);
                    break;
            }

            return result;
        }

        /// <summary>
        /// 取这颗棋子**能攻击到的敌方棋子位置**。
        /// 近战相邻一格；我方远程/敌方重弩全盘压制；敌弓六格直线，敌方法师仍须隔一颗炮架。
        /// </summary>
        public static List<BoardPos> GetAttackTargets(BoardModel board, Piece piece)
        {
            List<BoardPos> result = new List<BoardPos>();
            if (board == null || piece == null)
            {
                return result;
            }

            if (!piece.IsAlive || piece.IsLocked)
            {
                return result;
            }

            // 我方远程守阵输出；敌方重弩承担跨盘压制，避免守眼骨架整场没有射界。
            // 敌弓保留直线与阻挡，法师保留炮架；不是所有远程都跳过职业规则。
            if ((piece.Side == GameSide.Player && piece.Def.IsRanged) || (piece.Side == GameSide.Enemy && piece.Def.Kind == PieceKind.Crossbow))
            {
                List<Piece> foes = board.GetPiecesOf(piece.Side == GameSide.Player ? GameSide.Enemy : GameSide.Player);
                foes.Sort((a, b) =>
                {
                    int da = System.Math.Abs(a.Pos.X - piece.Pos.X) + System.Math.Abs(a.Pos.Y - piece.Pos.Y);
                    int db = System.Math.Abs(b.Pos.X - piece.Pos.X) + System.Math.Abs(b.Pos.Y - piece.Pos.Y);
                    return da == db ? a.Id.CompareTo(b.Id) : da.CompareTo(db);
                });
                foreach (Piece foe in foes)
                {
                    if (foe.IsAlive)
                    {
                        result.Add(foe.Pos);
                    }
                }
                return result;
            }

            CollectAttackRays(board, piece, result);
            return result;
        }

        /// <summary>单步走法：四方向各走 1~MoveRange 格（遇到棋子就停）。</summary>
        private static void CollectStep(BoardModel board, Piece piece, List<BoardPos> result)
        {
            int range = piece.Def.MoveRange <= 0 ? 1 : piece.Def.MoveRange;
            for (int i = 0; i < 4; i++)
            {
                for (int step = 1; step <= range; step++)
                {
                    BoardPos pos = new BoardPos(piece.Pos.X + Dx[i] * step, piece.Pos.Y + Dy[i] * step);
                    Cell cell = board.GetCell(pos);
                    if (cell == null)
                    {
                        break;
                    }

                    if (!board.CanFit(pos, piece.Size, piece.Side, piece))
                    {
                        break;
                    }

                    result.Add(pos);
                }
            }
        }

        /// <summary>滑步（车）：沿四方向一直走，直到出界或撞到棋子。</summary>
        private static void CollectSlide(BoardModel board, Piece piece, List<BoardPos> result, bool allowCapture)
        {
            for (int i = 0; i < 4; i++)
            {
                for (int step = 1;; step++)
                {
                    BoardPos pos = new BoardPos(piece.Pos.X + Dx[i] * step, piece.Pos.Y + Dy[i] * step);
                    // 大棋每走一步都检查整个身体，不能从一格宽的缝里穿过去。
                    if (!board.CanFit(pos, piece.Size, piece.Side, piece))
                    {
                        break;
                    }

                    result.Add(pos);
                }
            }
        }

        /// <summary>马步：8 个落点 + 蹩马腿（落点方向的邻格必须为空）。</summary>
        private static void CollectKnight(BoardModel board, Piece piece, List<BoardPos> result)
        {
            for (int i = 0; i < 8; i++)
            {
                int dx = KnightMoves[i, 0];
                int dy = KnightMoves[i, 1];
                // 蹩马腿：先沿"长边方向"走一格，那一格有棋子就跳不过去。
                int legX = piece.Pos.X + (System.Math.Abs(dx) == 2 ? dx / 2 : 0);
                int legY = piece.Pos.Y + (System.Math.Abs(dy) == 2 ? dy / 2 : 0);
                Cell leg = board.GetCell(new BoardPos(legX, legY));
                if (leg == null || !leg.IsEmpty)
                {
                    continue;
                }

                BoardPos pos = new BoardPos(piece.Pos.X + dx, piece.Pos.Y + dy);
                Cell cell = board.GetCell(pos);
                if (cell == null)
                {
                    continue;
                }

                if (!board.CanFit(pos, piece.Size, piece.Side, piece))
                {
                    continue;
                }

                result.Add(pos);
            }
        }

        /// <summary>从身体每个边缘格向外检查；同一颗大目标只进入候选列表一次。</summary>
        private static void CollectAttackRays(BoardModel board, Piece piece, List<BoardPos> result)
        {
            bool cannon = piece.Def.Pattern == MovePattern.Cannon;
            int range = cannon ? board.Size : (piece.Def.IsRanged ? Consts.EnemyArcherRange : 1);
            var seen = new HashSet<Piece>();
            for (int direction = 0; direction < 4; direction++)
            {
                for (int edge = 0; edge < piece.Size; edge++)
                {
                    Piece screen = null;
                    BoardPos origin = AttackOrigin(piece, direction, edge);
                    for (int step = 1; step <= range; step++)
                    {
                        var pos = new BoardPos(origin.X + Dx[direction] * step, origin.Y + Dy[direction] * step);
                        Cell cell = board.GetCell(pos);
                        if (cell == null)
                        {
                            break;
                        }

                        if (cell.IsEmpty)
                        {
                            continue;
                        }

                        if (cannon && screen == null)
                        {
                            screen = cell.Occupant;
                            continue;
                        }

                        // ★ 2×2/3×3 的第二个身体格仍是同一炮架，不可误算成「隔了一子后的目标」。
                        if (cannon && cell.Occupant == screen)
                        {
                            continue;
                        }

                        if (cell.Occupant.Side != piece.Side && seen.Add(cell.Occupant))
                        {
                            result.Add(pos);
                        }

                        break;
                    }
                }
            }
        }

        private static BoardPos AttackOrigin(Piece piece, int direction, int edge)
        {
            // 大棋只扩大接触面，射程格数仍与小棋相同；不能只从中心一条线打，漏掉贴着边角的敌人。
            if (direction == 0)
            {
                return new BoardPos(piece.Pos.X + piece.Size - 1, piece.Pos.Y + edge);
            }

            if (direction == 1)
            {
                return new BoardPos(piece.Pos.X, piece.Pos.Y + edge);
            }

            if (direction == 2)
            {
                return new BoardPos(piece.Pos.X + edge, piece.Pos.Y + piece.Size - 1);
            }

            return new BoardPos(piece.Pos.X + edge, piece.Pos.Y);
        }
    }
}

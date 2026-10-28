// ============================================================
// MoveRules.cs
// 只计算合法目标，不搬动棋子；所有移动最终由棋盘整块校验，避免大棋越界或闯入敌眼。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
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
        /// <summary>右、左、上、下的横坐标增量；与Dy同下标配对，固定顺序也是同分目标稳定选择的依据。</summary>
        private static readonly int[] Dx =
        {
            1,
            -1,
            0,
            0
        };
        /// <summary>四方向纵坐标增量；只创建一次数组，不在每颗棋子的攻击查询里重新分配。</summary>
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
            var result = new List<BoardPos>();
            GetMoveTargets(board, piece, result);
            return result;
        }

        /// <summary>
        /// 高频版本：结果写入调用方持有的列表，进入时Clear，容量保留。
        /// 服务为自己保存缓冲区，AI为一次查询保存局部缓冲区；不要用全局static列表，避免嵌套调用互相覆盖。
        /// </summary>
        public static void GetMoveTargets(BoardModel board, Piece piece, List<BoardPos> result)
        {
            result.Clear();
            if (board == null || piece == null)
            {
                return;
            }

            if (!piece.IsAlive || piece.BossHidden || piece.BossFrozen || piece.IsLocked || piece.EyeCount > 0)
            {
                return;
            }

            if (piece.Size > 1)
            {
                CollectSlide(board, piece, result, false);
                return;
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

            return;
        }

        /// <summary>
        /// 取这颗棋子**能攻击到的敌方棋子位置**。
        /// 我方全体守阵攻击：近战优先接触目标，没有接触目标时援击全盘敌人；远程始终全盘搜敌。
        /// 敌人仍按原职业射程判断，重弩全盘、弓六格直线、法师需要炮架，不会同步获得我方的援击规则。
        /// </summary>
        public static List<BoardPos> GetAttackTargets(BoardModel board, Piece piece)
        {
            var result = new List<BoardPos>();
            GetAttackTargets(board, piece, result);
            return result;
        }

        /// <summary>
        /// 高频版本：结果写入调用方持有的列表，进入时Clear，容量保留。
        /// 服务为自己保存缓冲区，AI为一次查询保存局部缓冲区；不要用全局static列表，避免嵌套调用互相覆盖。
        /// </summary>
        public static void GetAttackTargets(BoardModel board, Piece piece, List<BoardPos> result)
        {
            result.Clear();
            if (board == null || piece == null)
            {
                return;
            }

            if (!CanAttack(piece))
            {
                return;
            }

            // 近战先保护身边的阵位；只有没有接触敌人时才改用守阵援击。
            // 查询只读棋盘，不为了够到目标移动棋子，因此不会拆掉玩家的一眼、两眼阵型。
            if (piece.Side == GameSide.Player && !piece.Def.IsRanged)
            {
                CollectAttackRays(board, piece, result);
                if (result.Count > 0)
                {
                    return;
                }
            }

            // 我方全体都能在原位贡献火力；敌方重弩承担跨盘压制，避免守眼骨架整场没有射界。
            // 敌弓保留直线与阻挡，法师保留炮架；不是所有远程都跳过职业规则。
            if (piece.Side == GameSide.Player || (piece.Side == GameSide.Enemy && piece.Def.Kind == PieceKind.Crossbow))
            {
                // 最多81格，用有序插入保留“距离优先、编号破同分”的旧顺序。
                // 不再分配敌军名单和捕获piece的Sort闭包；死亡棋子不进入候选。
                foreach (Piece foe in board.Pieces)
                {
                    if (foe.Side == piece.Side || !foe.IsAlive || foe.BossHidden)
                    {
                        continue;
                    }
                    int index = result.Count;
                    int distance = Distance(piece.Pos, foe.Pos);
                    while (index > 0)
                    {
                        Piece previous = board.GetPiece(result[index - 1]);
                        int previousDistance = Distance(piece.Pos, previous.Pos);
                        if (previousDistance < distance || (previousDistance == distance && previous.Id < foe.Id))
                        {
                            break;
                        }
                        index--;
                    }
                    result.Insert(index, foe.Pos);
                }
                return;
            }

            CollectAttackRays(board, piece, result);
            return;
        }

        /// <summary>
        /// 取这颗棋子**攻击能覆盖到的格子**（几何射界，格子上不要求真有敌人）。
        ///
        /// 与 <see cref = "GetAttackTargets"/> 的区别（长按查看攻击范围就靠这一条）：
        /// - GetAttackTargets 回答"现在真能打到哪几颗敌棋"，空格永远不会出现；
        /// - 这里回答"火力能伸到哪些格"，空格也算 —— 玩家想看的是射界，不是当前目标。
        ///
        /// 分支必须与 GetAttackTargets 保持一致，否则玩家看到黄色格子却打不到，比不显示更糟：
        /// 我方所有棋种/敌方重弩全盘；其余按四方向射线 —— 近战一格、敌弓六格、炮先找炮架再打架后第一格。
        /// 我方近战当前优先打身边目标，但全盘仍是可援击的潜在范围；敌方被锁死时没有射界。
        /// </summary>
        public static List<BoardPos> GetAttackCoverage(BoardModel board, Piece piece)
        {
            var result = new List<BoardPos>();
            GetAttackCoverage(board, piece, result);
            return result;
        }

        /// <summary>高频版本：结果写入调用方持有的列表，进入时Clear，容量保留。</summary>
        public static void GetAttackCoverage(BoardModel board, Piece piece, List<BoardPos> result)
        {
            result.Clear();
            if (board == null || piece == null)
            {
                return;
            }

            // 与真实出手入口共用同一契约，不能出现长按显示全盘、战斗却被旧锁死判断拦住的情况。
            if (!CanAttack(piece))
            {
                return;
            }

            if (piece.Side == GameSide.Player || (piece.Side == GameSide.Enemy && piece.Def.Kind == PieceKind.Crossbow))
            {
                // 全盘压制：除自己的身体格以外，每个格子都在弹幕覆盖内（含空格和敌方棋子）。
                for (int y = 0; y < board.Size; y++)
                {
                    for (int x = 0; x < board.Size; x++)
                    {
                        var pos = new BoardPos(x, y);
                        if (!IsBodyCell(piece, pos))
                        {
                            result.Add(pos);
                        }
                    }
                }
                return;
            }

            bool cannon = piece.Def.Pattern == MovePattern.Cannon;
            int range = cannon ? board.Size : (piece.Def.IsRanged ? Balance.EnemyArcherRange : 1);
            for (int direction = 0; direction < 4; direction++)
            {
                // 大棋从每条边的每一格向外看，射界才是完整的；只从中心看会漏掉贴着边角的格子。
                for (int edge = 0; edge < piece.Size; edge++)
                {
                    BoardPos origin = AttackOrigin(piece, direction, edge);
                    if (cannon)
                    {
                        CollectCannonCoverage(board, piece, origin, direction, range, result);
                    }
                    else
                    {
                        CollectStraightCoverage(board, piece, origin, direction, range, result);
                    }
                }
            }
        }

        /// <summary>
        /// 统一的攻击资格：存活我方即使零气也会守阵反击，敌人仍保留锁死不能出手的规则。
        /// 这里只放宽我方的攻击资格，不清除IsLocked、不增加气、不免除原有被围受击惩罚，也不允许自动移动。
        /// 服务结算、实际选敌与范围预览必须复用本方法，避免只修显示却没有真正扣血。
        /// </summary>
        public static bool CanAttack(Piece piece)
        {
            return piece != null && piece.IsAlive && !piece.BossHidden && !piece.BossFrozen && (piece.Side == GameSide.Player || !piece.IsLocked);
        }

        /// <summary>近战/弓：沿射线把空格都算进覆盖；遇到棋子时敌方格算覆盖，自己人挡住的格不算。</summary>
        private static void CollectStraightCoverage(BoardModel board, Piece piece, BoardPos origin, int direction, int range, List<BoardPos> result)
        {
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
                    AddCoverage(result, pos);
                    continue;
                }

                // 有子就停：敌子这一格是着弹点，自己人这一格打不到也不该标黄。
                if (cell.Occupant.Side != piece.Side)
                {
                    AddCoverage(result, pos);
                }
                break;
            }
        }

        /// <summary>
        /// 炮：炮架之前的空格都覆盖，炮架自身不算着弹点；
        /// 炮架之后**只有第一格**是唯一着弹点（空格也算 —— 敌人走到那里就会被打），再远打不到。
        /// </summary>
        private static void CollectCannonCoverage(BoardModel board, Piece piece, BoardPos origin, int direction, int range, List<BoardPos> result)
        {
            Piece screen = null;
            for (int step = 1; step <= range; step++)
            {
                var pos = new BoardPos(origin.X + Dx[direction] * step, origin.Y + Dy[direction] * step);
                Cell cell = board.GetCell(pos);
                if (cell == null)
                {
                    break;
                }

                if (screen == null)
                {
                    if (cell.IsEmpty)
                    {
                        AddCoverage(result, pos);
                        continue;
                    }
                    screen = cell.Occupant;
                    continue;
                }

                // ★ 2×2/3×3 的后半截身体仍是同一个炮架，不能当成"架后第一子"。
                if (cell.Occupant == screen)
                {
                    continue;
                }

                if (cell.IsEmpty || cell.Occupant.Side != piece.Side)
                {
                    AddCoverage(result, pos);
                }
                break;
            }
        }

        private static bool IsBodyCell(Piece piece, BoardPos pos)
        {
            return pos.X >= piece.Pos.X && pos.Y >= piece.Pos.Y && pos.X < piece.Pos.X + piece.Size && pos.Y < piece.Pos.Y + piece.Size;
        }

        /// <summary>最多81格，直接线性去重即可；不为一次长按分配 HashSet。</summary>
        private static void AddCoverage(List<BoardPos> result, BoardPos pos)
        {
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i] == pos)
                {
                    return;
                }
            }
            result.Add(pos);
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
            int range = cannon ? board.Size : (piece.Def.IsRanged ? Balance.EnemyArcherRange : 1);

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

                        if (cell.Occupant.Side != piece.Side && !ContainsPiece(board, result, cell.Occupant))
                        {
                            result.Add(pos);
                        }

                        break;
                    }
                }
            }
        }

        /// <summary>射线结果最多是少量接触目标，直接比较已有目标即可去重，不为每次攻击new HashSet。</summary>
        private static bool ContainsPiece(BoardModel board, List<BoardPos> result, Piece target)
        {
            for (int i = 0; i < result.Count; i++)
            {
                if (board.GetPiece(result[i]) == target)
                {
                    return true;
                }
            }
            return false;
        }

        private static int Distance(BoardPos a, BoardPos b)
        {
            return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
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

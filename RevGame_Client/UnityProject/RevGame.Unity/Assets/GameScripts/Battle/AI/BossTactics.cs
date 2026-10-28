// ============================================================
// BossTactics.cs —— Boss 技能的“纯算法”：评估局面、给每个技能打分、算出目标点。
//
// 【它在 Boss AI 里的位置】
// · BossAI.cs     ：装配并驱动 RevHeavyFSM，不做决策；
// · BossStates.cs ：每个状态“什么时候蓄力、什么时候命中、命中后做什么”；
// · 本文件        ：回答“现在放哪个技能最划算、打哪里”，只读棋盘，不改任何数据。
// 拆开的好处：调权重不会碰到状态流转，改状态流转也不会碰到评分。
//
// 【评分思路（新手可以照着改）】
// 每个技能先算一个“局面分”（这一招此刻能打到多少人、值不值），再乘以表里的 Weight。
// 先从使用次数最少的可用技能中选择，再比较评分；防止全盘弹幕永远压过其它技能。
// 上一次刚用过的技能分数减半，避免 Boss 连放同一招让战斗单调。
//
// 【几何约定】
// 棋子的 Pos 是身体左下角，Size 是边长（Boss 可能 2×2 / 3×3）。
// · “方形范围”用切比雪夫距离：两块矩形之间隔了几格（0=重叠，1=紧贴）；
// · “圆形范围”用两块身体中心的欧氏距离，再扣掉目标半个身体，大棋子更容易被扫到，符合直觉。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>
    /// 一次技能计划：选定技能那一刻算好的目标。蓄力期间不再改，玩家才有“看见预警→躲开”的机会。
    /// 值类型、只存坐标和编号，不保存 Piece 引用，蓄力期间目标死掉也不会出错。
    /// </summary>
    public struct BossSkillPlan
    {
        /// <summary>技能种类。</summary>
        public BossSkillKind Kind;
        /// <summary>目标点 A/B/C（含义见 BossSkillInfo 注释）。</summary>
        public BoardPos A;
        public BoardPos B;
        public BoardPos C;
        /// <summary>评分（仅调试/测试读取）。</summary>
        public float Score;
    }

    public static class BossTactics
    {
        /// <summary>重复使用上一招时的分数系数。</summary>
        private const float RepeatPenalty = 0.5f;

        /// <summary>
        /// 从所有已解锁、冷却已好的技能里挑出最划算的一个。
        /// </summary>
        /// <param name="readyAt">按 BossSkillKind 下标存放的“冷却结束时刻”；小于等于 now 才可用。</param>
        /// <param name="last">上一次使用的技能，null 表示本场还没用过。</param>
        /// <param name="plan">选中的计划；返回 false 时无意义。</param>
        /// <returns>false = 当前没有任何技能值得放（例如所有技能都打不到人），Boss 继续普通攻击。</returns>
        public static bool TryChoose(BoardModel board, Piece boss, int chapter, int intelligence, float[] readyAt, float now, BossSkillKind? last, out BossSkillPlan plan, int[] useCounts = null)
        {
            plan = default;
            int leastUsed = int.MaxValue;
            bool found = false;
            float best = 0f;
            foreach (BossSkillKind kind in (BossSkillKind[])Enum.GetValues(typeof(BossSkillKind)))
            {
                BossSkillDef def = BossSkillCatalog.Get(kind);
                // 狂暴不参与挑选：它只由“血量跌破阈值”触发，见 BossIdleState。
                if (def.Weight <= 0 || !BossSkillCatalog.IsUnlocked(kind, chapter) || readyAt[(int)kind] > now)
                {
                    continue;
                }
                BossSkillPlan candidate;
                if (!TryPlan(board, boss, kind, intelligence, out candidate))
                {
                    continue;
                }
                candidate.Score *= def.Weight;
                if (last.HasValue && last.Value == kind)
                {
                    candidate.Score *= RepeatPenalty;
                }
                // 严格大于才替换：同分保留枚举靠前的技能，相同局面永远选同一招，方便复现和测试。
                int used = useCounts == null ? 0 : useCounts[(int)kind];
                if (used < leastUsed || (used == leastUsed && candidate.Score > best))
                {
                    leastUsed = used;
                    best = candidate.Score;
                    plan = candidate;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// 计算某个技能此刻的目标与局面分。返回 false 表示“这招现在放了也打不到人”。
        /// 狂暴单独由状态触发，这里给它一个固定计划（目标就是 Boss 自己）。
        /// </summary>
        public static bool TryPlan(BoardModel board, Piece boss, BossSkillKind kind, int intelligence, out BossSkillPlan plan)
        {
            plan = new BossSkillPlan { Kind = kind, A = boss.Pos, B = boss.Pos, C = boss.Pos };
            BossSkillDef def = BossSkillCatalog.Get(kind);
            var victims = new List<Piece>(16);
            if ((int)kind >= (int)BossSkillKind.StarStorm)
            {
                return BossExclusiveGeometry.TryPlan(board, boss, kind, out plan);
            }
            if ((int)kind >= (int)BossSkillKind.MeteorShower)
            {
                return BossPatternGeometry.TryPlan(board, boss, kind, out plan);
            }
            switch (kind)
            {
                case BossSkillKind.Cleave:
                {
                    // 贴身一圈：人越多越值。贴脸的敌人用这招比狙击更划算。
                    CollectInSquare(board, boss.Pos, boss.Size, def.Radius, victims);
                    plan.Score = victims.Count * 10f;
                    return victims.Count > 0;
                }
                case BossSkillKind.Snipe:
                {
                    // 狙击打“威胁最大”的那颗：远程、高攻击、高星级优先；残血再加分（有机会一击带走）。
                    // 只考虑与 Boss 至少隔一格的目标——贴脸的交给横扫，狙击要体现“远处点名”。
                    Piece target = null;
                    float bestThreat = 0f;
                    foreach (Piece p in board.GetPiecesOf(GameSide.Player))
                    {
                        if (!(p.IsAlive && !p.BossHidden) || RectGap(p, boss.Pos, boss.Size) < 2)
                        {
                            continue;
                        }
                        float threat = p.Def.Attack * (p.Def.IsRanged ? 1.5f : 1f) * (int)p.Def.Star + (1f - p.HpRatio) * 20f;
                        if (threat > bestThreat)
                        {
                            bestThreat = threat;
                            target = p;
                        }
                    }
                    if (target == null)
                    {
                        return false;
                    }
                    plan.A = target.Pos;
                    // 智能档位越高越爱用狙击：它是“看穿后排”的标志性能力。
                    plan.Score = 8f + intelligence * 2f;
                    return true;
                }
                case BossSkillKind.Bombard:
                {
                    // 在离 Boss 至少两格的地方找“人最密集”的 3×3 区域。
                    int bestCount = 0;
                    BoardPos center = boss.Pos;
                    for (int y = 0; y < board.Size; y++)
                    {
                        for (int x = 0; x < board.Size; x++)
                        {
                            var cell = new BoardPos(x, y);
                            if (CellGap(boss, cell) < 2)
                            {
                                continue;
                            }
                            victims.Clear();
                            CollectInSquare(board, cell, 1, def.Radius, victims);
                            if (victims.Count > bestCount)
                            {
                                bestCount = victims.Count;
                                center = cell;
                            }
                        }
                    }
                    if (bestCount == 0)
                    {
                        return false;
                    }
                    // 三发炸弹：第一发砸中心，后两发砸区域内威胁最高的两颗棋子所在格；
                    // 不够两颗时在中心两侧补点，保证三发都落在锁定区域内，不会飞到别处误导玩家。
                    victims.Clear();
                    CollectInSquare(board, center, 1, def.Radius, victims);
                    victims.Sort((a, b) => (b.Def.Attack * (int)b.Def.Star).CompareTo(a.Def.Attack * (int)a.Def.Star));
                    plan.A = center;
                    plan.B = victims.Count > 0 ? ClampInto(victims[0].Pos, center, def.Radius) : Offset(board, center, -1, 0);
                    plan.C = victims.Count > 1 ? ClampInto(victims[1].Pos, center, def.Radius) : Offset(board, center, 1, 0);
                    plan.Score = bestCount * 9f;
                    return true;
                }
                case BossSkillKind.BulletRing:
                {
                    // 全屏弹幕只要求棋盘上有存活我方棋子，不以旧 Radius 限制选招。
                    CollectAllPlayers(board, victims);
                    plan.Score = victims.Count * 8f;
                    return victims.Count > 0;
                }
                case BossSkillKind.BlinkBlast:
                {
                    // 枚举所有放得下 Boss 整个身体的空位，挑“落地爆炸能炸到最多人”的那个。
                    // 已经被围在中间时（身边人多）不急着走：用横扫更划算，所以减去当前身边人数。
                    victims.Clear();
                    CollectInSquare(board, boss.Pos, boss.Size, def.Radius, victims);
                    int here = victims.Count;
                    int bestCount = 0;
                    BoardPos destination = boss.Pos;
                    for (int y = 0; y <= board.Size - boss.Size; y++)
                    {
                        for (int x = 0; x <= board.Size - boss.Size; x++)
                        {
                            var pos = new BoardPos(x, y);
                            if (pos == boss.Pos || !board.CanFit(pos, boss.Size, GameSide.Enemy, boss, respectEnemyEyes: false))
                            {
                                continue;
                            }
                            victims.Clear();
                            CollectInSquare(board, pos, boss.Size, def.Radius, victims);
                            if (victims.Count > bestCount)
                            {
                                bestCount = victims.Count;
                                destination = pos;
                            }
                        }
                    }
                    if (bestCount == 0)
                    {
                        return false;
                    }
                    plan.A = destination;
                    plan.Score = bestCount * 11f - here * 3f;
                    return true;
                }
                case BossSkillKind.LaserSpin:
                {
                    // 激光覆盖一整圈，三人以上额外加分：人越扎堆，旋转激光越致命。
                    CollectInCircle(board, boss, def.Radius, victims);
                    plan.Score = victims.Count * 9f + (victims.Count >= 3 ? 10f : 0f);
                    return victims.Count > 0;
                }
                case BossSkillKind.SoulDrain:
                {
                    // 只有自己掉了血才值得吸：满血时吸血等于白放一个技能。
                    if (boss.HpRatio >= 0.9f || board.GetPiecesOf(GameSide.Player).Count == 0)
                    {
                        return false;
                    }
                    plan.Score = (1f - boss.HpRatio) * 40f;
                    return true;
                }
                case BossSkillKind.Summon:
                {
                    // 小兵越少越想召唤；场上已经很挤或达到上限就不召。
                    int minions = board.GetPiecesOf(GameSide.Enemy).Count - 1;
                    if (minions + 1 >= Balance.DeployLivingCap || EnemyTactics.CountEmptyCells(board) <= Balance.DeployMinEmptyCells)
                    {
                        return false;
                    }
                    plan.Score = Math.Max(0, 6 - minions) * 4f;
                    return plan.Score > 0f;
                }
                case BossSkillKind.Enrage:
                {
                    plan.Score = 1f;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 收集“与某块区域隔不超过 radius 格”的我方棋子（方形范围，切比雪夫距离）。
        /// result 由调用方复用，本方法会先清空它。
        /// </summary>
        public static void CollectInSquare(BoardModel board, BoardPos origin, int size, int radius, List<Piece> result)
        {
            result.Clear();
            foreach (Piece p in board.GetPiecesOf(GameSide.Player))
            {
                if ((p.IsAlive && !p.BossHidden) && RectGap(p, origin, size) <= radius)
                {
                    result.Add(p);
                }
            }
        }

        /// <summary>
        /// 收集以 Boss 身体中心为圆心、半径 radius 内的我方棋子（圆形范围）。
        /// 扣掉目标半个身体：2×2 的棋子边缘进圈也算被扫到。
        /// </summary>
        public static void CollectInCircle(BoardModel board, Piece boss, int radius, List<Piece> result)
        {
            result.Clear();
            float cx = boss.Pos.X + (boss.Size - 1) * 0.5f;
            float cy = boss.Pos.Y + (boss.Size - 1) * 0.5f;
            foreach (Piece p in board.GetPiecesOf(GameSide.Player))
            {
                if (!(p.IsAlive && !p.BossHidden))
                {
                    continue;
                }
                float dx = p.Pos.X + (p.Size - 1) * 0.5f - cx;
                float dy = p.Pos.Y + (p.Size - 1) * 0.5f - cy;
                float reach = radius + (boss.Size - 1) * 0.5f + (p.Size - 1) * 0.5f;
                if (dx * dx + dy * dy <= reach * reach)
                {
                    result.Add(p);
                }
            }
        }

        /// <summary>全屏技能收集全部存活我方棋子；与位置、距离和棋子身体大小无关。</summary>
        public static void CollectAllPlayers(BoardModel board, List<Piece> result)
        {
            result.Clear();
            foreach (Piece piece in board.GetPiecesOf(GameSide.Player))
            {
                if ((piece.IsAlive && !piece.BossHidden))
                {
                    result.Add(piece);
                }
            }
        }

        /// <summary>
        /// 收集生命比例最低的 count 颗我方棋子（灵魂汲取用）。同比例按编号小的优先，保证可复现。
        /// </summary>
        public static void CollectWeakest(BoardModel board, int count, List<Piece> result)
        {
            result.Clear();
            foreach (Piece p in board.GetPiecesOf(GameSide.Player))
            {
                if ((p.IsAlive && !p.BossHidden))
                {
                    result.Add(p);
                }
            }
            result.Sort((a, b) => a.HpRatio != b.HpRatio ? a.HpRatio.CompareTo(b.HpRatio) : a.Id.CompareTo(b.Id));
            if (result.Count > count)
            {
                result.RemoveRange(count, result.Count - count);
            }
        }

        /// <summary>
        /// 两块矩形之间隔了几格（切比雪夫距离）：0 = 有重叠，1 = 紧贴，2 = 中间空一格……
        /// 用“间隔”而不是“左下角距离”，大棋子才不会因为左下角离得远就被漏掉。
        /// </summary>
        public static int RectGap(Piece piece, BoardPos origin, int size)
        {
            int dx = Math.Max(0, Math.Max(piece.Pos.X - (origin.X + size - 1), origin.X - (piece.Pos.X + piece.Size - 1)));
            int dy = Math.Max(0, Math.Max(piece.Pos.Y - (origin.Y + size - 1), origin.Y - (piece.Pos.Y + piece.Size - 1)));
            return Math.Max(dx, dy);
        }

        /// <summary>某一格与 Boss 身体之间隔了几格。</summary>
        private static int CellGap(Piece boss, BoardPos cell)
        {
            int dx = Math.Max(0, Math.Max(cell.X - (boss.Pos.X + boss.Size - 1), boss.Pos.X - cell.X));
            int dy = Math.Max(0, Math.Max(cell.Y - (boss.Pos.Y + boss.Size - 1), boss.Pos.Y - cell.Y));
            return Math.Max(dx, dy);
        }

        /// <summary>把点限制在“中心 ± radius”的方形区域里，炸弹不会落到锁定框外。</summary>
        private static BoardPos ClampInto(BoardPos pos, BoardPos center, int radius)
        {
            return new BoardPos(Math.Max(center.X - radius, Math.Min(center.X + radius, pos.X)), Math.Max(center.Y - radius, Math.Min(center.Y + radius, pos.Y)));
        }

        /// <summary>中心偏移一格；越界时退回中心，保证落点一定在棋盘内。</summary>
        private static BoardPos Offset(BoardModel board, BoardPos center, int dx, int dy)
        {
            var pos = new BoardPos(center.X + dx, center.Y + dy);
            return pos.IsInside(board.Size) ? pos : center;
        }
    }
}

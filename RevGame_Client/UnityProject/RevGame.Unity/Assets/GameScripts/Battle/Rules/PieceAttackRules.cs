using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>
    /// 九种普通棋子的只读攻击几何。棋盘、AI预判和正式结算共用这里，表现只消费结算快照。
    /// 不移动实棋、不扣血、不创建临时集合；结果写入调用方私有列表，不能跨帧或交给异步回调持有。
    /// </summary>
    public static class PieceAttackRules
    {
        /// <summary>两个方形身体的切比雪夫间距；相邻为1，重叠为0，三星大棋不会因只测左下角而漏判。</summary>
        public static int Distance(Piece a, Piece b)
        {
            int x = Math.Max(0, Math.Max(a.Pos.X - b.Pos.X - b.Size + 1, b.Pos.X - a.Pos.X - a.Size + 1));
            int y = Math.Max(0, Math.Max(a.Pos.Y - b.Pos.Y - b.Size + 1, b.Pos.Y - a.Pos.Y - a.Size + 1));
            return Math.Max(x, y);
        }

        /// <summary>点到身体的最近距离，供范围预览和以目标格为中心的爆破复用。</summary>
        public static int Distance(Piece piece, BoardPos point)
        {
            int x = Math.Max(0, Math.Max(piece.Pos.X - point.X, point.X - piece.Pos.X - piece.Size + 1));
            int y = Math.Max(0, Math.Max(piece.Pos.Y - point.Y, point.Y - piece.Pos.Y - piece.Size + 1));
            return Math.Max(x, y);
        }

        /// <summary>普通攻击射程；重弩仅沿身体边缘的横纵直线，其余棋子允许向射程内任意方向瞄准。</summary>
        public static bool InRange(Piece actor, Piece target)
        {
            if (Distance(actor, target) > Balance.AttackProfile(actor.Def.Kind).Range)
            {
                return false;
            }
            return actor.Def.Kind != PieceKind.Crossbow || Aligned(actor, target);
        }

        /// <summary>空格预览使用同一射程和轴线条件；不因没有目标而改变玩家看到的可瞄准范围。</summary>
        public static bool Covers(Piece actor, BoardPos point)
        {
            int distance = Distance(actor, point);
            if (distance == 0 || distance > Balance.AttackProfile(actor.Def.Kind).Range)
            {
                return false;
            }
            return actor.Def.Kind != PieceKind.Crossbow
                || (point.X >= actor.Pos.X && point.X < actor.Pos.X + actor.Size)
                || (point.Y >= actor.Pos.Y && point.Y < actor.Pos.Y + actor.Size);
        }

        /// <summary>收集合法首目标，稳定按距离、编号排序；血量与密度选敌在出手时再算，不缓存动态评分。</summary>
        public static void Targets(BoardModel board, Piece actor, List<BoardPos> result)
        {
            result.Clear();
            foreach (Piece target in board.Pieces)
            {
                if (!Eligible(actor, target) || !InRange(actor, target))
                {
                    continue;
                }
                int at = result.Count;
                int distance = Distance(actor, target);
                while (at > 0)
                {
                    Piece previous = board.GetPiece(result[at - 1]);
                    int previousDistance = Distance(actor, previous);
                    if (previousDistance < distance || (previousDistance == distance && previous.Id < target.Id))
                    {
                        break;
                    }
                    at--;
                }
                result.Insert(at, target.Pos);
            }
        }

        /// <summary>刺客找最低血量比例；火法找最密集的爆心；其它棋种保留调用方的合法优先目标。</summary>
        public static Piece SelectPrimary(BoardModel board, Piece actor, Piece fallback)
        {
            PieceKind kind = actor.Def.Kind;
            if (actor.Side == GameSide.Enemy || (kind != PieceKind.Assassin && kind != PieceKind.MageFire))
            {
                return fallback;
            }
            Piece best = null;
            double bestScore = double.MinValue;
            int radius = Balance.AttackProfile(kind).Radius;
            foreach (Piece target in board.Pieces)
            {
                if (!Eligible(actor, target) || !InRange(actor, target))
                {
                    continue;
                }
                double score = -(double)target.Hp / target.MaxHp;
                if (kind == PieceKind.MageFire)
                {
                    score = CountBlastTargets(board, actor, target.Pos, radius);
                }
                if (best == null || score > bestScore || (score == bestScore && target.Id < best.Id))
                {
                    best = target;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// 在任何扣血发生前确定整次攻击名单，避免首目标死亡改变迭代器或让爆心随复活移动。
        /// 列表按实体去重，最多81个占格实体；后续结算仍逐项检查存活与在盘身份。
        /// </summary>
        public static void CollectHits(BoardModel board, Piece actor, Piece primary, List<Piece> hits)
        {
            hits.Clear();
            if (primary == null || !Eligible(actor, primary) || !InRange(actor, primary))
            {
                return;
            }
            hits.Add(primary);
            PieceAttackProfile profile = Balance.AttackProfile(actor.Def.Kind);
            if (actor.Def.Kind == PieceKind.MageFire)
            {
                // 3×3爆破只查九个格，不为每个候选再次遍历整盘；大棋以引用去重，首目标固定占第一个名额。
                int minX = Math.Max(0, primary.Pos.X - profile.Radius);
                int minY = Math.Max(0, primary.Pos.Y - profile.Radius);
                int maxX = Math.Min(board.Size - 1, primary.Pos.X + profile.Radius);
                int maxY = Math.Min(board.Size - 1, primary.Pos.Y + profile.Radius);
                if (board.Pieces.Count < (maxX - minX + 1) * (maxY - minY + 1))
                {
                    // 稀疏残局直接扫少量实体更便宜，避免为了找唯一Boss还遍历九个空格。
                    foreach (Piece candidate in board.Pieces)
                    {
                        if (candidate != primary && Eligible(actor, candidate) && Distance(candidate, primary.Pos) <= profile.Radius)
                        {
                            InsertBlastTarget(primary, candidate, hits);
                        }
                    }
                    if (hits.Count > profile.MaxTargets)
                    {
                        hits.RemoveRange(profile.MaxTargets, hits.Count - profile.MaxTargets);
                    }
                    return;
                }
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        Piece candidate = board.GetPiece(new BoardPos(x, y));
                        if (!Eligible(actor, candidate) || hits.Contains(candidate))
                        {
                            continue;
                        }
                        InsertBlastTarget(primary, candidate, hits);
                    }
                }
                if (hits.Count > profile.MaxTargets)
                {
                    hits.RemoveRange(profile.MaxTargets, hits.Count - profile.MaxTargets);
                }
                return;
            }
            while (hits.Count < profile.MaxTargets)
            {
                Piece origin = actor.Def.Kind == PieceKind.MageWater ? hits[hits.Count - 1] : primary;
                Piece best = null;
                int bestDistance = int.MaxValue;
                foreach (Piece candidate in board.Pieces)
                {
                    if (!Eligible(actor, candidate) || hits.Contains(candidate))
                    {
                        continue;
                    }
                    bool covered = SecondaryCovered(actor, primary, origin, candidate, profile);
                    int distance = Distance(origin, candidate);
                    if (covered && (distance < bestDistance || (distance == bestDistance && (best == null || candidate.Id < best.Id))))
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
                if (best == null)
                {
                    break;
                }
                hits.Add(best);
            }
        }

        /// <summary>形状只由棋种定义，距离和命中上限由表决定；所有比较在棋盘整数坐标进行。</summary>
        private static bool SecondaryCovered(Piece actor, Piece primary, Piece origin, Piece target, PieceAttackProfile profile)
        {
            switch (actor.Def.Kind)
            {
                case PieceKind.MageFire:
                    return Distance(target, primary.Pos) <= profile.Radius;
                case PieceKind.MageWater:
                    return Distance(origin, target) <= profile.Radius;
                case PieceKind.Crossbow:
                    // 先固定一条穿过攻击者与首目标身体的射线，后续不能拐到首目标的另一条轴线上。
                    bool horizontal = primary.Pos.X >= actor.Pos.X + actor.Size || primary.Pos.X + primary.Size <= actor.Pos.X;
                    int lane = horizontal ? Math.Max(actor.Pos.Y, primary.Pos.Y) : Math.Max(actor.Pos.X, primary.Pos.X);
                    bool sameLane = horizontal ? target.Pos.Y <= lane && target.Pos.Y + target.Size > lane
                        : target.Pos.X <= lane && target.Pos.X + target.Size > lane;
                    int direction = Math.Sign(horizontal ? primary.Pos.X - actor.Pos.X : primary.Pos.Y - actor.Pos.Y);
                    int forward = horizontal ? target.Pos.X - actor.Pos.X : target.Pos.Y - actor.Pos.Y;
                    return InRange(actor, target) && sameLane && forward * direction > 0;
                case PieceKind.HeavySword:
                    return InRange(actor, target) && Distance(primary, target) <= profile.Radius
                        && (target.Pos.X - actor.Pos.X) * (primary.Pos.X - actor.Pos.X)
                        + (target.Pos.Y - actor.Pos.Y) * (primary.Pos.Y - actor.Pos.Y) >= 0;
                case PieceKind.Shield:
                    return InRange(actor, target);
                default:
                    return false;
            }
        }

        /// <summary>身体在任意一轴投影重叠即属于横纵射线；无需浮点角度或逐帧Physics查询。</summary>
        private static bool Aligned(Piece a, Piece b)
        {
            return (a.Pos.X < b.Pos.X + b.Size && b.Pos.X < a.Pos.X + a.Size)
                || (a.Pos.Y < b.Pos.Y + b.Size && b.Pos.Y < a.Pos.Y + a.Size);
        }

        /// <summary>
        /// 火法密度评分只扫裁剪后的爆区。大棋只在自身与爆区交集的左下格计一次，免去临时HashSet。
        /// 生命/隐藏仍实时读取，不能把密度评分缓存到只随占格变化的射界缓存里。
        /// </summary>
        private static int CountBlastTargets(BoardModel board, Piece actor, BoardPos center, int radius)
        {
            int minX = Math.Max(0, center.X - radius);
            int minY = Math.Max(0, center.Y - radius);
            int maxX = Math.Min(board.Size - 1, center.X + radius);
            int maxY = Math.Min(board.Size - 1, center.Y + radius);
            int count = 0;
            if (board.Pieces.Count < (maxX - minX + 1) * (maxY - minY + 1))
            {
                foreach (Piece candidate in board.Pieces)
                {
                    if (Eligible(actor, candidate) && Distance(candidate, center) <= radius)
                    {
                        count++;
                    }
                }
                return count;
            }
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Piece candidate = board.GetPiece(new BoardPos(x, y));
                    if (Eligible(actor, candidate) && x == Math.Max(minX, candidate.Pos.X) && y == Math.Max(minY, candidate.Pos.Y))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>两条密度路径共用稳定排序；保留首目标，其余按距离/编号，保证性能分支不改变伤害顺序。</summary>
        private static void InsertBlastTarget(Piece primary, Piece candidate, List<Piece> hits)
        {
            int at = hits.Count;
            int distance = Distance(primary, candidate);
            while (at > 1)
            {
                Piece previous = hits[at - 1];
                int previousDistance = Distance(primary, previous);
                if (previousDistance < distance || (previousDistance == distance && previous.Id < candidate.Id))
                {
                    break;
                }
                at--;
            }
            hits.Insert(at, candidate);
        }

        /// <summary>只筛选敌对且可见的活棋；无敌仍可成为落点，是否受伤交给唯一伤害管线。</summary>
        private static bool Eligible(Piece actor, Piece target)
        {
            return target != null && target.IsAlive && !target.BossHidden && target.Side != actor.Side;
        }
    }
}

// 新招式唯一命中几何：计划、伤害与预警共享同一函数。此类只读局面，不移动真实棋子试算。
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Battle
{
    /// <summary>按固定目标格定义元素招式。调用者拥有名单缓冲，跨局不共享可变草稿。</summary>
    public static class BossExpandedGeometry
    {
        /// <summary>只扫描一次玩家，锁定三个不同目标；蓄力后不追踪，玩家移动出去可躲避。</summary>
        public static bool TryPlan(BoardModel board, Piece boss, BossSkillKind kind, out BossSkillPlan plan)
        {
            plan = new BossSkillPlan { Kind = kind, A = boss.Pos, B = boss.Pos, C = boss.Pos };
            Piece a = null, b = null, c = null;
            foreach (Piece p in board.Pieces)
            {
                if (p.Side != GameSide.Player || !p.IsAlive || p.BossHidden) { continue; }
                if (a == null || p.Def.Attack > a.Def.Attack) { c = b; b = a; a = p; }
                else if (b == null || p.Def.Attack > b.Def.Attack) { c = b; b = p; }
                else if (c == null || p.Def.Attack > c.Def.Attack) { c = p; }
            }
            if (a == null) { return false; }
            plan.A = a.Pos; plan.B = b == null ? a.Pos : b.Pos; plan.C = c == null ? plan.B : c.Pos;
            BossSkillDef def = BossSkillCatalog.Get(kind);
            int hits = 0;
            foreach (Piece p in board.Pieces)
            {
                if (p.Side != GameSide.Player || !p.IsAlive || p.BossHidden) { continue; }
                for (int index = 0; index < def.Count; index++)
                {
                    if (Touches(p, kind, plan.A, plan.B, plan.C, def.Radius, index)) { hits++; break; }
                }
            }
            plan.Score = hits * 8f; // AI相对评分只用于同组比较，不进入伤害公式。
            return hits > 0;
        }
        /// <summary>半径单位为格；index为伤害段。棋盘边界由调用者枚举裁剪，不存在越界命中。</summary>
        public static bool Contains(BossSkillKind kind, BoardPos a, BoardPos b, BoardPos c, int radius, int index, BoardPos cell)
        {
            int x = Math.Abs(cell.X-a.X), y = Math.Abs(cell.Y-a.Y);
            switch (kind)
            {
                case BossSkillKind.CometRails:
                    return x <= radius || y <= radius;
                case BossSkillKind.AstralShards:
                {
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                }
                case BossSkillKind.SolarSpears:
                    return Math.Abs(cell.X - a.X) <= radius || (index > 0 && cell.X == (index == 1 ? b.X : c.X));
                case BossSkillKind.EmberFan:
                    return y <= radius && (index % 2 == 0 ? cell.X >= a.X && x >= y : cell.X <= a.X && x >= y);
                case BossSkillKind.MagmaRings:
                    return Math.Max(x, y) == radius + index;
                case BossSkillKind.TidalLances:
                    return Math.Abs(cell.Y - a.Y) <= radius || (index > 0 && cell.Y == (index == 1 ? b.Y : c.Y));
                case BossSkillKind.FoamBurst:
                {
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                }
                case BossSkillKind.Undertow:
                    return Math.Max(x, y) <= radius && ((cell.X + cell.Y + index) & 1) == 0;
                case BossSkillKind.ReefSpikes:
                    return Math.Abs(x - y) <= radius && (index == 0 ? cell.X - a.X == cell.Y - a.Y : cell.X - a.X == a.Y - cell.Y);
                case BossSkillKind.BogSpines:
                    return Math.Abs(cell.X - a.X) <= radius || (index > 0 && cell.X == (index == 1 ? b.X : c.X));
                case BossSkillKind.VenomFan:
                    return y <= radius && (index % 2 == 0 ? cell.X >= a.X && x >= y : cell.X <= a.X && x >= y);
                case BossSkillKind.MireRipples:
                    return Math.Max(x, y) == radius + index;
                case BossSkillKind.BlightSeeds:
                {
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                }
                case BossSkillKind.SnowCross:
                    return x <= radius || y <= radius;
                case BossSkillKind.HailBarrage:
                {
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                }
                case BossSkillKind.IceFault:
                    return Math.Abs(x - y) <= radius && (index == 0 ? cell.X - a.X == cell.Y - a.Y : cell.X - a.X == a.Y - cell.Y);
                case BossSkillKind.JudgementLances:
                    return Math.Abs(cell.X - a.X) <= radius || (index > 0 && cell.X == (index == 1 ? b.X : c.X));
                case BossSkillKind.HaloPulse:
                    return Math.Max(x, y) == radius + index;
                case BossSkillKind.RunicGrid:
                    return Math.Max(x, y) <= radius && ((cell.X + cell.Y + index) & 1) == 0;
                case BossSkillKind.EventHorizon:
                    return Math.Max(x,y) == Math.Max(0,radius-index);
                case BossSkillKind.ConstellationSeal:
                    return x == y || x <= radius || y <= radius;
                case BossSkillKind.PhoenixDescent:
                {
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                }
                case BossSkillKind.SolarEclipse:
                    return Math.Max(x,y) == radius + index;
                case BossSkillKind.LeviathanWake:
                    return Math.Abs(cell.Y - a.Y) <= radius || (index > 0 && cell.Y == (index == 1 ? b.Y : c.Y));
                case BossSkillKind.AbyssalCage:
                    return Math.Max(x,y) == radius || (index > 0 && x <= radius && y <= radius);
                case BossSkillKind.PlagueBloom:
                    return Math.Max(x, y) <= radius && ((cell.X + cell.Y + index) & 1) == 0;
                case BossSkillKind.HydraFangs:
                    return x == y || x <= radius || y <= radius;
                case BossSkillKind.AbsoluteZero:
                    return Math.Max(x,y) == radius || (index > 0 && x <= radius && y <= radius);
                case BossSkillKind.Avalanche:
                    return Math.Abs(cell.Y - a.Y) <= radius || (index > 0 && cell.Y == (index == 1 ? b.Y : c.Y));
                case BossSkillKind.Heavenfall:
                    return x == y || x <= radius || y <= radius;
                case BossSkillKind.FinalEdict:
                    return Math.Max(x, y) <= radius && ((cell.X + cell.Y + index) & 1) == 0;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "未接线的新招式");
            }
        }
        /// <summary>大棋的任意身体格相交即可命中，但名单里只加入一次，避免按占格重复扣血。</summary>
        private static bool Touches(Piece p, BossSkillKind kind, BoardPos a, BoardPos b, BoardPos c, int radius, int index)
        {
            for (int y = 0; y < p.Size; y++)
            {
                for (int x = 0; x < p.Size; x++)
                {
                    if (Contains(kind,a,b,c,radius,index,new BoardPos(p.Pos.X+x,p.Pos.Y+y))) { return true; }
                }
            }
            return false;
        }
        /// <summary>先冻结名单再交给服务扣血，死亡移除不会破坏棋盘枚举。</summary>
        public static void Collect(BoardModel board, BossSkillInfo info, List<Piece> result)
        {
            result.Clear();
            foreach (Piece p in board.Pieces)
            {
                if (p.Side == GameSide.Player && p.IsAlive && !p.BossHidden && Touches(p,info.Kind,info.A,info.B,info.C,info.Radius,info.Index)) { result.Add(p); }
            }
        }
    }
}

// 九种新增技能共用的格子几何。规则与预警都调用Contains，避免画红框的位置和真正掉血的位置不一致。
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Battle
{
    public static class BossPatternGeometry
    {
        /// <summary>计划只存坐标。优先锁定高攻击玩家，三枚不同目标作为流星/连锁的固定落点，预警后不追人。</summary>
        public static bool TryPlan(BoardModel board, Piece boss, BossSkillKind kind, out BossSkillPlan plan)
        {
            plan = new BossSkillPlan { Kind = kind, A = boss.Pos, B = boss.Pos, C = boss.Pos };
            Piece a = null, b = null, c = null;
            foreach (Piece p in board.Pieces)
            {
                if (p.Side != GameSide.Player || !(p.IsAlive && !p.BossHidden)) { continue; }
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
                if (p.Side != GameSide.Player || !(p.IsAlive && !p.BossHidden)) { continue; }
                for (int index = 0; index < def.Count; index++)
                {
                    if (Touches(p, kind, boss.Pos, boss.Size, plan, def.Radius, index)) { hits++; break; }
                }
            }
            plan.Score = hits * 8f;
            return hits > 0;
        }

        /// <summary>判定一次命中的固定格集合；棋盘以外的格子由调用者不枚举，自然裁剪到9×9。</summary>
        public static bool Contains(BossSkillKind kind, BoardPos origin, int originSize, BossSkillPlan plan, int radius, int index, BoardPos cell)
        {
            BoardPos center = new BoardPos(origin.X + originSize / 2, origin.Y + originSize / 2);
            BoardPos point = index % 3 == 0 ? plan.A : index % 3 == 1 ? plan.B : plan.C;
            int dx = Math.Abs(cell.X - center.X), dy = Math.Abs(cell.Y - center.Y);
            int ax = Math.Abs(cell.X - plan.A.X), ay = Math.Abs(cell.Y - plan.A.Y);
            switch (kind)
            {
                case BossSkillKind.MeteorShower: return Math.Max(Math.Abs(cell.X - point.X), Math.Abs(cell.Y - point.Y)) <= radius;
                case BossSkillKind.CrossLaser: return cell.X == center.X || cell.Y == center.Y;
                case BossSkillKind.FrostNova: return Math.Max(dx, dy) == radius + index;
                case BossSkillKind.ChainLightning: return cell == point;
                case BossSkillKind.PoisonMarsh: return Math.Max(ax, ay) <= radius;
                case BossSkillKind.FlameWall: return cell.X == plan.A.X;
                case BossSkillKind.IceLance: return cell.Y == plan.A.Y;
                case BossSkillKind.GravityWell: return Math.Max(ax, ay) <= Math.Max(0, radius - index);
                case BossSkillKind.DoomSigil: return index == 0 ? ax == ay : Math.Max(ax, ay) <= radius;
                default: return false;
            }
        }

        private static bool Touches(Piece target, BossSkillKind kind, BoardPos origin, int originSize, BossSkillPlan plan, int radius, int index)
        {
            for (int y = 0; y < target.Size; y++)
            {
                for (int x = 0; x < target.Size; x++)
                {
                    if (Contains(kind, origin, originSize, plan, radius, index, new BoardPos(target.Pos.X + x, target.Pos.Y + y))) { return true; }
                }
            }
            return false;
        }

        /// <summary>大棋只入名单一次。伤害会移除棋盘对象，必须先收集到状态自己的缓冲再结算。</summary>
        public static void Collect(BoardModel board, BoardPos origin, int originSize, BossSkillPlan plan, int radius, int index, List<Piece> destination)
        {
            destination.Clear();
            foreach (Piece target in board.Pieces)
            {
                if (target.Side == GameSide.Player && target.IsAlive && Touches(target, plan.Kind, origin, originSize, plan, radius, index)) { destination.Add(target); }
            }
        }
    }
}

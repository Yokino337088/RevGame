// 专属技能的唯一格子几何：AI、真实命中、地面预警必须调用同一判定，避免“看着躲开仍掉血”。
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Battle
{
    // 专属技能的目标/身体相交几何，AI评分与真实预警共用。今日直接遍历实盘筛Player，结果规则保持原样。
    public static class BossExclusiveGeometry
    {
        /// <summary>先锁定目标，再蓄力。黑洞只选择已经成眼的玩家棋群，不偷偷改成任意吞棋。</summary>
        // 即时同步读取当前成员，返回值类型坐标计划；不要把枚举中的Piece引用保留到蓄力结束。
        public static bool TryPlan(BoardModel board, Piece boss, BossSkillKind kind, out BossSkillPlan plan)
        {
            plan = new BossSkillPlan { Kind = kind, A = boss.Pos, B = boss.Pos, C = boss.Pos };
            Piece a = null, b = null, c = null;
            foreach (Piece p in board.Pieces)
            {
                if (p.Side != GameSide.Player) { continue; }
                if (!p.IsAlive || p.BossHidden) { continue; }
                if (kind == BossSkillKind.BlackHole && p.EyeCount <= 0) { continue; }
                if (kind == BossSkillKind.MireChains && !p.Def.IsRanged) { continue; }
                if (a == null || p.Def.Attack > a.Def.Attack) { c = b; b = a; a = p; }
                else if (b == null || p.Def.Attack > b.Def.Attack) { c = b; b = p; }
                else if (c == null) { c = p; }
            }
            if (a == null) { return false; }
            plan.A = kind == BossSkillKind.StarStorm ? new BoardPos(board.Size / 2, board.Size / 2) : a.Pos;
            plan.B = b == null ? a.Pos : b.Pos;
            plan.C = c == null ? plan.B : c.Pos;
            plan.Score = 20f; // 算法相对评分；轮换次数优先于评分，不参与伤害数值。
            return true;
        }

        /// <summary>index是当前伤害段。风暴为中心向外的方环；全盘边界由棋盘枚举自然裁剪。</summary>
        public static bool Contains(BossSkillKind kind, BoardPos a, BoardPos b, BoardPos c, int radius, int index, BoardPos cell)
        {
            if (BossSkillCatalog.IsExpanded(kind))
            {
                return BossExpandedGeometry.Contains(kind,a,b,c,radius,index,cell);
            }
            int x = Math.Abs(cell.X - a.X), y = Math.Abs(cell.Y - a.Y);
            switch (kind)
            {
                case BossSkillKind.StarStorm: return Math.Max(x, y) == radius + index;
                case BossSkillKind.SunBrand:
                    BoardPos mark = index % 3 == 0 ? a : index % 3 == 1 ? b : c;
                    return Math.Max(Math.Abs(cell.X - mark.X), Math.Abs(cell.Y - mark.Y)) <= radius;
                case BossSkillKind.TidalSurge: return cell.Y == a.Y;
                case BossSkillKind.DivineCross: return x <= radius || y <= radius;
                case BossSkillKind.GlacialAegis: return true;
                default: return Math.Max(x, y) <= radius;
            }
        }

        /// <summary>按身体格相交判断，大棋只收一次；名单先冻结，之后服务才可位移或扣血。</summary>
        public static bool Touches(Piece p, BossSkillInfo info)
        {
            for (int y = 0; y < p.Size; y++)
            {
                for (int x = 0; x < p.Size; x++)
                {
                    if (Contains(info.Kind, info.A, info.B, info.C, info.Radius, info.Index, new BoardPos(p.Pos.X + x, p.Pos.Y + y))) { return true; }
                }
            }
            return false;
        }
        // result归技能调用方持有，先Clear再收集；名单冻结后服务才可移棋或扣血，避免枚举字典时修改它。
        public static void Collect(BoardModel board, BossSkillInfo info, List<Piece> result)
        {
            result.Clear();
            foreach (Piece p in board.Pieces)
            {
                if (p.Side != GameSide.Player) { continue; }
                if (p.IsAlive && !p.BossHidden && Touches(p, info)) { result.Add(p); }
            }
        }
    }
}

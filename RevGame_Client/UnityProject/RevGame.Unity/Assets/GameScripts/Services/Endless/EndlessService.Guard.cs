// ============================================================
// EndlessService.Guard.cs —— 盾战防御区与刺客直线突刺（新手先读这段）
//
// 【盾战防御区】每个己方盾战以自身为中心铺一块 ShieldZoneSize×ShieldZoneSize 的区域（4×4 无法以单格为正中心，
//   约定为 x-1…x+2、y-1…y+2：二星 2×2 盾战正好居中，一星偏右上半格）。多块区按“并集”合并，
//   区内棋子受伤时防御 +ShieldDefenseBonus（1 = +100%，伤害减半），重叠也只算一次。
//   每次真的减免了伤害就写一条“格挡回执”，表现层让那枚棋子的护盾泡泡波动一下。
// 【刺客突刺】刺客不再用普攻射界：每次出手在上下左右四个方向里挑敌人最多的一条线，
//   对 AssassinRange 格内这条线上所有敌人造成贯穿伤害，并写一条突刺回执给表现层画尖刺冲击。
// 数值来自 Excel EndlessRule。防御区和突刺均以棋子当前站位为起点。
// ============================================================
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>刺客突刺回执的路线号（7 剑气、9 组合技已占用）。</summary>
        public const int ThrustRoute = 8;

        /// <summary>盾战防御区的并集，下标 = y * 9 + x。每个模拟步重算，表现层读它画区域。</summary>
        public readonly bool[] ShieldZone = new bool[81];
        /// <summary>防御区形状变化时 +1，表现层只在版本变化时重新上传 81 格遮罩。</summary>
        public int ShieldZoneVersion
        {
            get;
            private set;
        }
        /// <summary>格挡回执（防御区减免了一次伤害），表现层据此让护盾泡泡波动；环形缓冲只影响画面。</summary>
        public readonly EndlessBlockReceipt[] BlockReceipts = new EndlessBlockReceipt[64];
        public int BlockSerial
        {
            get;
            private set;
        }
        private readonly bool[] _zoneScratch = new bool[81];

        /// <summary>盾战防御区的左下角与边长（格）。4×4 时为 x-1…x+2，见文件头说明。</summary>
        public static void ShieldZoneRect(Piece p, out int x0, out int y0, out int size)
        {
            size = N("ShieldZoneSize");
            int offset = (size - p.Size) / 2;
            x0 = p.Pos.X - offset;
            y0 = p.Pos.Y - offset;
        }
        /// <summary>重算防御区并集；形状有变化才递增版本。盾战石子（守眼骨架）不提供防御区。</summary>
        private void RefreshShieldZones()
        {
            System.Array.Clear(_zoneScratch, 0, _zoneScratch.Length);
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                Piece p = u.Piece;
                if (u.Stone || p.Side != GameSide.Player || !p.IsAlive || p.Def.Kind != PieceKind.Shield)
                {
                    continue;
                }
                ShieldZoneRect(p, out int x0, out int y0, out int size);
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(9, y0 + size); y++)
                {
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(9, x0 + size); x++)
                    {
                        _zoneScratch[y * 9 + x] = true;
                    }
                }
            }
            bool changed = false;
            for (int i = 0; i < 81; i++)
            {
                if (ShieldZone[i] != _zoneScratch[i])
                {
                    ShieldZone[i] = _zoneScratch[i];
                    changed = true;
                }
            }
            if (changed)
            {
                ShieldZoneVersion++;
            }
        }
        /// <summary>棋子身体任意一格在防御区内就算受保护（大体型棋子只要沾边就享受加成）。</summary>
        public bool InShieldZone(Piece p)
        {
            if (p.Side != GameSide.Player)
            {
                return false;
            }
            for (int y = p.Pos.Y; y < p.Pos.Y + p.Size; y++)
            {
                for (int x = p.Pos.X; x < p.Pos.X + p.Size; x++)
                {
                    if (x >= 0 && x < 9 && y >= 0 && y < 9 && ShieldZone[y * 9 + x])
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        private void ReportBlock(int id, Vector2 position)
        {
            int serial = ++BlockSerial;
            BlockReceipts[serial % BlockReceipts.Length] = new EndlessBlockReceipt { serial = serial, id = id, position = position };
        }

        /// <summary>四个突刺方向：右、左、上、下。</summary>
        private static readonly Vector2[] ThrustDirections =
        {
            Vector2.right, Vector2.left, Vector2.up, Vector2.down
        };
        /// <summary>
        /// 刺客出手：数一数四个方向的直线上各有几个敌人，挑最多的一条（并列取先数到的），
        /// 对线上 AssassinRange 格内的全部敌人造成一次伤害（贯穿，不衰减）。一个敌人都没有就不出手，攻击计时照常重置。
        /// </summary>
        private void AssassinThrust(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            Vector2 from = Position(p);
            float length = V("AssassinRange") + (p.Size - 1) * .5f;
            int bestDirection = -1, bestCount = 0;
            for (int d = 0; d < ThrustDirections.Length; d++)
            {
                int count = CountOnRay(from, ThrustDirections[d], length);
                if (count > bestCount)
                {
                    bestCount = count;
                    bestDirection = d;
                }
            }
            if (bestDirection < 0)
            {
                return;
            }
            Vector2 ray = ThrustDirections[bestDirection];
            float damage = p.Def.Attack * p.AttackScale * p.EyeAttackMultiplier * (1 + Run.mains[5] * V("BlessingDamage") + Run.seals[5] * V("SealDamage")) * BoonDamage();
            if (Run.evolved[5])
            {
                damage *= V("EvolutionDamage");
            }
            Vector2 first = from + ray * length;
            float nearest = float.MaxValue;
            QueryRay(from, ray, length, _rayCandidates);
            for (int cursor = 0; cursor < _rayCandidates.Count; cursor++)
            {
                int i = _rayCandidates[cursor];
                EndlessHorde h = Horde[i];
                if (h.active && h.hp > 0 && OnRay(from, ray, new Vector2(h.x, h.y), length, out float along))
                {
                    HitHorde(i, damage, 5);
                    if (along < nearest)
                    {
                        nearest = along;
                        first = new Vector2(h.x, h.y);
                    }
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side == GameSide.Enemy && e.IsAlive && OnRay(from, ray, ActorPosition(Units[i]), length + (e.Size - 1) * .5f, out float along))
                {
                    HitUnit(Units[i], damage, 5);
                    if (along < nearest)
                    {
                        nearest = along;
                        first = ActorPosition(Units[i]);
                    }
                }
            }
            // 突刺回执画满整条线（看得出攻击长度），起点是刺客、终点是线尾。
            ImpactFx(from, from + ray * length, ThrustRoute, 0, p.Id, PieceKind.Assassin, true, 0);
            TryCombo(unit, first, damage);
        }
        private int CountOnRay(Vector2 from, Vector2 ray, float length)
        {
            int count = 0;
            QueryRay(from, ray, length, _rayCandidates);
            for (int cursor = 0; cursor < _rayCandidates.Count; cursor++)
            {
                int i = _rayCandidates[cursor];
                EndlessHorde h = Horde[i];
                if (h.active && h.hp > 0 && OnRay(from, ray, new Vector2(h.x, h.y), length, out _))
                {
                    count++;
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side == GameSide.Enemy && e.IsAlive && OnRay(from, ray, ActorPosition(Units[i]), length + (e.Size - 1) * .5f, out _))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>按当前棋格中心统计驻守范围。棋盘分左右两侧，每侧按 0～2、3～5、6～8 行分为三段。</summary>
        public static int ZoneOf(Vector2 position)
        {
            int side = position.x < 4 ? 0 : 1;
            int band = position.y < 2.5f ? 0 : position.y < 5.5f ? 1 : 2;
            return side * 3 + band;
        }
    }
}

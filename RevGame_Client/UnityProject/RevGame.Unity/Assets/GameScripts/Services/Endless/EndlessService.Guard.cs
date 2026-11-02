// ============================================================
// EndlessService.Guard.cs —— 近战智能驻守、盾战防御区、刺客直线突刺（新手先读这段）
//
// 【智能驻守】把战场分成 6 个区：左/右两侧 × 下/中/上三段行（0~2、3~5、6~8 行）。
//   每隔 MeleeMoveSeconds 秒重新规划一次：
//     威胁 = 区内所有敌人（外围杂兵 + 盘内入侵者）的剩余血量之和；
//     防守 = 已分到该区的近战棋“剩余血量比例”之和（满血算 1，半血算 0.5）；
//   然后贪心分配：每次挑“威胁 ÷ (1 + 防守) ÷ (1 + 距离权重 × 距离)”最高的（棋子, 区）组合，
//   分配后该区防守立刻加上这枚棋子，下一枚棋子就会倾向去别的缺人区——敌人多、守军少、守军残血的区优先补人。
//   玩家手动摆放/移动的棋子在 ManualHoldSeconds 内原地不动，但计入所在区的防守。
// 【盾战防御区】每个己方盾战以自身为中心铺一块 ShieldZoneSize×ShieldZoneSize 的区域（4×4 无法以单格为正中心，
//   约定为 x-1…x+2、y-1…y+2：二星 2×2 盾战正好居中，一星偏右上半格）。多块区按“并集”合并，
//   区内棋子受伤时防御 +ShieldDefenseBonus（1 = +100%，伤害减半），重叠也只算一次。
//   每次真的减免了伤害就写一条“格挡回执”，表现层让那枚棋子的护盾泡泡波动一下。
// 【刺客突刺】刺客不再用普攻射界：每次出手在上下左右四个方向里挑敌人最多的一条线，
//   对 AssassinRange 格内这条线上所有敌人造成贯穿伤害，并写一条突刺回执给表现层画尖刺冲击。
// 数值全部来自 Excel EndlessRule；这里留在代码里的只有分区结构（6 区）和评分里的相对权重。
// ============================================================
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>刺客突刺回执的路线号（7 剑气、9 组合技已占用）。</summary>
        public const int ThrustRoute = 8;
        /// <summary>驻守分区数：左右两侧 × 三段行。这是棋盘结构，不是数值。</summary>
        public const int ZoneCount = 6;
        /// <summary>评分里距离的相对权重：越大越倾向就近驻守。算法内部权重，不进表。</summary>
        private const float ZoneDistanceWeight = .15f;

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
        private readonly float[] _zoneThreat = new float[ZoneCount], _zoneDefense = new float[ZoneCount];
        private float _zonePlanAt;

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
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
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
                if (e.Side == GameSide.Enemy && e.IsAlive && OnRay(from, ray, Position(e), length + (e.Size - 1) * .5f, out float along))
                {
                    HitUnit(Units[i], damage, 5);
                    if (along < nearest)
                    {
                        nearest = along;
                        first = Position(e);
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
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (h.active && h.hp > 0 && OnRay(from, ray, new Vector2(h.x, h.y), length, out _))
                {
                    count++;
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side == GameSide.Enemy && e.IsAlive && OnRay(from, ray, Position(e), length + (e.Size - 1) * .5f, out _))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>位置属于哪个驻守区：x 小于中心列算左侧；行 0~2/3~5/6~8 分三段。外围（棋盘外）的敌人也按同一规则归区。</summary>
        public static int ZoneOf(Vector2 position)
        {
            int side = position.x < 4 ? 0 : 1;
            int band = position.y < 2.5f ? 0 : position.y < 5.5f ? 1 : 2;
            return side * 3 + band;
        }
        /// <summary>每个区的驻守点：左侧第 1 列 / 右侧第 7 列，三段行的中间一行。没有敌人可追时走到这里守门。</summary>
        public static Vector2 ZoneAnchor(int zone)
        {
            return new Vector2(zone < 3 ? 1 : 7, zone % 3 * 3 + 1);
        }
        private static bool CanGuard(EndlessUnit u)
        {
            return u.Piece.Side == GameSide.Player && u.Piece.IsAlive && !u.Stone && !u.Summoned && IsMelee(u.Piece.Def.Kind);
        }
        /// <summary>
        /// 规划驻守区（每 MeleeMoveSeconds 秒最多一次）。先统计威胁，再把“暂时不能动”的棋子（手动保持/被锁）计入所在区防守，
        /// 最后对可动的近战棋做贪心分配。全程只用预分配数组，不产生垃圾。
        /// </summary>
        private void PlanZones()
        {
            if (Run.seconds < _zonePlanAt)
            {
                return;
            }
            _zonePlanAt = Run.seconds + V("MeleeMoveSeconds");
            System.Array.Clear(_zoneThreat, 0, ZoneCount);
            System.Array.Clear(_zoneDefense, 0, ZoneCount);
            bool anyThreat = false;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (h.active && h.hp > 0)
                {
                    _zoneThreat[ZoneOf(new Vector2(h.x, h.y))] += h.hp;
                    anyThreat = true;
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                if (u.Piece.Side == GameSide.Enemy && u.Piece.IsAlive)
                {
                    _zoneThreat[ZoneOf(Position(u.Piece))] += u.Piece.Hp;
                    anyThreat = true;
                }
                u.Zone = -1;
            }
            int free = 0;
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                if (!CanGuard(u))
                {
                    continue;
                }
                if (u.HoldUntil > Run.seconds || u.Piece.IsLocked)
                {
                    u.Zone = ZoneOf(Position(u.Piece));
                    _zoneDefense[u.Zone] += u.Piece.HpRatio;
                }
                else
                {
                    free++;
                }
            }
            if (!anyThreat)
            {
                return;
            }
            for (int round = 0; round < free; round++)
            {
                EndlessUnit bestUnit = null;
                int bestZone = -1;
                float bestScore = 0;
                for (int i = 0; i < Units.Count; i++)
                {
                    EndlessUnit u = Units[i];
                    if (!CanGuard(u) || u.Zone >= 0)
                    {
                        continue;
                    }
                    Vector2 at = Position(u.Piece);
                    for (int z = 0; z < ZoneCount; z++)
                    {
                        if (_zoneThreat[z] <= 0)
                        {
                            continue;
                        }
                        float score = _zoneThreat[z] / (1 + _zoneDefense[z]) / (1 + ZoneDistanceWeight * Vector2.Distance(at, ZoneAnchor(z)));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestUnit = u;
                            bestZone = z;
                        }
                    }
                }
                if (bestUnit == null)
                {
                    return;
                }
                bestUnit.Zone = bestZone;
                _zoneDefense[bestZone] += bestUnit.Piece.HpRatio;
            }
        }
        /// <summary>
        /// 近战棋朝自己负责的区移动一步：区里有敌人就追最近的那个，没有就回驻守点。
        /// 已经够得着就原地打：剑士/重剑看剑气半径，盾战贴到 1.5 格内当墙，刺客与目标同行/同列且在突刺长度内即可。
        /// </summary>
        private void MeleeAdvance(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            if (!CanGuard(unit) || p.IsLocked || unit.HoldUntil > Run.seconds || unit.MoveAt > Run.seconds || unit.Zone < 0)
            {
                return;
            }
            unit.MoveAt = Run.seconds + V("MeleeMoveSeconds");
            Vector2 center = Position(p);
            Vector2 target = ZoneAnchor(unit.Zone);
            bool enemy = NearestEnemyInZone(center, unit.Zone, out Vector2 foe);
            if (enemy)
            {
                target = foe;
            }
            if (enemy && InReach(p, center, foe))
            {
                return;
            }
            if (enemy && p.Def.Kind == PieceKind.Assassin)
            {
                // 刺客要和目标站成一条线：走到目标同一行、朝棋盘中心偏 2 格的位置，突刺正好扎穿一串。
                target = new Vector2(Mathf.Clamp(foe.x + (foe.x < 4 ? 2 : -2), 0, 8), foe.y);
            }
            StepToward(unit, target);
        }
        private bool InReach(Piece p, Vector2 center, Vector2 foe)
        {
            Vector2 delta = foe - center;
            switch (p.Def.Kind)
            {
                case PieceKind.Assassin:
                    return (Mathf.Abs(delta.x) < .5f || Mathf.Abs(delta.y) < .5f) && delta.magnitude <= V("AssassinRange");
                case PieceKind.Shield:
                    // 盾击打的是自身为中心的方形范围（3×3），所以用切比雪夫距离；留 15% 余量，走到“稳稳打得到”的位置。
                    return Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y)) <= ShieldReach(p) * .85f;
                default:
                    return delta.magnitude <= AuraRadius(p) * .85f;
            }
        }
        /// <summary>按棋子自己的走法挑一步离目标最近的合法格子（Board.Move，围棋规则照常刷新）；走不动就原地等下一轮。</summary>
        private void StepToward(EndlessUnit unit, Vector2 target)
        {
            Piece p = unit.Piece;
            float offset = (p.Size - 1) * .5f;
            float best = (Position(p) - target).sqrMagnitude;
            MoveRules.GetMoveTargets(Board, p, _targets);
            BoardPos chosen = p.Pos;
            for (int i = 0; i < _targets.Count; i++)
            {
                BoardPos pos = _targets[i];
                float distance = (new Vector2(pos.X + offset, pos.Y + offset) - target).sqrMagnitude;
                if (distance < best && Fits(pos, p.Size, GameSide.Player, p))
                {
                    best = distance;
                    chosen = pos;
                }
            }
            if (!chosen.Equals(p.Pos) && Board.Move(p, chosen))
            {
                RefreshRules();
                unit.StationarySince = Run.seconds;
                unit.SteadyShield = false;
            }
        }
        /// <summary>某个区里离 from 最近的敌人（外围杂兵/入侵者或盘内敌方棋子）。</summary>
        private bool NearestEnemyInZone(Vector2 from, int zone, out Vector2 target)
        {
            target = from;
            float best = float.MaxValue;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                Vector2 at = new Vector2(h.x, h.y);
                if (!h.active || h.hp <= 0 || ZoneOf(at) != zone)
                {
                    continue;
                }
                float distance = (at - from).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    target = at;
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side != GameSide.Enemy || !e.IsAlive || ZoneOf(Position(e)) != zone)
                {
                    continue;
                }
                float distance = (Position(e) - from).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    target = Position(e);
                }
            }
            return best < float.MaxValue;
        }
    }
}

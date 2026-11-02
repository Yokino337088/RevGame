// ============================================================
// EndlessService.Feel.cs —— 割草手感：近战剑气 + 远程组合技（新手先读这段）
//
// 参考《吸血鬼幸存者》的两条核心设计：
//   1. 被动武器自动输出：玩家不用操作，近战棋身边常驻一圈剑气，每隔一小段时间切割圈内所有敌人；
//   2. 组合进化：两种棋子同时在场就解锁组合技（例如弓箭手 + 火法师 = 每 3 次普攻放一发贯穿火焰箭），
//      鼓励玩家凑搭配，也让满屏弹幕越打越密。
//
// 【数值】半径、间隔、伤害倍率、目标上限、组合技的触发次数/倍率/半径/数量全部来自 Excel
//         （EndlessRule 的 Aura* 行和 EndlessCombo 表），代码里只有“怎么打”的几何规则。
// 【表现】这里只写回执（Impacts / DamageReceipts），由 EndlessArena 用六章特效 + 剑气 Shader 画出来；
//         回执被覆盖只会少画一下，伤害在这里已经结算完毕。
// 【确定性】随机目标只用 Next()（存档里的随机源），读档后同一次组合技不会抽出不同目标。
// ============================================================
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>剑气切割和剑气闪光的回执路线号（0~6 是祝福路线，7 留给剑气）。</summary>
        public const int AuraRoute = 7;
        /// <summary>组合技喊招回执的路线号。</summary>
        public const int ComboRoute = 9;
        /// <summary>每次剑气最多给几条“斩击线”回执：只影响画面密度，所有圈内敌人都会挨刀。</summary>
        private const int AuraSlashVisuals = 2;
        /// <summary>弹道和敌人的横向命中宽度（格）。与原有贯穿判定（0.7）同一量级，是几何容差不是数值。</summary>
        private const float RayWidth = .6f;
        /// <summary>组合技扇射的半张角（度），三发时为 -18°/0°/+18°。纯几何外观，不改伤害。</summary>
        private const float SpreadHalfAngle = 18f;
        /// <summary>箭雨从目标正上方多高处落下（格），只是画面起点。</summary>
        private const float RainHeight = 3.5f;

        /// <summary>本局累计释放的组合技次数，验收用只读计数（开局清零）。</summary>
        public int ComboCasts
        {
            get;
            private set;
        }

        /// <summary>近战棋：剑士、盾战、重剑、刺客。它们都会被智能索敌派去驻守（见 EndlessService.Guard）。</summary>
        public static bool IsMelee(PieceKind kind)
        {
            return kind == PieceKind.Swordman || kind == PieceKind.Shield || kind == PieceKind.HeavySword || kind == PieceKind.Assassin;
        }
        /// <summary>
        /// 只有剑士和重剑带环绕剑气。盾战改为环绕盾牌 + 防御区（见 Guard），刺客改为直线贯穿突刺，都不切剑气。
        /// </summary>
        public static bool IsSwordAuraKind(PieceKind kind)
        {
            return kind == PieceKind.Swordman || kind == PieceKind.HeavySword;
        }
        /// <summary>这枚棋子此刻是否显示剑气：己方、剑士/重剑、不是石子。表现层每帧用它决定画不画剑气。</summary>
        public static bool HasAura(EndlessUnit unit)
        {
            return unit.Piece.Side == GameSide.Player && !unit.Stone && unit.Piece.IsAlive && IsSwordAuraKind(unit.Piece.Def.Kind);
        }
        /// <summary>
        /// 剑气半径（格）：一星取 AuraRadius，每升一星按 AuraStarRadius 放大，再乘领主奖励“领域扩张”；
        /// 表现层用同一个值画圈，保证“看到多大就切多大”。
        /// </summary>
        public float AuraRadius(Piece p)
        {
            float boon = Run == null ? 0 : Run.boonAura;
            return (V("AuraRadius") * (1 + ((int)p.Def.Star - 1) * V("AuraStarRadius")) + (p.Size - 1) * .5f) * (1 + boon);
        }

        /// <summary>
        /// 剑气切割：到点就对圈内敌人（外围杂兵 + 盘内入侵者）各造成一次伤害，最多 AuraTargetLimit 个。
        /// 伤害吃近战祝福（路线 5）和进化加成，和普攻一样会被眼位攻击倍率放大。
        /// </summary>
        private void UpdateAura(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            if (unit.Stone || !IsSwordAuraKind(p.Def.Kind) || unit.AuraAt > Run.seconds || p.IsLocked)
            {
                return;
            }
            unit.AuraAt = Run.seconds + V("AuraTickSeconds");
            float radius = AuraRadius(p);
            float damage = p.Def.Attack * p.AttackScale * p.EyeAttackMultiplier * V("AuraDamageRatio") * (1 + Run.mains[5] * V("BlessingDamage")) * BoonDamage();
            if (Run.evolved[5])
            {
                damage *= V("EvolutionDamage");
            }
            Vector2 center = Position(p);
            float squared = radius * radius;
            int limit = N("AuraTargetLimit"), hits = 0, shown = 0;
            for (int i = 0; i < Run.horde.Length && hits < limit; i++)
            {
                EndlessHorde h = Run.horde[i];
                Vector2 at = new Vector2(h.x, h.y);
                if (!h.active || h.hp <= 0 || (at - center).sqrMagnitude > squared)
                {
                    continue;
                }
                HitHorde(i, damage, 5);
                hits++;
                if (shown < AuraSlashVisuals)
                {
                    ImpactFx(center, at, AuraRoute, 0, p.Id, p.Def.Kind, true, 0);
                    shown++;
                }
            }
            for (int i = 0; i < Units.Count && hits < limit; i++)
            {
                EndlessUnit enemy = Units[i];
                Piece e = enemy.Piece;
                if (e.Side != GameSide.Enemy || !e.IsAlive)
                {
                    continue;
                }
                float reach = radius + (e.Size - 1) * .5f;
                if ((Position(e) - center).sqrMagnitude > reach * reach)
                {
                    continue;
                }
                HitUnit(enemy, damage, 5);
                hits++;
                if (shown < AuraSlashVisuals)
                {
                    ImpactFx(center, Position(e), AuraRoute, 0, p.Id, p.Def.Kind, true, 0);
                    shown++;
                }
            }
            if (hits > 0)
            {
                // 剑气闪光回执：半径>0、起终点相同，表现层只让这枚棋子的剑气亮一下，不画范围特效。
                ImpactFx(center, center, AuraRoute, radius, p.Id, p.Def.Kind, true, 0);
            }
        }

        /// <summary>
        /// 组合技表解析后的缓存：棋种名/效果名只在表实例变化时解析一次。
        /// 普攻是高频路径，每次都 Enum.TryParse 字符串会产生开销，所以缓存成值类型数组。
        /// </summary>
        private struct ComboRule
        {
            public EndlessCombo Row;
            public PieceKind KindA, KindB;
            public EndlessComboEffect Effect;
        }
        private ComboRule[] _combos;
        private EndlessComboTable _comboSource;
        private ComboRule[] Combos()
        {
            EndlessComboTable table = RevDataTableManager.Get<EndlessComboTable>();
            if (table == null)
            {
                return null;
            }
            if (_combos == null || _comboSource != table)
            {
                _comboSource = table;
                _combos = new ComboRule[table.All.Count];
                for (int i = 0; i < _combos.Length; i++)
                {
                    EndlessCombo row = table.All[i];
                    _combos[i] = new ComboRule { Row = row, KindA = EndlessConfig.ComboKindA(row), KindB = EndlessConfig.ComboKindB(row), Effect = EndlessConfig.ComboEffect(row) };
                }
            }
            return _combos;
        }

        /// <summary>
        /// 普攻命中后调用：场上有搭档才计数，计数到 Every 的整数倍就释放对应组合技。
        /// 同一棋种可能是多条组合技的触发者（弓箭手：火焰箭、箭雨），各按自己的 Every 触发。
        /// </summary>
        private void TryCombo(EndlessUnit unit, Vector2 target, float damage)
        {
            ComboRule[] combos = Combos();
            if (combos == null)
            {
                return;
            }
            PieceKind kind = unit.Piece.Def.Kind;
            bool counted = false;
            for (int i = 0; i < combos.Length; i++)
            {
                if (combos[i].KindA == kind && HasPlayerKind(combos[i].KindB, unit))
                {
                    counted = true;
                    break;
                }
            }
            if (!counted)
            {
                return;
            }
            // 只要有任意一条组合技的搭档在场就计数一次，再看哪些组合技到点。
            unit.ComboCount++;
            for (int i = 0; i < combos.Length; i++)
            {
                ComboRule rule = combos[i];
                if (rule.KindA == kind && unit.ComboCount % rule.Row.Every == 0 && HasPlayerKind(rule.KindB, unit))
                {
                    CastCombo(unit, rule, target, damage * rule.Row.Power * (1 + Run.boonCombo));
                }
            }
        }
        /// <summary>场上（己方、非石子、存活）是否有这种棋；self 本身不算搭档。</summary>
        private bool HasPlayerKind(PieceKind kind, EndlessUnit self)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                if (u != self && !u.Stone && u.Piece.Side == GameSide.Player && u.Piece.IsAlive && u.Piece.Def.Kind == kind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>释放一次组合技：先写喊招回执（飘字 + 音效），再按效果类型结算伤害并写特效回执。</summary>
        private void CastCombo(EndlessUnit unit, ComboRule rule, Vector2 target, float damage)
        {
            EndlessCombo row = rule.Row;
            Piece p = unit.Piece;
            Vector2 from = Position(p);
            int route = RouteOf(p.Def.Kind);
            ImpactFx(from, from, ComboRoute, 0, p.Id, p.Def.Kind, true, row.Id + 1);
            ComboCasts++;
            switch (rule.Effect)
            {
                case EndlessComboEffect.Pierce:
                    ComboPierce(from, target, damage, row.Radius, route);
                    break;
                case EndlessComboEffect.Spread:
                    ComboSpread(from, target, damage, row.Count, route);
                    break;
                case EndlessComboEffect.Rain:
                    ComboRain(from, damage, row.Radius, row.Count, route, p);
                    break;
                case EndlessComboEffect.Burst:
                    Area(target, row.Radius, damage, route, -1, false);
                    SlowAround(target, row.Radius);
                    ImpactFx(target, target, 4, row.Radius, 0, PieceKind.MageWater, true, 0);
                    ImpactFx(target, target, 1, row.Radius, 0, PieceKind.MageFire, true, 0);
                    break;
                case EndlessComboEffect.Pillar:
                    Area(target, row.Radius, damage, route, -1, false);
                    ImpactFx(target, target, 2, row.Radius, 0, PieceKind.Priest, true, 0);
                    break;
                case EndlessComboEffect.Chain:
                    ComboChain(from, damage, row.Radius, row.Count, route, p.Def.Kind);
                    break;
                default:
                    Area(from, row.Radius, damage, route, -1, false);
                    ImpactFx(from, from, 5, row.Radius, 0, PieceKind.HeavySword, true, 0);
                    break;
            }
        }
        /// <summary>伤害归属路线：与 PlayerAttack 同一规则（火 1、圣光 2、水 4、远程 0、近战 5）。</summary>
        private static int RouteOf(PieceKind kind)
        {
            return kind == PieceKind.MageFire ? 1 : kind == PieceKind.Priest ? 2 : kind == PieceKind.MageWater ? 4 : PieceCatalog.Get(kind, StarTier.One).IsRanged ? 0 : 5;
        }

        /// <summary>贯穿火焰箭：沿射线命中射程内所有敌人并点燃，末端按半径爆炸。</summary>
        private void ComboPierce(Vector2 from, Vector2 target, float damage, float radius, int route)
        {
            Vector2 ray = (target - from).sqrMagnitude > .0001f ? (target - from).normalized : Vector2.right;
            float length = V("OuterRange");
            float farthest = Mathf.Min(length, Vector2.Distance(from, target));
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (!h.active || h.hp <= 0 || !OnRay(from, ray, new Vector2(h.x, h.y), length, out float along))
                {
                    continue;
                }
                HitHorde(i, damage, route);
                Ignite(i, damage);
                farthest = Mathf.Max(farthest, along);
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side == GameSide.Enemy && e.IsAlive && OnRay(from, ray, Position(e), length, out float along))
                {
                    HitUnit(Units[i], damage, route);
                    farthest = Mathf.Max(farthest, along);
                }
            }
            Vector2 end = from + ray * farthest;
            ImpactFx(from, end, 1, 0, 0, PieceKind.MageFire, true, 0);
            if (radius > 0)
            {
                Area(end, radius, damage, route, -1, false);
                ImpactFx(end, end, 1, radius, 0, PieceKind.MageFire, true, 0);
            }
        }
        /// <summary>扇射：以目标方向为中线均匀张开 count 发，每发命中射线上最近的一个敌人并减速。</summary>
        private void ComboSpread(Vector2 from, Vector2 target, float damage, int count, int route)
        {
            Vector2 forward = (target - from).sqrMagnitude > .0001f ? (target - from).normalized : Vector2.right;
            float length = V("OuterRange");
            for (int n = 0; n < count; n++)
            {
                float angle = count == 1 ? 0 : Mathf.Lerp(-SpreadHalfAngle, SpreadHalfAngle, n / (float)(count - 1));
                Vector2 ray = Quaternion.Euler(0, 0, angle) * forward;
                int best = -1;
                float nearest = float.MaxValue;
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    if (h.active && h.hp > 0 && OnRay(from, ray, new Vector2(h.x, h.y), length, out float along) && along < nearest)
                    {
                        best = i;
                        nearest = along;
                    }
                }
                Vector2 end = from + ray * (best >= 0 ? nearest : length);
                if (best >= 0)
                {
                    HitHorde(best, damage, route);
                    EndlessHorde h = Run.horde[best];
                    h.slowUntil = Run.seconds + V("FreezeSeconds");
                    Run.horde[best] = h;
                }
                ImpactFx(from, end, 0, 0, 0, PieceKind.Crossbow, true, 0);
            }
        }
        /// <summary>箭雨：在射程内随机挑 count 只活着的外围敌人，每支箭砸中后再波及半径内的敌人。</summary>
        private void ComboRain(Vector2 from, float damage, float radius, int count, int route, Piece source)
        {
            float range = V("OuterRange");
            for (int n = 0; n < count; n++)
            {
                int candidates = 0;
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    if (h.active && h.hp > 0 && Mathf.Max(Mathf.Abs(h.x - from.x), Mathf.Abs(h.y - from.y)) <= range)
                    {
                        candidates++;
                    }
                }
                if (candidates == 0)
                {
                    return;
                }
                // 第 pick 个候选；Next 来自存档随机源，读档后结果一致。
                int pick = Next(candidates);
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    if (!h.active || h.hp <= 0 || Mathf.Max(Mathf.Abs(h.x - from.x), Mathf.Abs(h.y - from.y)) > range)
                    {
                        continue;
                    }
                    if (pick-- > 0)
                    {
                        continue;
                    }
                    Vector2 at = new Vector2(h.x, h.y);
                    HitHorde(i, damage, route);
                    if (radius > 0)
                    {
                        Area(at, radius, damage, route, -1, false);
                    }
                    ImpactFx(at + Vector2.up * RainHeight, at, 0, 0, 0, PieceKind.Archer, true, 0);
                    break;
                }
            }
        }
        /// <summary>连斩：从自身出发，依次瞬斩半径内最近的、还没被斩过的敌人，共 count 次。</summary>
        private void ComboChain(Vector2 from, float damage, float radius, int count, int route, PieceKind kind)
        {
            int previous = -1;
            Vector2 cursor = from;
            float squared = radius * radius;
            for (int n = 0; n < count && n < _chainHit.Length; n++)
            {
                int best = -1;
                float nearest = squared;
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    if (!h.active || h.hp <= 0 || i == previous)
                    {
                        continue;
                    }
                    bool seen = false;
                    for (int k = 0; k < n; k++)
                    {
                        seen |= _chainHit[k] == i;
                    }
                    float distance = (new Vector2(h.x, h.y) - from).sqrMagnitude;
                    if (!seen && distance <= nearest)
                    {
                        best = i;
                        nearest = distance;
                    }
                }
                if (best < 0)
                {
                    return;
                }
                _chainHit[n] = best;
                previous = best;
                Vector2 at = new Vector2(Run.horde[best].x, Run.horde[best].y);
                HitHorde(best, damage, route);
                ImpactFx(cursor, at, 5, 0, 0, kind, true, 0);
                cursor = at;
            }
        }
        /// <summary>点燃外围敌人：沿用原有灼烧规则（BurnSeconds 秒、每秒 BurnRatio×伤害）。</summary>
        private void Ignite(int index, float damage)
        {
            EndlessHorde h = Run.horde[index];
            h.burnUntil = Run.seconds + V("BurnSeconds");
            h.burnDamage = damage * V("BurnRatio");
            Run.horde[index] = h;
        }
        /// <summary>半径内外围敌人减速 FreezeSeconds 秒。</summary>
        private void SlowAround(Vector2 center, float radius)
        {
            float squared = radius * radius;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (h.active && (new Vector2(h.x, h.y) - center).sqrMagnitude <= squared)
                {
                    h.slowUntil = Run.seconds + V("FreezeSeconds");
                    Run.horde[i] = h;
                }
            }
        }
        /// <summary>点是否落在从 from 出发、方向 ray、长度 length、宽 RayWidth 的射线带里；along 返回沿射线的距离。</summary>
        private static bool OnRay(Vector2 from, Vector2 ray, Vector2 point, float length, out float along)
        {
            Vector2 delta = point - from;
            along = Vector2.Dot(delta, ray);
            return along > 0 && along <= length && Mathf.Abs(delta.x * ray.y - delta.y * ray.x) < RayWidth;
        }
    }
}

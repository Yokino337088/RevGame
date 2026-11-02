// ============================================================
// EndlessService.Snowball.cs —— 波次、领主奖励滚雪球、盾战盾击（新手先读这段）
//
// 【波次】一波 = MinuteSeconds 秒（本轮配成 20 秒）。第 N 波（从 0 数）的敌人血量 ×(1 + N×WaveHpGrowth)、
//        攻击 ×(1 + N×WaveAttackGrowth)，与原有的巡回成长相乘：越打越久，敌人越强，紧张感持续上升。
//        每 BossInterval 秒（= 10 波）来一只领主。
// 【滚雪球】每击杀一只领主，从 Excel EndlessBoon 表里随机抽一条强力增益，永久累加到本局（攻击、攻速、生命、范围、组合技、棋心）。
//        随机走存档随机源，读档后结果一致；增益累加值存在 EndlessRun 里，跟存档走。
//        拿到增益时推一条“大横幅”（单独通道，HUD 右侧奖励卡用更夸张的动画弹出），告诉玩家变强了多少。
// 【盾击】盾战的环绕盾牌会打到以自身为中心 ShieldZoneSize×ShieldZoneSize（本轮 3×3）范围内的敌人，
//        间隔与剑气相同（AuraTickSeconds），单次伤害 = 攻击 × ShieldBashRatio，同样吃近战祝福和增益。
// 所有数值来自 Excel，代码里只有“怎么算”。
// ============================================================
using System;
using Revolution;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>当前是第几波（从 1 开始，给玩家看）。</summary>
        public int WaveNumber => Run == null ? 0 : (int)(Run.seconds / V("MinuteSeconds")) + 1;
        /// <summary>出生密度随每波递增并封顶；改变下一次出生间隔，不降低后期单位预算。</summary>
        public float WaveDensityScale()
        {
            return Mathf.Min(V("WaveDensityCap"), 1 + (WaveNumber - 1) * V("WaveDensityGrowth"));
        }
        /// <summary>全场现存敌人也随波提速，避免只有新生单位变快。上限由Excel控制。</summary>
        public float WaveSpeedScale()
        {
            return Mathf.Min(V("WaveSpeedCap"), 1 + (WaveNumber - 1) * V("WaveSpeedGrowth"));
        }
        /// <summary>按波次的敌人血量倍率，与出生密度和速度分别计算。</summary>
        private float WaveHpScale()
        {
            return 1 + (WaveNumber - 1) * V("WaveHpGrowth");
        }
        /// <summary>按波次的敌人攻击倍率。</summary>
        private float WaveAttackScale()
        {
            return 1 + (WaveNumber - 1) * V("WaveAttackGrowth");
        }
        /// <summary>领主奖励累计的伤害倍率（所有己方伤害都乘它）。</summary>
        private float BoonDamage()
        {
            return 1 + Run.boonAttack;
        }
        /// <summary>己方棋子生命上限倍率：纸甲稀有祝福的损失与领主奖励的加成相乘。</summary>
        private float PlayerHealthScale()
        {
            return (Run.rare[2] ? 1 - V("PaperHealthLoss") : 1) * (1 + Run.boonHealth);
        }

        /// <summary>验收/HUD 用：本局领主奖励累计次数。</summary>
        public int BoonCount => Run == null ? 0 : Run.boonCount;

        /// <summary>
        /// 击杀领主后的奖励：从 EndlessBoon 表随机抽一条，按效果累加到本局，然后推大横幅。
        /// 生命类会立刻按新上限给全体回满（SetRoundHealthScale 保持血量比例，再 Heal 补满）。
        /// </summary>
        private void GrantBossBoon()
        {
            EndlessBoonTable table = RevDataTableManager.Get<EndlessBoonTable>();
            if (table == null || table.All.Count == 0)
            {
                return;
            }
            EndlessBoon row = table.All[Next(table.All.Count)];
            EndlessBoonEffect effect = EndlessConfig.BoonEffect(row);
            string gain;
            switch (effect)
            {
                case EndlessBoonEffect.Attack:
                    Run.boonAttack += row.Value;
                    gain = "全体伤害 +" + Percent(row.Value);
                    break;
                case EndlessBoonEffect.AttackSpeed:
                    Run.boonSpeed += row.Value;
                    gain = "全体攻速 +" + Percent(row.Value);
                    break;
                case EndlessBoonEffect.Health:
                    Run.boonHealth += row.Value;
                    float scale = PlayerHealthScale();
                    for (int i = 0; i < Units.Count; i++)
                    {
                        Piece p = Units[i].Piece;
                        if (p.Side == GameSide.Player && p.IsAlive)
                        {
                            p.SetRoundHealthScale(scale);
                            p.Heal(p.MaxHp);
                        }
                    }
                    gain = "全体生命上限 +" + Percent(row.Value) + " 并回满";
                    break;
                case EndlessBoonEffect.AuraRange:
                    Run.boonAura += row.Value;
                    gain = "剑气与盾击范围 +" + Percent(row.Value);
                    break;
                case EndlessBoonEffect.ComboPower:
                    Run.boonCombo += row.Value;
                    gain = "组合技伤害 +" + Percent(row.Value);
                    break;
                default:
                    Run.heart = Mathf.Min(V("HeartHp"), Run.heart + row.Value);
                    gain = "棋心回复 " + Mathf.RoundToInt(row.Value);
                    break;
            }
            Run.boonCount++;
            PushMajorBanner("领主击破！获得「" + row.Name + "」\n" + gain, row.ColorHex);
        }
        private static string Percent(float value)
        {
            return Mathf.RoundToInt(value * 100) + "%";
        }

        /// <summary>
        /// 盾击：盾战每 AuraTickSeconds 秒打一次自身为中心的 N×N 范围（N = ShieldZoneSize，领主“领域扩张”会放大）。
        /// 外围杂兵按坐标、盘内敌人按身体中心判断是否在范围里；写最多两条盾击回执给表现层画盾击。
        /// </summary>
        private void UpdateShieldBash(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            if (unit.Stone || p.Def.Kind != PieceKind.Shield || p.IsLocked || unit.AuraAt > Run.seconds)
            {
                return;
            }
            unit.AuraAt = Run.seconds + V("AuraTickSeconds");
            float reach = ShieldReach(p);
            float damage = p.Def.Attack * p.AttackScale * p.EyeAttackMultiplier * V("ShieldBashRatio") * (1 + Run.mains[5] * V("BlessingDamage")) * BoonDamage();
            if (Run.evolved[5])
            {
                damage *= V("EvolutionDamage");
            }
            Vector2 center = Position(p);
            int hits = 0, shown = 0;
            World.Query(center.x - reach, center.y - reach, center.x + reach, center.y + reach, _auraCandidates);
            for (int cursor = 0; cursor < _auraCandidates.Count; cursor++)
            {
                int i = _auraCandidates[cursor];
                EndlessHorde h = Horde[i];
                Vector2 at = new Vector2(h.x, h.y);
                if (!h.active || h.hp <= 0 || Mathf.Abs(at.x - center.x) > reach || Mathf.Abs(at.y - center.y) > reach)
                {
                    continue;
                }
                HitHorde(i, damage, 5);
                hits++;
                if (shown < AuraSlashVisuals)
                {
                    ImpactFx(center, at, AuraRoute, 0, p.Id, PieceKind.Shield, true, 0);
                    shown++;
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece e = Units[i].Piece;
                if (e.Side != GameSide.Enemy || !e.IsAlive)
                {
                    continue;
                }
                Vector2 at = ActorPosition(Units[i]);
                float bodyReach = reach + (e.Size - 1) * .5f;
                if (Mathf.Abs(at.x - center.x) > bodyReach || Mathf.Abs(at.y - center.y) > bodyReach)
                {
                    continue;
                }
                HitUnit(Units[i], damage, 5);
                hits++;
                if (shown < AuraSlashVisuals)
                {
                    ImpactFx(center, at, AuraRoute, 0, p.Id, PieceKind.Shield, true, 0);
                    shown++;
                }
            }
            if (hits > 0)
            {
                ImpactFx(center, center, AuraRoute, reach, p.Id, PieceKind.Shield, true, 0);
            }
        }
        /// <summary>盾击半宽（格）：N×N 范围的一半（3×3 → 1.5），加上大体型和领域扩张。</summary>
        public float ShieldReach(Piece p)
        {
            return (N("ShieldZoneSize") * .5f + (p.Size - 1) * .5f) * (1 + Run.boonAura);
        }
    }
}

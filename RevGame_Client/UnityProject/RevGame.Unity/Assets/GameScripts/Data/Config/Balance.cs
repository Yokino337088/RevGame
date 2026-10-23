// ============================================================
// Balance.cs —— “战斗数值参数”表（BalanceRule）的类型化读取与校验。
//
// 为什么不直接在业务里 FindByKey("Battle.DurationSeconds")：
// 字符串键拼错只会在运行到那一行时才发现，且每处各写各的默认值。这里在装配阶段一次性读出全部参数并校验：
//   · 代码需要的键缺了        → 报“缺少参数 xxx”
//   · 表里多了代码不认识的键  → 报“多余参数 xxx”（通常是拼错了键，改了却没生效）
//   · 数值越界 / NaN / 非整数 → 报具体范围
// 通过之后业务读 Balance.BattleDurationSeconds 这类强类型只读属性，拿不到 0 这种“悄悄的坏值”。
//
// 加一个新参数：① 在 Excel（战斗数值参数.xlsx）加一行；② 在下面三处各加一行（字段、读取、属性）；③ 导表。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using Revolution;

namespace RevGame.Data
{
    /// <summary>参数表读取器：记录哪些键被读过，用来发现“表里有、代码不认识”的多余键。</summary>
    internal sealed class BalanceReader
    {
        private readonly BalanceRuleTable _table;
        private readonly List<string> _problems;
        private readonly HashSet<string> _used = new HashSet<string>();

        public BalanceReader(BalanceRuleTable table, List<string> problems)
        {
            _table = table;
            _problems = problems;
        }

        public float Float(string key, float min, float max)
        {
            _used.Add(key);
            if (!_table.FindByKey(key, out BalanceRule row))
            {
                _problems.Add("BalanceRule 缺少参数 " + key + "（代码需要它，请在“战斗数值参数.xlsx”补一行）"); return min;
            }
            if (float.IsNaN(row.Value) || float.IsInfinity(row.Value) || row.Value < min || row.Value > max)
            {
                _problems.Add("BalanceRule[" + key + "] = " + row.Value.ToString("R", CultureInfo.InvariantCulture) + " 超出合法范围 [" + GameConfig.Fmt(min) + ", " + GameConfig.Fmt(max) + "]");
                return min;
            }
            return row.Value;
        }

        public int Int(string key, int min, int max)
        {
            _used.Add(key);
            if (!_table.FindByKey(key, out BalanceRule row))
            {
                _problems.Add("BalanceRule 缺少参数 " + key + "（代码需要它，请在“战斗数值参数.xlsx”补一行）"); return min;
            }
            float v = row.Value;
            if (float.IsNaN(v) || float.IsInfinity(v) || v != (float)Math.Floor(v) || v < min || v > max)
            {
                _problems.Add("BalanceRule[" + key + "] = " + v.ToString("R", CultureInfo.InvariantCulture) + " 必须是 [" + min + ", " + max + "] 内的整数");
                return min;
            }
            return (int)v;
        }

        /// <summary>读完全部参数后调用：表里没被读到的键都是多余的。</summary>
        public void ReportUnused()
        {
            foreach (string key in _table.Keys)
            {
                if (!_used.Contains(key))
                {
                    _problems.Add("BalanceRule 里的参数 " + key + " 代码并不使用（多半是键名拼错，改了不会生效；确需新增请让程序同步读取）");
                }
            }
        }
    }

    /// <summary>一份不可变的参数快照。</summary>
    internal sealed class BalanceValues
    {
        // ---- 战斗节奏
        public float BossCaptureHpRatio;
        public float EventEffectMultiplier;
        public float PlayerMoveSeconds;
        public int PlayerRetreatDistance;
        public float RegionIntervalSeconds;
        public float RegionWarningSeconds;
        public int RegionSize;
        public float RegionCellDelaySeconds;
        public float RegionDamageHpRatio;
        public float BombIntervalSeconds;
        public float BombWarningSeconds;
        public float BombDamageHpRatio;
        public float CombatBuffCap;
        public float BattleDurationSeconds;
        public float BattleTickSeconds;
        public int BattleStepsPerTick;
        public float AttackSpeedScale;
        public float CombatBaseScale;
        public int EnemyMoveEveryTicks;
        public float EyeHealSeconds;
        public float EyeHealRatio;
        public int EyeHealMinEyes;
        // ---- 伤害
        public float DamageBaseMultiplier;
        public float LockedDamageMultiplier;
        public float IsolatedDamageMultiplier;
        public float LinkAttackPerCell;
        public float MaxLinkAttackBonus;
        public float ClassAttackBonus;
        public int ClassThresholdLow;
        public int ClassThresholdHigh;
        // ---- Boss
        public float BossAccelerationSeconds;
        public float BossAccelerationBonus;
        public float BossHpGrowthPerChapter;
        // ---- Boss 技能节奏（技能本身的参数在 BossSkillConfig 表里）
        public float BossFirstSkillSeconds;
        public float BossSkillGapSeconds;
        public float BossRecoverSeconds;
        public float BossEnrageHpRatio;
        public float BossEnrageCooldownScale;
        public float BossCooldownPerChapter;
        public float BossPowerPerChapter;
        // ---- 敌方成长
        public float EnemyHpBase;
        public float EnemyHpMultiplier;
        public float EnemyHpGrowthPerRound;
        public float EnemyAttackGrowthPerRound;
        public int EnemyAiRoundsPerLevel;
        public int EnemyAiMaxLevel;
        public int EnemyCountBase;
        public int EnemyCountRoundsPerExtra;
        public int EnemyCountMax;
        public float EnemyPressureStarDivisor;
        public float EnemyPressureHpMax;
        public float EnemyPressureAttackMax;
        public int EnemyArcherRange;
        // ---- 战斗中敌方补兵
        public float DeployStartSeconds;
        public float DeployStopSeconds;
        public int DeployLivingCap;
        public int DeployBaseCount;
        public float DeployIntervalMinSeconds;
        public float DeployIntervalBaseSeconds;
        public float DeployIntervalPerLevelSeconds;
        public int DeployMinEmptyCells;
        // ---- 战斗事件
        public float EventScheduleChance;
        public float EventRareChance;
        public float EventDelaySeconds;
        public float EventIntervalSeconds;
        public int EventMaxPerRound;
        public float EventFallbackHealRatio;
        public int EventArtilleryRadius;
        public float EventStageDelaySeconds;
        // ---- 通关辅助
        public int AssistSupportDamage;
        // ---- 准备期
        public float PrepDelaySeconds;
        public int PrepBuffCountMin;
        public int PrepBuffCountMax;
        public float PrepRegenIntervalSeconds;
        // ---- 成长与奖励
        public int RoundNormalPerChapter;
        public float RoundSkipAttackBonus;
        public int HandKindBaseWeight;
        public int HandClassBonusWeight;
        public int UpgradePairsPerStar;
        public int RewardOutnumberedFactor;

        public static BalanceValues Read(BalanceRuleTable table, List<string> problems)
        {
            var r = new BalanceReader(table, problems);
            var v = new BalanceValues();
            v.BattleDurationSeconds = r.Float("Battle.DurationSeconds", 1f, 3600f);
            v.BattleTickSeconds = r.Float("Battle.TickSeconds", 0.05f, 60f);
            v.BattleStepsPerTick = r.Int("Battle.StepsPerTick", 1, 60);
            v.AttackSpeedScale = r.Float("Battle.AttackSpeedScale", 0.1f, 20f);
            v.CombatBaseScale = r.Float("Battle.CombatBaseScale", 0.01f, 20f);
            v.EnemyMoveEveryTicks = r.Int("Battle.EnemyMoveEveryTicks", 1, 100);
            v.EyeHealSeconds = r.Float("Battle.EyeHealSeconds", 0.1f, 600f);
            v.EyeHealRatio = r.Float("Battle.EyeHealRatio", 0f, 1f);
            v.EyeHealMinEyes = r.Int("Battle.EyeHealMinEyes", 1, 8);

            v.DamageBaseMultiplier = r.Float("Damage.BaseMultiplier", 0.01f, 100f);
            v.LockedDamageMultiplier = r.Float("Damage.LockedMultiplier", 1f, 100f);
            v.IsolatedDamageMultiplier = r.Float("Damage.IsolatedMultiplier", 1f, 100f);
            v.LinkAttackPerCell = r.Float("Damage.LinkPerCell", 0f, 10f);
            v.MaxLinkAttackBonus = r.Float("Damage.LinkMax", 0f, 100f);
            v.ClassAttackBonus = r.Float("Damage.ClassBonus", 0f, 100f);
            v.ClassThresholdLow = r.Int("Damage.ClassThresholdLow", 1, 81);
            v.ClassThresholdHigh = r.Int("Damage.ClassThresholdHigh", 1, 81);

            v.BossAccelerationSeconds = r.Float("Boss.AccelerationSeconds", 1f, 3600f);
            v.BossAccelerationBonus = r.Float("Boss.AccelerationBonus", 0f, 100f);
            v.BossHpGrowthPerChapter = r.Float("Boss.HpGrowthPerChapter", 0f, 100f);
            v.BossFirstSkillSeconds = r.Float("Boss.FirstSkillSeconds", 0f, 120f);
            v.BossSkillGapSeconds = r.Float("Boss.SkillGapSeconds", 0f, 60f);
            v.BossRecoverSeconds = r.Float("Boss.RecoverSeconds", 0f, 10f);
            v.BossEnrageHpRatio = r.Float("Boss.EnrageHpRatio", 0f, 1f);
            v.BossEnrageCooldownScale = r.Float("Boss.EnrageCooldownScale", 0.1f, 1f);
            // 上限 0.15：第六章冷却最多缩到 1-5×0.15=25%，不会出现 0 或负冷却。
            v.BossCooldownPerChapter = r.Float("Boss.CooldownPerChapter", 0f, 0.15f);
            v.BossPowerPerChapter = r.Float("Boss.PowerPerChapter", 0f, 2f);

            v.EnemyHpBase = r.Float("Enemy.HpBase", 0.01f, 1000f);
            v.EnemyHpMultiplier = r.Float("Enemy.HpMultiplier", 0.01f, 1000f);
            v.EnemyHpGrowthPerRound = r.Float("Enemy.HpGrowthPerRound", 0f, 10f);
            v.EnemyAttackGrowthPerRound = r.Float("Enemy.AttackGrowthPerRound", 0f, 10f);
            v.EnemyAiRoundsPerLevel = r.Int("Enemy.AiRoundsPerLevel", 1, 1000);
            v.EnemyAiMaxLevel = r.Int("Enemy.AiMaxLevel", 1, 4);
            v.EnemyCountBase = r.Int("Enemy.CountBase", 1, 80);
            v.EnemyCountRoundsPerExtra = r.Int("Enemy.CountRoundsPerExtra", 1, 1000);
            v.EnemyCountMax = r.Int("Enemy.CountMax", 1, 80);
            v.EnemyPressureStarDivisor = r.Float("Enemy.PressureStarDivisor", 0.1f, 1000f);
            v.EnemyPressureHpMax = r.Float("Enemy.PressureHpMax", 1f, 1000f);
            v.EnemyPressureAttackMax = r.Float("Enemy.PressureAttackMax", 1f, 1000f);
            v.EnemyArcherRange = r.Int("Enemy.ArcherRange", 1, 20);

            v.DeployStartSeconds = r.Float("Deploy.StartSeconds", 0f, 3600f);
            v.DeployStopSeconds = r.Float("Deploy.StopSeconds", 0f, 3600f);
            v.DeployLivingCap = r.Int("Deploy.LivingCap", 1, 81);
            v.DeployBaseCount = r.Int("Deploy.BaseCount", 0, 100);
            v.DeployIntervalMinSeconds = r.Float("Deploy.IntervalMinSeconds", 0.1f, 3600f);
            v.DeployIntervalBaseSeconds = r.Float("Deploy.IntervalBaseSeconds", 0.1f, 3600f);
            v.DeployIntervalPerLevelSeconds = r.Float("Deploy.IntervalPerLevelSeconds", 0f, 3600f);
            v.DeployMinEmptyCells = r.Int("Deploy.MinEmptyCells", 0, 81);

            v.EventScheduleChance = r.Float("Event.ScheduleChance", 0f, 1f);
            v.EventRareChance = r.Float("Event.RareChance", 0f, 1f);
            v.EventDelaySeconds = r.Float("Event.DelaySeconds", 0f, 3600f);
            v.EventIntervalSeconds = r.Float("Event.IntervalSeconds", 0f, 3600f);
            v.EventMaxPerRound = r.Int("Event.MaxPerRound", 0, 1000);
            v.EventFallbackHealRatio = r.Float("Event.FallbackHealRatio", 0f, 1f);
            v.EventArtilleryRadius = r.Int("Event.ArtilleryRadius", 0, 10);
            v.EventStageDelaySeconds = r.Float("Event.StageDelaySeconds", 0f, 3600f);

            v.AssistSupportDamage = r.Int("Assist.SupportDamage", 1, 100000);

            v.PrepDelaySeconds = r.Float("Prep.DelaySeconds", 0f, 3600f);
            v.PrepBuffCountMin = r.Int("Prep.BuffCountMin", 0, 16);
            v.PrepBuffCountMax = r.Int("Prep.BuffCountMax", 0, 16);
            v.PrepRegenIntervalSeconds = r.Float("Prep.RegenIntervalSeconds", 0.1f, 3600f);

            v.RoundNormalPerChapter = r.Int("Round.NormalPerChapter", 1, 100);
            v.RoundSkipAttackBonus = r.Float("Round.SkipAttackBonus", 0f, 100f);
            v.HandKindBaseWeight = r.Int("Hand.KindBaseWeight", 1, 1000);
            v.HandClassBonusWeight = r.Int("Hand.ClassBonusWeight", 0, 1000);
            v.UpgradePairsPerStar = r.Int("Upgrade.PairsPerStar", 1, 100);
            v.RewardOutnumberedFactor = r.Int("Reward.OutnumberedFactor", 1, 100);
            v.BossCaptureHpRatio = r.Float("Boss.CaptureHpRatio", 0.01f, 1f);
            v.EventEffectMultiplier = r.Float("Event.EffectMultiplier", 1f, 10f);
            v.PlayerMoveSeconds = r.Float("PlayerAI.MoveSeconds", 0.5f, 60f);
            v.PlayerRetreatDistance = r.Int("PlayerAI.RetreatDistance", 1, 8);
            v.RegionIntervalSeconds = r.Float("Region.IntervalSeconds", 3f, 600f);
            v.RegionWarningSeconds = r.Float("Region.WarningSeconds", 0.5f, 5f);
            v.RegionSize = r.Int("Region.Size", 1, 9);
            v.RegionCellDelaySeconds = r.Float("Region.CellDelaySeconds", 0.02f, 0.2f);
            v.RegionDamageHpRatio = r.Float("Region.DamageHpRatio", 0.001f, 0.2f);
            v.BombIntervalSeconds = r.Float("Bomb.IntervalSeconds", 5f, 600f);
            v.BombWarningSeconds = r.Float("Bomb.WarningSeconds", 0.5f, 5f);
            v.BombDamageHpRatio = r.Float("Bomb.DamageHpRatio", 0.001f, 0.15f);
            v.CombatBuffCap = r.Float("Combat.BuffCap", 0.1f, 10f);
            r.ReportUnused();

            // ---- 跨参数的逻辑约束：单个值合法，组合起来不合法也会出事
            void Rule(bool ok, string message) {
            if (!ok)
            {
                problems.Add("BalanceRule 参数组合不合法：" + message);
            }
            }
            Rule(v.PrepBuffCountMin <= v.PrepBuffCountMax, "Prep.BuffCountMin 不能大于 Prep.BuffCountMax");
            Rule(v.ClassThresholdLow <= v.ClassThresholdHigh, "Damage.ClassThresholdLow 不能大于 Damage.ClassThresholdHigh");
            Rule(v.EnemyCountBase <= v.EnemyCountMax, "Enemy.CountBase 不能大于 Enemy.CountMax");
            Rule(v.DeployStartSeconds <= v.DeployStopSeconds, "Deploy.StartSeconds 不能大于 Deploy.StopSeconds");
            Rule(v.EyeHealMinEyes >= 1, "Battle.EyeHealMinEyes 至少为 1");
            Rule(v.RegionWarningSeconds + (v.RegionSize * v.RegionSize - 1) * v.RegionCellDelaySeconds < v.RegionIntervalSeconds, "区域预警与逐格命中总时长必须小于波动间隔");
            Rule(v.BombWarningSeconds < v.BombIntervalSeconds, "炸弹蓄力必须小于施放间隔");
            return v;
        }
    }

    /// <summary>
    /// 战斗数值参数的强类型只读入口（业务一律读这里，不要在代码里再写一份数值）。
    /// 未装配配置就读取会立刻抛异常。
    /// </summary>
    public static class Balance
    {
        private static BalanceValues V => GameConfig.Current.Balance;

        // ---- 战斗节奏
        public static float BattleDurationSeconds => V.BattleDurationSeconds;
        public static float BattleTickSeconds => V.BattleTickSeconds;
        /// <summary>固定模拟小步：一个行动周期拆成 StepsPerTick 份。</summary>
        public static float BattleSimulationStepSeconds => V.BattleTickSeconds / V.BattleStepsPerTick;
        public static float AttackSpeedScale => V.AttackSpeedScale;
        /// <summary>单发伤害系数 = 基础系数 ÷ 攻击提速：提高出手频率时摊薄单发伤害，维持总火力。</summary>
        public static float CombatDamageScale => V.CombatBaseScale / V.AttackSpeedScale;
        public static int EnemyMoveEveryTicks => V.EnemyMoveEveryTicks;
        public static float EyeHealSeconds => V.EyeHealSeconds;
        public static float EyeHealRatio => V.EyeHealRatio;
        public static int EyeHealMinEyes => V.EyeHealMinEyes;
        // ---- 伤害
        public static float BaseDamageMultiplier => V.DamageBaseMultiplier;
        public static float LockedDamageMultiplier => V.LockedDamageMultiplier;
        public static float IsolatedDamageMultiplier => V.IsolatedDamageMultiplier;
        public static float LinkAttackPerCell => V.LinkAttackPerCell;
        public static float MaxLinkAttackBonus => V.MaxLinkAttackBonus;
        public static float ClassAttackBonus => V.ClassAttackBonus;
        public static int ClassThresholdLow => V.ClassThresholdLow;
        public static int ClassThresholdHigh => V.ClassThresholdHigh;
        // ---- Boss
        public static float BossAccelerationSeconds => V.BossAccelerationSeconds;
        public static float BossAccelerationBonus => V.BossAccelerationBonus;
        public static float BossHpGrowthPerChapter => V.BossHpGrowthPerChapter;
        /// <summary>开战后 Boss 第一次放技能前的等待秒数。</summary>
        public static float BossFirstSkillSeconds => V.BossFirstSkillSeconds;
        /// <summary>任意两次技能之间的公共间隔秒数，防止技能首尾相接。</summary>
        public static float BossSkillGapSeconds => V.BossSkillGapSeconds;
        /// <summary>技能最后一段命中后的收招硬直秒数。</summary>
        public static float BossRecoverSeconds => V.BossRecoverSeconds;
        /// <summary>生命比例低于它时触发一次狂暴。</summary>
        public static float BossEnrageHpRatio => V.BossEnrageHpRatio;
        /// <summary>狂暴后技能冷却乘以的系数。</summary>
        public static float BossEnrageCooldownScale => V.BossEnrageCooldownScale;
        /// <summary>每高一章冷却缩短的比例。</summary>
        public static float BossCooldownPerChapter => V.BossCooldownPerChapter;
        /// <summary>每高一章技能伤害提高的比例。</summary>
        public static float BossPowerPerChapter => V.BossPowerPerChapter;
        // ---- 敌方成长
        /// <summary>敌方生命基础系数（旧名 EnemyHpScale）。</summary>
        public static float EnemyHpScale => V.EnemyHpBase;
        public static float EnemyHpMultiplier => V.EnemyHpMultiplier;
        public static float EnemyHpGrowthPerRound => V.EnemyHpGrowthPerRound;
        public static float EnemyAttackGrowthPerRound => V.EnemyAttackGrowthPerRound;
        public static int EnemyAiRoundsPerLevel => V.EnemyAiRoundsPerLevel;
        public static int EnemyAiMaxLevel => V.EnemyAiMaxLevel;
        public static int EnemyCountBase => V.EnemyCountBase;
        public static int EnemyCountRoundsPerExtra => V.EnemyCountRoundsPerExtra;
        public static int EnemyCountMax => V.EnemyCountMax;
        public static float EnemyPressureStarDivisor => V.EnemyPressureStarDivisor;
        public static float EnemyPressureHpMax => V.EnemyPressureHpMax;
        public static float EnemyPressureAttackMax => V.EnemyPressureAttackMax;
        public static int EnemyArcherRange => V.EnemyArcherRange;
        // ---- 战斗中敌方补兵
        public static float DeployStartSeconds => V.DeployStartSeconds;
        public static float DeployStopSeconds => V.DeployStopSeconds;
        public static int DeployLivingCap => V.DeployLivingCap;
        public static int DeployBaseCount => V.DeployBaseCount;
        public static float DeployIntervalMinSeconds => V.DeployIntervalMinSeconds;
        public static float DeployIntervalBaseSeconds => V.DeployIntervalBaseSeconds;
        public static float DeployIntervalPerLevelSeconds => V.DeployIntervalPerLevelSeconds;
        public static int DeployMinEmptyCells => V.DeployMinEmptyCells;
        // ---- 战斗事件
        /// <summary>来自 BalanceRule[Boss.CaptureHpRatio]，单位与取值说明见原始 Excel。</summary>
        public static float BossCaptureHpRatio => V.BossCaptureHpRatio;
        /// <summary>来自 BalanceRule[Event.EffectMultiplier]，单位与取值说明见原始 Excel。</summary>
        public static float EventEffectMultiplier => V.EventEffectMultiplier;
        /// <summary>来自 BalanceRule[PlayerAI.MoveSeconds]，单位与取值说明见原始 Excel。</summary>
        public static float PlayerMoveSeconds => V.PlayerMoveSeconds;
        /// <summary>来自 BalanceRule[PlayerAI.RetreatDistance]，单位与取值说明见原始 Excel。</summary>
        public static int PlayerRetreatDistance => V.PlayerRetreatDistance;
        /// <summary>来自 BalanceRule[Region.IntervalSeconds]，单位与取值说明见原始 Excel。</summary>
        public static float RegionIntervalSeconds => V.RegionIntervalSeconds;
        /// <summary>来自 BalanceRule[Region.WarningSeconds]，单位与取值说明见原始 Excel。</summary>
        public static float RegionWarningSeconds => V.RegionWarningSeconds;
        /// <summary>来自 BalanceRule[Region.Size]，单位与取值说明见原始 Excel。</summary>
        public static int RegionSize => V.RegionSize;
        /// <summary>来自 BalanceRule[Region.CellDelaySeconds]，单位与取值说明见原始 Excel。</summary>
        public static float RegionCellDelaySeconds => V.RegionCellDelaySeconds;
        /// <summary>来自 BalanceRule[Region.DamageHpRatio]，单位与取值说明见原始 Excel。</summary>
        public static float RegionDamageHpRatio => V.RegionDamageHpRatio;
        /// <summary>来自 BalanceRule[Bomb.IntervalSeconds]，单位与取值说明见原始 Excel。</summary>
        public static float BombIntervalSeconds => V.BombIntervalSeconds;
        /// <summary>来自 BalanceRule[Bomb.WarningSeconds]，单位与取值说明见原始 Excel。</summary>
        public static float BombWarningSeconds => V.BombWarningSeconds;
        /// <summary>来自 BalanceRule[Bomb.DamageHpRatio]，单位与取值说明见原始 Excel。</summary>
        public static float BombDamageHpRatio => V.BombDamageHpRatio;
        /// <summary>来自 BalanceRule[Combat.BuffCap]，单位与取值说明见原始 Excel。</summary>
        public static float CombatBuffCap => V.CombatBuffCap;
        public static float BattleEventChance => V.EventScheduleChance;
        public static float RareEventChance => V.EventRareChance;
        public static float BattleEventDelay => V.EventDelaySeconds;
        public static float BattleEventInterval => V.EventIntervalSeconds;
        public static int MaxBattleEventsPerRound => V.EventMaxPerRound;
        public static float EventFallbackHealRatio => V.EventFallbackHealRatio;
        public static int EventArtilleryRadius => V.EventArtilleryRadius;
        public static float EventStageDelaySeconds => V.EventStageDelaySeconds;
        // ---- 通关辅助
        public static int SupportDamage => V.AssistSupportDamage;
        // ---- 准备期
        public static float PrepDelaySeconds => V.PrepDelaySeconds;
        public static int PrepBuffCountMin => V.PrepBuffCountMin;
        public static int PrepBuffCountMax => V.PrepBuffCountMax;
        public static float PrepRegenIntervalSeconds => V.PrepRegenIntervalSeconds;
        // ---- 回合结构与成长
        public static int NormalRoundsPerChapter => V.RoundNormalPerChapter;
        /// <summary>每章第一个 Boss 回合序号 = 普通回合数 + 1。</summary>
        public static int BossRoundIndex => V.RoundNormalPerChapter + 1;
        /// <summary>每章总回合数（普通 + 1 个 Boss）。</summary>
        public static int RoundsPerChapter => V.RoundNormalPerChapter + 1;
        public static float RoundSkipAttackBonus => V.RoundSkipAttackBonus;
        public static int HandKindBaseWeight => V.HandKindBaseWeight;
        public static int HandClassBonusWeight => V.HandClassBonusWeight;
        public static int UpgradePairsPerStar => V.UpgradePairsPerStar;
        public static int RewardOutnumberedFactor => V.RewardOutnumberedFactor;
    }
}

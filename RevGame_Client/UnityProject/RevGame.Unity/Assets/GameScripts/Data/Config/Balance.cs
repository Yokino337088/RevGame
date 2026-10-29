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
        // ---- 积分与暴击；所有数值来自Excel，范围在Read中一次性校验。
        public int DeployBatchCount;
        public float CriticalChance;
        public float CriticalMultiplier;
        public int ScoreKillPoints;
        public int ScoreCriticalPoints;
        public float ScoreRoundGrowth;
        public float ScorePieceGrowth;
        public float ScoreMultiplierCap;
        // ---- 战斗节奏
        public float BossCaptureHpRatio;
        public float EventEffectMultiplier;
        /// <summary>驻眼加成比例，1表示增加100%；配置装配后只读。</summary>
        public float PlayerEyeAttackBonus;
        /// <summary>驻眼加成比例，1表示增加100%；配置装配后只读。</summary>
        public float PlayerEyeSpeedBonus;
        /// <summary>驻眼加成比例，1表示增加100%；配置装配后只读。</summary>
        public float PlayerEyeDefenseBonus;
        /// <summary>驻眼加成比例，1表示增加100%；配置装配后只读。</summary>
        public float EnemyEyeAttackBonus;
        /// <summary>守眼四邻防御加成，比例，多个眼不叠加。</summary>
        public float EyeGuardDefenseBonus;
        /// <summary>空眼远程支援间隔，秒。</summary>
        public float EyeSupportSeconds;
        /// <summary>驻眼原地复活生命比例，每颗棋一生一次。</summary>
        public float EyeReviveHpRatio;
        /// <summary>复活时四邻友军治疗比例，按各自最大生命计算。</summary>
        public float EyeReviveHealRatio;
        /// <summary>敌方驻眼攻速加成比例，0.5表示增加50%；不影响移动速度。</summary>
        public float EnemyEyeSpeedBonus;
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
        // 失误支援只在辅助模式和仍有活棋时生效；数值来自Excel，不把GM无敌当正常平衡。
        public float EmergencyFirstSeconds, EmergencyIntervalSeconds, EmergencyHpRatio, EmergencyHealRatio;
        public int EmergencyMaxCount;
        /// <summary>玩家存活远程上限，所有新增入口共用，不扣失败手牌。</summary>
        public int RangedLimit;
        /// <summary>进入Boss回合时超过此数量吞噬低星棋，先近战。</summary>
        public int BossPlayerPieceLimit;
        /// <summary>敌近战跳跃接敌冷却秒，保眼与占格预检不变。</summary>
        public float EnemyJumpSeconds;
        /// <summary>玩家普攻真实命中基础积分；主要能源来源。</summary>
        public int HelperPlayerHitScore;
        /// <summary>风暴触发上界：存活玩家棋数大于0且小于此数；由战斗服务消费，不能由视图改变。</summary>
        public int HelperStormThreshold;
        /// <summary>风暴导弹间隔，模拟秒；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormMissileSeconds;
        /// <summary>风暴导弹每次直接命中基础伤害；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormMissileDamage;
        /// <summary>导弹爆炸半径，棋盘格；溅射为直接伤害的一半；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormBlastRadius;
        /// <summary>风暴水母弹幕间隔，模拟秒；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormJellySeconds;
        /// <summary>每轮水母颗数，按敌棋稳定轮转；由战斗服务消费，不能由视图改变。</summary>
        public int HelperStormJellyCount;
        /// <summary>单颗水母基础伤害；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormJellyDamage;
        /// <summary>风暴激光持续模拟秒；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormBeamSeconds;
        /// <summary>风暴激光启动间隔，模拟秒；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormBeamCooldown;
        /// <summary>风暴激光每个命中敌人的每秒基础伤害；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormBeamDps;
        /// <summary>风暴激光完整宽度，棋盘格，几何检测与画面共用；由战斗服务消费，不能由视图改变。</summary>
        public float HelperStormBeamWidth;

        /// <summary>帮手单发命中返还积分；必须小于单发成本。</summary>
        public int HelperHitScore;
        /// <summary>每个普通攻击粒子消耗可用积分。</summary>
        public int HelperShotCost;
        /// <summary>帮手单发基础伤害，仍走统一扣血管线。</summary>
        public float HelperBaseDamage;
        /// <summary>每积累该章积分提升一档，最多配置档数。</summary>
        public int HelperScoreStep;
        /// <summary>攻击成长档数上限，防无限火力。</summary>
        public int HelperMaxTier;
        /// <summary>每档单发伤害增加比例。</summary>
        public float HelperDamageGrowth;
        /// <summary>普通攻击间隔秒。</summary>
        public float HelperBaseInterval;
        /// <summary>最快普通攻击间隔秒。</summary>
        public float HelperMinInterval;
        /// <summary>Boss回合帮手额外攻速比例与单发伤害倍率；独立配置，避免弹幕加密等于伤害翻倍。</summary>
        public float HelperBossSpeedBonus, HelperBossDamageScale;
        /// <summary>最后一颗棋子的每秒最大生命回复比例、攻击与攻速加成；服务逐步结算，不由动画回血。</summary>
        public float HelperProtectionHealPerSecond, HelperProtectionAttackBonus, HelperProtectionSpeedBonus;
        /// <summary>通用主动技能放几次后轮到本章专属；狂暴不消耗轮换名额。</summary>
        public int BossCommonSkillsPerExclusive;
        /// <summary>爬行或跳至外圈的决策间隔秒。</summary>
        public float HelperMoveSeconds;
        /// <summary>活棋达到此数量时转棋盘外攻击。</summary>
        public int HelperCrowdedCount;
        /// <summary>本章累计积分达到该值解锁激光。</summary>
        public int HelperBeamScore;
        /// <summary>开始激光所需可用积分储备。</summary>
        public int HelperBeamReserve;
        /// <summary>激光每次持续命中消耗积分，耗尽立即停止。</summary>
        public int HelperBeamTickCost;
        /// <summary>每个固定模拟步激光伤害，不返还积分。</summary>
        public float HelperBeamDamage;
        /// <summary>激光最长持续秒。</summary>
        public float HelperBeamSeconds;
        /// <summary>两次巨大能量束最短间隔秒。</summary>
        public float HelperBeamCooldown;
        /// <summary>帮手伤害/频率成长传给远程棋的比例，上限防高分跨章成长失控。</summary>
        public float HelperRangedAttackLink, HelperRangedSpeedLink, HelperRangedAttackCap, HelperRangedSpeedCap;

        /// <summary>超过正常编队规模后火力递减，单位为活棋数和无量纲指数；不删棋、不限制落子。</summary>
        public int PlayerFirepowerSoftCount;
        public float PlayerFirepowerExponent, PlayerFirepowerMinScale;
        public float BattleDurationSeconds;
        public float BattleTickSeconds;
        public int BattleStepsPerTick;
        public float AttackSpeedScale;
        /// <summary>敌军普通攻击的独立倍率；不影响移动、技能冷却或玩家攻速。</summary>
        public float EnemyAttackRate;
        /// <summary>无眼近战一次冲锋最多经过的空格数，路径上的每一格都必须放得下整个身体。</summary>
        public int EnemyChaseSteps;
        public int RangedFormationEnabled;
        public int RangedFormationMinCount;
        public float RangedFormationSeconds;
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
        /// <summary>仅主将的额外最大生命倍率；护卫沿用普通敌军成长，不通过增加Boss数量堆耐久。</summary>
        public float BossHpMultiplier;
        /// <summary>主将出生时独立乘入攻击倍率，普攻与技能共用；不降低普通敌棋和护卫的伤害。</summary>
        public float BossAttackMultiplier;
        /// <summary>主将承受最大生命比例伤害的系数，范围(0,1]；固定伤害与棋子普攻不受影响。</summary>
        public float BossPercentDamageScale;
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
            v.DeployBatchCount = r.Int("Deploy.BatchCount", 1, 2);
            v.CriticalChance = r.Float("Damage.CriticalChance", 0f, 1f);
            v.CriticalMultiplier = r.Float("Damage.CriticalMultiplier", 1f, 10f);
            v.ScoreKillPoints = r.Int("Score.KillPoints", 0, 100000);
            v.ScoreCriticalPoints = r.Int("Score.CriticalPoints", 0, 100000);
            v.ScoreRoundGrowth = r.Float("Score.RoundGrowth", 0f, 10f);
            v.ScorePieceGrowth = r.Float("Score.PieceGrowth", 0f, 10f);
            v.ScoreMultiplierCap = r.Float("Score.MultiplierCap", 1f, 1000f);

            v.EnemyAttackRate = r.Float("EnemyAI.AttackRate", 0.1f, 5f);
            v.EnemyChaseSteps = r.Int("EnemyAI.ChaseSteps", 1, 4);
            v.RangedFormationEnabled = r.Int("PlayerAI.FormationEnabled", 0, 1);
            v.RangedFormationMinCount = r.Int("PlayerAI.FormationMinCount", 2, 20);
            v.RangedFormationSeconds = r.Float("PlayerAI.FormationSeconds", 1f, 60f);
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
            v.BossHpMultiplier = r.Float("Boss.HpMultiplier", 1f, 100f);
            v.BossCommonSkillsPerExclusive = r.Int("Boss.CommonSkillsPerExclusive", 1, 10);
            v.BossAttackMultiplier = r.Float("Boss.AttackMultiplier", 0.01f, 1f);
            v.BossPercentDamageScale = r.Float("Boss.PercentDamageScale", 0.01f, 1f);
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
            v.PlayerEyeAttackBonus = r.Float("Eye.PlayerAttackBonus", 0f, 10f);
            v.PlayerEyeSpeedBonus = r.Float("Eye.PlayerSpeedBonus", 0f, 10f);
            v.PlayerEyeDefenseBonus = r.Float("Eye.PlayerDefenseBonus", 0f, 10f);
            v.EnemyEyeAttackBonus = r.Float("Eye.EnemyAttackBonus", 0f, 10f);
            v.EyeGuardDefenseBonus = r.Float("Eye.GuardDefenseBonus", 0f, 10f);
            v.EyeSupportSeconds = r.Float("Eye.SupportSeconds", 0.1f, 600f);
            v.EyeReviveHpRatio = r.Float("Eye.ReviveHpRatio", 0.01f, 1f);
            v.EyeReviveHealRatio = r.Float("Eye.ReviveHealRatio", 0f, 1f);
            v.EnemyEyeSpeedBonus = r.Float("Eye.EnemySpeedBonus", 0f, 10f);
            v.PlayerMoveSeconds = r.Float("PlayerAI.MoveSeconds", 0.5f, 60f);
            v.PlayerRetreatDistance = r.Int("PlayerAI.RetreatDistance", 1, 8);
            v.RegionIntervalSeconds = r.Float("Region.IntervalSeconds", 3f, 600f);
            v.RegionWarningSeconds = r.Float("Region.WarningSeconds", 0.5f, 5f);
            v.RegionSize = r.Int("Region.Size", 1, 9);
            v.RegionCellDelaySeconds = r.Float("Region.CellDelaySeconds", 0.02f, 0.2f);
            v.RegionDamageHpRatio = r.Float("Region.DamageHpRatio", 0.001f, 0.2f);
            v.BombIntervalSeconds = r.Float("Bomb.IntervalSeconds", 5f, 600f);
            v.BombWarningSeconds = r.Float("Bomb.WarningSeconds", 0.5f, 5f);
            v.BombDamageHpRatio = r.Float("Bomb.DamageHpRatio", 0.001f, 1f);
            v.CombatBuffCap = r.Float("Combat.BuffCap", 0.1f, 10f);
            v.EmergencyFirstSeconds = r.Float("Assist.EmergencyFirstSeconds",0f,120f);
            v.EmergencyIntervalSeconds = r.Float("Assist.EmergencyIntervalSeconds",1f,120f);
            v.EmergencyHpRatio = r.Float("Assist.EmergencyHpRatio",.05f,1f);
            v.EmergencyHealRatio = r.Float("Assist.EmergencyHealRatio",.01f,.8f);
            v.RangedLimit = r.Int("Player.RangedLimit",1,81);
            v.BossPlayerPieceLimit = r.Int("Boss.PlayerPieceLimit",1,70);
            v.EnemyJumpSeconds = r.Float("Enemy.JumpSeconds",0.3f,20f);
            v.HelperStormThreshold = r.Int("Helper.StormThreshold",1,10);
            v.HelperStormMissileSeconds = r.Float("Helper.StormMissileSeconds",0.2f,30f);
            v.HelperStormMissileDamage = r.Float("Helper.StormMissileDamage",1f,1000f);
            v.HelperStormBlastRadius = r.Float("Helper.StormBlastRadius",0.1f,5f);
            v.HelperStormJellySeconds = r.Float("Helper.StormJellySeconds",0.2f,30f);
            v.HelperStormJellyCount = r.Int("Helper.StormJellyCount",1,32);
            v.HelperStormJellyDamage = r.Float("Helper.StormJellyDamage",1f,100f);
            v.HelperStormBeamSeconds = r.Float("Helper.StormBeamSeconds",0.2f,10f);
            v.HelperStormBeamCooldown = r.Float("Helper.StormBeamCooldown",1f,60f);
            v.HelperStormBeamDps = r.Float("Helper.StormBeamDps",1f,1000f);
            v.HelperStormBeamWidth = r.Float("Helper.StormBeamWidth",0.2f,5f);
            v.HelperPlayerHitScore = r.Int("Helper.PlayerHitScore",1,100);
            v.HelperHitScore = r.Int("Helper.HitScore",0,10);
            v.HelperShotCost = r.Int("Helper.ShotCost",1,100);
            v.HelperBaseDamage = r.Float("Helper.BaseDamage",1f,100f);
            v.HelperScoreStep = r.Int("Helper.ScoreStep",10,10000);
            v.HelperMaxTier = r.Int("Helper.MaxTier",1,10);
            v.HelperDamageGrowth = r.Float("Helper.DamageGrowth",0f,1f);
            v.HelperBaseInterval = r.Float("Helper.BaseInterval",0.2f,10f);
            v.HelperMinInterval = r.Float("Helper.MinInterval",0.2f,10f);
            v.HelperBossSpeedBonus = r.Float("Helper.BossSpeedBonus",0f,3f);
            v.HelperProtectionHealPerSecond = r.Float("Helper.ProtectionHealPerSecond",0f,1f);
            v.HelperProtectionAttackBonus = r.Float("Helper.ProtectionAttackBonus",0f,2f);
            v.HelperProtectionSpeedBonus = r.Float("Helper.ProtectionSpeedBonus",0f,2f);
            v.HelperBossDamageScale = r.Float("Helper.BossDamageScale",0.1f,1f);
            v.HelperMoveSeconds = r.Float("Helper.MoveSeconds",0.3f,10f);
            v.HelperCrowdedCount = r.Int("Helper.CrowdedCount",1,81);
            v.HelperBeamScore = r.Int("Helper.BeamScore",10,100000);
            v.HelperBeamReserve = r.Int("Helper.BeamReserve",10,10000);
            v.HelperBeamTickCost = r.Int("Helper.BeamTickCost",1,100);
            v.HelperBeamDamage = r.Float("Helper.BeamDamage",1f,200f);
            v.HelperBeamSeconds = r.Float("Helper.BeamSeconds",0.3f,10f);
            v.HelperBeamCooldown = r.Float("Helper.BeamCooldown",3f,120f);
            v.HelperRangedAttackLink = r.Float("Helper.RangedAttackLink",0f,2f);
            v.HelperRangedSpeedLink = r.Float("Helper.RangedSpeedLink",0f,2f);
            v.HelperRangedAttackCap = r.Float("Helper.RangedAttackCap",0f,2f);
            v.HelperRangedSpeedCap = r.Float("Helper.RangedSpeedCap",0f,2f);
            v.EmergencyMaxCount = r.Int("Assist.EmergencyMaxCount",0,10);
            v.PlayerFirepowerSoftCount = r.Int("Player.FirepowerSoftCount",1,81);
            v.PlayerFirepowerExponent = r.Float("Player.FirepowerExponent",0f,3f);
            v.PlayerFirepowerMinScale = r.Float("Player.FirepowerMinScale",.01f,1f);
            r.ReportUnused();

            // ---- 跨参数的逻辑约束：单个值合法，组合起来不合法也会出事
            if (v.HelperHitScore >= v.HelperShotCost) { problems.Add("帮手命中返分必须小于单发消耗，避免自给自足无限开火"); }
            if (v.HelperMinInterval > v.HelperBaseInterval) { problems.Add("帮手最快间隔不能超过基础间隔"); }
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
        public static int DeployBatchCount => V.DeployBatchCount;
        public static float CriticalChance => V.CriticalChance;
        public static float CriticalMultiplier => V.CriticalMultiplier;
        public static int ScoreKillPoints => V.ScoreKillPoints;
        public static int ScoreCriticalPoints => V.ScoreCriticalPoints;
        public static float ScoreRoundGrowth => V.ScoreRoundGrowth;
        public static float ScorePieceGrowth => V.ScorePieceGrowth;
        public static float ScoreMultiplierCap => V.ScoreMultiplierCap;


        // ---- 战斗节奏
        public static float BattleDurationSeconds => V.BattleDurationSeconds;
        public static float BattleTickSeconds => V.BattleTickSeconds;
        /// <summary>固定模拟小步：一个行动周期拆成 StepsPerTick 份。</summary>
        public static float BattleSimulationStepSeconds => V.BattleTickSeconds / V.BattleStepsPerTick;
        public static float AttackSpeedScale => V.AttackSpeedScale;
        /// <summary>敌人普攻倍率，包含主将普攻；不缩短主将技能预警。</summary>
        public static float EnemyAttackRate => V.EnemyAttackRate;
        public static int EnemyChaseSteps => V.EnemyChaseSteps;
        public static bool RangedFormationEnabled => V.RangedFormationEnabled == 1;
        public static int RangedFormationMinCount => V.RangedFormationMinCount;
        public static float RangedFormationSeconds => V.RangedFormationSeconds;
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
        public static float BossHpMultiplier => V.BossHpMultiplier;
        public static int BossCommonSkillsPerExclusive => V.BossCommonSkillsPerExclusive;
        public static float BossAttackMultiplier => V.BossAttackMultiplier;
        public static float BossPercentDamageScale => V.BossPercentDamageScale;
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
        /// <summary>来自驻眼配置Eye.PlayerAttackBonus，离眼立即还原，不累乘基础属性。</summary>
        public static float PlayerEyeAttackBonus => V.PlayerEyeAttackBonus;
        /// <summary>来自驻眼配置Eye.PlayerSpeedBonus，离眼立即还原，不累乘基础属性。</summary>
        public static float PlayerEyeSpeedBonus => V.PlayerEyeSpeedBonus;
        /// <summary>来自驻眼配置Eye.PlayerDefenseBonus，离眼立即还原，不累乘基础属性。</summary>
        public static float PlayerEyeDefenseBonus => V.PlayerEyeDefenseBonus;
        /// <summary>来自驻眼配置Eye.EnemyAttackBonus，离眼立即还原，不累乘基础属性。</summary>
        public static float EnemyEyeAttackBonus => V.EnemyEyeAttackBonus;
        /// <summary>守眼四邻防御加成，比例，多个眼不叠加；来自Excel的Eye.GuardDefenseBonus。</summary>
        public static float EyeGuardDefenseBonus => V.EyeGuardDefenseBonus;
        /// <summary>空眼远程支援间隔，秒；来自Excel的Eye.SupportSeconds。</summary>
        public static float EyeSupportSeconds => V.EyeSupportSeconds;
        /// <summary>驻眼原地复活生命比例，每颗棋一生一次；来自Excel的Eye.ReviveHpRatio。</summary>
        public static float EyeReviveHpRatio => V.EyeReviveHpRatio;
        /// <summary>复活时四邻友军治疗比例，按各自最大生命计算；来自Excel的Eye.ReviveHealRatio。</summary>
        public static float EyeReviveHealRatio => V.EyeReviveHealRatio;
        /// <summary>来自驻眼配置Eye.EnemySpeedBonus，离眼立即还原，不累乘基础属性。</summary>
        public static float EnemyEyeSpeedBonus => V.EnemyEyeSpeedBonus;
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
        public static float EmergencyFirstSeconds => V.EmergencyFirstSeconds;
        public static float EmergencyIntervalSeconds => V.EmergencyIntervalSeconds;
        public static float EmergencyHpRatio => V.EmergencyHpRatio;
        public static float EmergencyHealRatio => V.EmergencyHealRatio;
        public static int EmergencyMaxCount => V.EmergencyMaxCount;
        public static int RangedLimit => V.RangedLimit;
        public static int BossPlayerPieceLimit => V.BossPlayerPieceLimit;
        public static float EnemyJumpSeconds => V.EnemyJumpSeconds;
        public static int HelperStormThreshold => V.HelperStormThreshold;
        public static float HelperStormMissileSeconds => V.HelperStormMissileSeconds;
        public static float HelperStormMissileDamage => V.HelperStormMissileDamage;
        public static float HelperStormBlastRadius => V.HelperStormBlastRadius;
        public static float HelperStormJellySeconds => V.HelperStormJellySeconds;
        public static int HelperStormJellyCount => V.HelperStormJellyCount;
        public static float HelperStormJellyDamage => V.HelperStormJellyDamage;
        public static float HelperStormBeamSeconds => V.HelperStormBeamSeconds;
        public static float HelperStormBeamCooldown => V.HelperStormBeamCooldown;
        public static float HelperStormBeamDps => V.HelperStormBeamDps;
        public static float HelperStormBeamWidth => V.HelperStormBeamWidth;
        public static int HelperPlayerHitScore => V.HelperPlayerHitScore;
        public static int HelperHitScore => V.HelperHitScore;
        public static int HelperShotCost => V.HelperShotCost;
        public static float HelperBaseDamage => V.HelperBaseDamage;
        public static int HelperScoreStep => V.HelperScoreStep;
        public static int HelperMaxTier => V.HelperMaxTier;
        public static float HelperDamageGrowth => V.HelperDamageGrowth;
        public static float HelperBaseInterval => V.HelperBaseInterval;
        public static float HelperMinInterval => V.HelperMinInterval;
        public static float HelperBossSpeedBonus => V.HelperBossSpeedBonus;
        public static float HelperProtectionHealPerSecond => V.HelperProtectionHealPerSecond;
        public static float HelperProtectionAttackBonus => V.HelperProtectionAttackBonus;
        public static float HelperProtectionSpeedBonus => V.HelperProtectionSpeedBonus;
        public static float HelperBossDamageScale => V.HelperBossDamageScale;
        public static float HelperMoveSeconds => V.HelperMoveSeconds;
        public static int HelperCrowdedCount => V.HelperCrowdedCount;
        public static int HelperBeamScore => V.HelperBeamScore;
        public static int HelperBeamReserve => V.HelperBeamReserve;
        public static int HelperBeamTickCost => V.HelperBeamTickCost;
        public static float HelperBeamDamage => V.HelperBeamDamage;
        public static float HelperBeamSeconds => V.HelperBeamSeconds;
        public static float HelperBeamCooldown => V.HelperBeamCooldown;
        public static float HelperRangedAttackLink => V.HelperRangedAttackLink;
        public static float HelperRangedSpeedLink => V.HelperRangedSpeedLink;
        public static float HelperRangedAttackCap => V.HelperRangedAttackCap;
        public static float HelperRangedSpeedCap => V.HelperRangedSpeedCap;
        public static int PlayerFirepowerSoftCount => V.PlayerFirepowerSoftCount;
        public static float PlayerFirepowerExponent => V.PlayerFirepowerExponent;
        public static float PlayerFirepowerMinScale => V.PlayerFirepowerMinScale;
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

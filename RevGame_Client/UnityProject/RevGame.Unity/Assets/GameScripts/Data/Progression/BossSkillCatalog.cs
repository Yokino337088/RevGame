// ============================================================
// BossSkillCatalog.cs —— Boss 技能表（BossSkillConfig）的类型化读取、校验与章节难度换算。
//
// 为什么技能参数要走 Excel：
// 冷却、蓄力、伤害倍率、半径、解锁章节都是“手感数值”，策划会反复调。
// 写死在状态类里，每调一次都要改代码重新编译；放进表里，改完导表即可生效。
//
// 难度怎么随章节提升（第一章最温和，第六章最难）：
// 1) 解锁：UnlockChapter ≤ 当前章节的技能才会被 Boss 使用，所以章节越高技能越多，第六章全部可用；
// 2) 冷却：冷却 × (1 − (章节−1) × Boss.CooldownPerChapter)，章节越高放技能越频繁；
// 3) 伤害：倍率 × (1 + (章节−1) × Boss.PowerPerChapter)，章节越高每一击越痛；
// 4) 狂暴：只有解锁了 Enrage 的章节，Boss 残血时才会狂暴并进一步缩短冷却。
// 这里只做数值换算，不决定“现在放哪个技能”——那由 BossTactics 评分、BossStates 执行。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using Revolution;

namespace RevGame.Data
{
    /// <summary>一条 Boss 技能的不可变定义（第 1 章基准值）。章节换算请用 <see cref="BossSkillCatalog"/> 的方法。</summary>
    public sealed class BossSkillDef
    {
        /// <summary>技能种类，与枚举一一对应。</summary>
        public BossSkillKind Kind { get; internal set; }
        /// <summary>技能中文名（HUD 弹幕标题、战报）。</summary>
        public string Name { get; internal set; }
        /// <summary>给玩家的应对提示（HUD 弹幕副标题）。</summary>
        public string Hint { get; internal set; }
        /// <summary>从第几章开始可用。</summary>
        public int UnlockChapter { get; internal set; }
        /// <summary>挑选权重；0 = 只能由条件触发（狂暴）。</summary>
        public int Weight { get; internal set; }
        /// <summary>第 1 章的冷却秒数。</summary>
        public float Cooldown { get; internal set; }
        /// <summary>蓄力预警秒数（不随章节缩短：再难也要给玩家看清的时间）。</summary>
        public float Windup { get; internal set; }
        /// <summary>多段之间的间隔秒数。</summary>
        public float Interval { get; internal set; }
        /// <summary>段数/数量，含义见表格说明。</summary>
        public int Count { get; internal set; }
        /// <summary>第 1 章的伤害倍率（相对 Boss 一次普通攻击）。</summary>
        public float Power { get; internal set; }
        /// <summary>伤害半径（格）。</summary>
        public int Radius { get; internal set; }
        /// <summary>
        /// 弹幕飞行速度（格/秒）。只有弹幕类技能使用，0 表示该技能不发射飞行弹幕。
        /// 单位取"格/秒"而不是"世界单位/秒"：策划改棋盘大小时手感不会跟着变。
        /// </summary>
        public float BulletSpeed { get; internal set; }
    }

    public static class BossSkillCatalog
    {
        /// <summary>按种类取技能定义；配置装配时已保证每个枚举都有一行，所以这里不会返回 null。</summary>
        public static BossSkillDef Get(BossSkillKind kind)
        {
            return GameConfig.Current.BossSkills[kind];
        }

        /// <summary>该章节的 Boss 能不能使用这个技能。</summary>
        public static bool IsUnlocked(BossSkillKind kind, int chapter)
        {
            return chapter >= Get(kind).UnlockChapter;
        }

        /// <summary>
        /// 换算后的冷却：章节越高越短，狂暴后再乘一次系数。
        /// 最少保留 0.5 秒，避免策划把系数填得过大时技能每个模拟步都触发。
        /// </summary>
        public static float CooldownFor(BossSkillKind kind, int chapter, bool enraged)
        {
            float scale = 1f - (System.Math.Max(1, chapter) - 1) * Balance.BossCooldownPerChapter;
            if (enraged)
            {
                scale *= Balance.BossEnrageCooldownScale;
            }
            return System.Math.Max(0.5f, Get(kind).Cooldown * scale);
        }

        /// <summary>换算后的伤害倍率：章节越高越痛。</summary>
        public static float PowerFor(BossSkillKind kind, int chapter)
        {
            return Get(kind).Power * (1f + (System.Math.Max(1, chapter) - 1) * Balance.BossPowerPerChapter);
        }

        /// <summary>
        /// 校验并装配 BossSkillConfig。由 GameConfig.Initialize 调用；问题写入 problems，不在这里抛出，
        /// 这样所有表的问题能汇总成一份清单一起给策划。
        /// </summary>
        internal static void Build(GameConfigTables tables, ConfigSnapshot snapshot, List<string> problems)
        {
            var keys = new List<string>();
            foreach (BossSkillConfig row in tables.BossSkills.All)
            {
                keys.Add(row.Skill);
                string where = "BossSkillConfig[" + row.Skill + "]";
                if (!GameConfig.TryEnum(row.Skill, where, "Skill", problems, out BossSkillKind kind))
                {
                    continue;
                }
                // 每个检查都要执行（不用 && 短路），这样同一行的多个问题能一次全部报出来。
                bool ok = true;
                if (string.IsNullOrWhiteSpace(row.Name))
                {
                    problems.Add(where + " 的 Name 不能为空"); ok = false;
                }
                if (string.IsNullOrWhiteSpace(row.Hint))
                {
                    problems.Add(where + " 的 Hint 不能为空"); ok = false;
                }
                ok &= GameConfig.CheckRange(row.UnlockChapter, 1, 6, where, "UnlockChapter", problems);
                ok &= GameConfig.CheckRange(row.Weight, 0, 1000, where, "Weight", problems);
                ok &= GameConfig.CheckRange(row.Cooldown, 0f, 600f, where, "Cooldown", problems);
                // 蓄力至少 0.3 秒：再短玩家根本看不到预警，等于没有预警。
                ok &= GameConfig.CheckRange(row.Windup, 0.3f, 10f, where, "Windup", problems);
                ok &= GameConfig.CheckRange(row.Interval, 0f, 10f, where, "Interval", problems);
                ok &= GameConfig.CheckRange(row.Count, 1, 32, where, "Count", problems);
                ok &= GameConfig.CheckRange(row.Power, 0f, 50f, where, "Power", problems);
                ok &= GameConfig.CheckRange(row.Radius, 0, 8, where, "Radius", problems);
                ok &= GameConfig.CheckRange(row.BulletSpeed, 0f, 60f, where, "BulletSpeed", problems);
                if (kind != BossSkillKind.Enrage && kind != BossSkillKind.Summon && row.Power <= 0f)
                {
                    problems.Add(where + " 是伤害技能，Power 必须大于 0"); ok = false;
                }
                // 弹幕技能必须有飞行速度，否则子弹会停在 Boss 身上（表现为"没打出去"），
                // 这类配置错误在编辑器里很难看出来，所以启动期直接报出来。
                if (kind == BossSkillKind.BulletRing && row.BulletSpeed <= 0f)
                {
                    problems.Add(where + " 是弹幕技能，BulletSpeed 必须大于 0（单位：格/秒）"); ok = false;
                }
                if (kind != BossSkillKind.Enrage && row.Weight == 0)
                {
                    problems.Add(where + " 的 Weight = 0 会让技能永远不被使用；只有 Enrage 允许为 0"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                snapshot.BossSkills[kind] = new BossSkillDef
                {
                    Kind = kind,
                    Name = row.Name.Trim(),
                    Hint = row.Hint.Trim(),
                    UnlockChapter = row.UnlockChapter,
                    Weight = row.Weight,
                    Cooldown = row.Cooldown,
                    Windup = row.Windup,
                    Interval = row.Interval,
                    Count = row.Count,
                    Power = row.Power,
                    Radius = row.Radius,
                    BulletSpeed = row.BulletSpeed,
                };
            }
            GameConfig.CheckCoverage<BossSkillKind>(keys, "BossSkillConfig", problems);
        }
    }
}

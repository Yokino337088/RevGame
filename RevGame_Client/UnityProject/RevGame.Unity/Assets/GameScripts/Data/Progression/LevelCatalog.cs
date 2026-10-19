// ============================================================
// LevelCatalog.cs
// 关卡规则：敌方人数/成长/AI 档位来自“战斗数值参数.xlsx”，每章敌方星级与 Boss 阵容、出场名单、补兵规则来自
// “关卡与奖励配置.xlsx”。这里只保留“把配置换算成每个回合的 RoundDef”这一步，不再写死任何数值。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>
    /// 一个回合的静态配置：第几章、第几回合、是不是 Boss 回合、敌方阵容。
    /// </summary>
    public sealed class RoundDef
    {
        public int Chapter { get; }
        public int Round { get; }
        /// <summary>连续进度从零起算；跨章不归零，防止第2章第1轮比上一章Boss前还弱。</summary>
        public int Progress => (Chapter - 1) * Balance.RoundsPerChapter + Round - 1;
        /// <summary>
        /// 敌方生命成长系数 = 基础系数 × 额外倍率 × (1 + 进度 × 每回合增幅)，三项都在“战斗数值参数.xlsx”。
        /// 成长只改运行期棋子，不修改所有单位共用的图鉴。
        /// </summary>
        public float HpScale => Balance.EnemyHpScale * Balance.EnemyHpMultiplier * (1f + Progress * Balance.EnemyHpGrowthPerRound);
        public float AttackScale => 1f + Progress * Balance.EnemyAttackGrowthPerRound;
        /// <summary>四档逐步开放选目标、收残血、压气和规避火力；移动冷却始终不缩短。</summary>
        public int Intelligence => LevelCatalog.GetIntelligence(Chapter, Round);
        /// <summary>是否 Boss 回合（每章最后一个回合）。</summary>
        public bool IsBoss { get; }
        /// <summary>敌方普通棋名单；布阵算法优先用前三种远程构建眼阵，Boss 大棋由战斗服务另加。</summary>
        public IReadOnlyList<PieceKind> EnemyKinds { get; }

        public RoundDef(int chapter, int round, bool isBoss, IReadOnlyList<PieceKind> enemyKinds)
        {
            Chapter = chapter;
            Round = round;
            IsBoss = isBoss;
            EnemyKinds = enemyKinds;
        }

        public override string ToString()
        {
            return "第 " + Chapter + " 章 · 第 " + Round + " 回合" + (IsBoss ? "（Boss）" : "");
        }
    }

    /// <summary>一章的 Boss 阵容（来自 ChapterConfig）。</summary>
    public sealed class BossLineup
    {
        public PieceKind Kind { get; }
        public StarTier Star { get; }
        /// <summary>每颗 Boss 大棋的占格边长，第一颗是带王冠的主将。</summary>
        public IReadOnlyList<int> BodySizes { get; }

        internal BossLineup(PieceKind kind, StarTier star, int[] sizes)
        {
            Kind = kind;
            Star = star;
            BodySizes = sizes;
        }
    }

    /// <summary>补兵规则命中的计算放在这里，装配校验与战斗运行共用同一份逻辑。</summary>
    internal static class LevelCatalogInternals
    {
        /// <summary>
        /// 在“已补兵 count 次、AI 档位 intelligence”时挑规则：取 (count % Every == Offset 且 档位够) 里 Priority 最大者，
        /// 同优先级取靠前一行。没有命中返回 null。
        /// </summary>
        public static PieceKind? PickReinforce(ConfigSnapshot snapshot, int count, int intelligence)
        {
            ReinforceRule best = null;
            foreach (ReinforceRule rule in snapshot.Reinforce)
            {
                if (intelligence < rule.MinIntelligence || count % rule.Every != rule.Offset) { continue; }
                if (best == null || rule.Priority > best.Priority) { best = rule; }
            }
            return best == null ? (PieceKind?)null : best.Kind;
        }
    }

    /// <summary>
    /// 关卡表：章数 × 每章（普通回合数 + 1 个 Boss）。
    /// 敌方阵容生成规则（有意做得简单、可预期，参数都在 Excel）：
    /// - 普通回合：先排 Core 核心棋，人数不足时循环 Filler 填充，人数随进度增长并封顶；
    /// - Boss 回合：额外标记 <see cref = "RoundDef.IsBoss"/>，大棋阵容见 <see cref="BossOf"/>。
    /// </summary>
    public static class LevelCatalog
    {
        /// <summary>
        /// 只查AI档位，不创建完整RoundDef和敌军名单。攻击选目标会频繁查询，不能为一个整数反复生成整套阵容。
        /// 与回合配置共用这一个公式：每 Enemy.AiRoundsPerLevel 个连续回合提升一档，跨章不归零，上限 Enemy.AiMaxLevel；未开局按1档处理。
        /// </summary>
        public static int GetIntelligence(int chapter, int round)
        {
            if (chapter < 1 || chapter > Consts.ChapterCount || round < 1 || round > Balance.RoundsPerChapter) { return 1; }
            int progress = (chapter - 1) * Balance.RoundsPerChapter + round - 1;
            return System.Math.Min(Balance.EnemyAiMaxLevel, 1 + progress / Balance.EnemyAiRoundsPerLevel);
        }

        /// <summary>取某一章某一回合的配置。参数越界返回 null（调用方判空）。</summary>
        public static RoundDef GetRound(int chapter, int round)
        {
            if (chapter < 1 || chapter > Consts.ChapterCount)
            {
                return null;
            }

            if (round < 1 || round > Balance.RoundsPerChapter)
            {
                return null;
            }

            ConfigSnapshot snapshot = GameConfig.Current;
            bool isBoss = round == Balance.BossRoundIndex;
            List<PieceKind> enemies = new List<PieceKind>();
            // 前几名是输出骨架（Core）：重弩全盘压制、弓箭直线远射、火法利用炮架。
            // 后面的近战（Filler）连接成守眼骨架。数量与眼阵规模对应，不能只多生成一排散兵。
            enemies.AddRange(snapshot.CoreRoster);
            int progress = (chapter - 1) * Balance.RoundsPerChapter + round - 1;
            int count = System.Math.Min(Balance.EnemyCountMax, Balance.EnemyCountBase + progress / Balance.EnemyCountRoundsPerExtra);
            for (int i = enemies.Count; i < count; i++)
            {
                enemies.Add(snapshot.FillerRoster[i % snapshot.FillerRoster.Count]);
            }

            return new RoundDef(chapter, round, isBoss, enemies);
        }

        /// <summary>
        /// 某个回合敌方棋子的星级（ChapterConfig.EnemyStar）。Boss 额外增加大棋，不临时提高普通兵星级。
        /// </summary>
        public static StarTier GetEnemyStar(int chapter, bool isBoss)
        {
            // Boss 已有大棋和额外生命，不再临时升普通兵星级；否则下一轮普通兵会出现倒退。
            return (StarTier)ChapterOf(chapter).EnemyStar;
        }

        /// <summary>本章 Boss 大棋阵容（外形、星级、每颗占格）。</summary>
        public static BossLineup BossOf(int chapter)
        {
            ChapterRule rule = ChapterOf(chapter);
            return new BossLineup(rule.BossKind, (StarTier)rule.BossStar, rule.BossBodySizes);
        }

        /// <summary>战斗中敌方补兵的外形：按“已补兵次数 + AI 档位”套用 EnemyReinforceConfig；没有命中返回 null。</summary>
        public static PieceKind? PickReinforcement(int deployedCount, int intelligence)
        {
            return LevelCatalogInternals.PickReinforce(GameConfig.Current, deployedCount, intelligence);
        }

        private static ChapterRule ChapterOf(int chapter)
        {
            ConfigSnapshot snapshot = GameConfig.Current;
            if (chapter < 1 || chapter > snapshot.Chapters.Count)
            {
                throw new System.ArgumentOutOfRangeException(nameof(chapter), "章节 " + chapter + " 不在配置表 ChapterConfig 范围内（1~" + snapshot.Chapters.Count + "）");
            }
            return snapshot.Chapters[chapter - 1];
        }

        /// <summary>整局总回合数（章数 × 每章回合数）。</summary>
        public static int TotalRounds
        {
            get
            {
                return Consts.ChapterCount * Balance.RoundsPerChapter;
            }
        }
    }
}

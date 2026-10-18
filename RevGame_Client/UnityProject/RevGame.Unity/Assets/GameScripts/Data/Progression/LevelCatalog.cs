// ============================================================
// LevelCatalog.cs
// 目前提供可运行的占位阵容，正式关卡表到位后替换数据来源，流程不需要改写。
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
        public int Progress => (Chapter - 1) * Consts.RoundsPerChapter + Round - 1;
        /// <summary>成长只改运行期棋子，不修改所有单位共用的图鉴。</summary>
        // 六牌保留、双牌升级和每轮增益都提高了我方火力，因此敌方基础生命同步提高，避免首轮几秒清场。
        // 强援现在至少二星且会选安全射位，基础承伤同步小幅提高，避免首关退回十几秒内清场。
        public float HpScale => Consts.EnemyHpScale * 4.5f * (1f + Progress * 0.018f);
        public float AttackScale => 1f + Progress * 0.009f;
        /// <summary>四档逐步开放选目标、收残血、压气和规避火力；移动冷却始终不缩短。</summary>
        public int Intelligence => LevelCatalog.GetIntelligence(Chapter, Round);
        /// <summary>是否 Boss 回合（每章第 11 回合）。</summary>
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

    /// <summary>
    /// 关卡表：6 章 × 每章 11 回合。
    ///
    /// ⚠️ 敌方阵容是**程序生成的占位**（等 Excel 关卡表接入后替换）。
    /// 生成规则（有意做得简单、可预期）：
    /// - 普通回合：敌方 6~19 颗，固定有重弩、弓箭和火法，星级随章数提升；
    /// - Boss 回合：在第 11 回合，额外标记 <see cref = "RoundDef.IsBoss"/>。
    /// </summary>
    public static class LevelCatalog
    {
        /// <summary>
        /// 只查AI档位，不创建完整RoundDef和敌军名单。攻击选目标会频繁查询，不能为一个整数反复生成整套阵容。
        /// 与回合配置共用这一个公式：每17个连续回合提升一档，跨章不归零，上限4；未开局按1档处理。
        /// </summary>
        public static int GetIntelligence(int chapter, int round)
        {
            if (chapter < 1 || chapter > Consts.ChapterCount || round < 1 || round > Consts.RoundsPerChapter) { return 1; }
            int progress = (chapter - 1) * Consts.RoundsPerChapter + round - 1;
            return System.Math.Min(4, 1 + progress / 17);
        }

        /// <summary>取某一章某一回合的配置。参数越界返回 null（调用方判空）。</summary>
        public static RoundDef GetRound(int chapter, int round)
        {
            if (chapter < 1 || chapter > Consts.ChapterCount)
            {
                return null;
            }

            if (round < 1 || round > Consts.RoundsPerChapter)
            {
                return null;
            }

            bool isBoss = round == Consts.BossRoundIndex;
            List<PieceKind> enemies = new List<PieceKind>();
            // 前三名是输出骨架：重弩全盘压制、弓箭直线远射、火法利用炮架。
            // 后面的近战连接成守眼骨架。数量与眼阵规模对应，不能只多生成一排散兵。
            enemies.Add(PieceKind.Crossbow);
            enemies.Add(PieceKind.Archer);
            enemies.Add(PieceKind.MageFire);
            int progress = (chapter - 1) * Consts.RoundsPerChapter + round - 1;
            int count = System.Math.Min(19, 6 + progress / 5);
            for (int i = enemies.Count; i < count; i++)
            {
                enemies.Add(i % 3 == 0 ? PieceKind.Shield : (i % 3 == 1 ? PieceKind.HeavySword : PieceKind.Swordman));
            }

            return new RoundDef(chapter, round, isBoss, enemies);
        }

        /// <summary>
        /// 某个回合敌方棋子的星级（占位规则：第 1~2 章 1★、3~4 章 2★、5~6 章 3★；Boss额外增加大棋，不临时提高普通兵星级）。
        /// </summary>
        public static StarTier GetEnemyStar(int chapter, bool isBoss)
        {
            int star = chapter <= 2 ? 1 : (chapter <= 4 ? 2 : 3);
            // Boss 已有大棋和额外生命，不再临时升普通兵星级；否则下一轮普通兵会出现倒退。
            return (StarTier)star;
        }

        /// <summary>整局总回合数（6 章 × 11）。</summary>
        public static int TotalRounds
        {
            get
            {
                return Consts.ChapterCount * Consts.RoundsPerChapter;
            }
        }
    }
}

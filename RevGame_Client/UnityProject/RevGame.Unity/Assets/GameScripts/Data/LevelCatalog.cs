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
        /// <summary>是否 Boss 回合（每章第 11 回合）。</summary>
        public bool IsBoss { get; }
        /// <summary>敌方棋子外形（按顺序摆放；Boss 回合会额外加一个 Boss 大棋）。</summary>
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
    /// - 普通回合：敌方 3~5 颗，外形按章号轮换，星级随章数提升；
    /// - Boss 回合：在第 11 回合，额外标记 <see cref = "RoundDef.IsBoss"/>。
    /// </summary>
    public static class LevelCatalog
    {
        private static readonly PieceKind[] Pool =
        {
            PieceKind.Swordman,
            PieceKind.Shield,
            PieceKind.Archer,
            PieceKind.MageFire,
            PieceKind.Assassin,
        };
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
            if (isBoss)
            {
                // Boss 回合：先放 2 颗精英，再标记 Boss（Boss 本体由 Battle 层生成大棋）。
                enemies.Add(Pool[chapter % Pool.Length]);
                enemies.Add(Pool[(chapter + 2) % Pool.Length]);
            }
            else
            {
                // 普通回合：3 + 章内进度，最多 5 颗。
                int count = 3 + (round - 1) / 4;
                if (count > 5)
                {
                    count = 5;
                }

                for (int i = 0; i < count; i++)
                {
                    int index = (chapter * 3 + round + i * 2) % Pool.Length;
                    enemies.Add(Pool[index]);
                }
            }

            return new RoundDef(chapter, round, isBoss, enemies);
        }

        /// <summary>
        /// 某个回合敌方棋子的星级（占位规则：第 1~2 章 1★、3~4 章 2★、5~6 章 3★；Boss 回合 +1 星，最高 3）。
        /// </summary>
        public static StarTier GetEnemyStar(int chapter, bool isBoss)
        {
            int star = chapter <= 2 ? 1 : (chapter <= 4 ? 2 : 3);
            if (isBoss && star < 3)
            {
                star++;
            }

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

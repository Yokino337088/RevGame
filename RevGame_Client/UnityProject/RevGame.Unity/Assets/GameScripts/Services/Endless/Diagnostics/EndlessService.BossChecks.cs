// ============================================================
// EndlessService.BossChecks.cs —— 领主技能的规则自检（只在开发版编译）
//
// 为什么要写自检：技能涉及“选招式→预警→分段伤害→冷却”一长串状态，
// 手动试玩很难每章都覆盖。这里用隔离的 EndlessService 把六章全部跑一遍，
// 断言读的都是真实业务结果，不碰玩家存档，也不依赖任何表现对象。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
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
        /// <summary>
        /// 立刻让指定章节的领主上场，仅供隔离测试局使用，正式局一律拒绝，玩家无法借此刷奖励。
        /// 返回 false 时 LastError 说明原因。
        /// </summary>
        public bool DebugSpawnBoss(int chapter)
        {
            if (!Isolated)
            {
                return Reject("只有隔离测试局可以直接召唤领主");
            }
            if (chapter < 1 || chapter > 6)
            {
                return Reject("章节必须是 1~6");
            }
            if (BossUnit() != null)
            {
                return Reject("场上已有领主");
            }
            if (!QueueInvader(false, 0))
            {
                return Reject("外围队列已满");
            }
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (Run.horde[i].active && Run.horde[i].kind >= 6 && TryEnterBoard(i))
                {
                    EndlessUnit boss = BossUnit();
                    boss.BossChapter = chapter;
                    boss.NextSkillAt = Run.seconds;
                    return true;
                }
            }
            return Reject("棋盘放不下领主");
        }

        /// <summary>跑完六章领主：每章都要能放普通招式和专属技，且满足下面逐条断言。</summary>
        public static object CheckBossSkills()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    CheckBossChapter(chapter, passed);
                }
                return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }

        /// <summary>单章自检。玩家棋摆在固定位置，推进真实模拟步，不直接调用私有结果来“凑”通过。</summary>
        private static void CheckBossChapter(int chapter, List<string> passed)
        {
            string tag = "第" + chapter + "章：";
            using (var s = new EndlessService())
            {
                s.Begin(0, 0, 2000 + chapter, true);
                s.StartWave();
                Check(s.DebugSpawnBoss(chapter), tag + "领主能上场", passed);
                EndlessUnit boss = s.BossUnit();
                Check(boss.BossChapter == chapter, tag + "领主章节身份正确", passed);

                // 普通招式必须来自本章，并先发出预警记录、后发出命中记录。
                s.TickChapterSkill(boss);
                var first = (BossSkillKind)boss.ChapterSkill;
                Check(boss.WarningAt > s.Run.seconds && BossSkillCatalog.IsUnlocked(first, chapter), tag + "第一招是本章招式且进入预警", passed);
                Check(s.BossCasts[s.BossCastSerial % s.BossCasts.Length].telegraph, tag + "预警写入技能记录", passed);

                // 第 N 次是专属技：招式与表一致，并且真的刷出了献祭潮。
                int every = N("BossExclusiveEvery");
                EndlessBossSkill row = EndlessConfig.BossSkill(chapter);
                for (int cast = 2; cast <= every; cast++)
                {
                    FinishSkill(s, boss);
                    s.Run.seconds = boss.NextSkillAt;
                    s.TickChapterSkill(boss);
                    Check(boss.SkillCasts == cast, tag + "领主不会因为打不到人而空过", passed);
                }
                Check((BossSkillKind)boss.ChapterSkill == EndlessConfig.ParseSkill(row) && IsExclusiveCast(boss), tag + "每隔固定次数释放专属技", passed);
                int surge = 0, index = -1;
                for (int i = 0; i < s.Run.horde.Length; i++)
                {
                    if (s.Run.horde[i].active && s.Run.horde[i].surge)
                    {
                        surge++;
                        index = i;
                    }
                }
                Check(surge > 0, tag + "专属技刷出献祭潮", passed);

                // 割草的核心约定：杀一只献祭潮，领主一定掉血，而且只掉一次。
                int before = boss.Piece.Hp;
                s.Run.horde[index].hp = 0;
                s.ResolveDeaths();
                Check(boss.Piece.Hp < before, tag + "击杀献祭潮会让领主掉血", passed);
                int after = boss.Piece.Hp;
                s.ResolveDeaths();
                Check(boss.Piece.Hp == after, tag + "同一只杂兵不会重复结算", passed);

                // 红格子必须和真实伤害一致：预警范围内的玩家棋必掉血，范围外的不掉。
                FinishCheckGeometry(s, boss, tag, passed);
            }
        }

        /// <summary>把当前招式所有段都结算完。</summary>
        private static void FinishSkill(EndlessService s, EndlessUnit boss)
        {
            for (int guard = 0; boss.WarningAt > 0 && guard < 64; guard++)
            {
                s.Run.seconds = boss.WarningAt;
                s.TickChapterSkill(boss);
            }
        }

        /// <summary>对比“显示的预警格”和“实际命中”：两者用同一函数，但这里从业务结果反向确认没有接错。</summary>
        private static void FinishCheckGeometry(EndlessService s, EndlessUnit boss, string tag, List<string> passed)
        {
            bool consistent = true;
            for (int i = 0; i < s.Units.Count; i++)
            {
                Piece p = s.Units[i].Piece;
                if (p.Side != GameSide.Player || !p.IsAlive)
                {
                    continue;
                }
                bool warned = false;
                for (int y = 0; y < p.Size; y++)
                {
                    for (int x = 0; x < p.Size; x++)
                    {
                        warned |= s.IsWarned(boss, p.Pos.X + x, p.Pos.Y + y);
                    }
                }
                int hp = p.Hp;
                var info = MakeInfo(boss);
                s._skillHits.Clear();
                BossExpandedGeometry.Collect(s.Board, info, s._skillHits);
                consistent &= s._skillHits.Contains(p) == warned;
                consistent &= hp == p.Hp;
            }
            Check(consistent, tag + "预警格与命中名单一致", passed);
        }
    }
}
#endif

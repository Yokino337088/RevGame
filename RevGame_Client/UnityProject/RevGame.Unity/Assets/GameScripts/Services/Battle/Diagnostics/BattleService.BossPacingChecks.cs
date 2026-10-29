// Boss节奏规则的开发态验收：调度使用真实BossTactics/FSM，测试不会强制跳招或改变生产数值。
// 固定高生命夹具只用于证明轮换/预警/冷却契约；正常战斗时长另由完整章节策略回放测量。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REVGAME_OFFLINE_TESTS
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>
        /// 验证六章通用/专属分组轮换、冷却拒绝与自然FSM施法频率。
        /// check由测试宿主记录断言；任一失败应抛出并保留原因，finally仍释放本方法建立的AI和事件。
        /// </summary>
        public static string VerifyBossPacingRules(Action<bool, string> check)
        {
            var summaries = new List<string>(Consts.ChapterCount);
            for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
            {
                var battle = new BattleService(false, 1029);
                object eventOwner = new object();
                try
                {
                    check(battle.StartRound(chapter, Balance.BossRoundIndex), "节奏夹具建立章节" + chapter);
                    check(battle.LoadBossSkillPreview(chapter), "节奏夹具预警与合法目标" + chapter);
                    Piece boss = battle._enemyPieces[0];
                    VerifyBossChooseGroups(battle, boss, chapter, check);
                    battle.Phase = GamePhase.Battle;
                    var owner = new BossOwner(battle, boss);
                    var startedAt = new float[owner.SkillUseCounts.Length];
                    for (int i = 0; i < startedAt.Length; i++)
                    {
                        startedAt[i] = -1f;
                    }
                    int cooldownChecks = 0;
                    RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, info =>
                    {
                        // OnEnter先统计本次施法再广播预警；同一招上次结束后才开始冷却，故两个开始时刻至少间隔冷却。
                        float previous = startedAt[(int)info.Kind];
                        if (previous >= 0f && info.Kind != BossSkillKind.Enrage)
                        {
                            float minimum = BossSkillCatalog.CooldownFor(info.Kind, chapter, owner.Enraged);
                            check(battle._battleElapsed - previous >= minimum, "第" + chapter + "章重复" + info.Kind + "遵守独立冷却");
                            cooldownChecks++;
                        }
                        startedAt[(int)info.Kind] = battle._battleElapsed;
                    }, owner: eventOwner);
                    using (var ai = new BossAI(owner))
                    {
                        // 300秒是本测试的观察窗口，不是游戏时间限制；真实正常胜率另跑未强化玩家的完整章节。
                        const float observationSeconds = 300f;
                        while (battle._battleElapsed < observationSeconds)
                        {
                            battle._battleElapsed += Balance.BattleSimulationStepSeconds;
                            battle.TickBossEffects();
                            ai.Tick(Balance.BattleSimulationStepSeconds);
                        }
                        int distinct = 0;
                        int total = 0;
                        foreach (int used in owner.SkillUseCounts)
                        {
                            total += used;
                            if (used > 0)
                            {
                                distinct++;
                            }
                        }
                        check(distinct == 10, "第" + chapter + "章300秒自然轮换六通用四专属，实际=" + distinct);
                        check(cooldownChecks > 0, "第" + chapter + "章实际复用技能并检查冷却");
                        for (int i = 0; i < owner.SkillUseCounts.Length; i++)
                        {
                            BossSkillDef def = BossSkillCatalog.Get((BossSkillKind)i);
                            if (def.ExclusiveChapter == chapter)
                            {
                                check(owner.SkillUseCounts[i] >= 2, "第" + chapter + "章专属" + def.Kind + "自然施放至少2次，实际=" + owner.SkillUseCounts[i]);
                            }
                            else if (!BossSkillCatalog.IsUnlocked((BossSkillKind)i,chapter))
                            {
                                check(owner.SkillUseCounts[i] == 0, "第" + chapter + "章不会施放其它章专属" + def.Kind);
                            }
                        }
                        summaries.Add("ch=" + chapter + ",seconds=" + observationSeconds + ",distinct=" + distinct + ",starts=" + total
                            + ",counts=" + string.Join(",", owner.SkillUseCounts));
                    }
                }
                finally
                {
                    RevEvent.RemoveAllByOwner(eventOwner);
                    battle.StopEnemyAI();
                }
            }
            return string.Join("\n", summaries);
        }

        /// <summary>
        /// 不推进战斗的选招契约：配额、冷却、非法章节都由同一生产选择器验证。
        /// 只改调用方自己的冷却/次数数组；不扣血、不移动、不提交实际技能。
        /// </summary>
        private static void VerifyBossChooseGroups(BattleService battle, Piece boss, int chapter, Action<bool, string> check)
        {
            int length = Enum.GetValues(typeof(BossSkillKind)).Length;
            var ready = new float[length];
            var counts = new int[length];
            int hp = boss.Hp;
            int common = 0;
            int exclusive = 0;
            BossSkillKind? previous = null;
            // 八个专属配额足够确认四招各自轮换；固定数量是测试覆盖范围，不是生产调度参数。
            int selections = (Balance.BossCommonSkillsPerExclusive + 1) * 8;
            for (int step = 0; step < selections; step++)
            {
                check(BossTactics.TryChoose(battle._board, boss, chapter, battle.EnemyIntelligence, ready, 0f, previous,
                    out BossSkillPlan plan, counts), "第" + chapter + "章可用招式应产生计划");
                BossSkillDef chosen = BossSkillCatalog.Get(plan.Kind);
                bool expectedExclusive = common >= (exclusive + 1) * Balance.BossCommonSkillsPerExclusive;
                check((chosen.ExclusiveChapter == chapter) == expectedExclusive, "第" + chapter + "章通用配额后准确插入专属，step=" + step);
                if (chosen.ExclusiveChapter == chapter)
                {
                    exclusive++;
                }
                else
                {
                    common++;
                }
                counts[(int)plan.Kind]++;
                previous = plan.Kind;
            }
            for (int i = 0; i < length; i++)
            {
                if (BossSkillCatalog.Get((BossSkillKind)i).ExclusiveChapter == chapter)
                { check(counts[i] == 2,"第"+chapter+"章四种专属各轮到2次："+(BossSkillKind)i); }
            }
            check(boss.Hp == hp, "选招查询不能改变Boss生命");

            // 把本章专属全部放入冷却，即使配额已到也必须安全回退通用，不能为了热闹绕过冷却。
            Array.Clear(counts, 0, length);
            foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
            {
                if (BossSkillCatalog.IsUnlocked(kind,chapter) && !BossSkillCatalog.IsSpecial(kind) && kind != BossSkillKind.Enrage)
                { counts[(int)kind] = Balance.BossCommonSkillsPerExclusive; break; }
            }
            for (int i = 0; i < length; i++)
            {
                if (BossSkillCatalog.Get((BossSkillKind)i).ExclusiveChapter == chapter) { ready[i] = 100f; }
            }
            check(BossTactics.TryChoose(battle._board, boss, chapter, battle.EnemyIntelligence, ready, 0f, null,
                out BossSkillPlan fallback, counts) && BossSkillCatalog.Get(fallback.Kind).ExclusiveChapter == 0,
                "第" + chapter + "章专属冷却时回退合法通用技能");
            for (int i = 0; i < length; i++)
            {
                ready[i] = 100f;
            }
            check(!BossTactics.TryChoose(battle._board, boss, chapter, battle.EnemyIntelligence, ready, 0f, null,
                out fallback, counts), "第" + chapter + "章全技能冷却时拒绝施法");
        }
    }
}
#endif

// 新技能固定输入断言：链接生产计划、FSM、真实伤害与控制状态，禁止只数枚举就宣布技能可用。
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
        /// <summary>每招独立造局、实际施放、到期、停止；观察器属于当前case，finally统一释放。</summary>
        public static void VerifyExpandedBossSkills(Action<bool, string> check)
        {
            foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
            {
                if (!BossSkillCatalog.IsExpanded(kind))
                {
                    continue;
                }
                int chapter = BossSkillCatalog.ThemeChapter(kind);
                var battle = new BattleService(false, 1029);
                object observerOwner = new object();
                try
                {
                    check(battle.StartRound(chapter, Balance.BossRoundIndex) && battle.LoadBossSkillPreview(chapter, 1f), kind + "独立造局");
                    Piece boss = battle._enemyPieces[0];
                    BossSkillDef def = BossSkillCatalog.Get(kind);
                    check(BossExpandedGeometry.TryPlan(battle._board, boss, kind, out BossSkillPlan plan), kind + "有合法固定计划");
                    var expected = new List<int>();
                    var info = new BossSkillInfo(boss.Id, kind, boss.Pos, boss.Size, plan.A, plan.B, plan.C, def.Radius, def.Windup, 0, def.Count, false);
                    for (int index = 0; index < def.Count; index++)
                    {
                        BossExpandedGeometry.Collect(battle._board, info.WithIndex(index), battle._exclusiveVictims);
                        foreach (Piece victim in battle._exclusiveVictims)
                        {
                            expected.Add(victim.Id);
                        }
                    }
                    int casts = 0, hits = 0;
                    RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, cast =>
                    {
                        if (cast.Kind == kind)
                        {
                            check(cast.Index == casts, kind + "段序准确");
                            casts++;
                        }
                    }, owner: observerOwner);
                    RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
                    {
                        if (hit.DamageKind != (BattleDamageKind)((int)BattleDamageKind.BossCleave + (int)kind))
                        {
                            return;
                        }
                        check(expected.Contains(hit.TargetId), kind + "伤害目标属于已预警格集合");
                        check(hit.Damage > 0 && hit.TargetSide == GameSide.Player, kind + "实际玩家正伤害");
                        hits++;
                    }, owner: observerOwner);
                    battle.Phase = GamePhase.Battle;
                    var owner = new BossOwner(battle, boss) { NextSkillAt = float.MaxValue, Enraged = true };
                    boss.NextMoveAt = float.MaxValue;
                    using (var ai = new BossAI(owner))
                    {
                        check(ai.TryCast(kind), kind + "生产FSM接受施放");
                        float step = Balance.BattleSimulationStepSeconds;
                        float end = def.Windup + (def.Count - 1) * def.Interval + Math.Max(1f, def.Duration) + Balance.BossRecoverSeconds + step;
                        while (battle._battleElapsed < end)
                        {
                            battle._battleElapsed += step;
                            battle.TickBossEffects();

                            // 收招后只推进状态租约，禁止下一招自然施放污染单招段数与命中观察。
                            if (ai.State != BossAIState.Idle)
                            {
                                ai.Tick(step);
                            }
                        }
                        check(casts == def.Count && ai.State == BossAIState.Idle, kind + "完整段数与收招");
                        check(hits > 0, kind + "观察到真实命中");
                        foreach (Piece p in battle._playerPieces)
                        {
                            check(!p.BossFrozen && !p.BossHidden && p.BossAttackFactor == 1f && p.BossSpeedFactor == 1f, kind + "到期清除控制与减益");
                        }
                        check(battle._bossEffects.Count == 0, kind + "效果租约归零");
                    }
                    // 其它章节即使目标合法也应拒绝，拒绝不改变已完成状态或占格。
                    int version = battle._board.TopologyVersion;
                    int foreignChapter = chapter % Consts.ChapterCount + 1;
                    check(!BossSkillCatalog.IsUnlocked(kind, foreignChapter), kind + "其它章节不继承");
                    check(battle._board.TopologyVersion == version, kind + "章节查询无副作用");
                }
                finally
                {
                    RevEvent.RemoveAllByOwner(observerOwner);
                    battle.StopEnemyAI();
                }
            }
        }
    }
}
#endif

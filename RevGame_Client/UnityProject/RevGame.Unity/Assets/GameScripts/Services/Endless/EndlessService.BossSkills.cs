// ============================================================
// EndlessService.BossSkills.cs —— 无尽领主的技能调度（新手先读这段）
//
// 【这个文件解决什么】
// 六章 Boss 的招式“长什么样、打哪些格子”已经写好了（BossExpandedGeometry 决定命中范围，
// BossSkillFxView 负责画特效）。这里只负责“什么时候放、放哪招、打完结算什么”。
// 所以无尽和六章共用同一套几何与特效：看到的红格子一定就是真实伤害范围。
//
// 【一次技能的流程】
//   冷却结束 → 选招式并锁定目标格（BeginSkill）→ 蓄力预警（玩家可以挪棋躲开）
//   → 到点结算一段伤害（ResolveStage）→ 多段技能重复上一步 → 进入下一轮冷却
//
// 【无尽专属技为什么能割草】
// 每放几次普通招式，就放一次“专属技”：领主召来一波很脆的“献祭潮”杂兵（Surge）。
// 玩家每杀一只，领主就损失一点血，于是清杂兵本身就是在打 Boss；
// 领主的招式还会误伤自己的杂兵。这样 Boss 战不是单调磨血，而是满屏清怪。
//
// 【和表现层的关系】
// 本类不碰任何 Unity 表现对象，只把“发生了什么”写进 BossCasts 环形缓冲，
// EndlessArena 每帧读取并播放特效。特效丢了不影响伤害，这是全项目统一的规矩。
// ============================================================
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
    /// <summary>一条“领主放了技能”的记录，业务写入、表现层读取。用值类型，写入时不产生垃圾回收压力。</summary>
    public struct EndlessBossCast
    {
        /// <summary>递增序号；表现层记住“上次播到第几条”，就不会重复播放或漏播。</summary>
        public int serial;
        /// <summary>技能快照：招式、三个目标格、第几段。特效只读它，不读可能已被改动的棋子。</summary>
        public BossSkillInfo info;
        /// <summary>true 表示蓄力预警开始；false 表示这一段真正命中。</summary>
        public bool telegraph;
        /// <summary>是不是无尽专属技，表现层据此加强屏幕反馈。</summary>
        public bool exclusive;
    }

    public sealed partial class EndlessService
    {
        /// <summary>
        /// 技能记录环形缓冲：写满后覆盖最旧的一条。
        /// 16 条足够覆盖一帧内可能发生的所有技能事件，旧记录被覆盖只会少播特效，不影响伤害。
        /// </summary>
        public readonly EndlessBossCast[] BossCasts = new EndlessBossCast[16];

        /// <summary>目前写到第几条记录；表现层对比它就知道有没有新事件。</summary>
        public int BossCastSerial { get; private set; }

        /// <summary>给 HUD 显示的当前技能播报，例如“星潮陨幕：……”。没有技能时是空串。</summary>
        public string BossBanner { get; private set; } = "";

        /// <summary>本段技能命中的玩家棋名单。复用同一个 List，避免每次放技能都 new 产生垃圾。</summary>
        private readonly List<Piece> _skillHits = new List<Piece>(81);

        /// <summary>
        /// 由 UpdateBoss 每个模拟步调用：正在蓄力就等到点结算，没在蓄力就看冷却是否结束。
        /// 破阵窗口期间 UpdateBoss 会提前返回，所以领主被围住时不会放技能。
        /// </summary>
        private void TickChapterSkill(EndlessUnit boss)
        {
            if (boss.WarningAt > 0)
            {
                if (boss.WarningAt <= Run.seconds)
                {
                    ResolveStage(boss);
                }
                return;
            }
            if (boss.NextSkillAt <= Run.seconds)
            {
                BeginSkill(boss);
            }
        }

        /// <summary>当前这一招是否为专属技：释放次数是配置间隔的整数倍。由次数推导，读档后结果也一致。</summary>
        private static bool IsExclusiveCast(EndlessUnit boss)
        {
            return boss.SkillCasts > 0 && boss.SkillCasts % N("BossExclusiveEvery") == 0;
        }

        /// <summary>
        /// 选招式、锁定目标、开始蓄力。
        /// TryPlan 的返回值表示“这招现在能不能打到玩家棋”，六章用它决定要不要放；
        /// 无尽不看这个结果：玩家棋很少时环形招式经常打不到人，领主就会一直空过，专属技永远等不到。
        /// 目标格 plan 在返回 false 时也已填好（最强的玩家棋），所以照常放，打不到棋也能打杂兵和棋心。
        /// </summary>
        private void BeginSkill(EndlessUnit boss)
        {
            int casts = boss.SkillCasts + 1;
            bool exclusive = casts % N("BossExclusiveEvery") == 0;
            EndlessBossSkill row = exclusive ? EndlessConfig.BossSkill(boss.BossChapter) : default;
            BossSkillKind kind = exclusive ? EndlessConfig.ParseSkill(row) : PickChapterSkill(boss.BossChapter);
            BossExpandedGeometry.TryPlan(Board, boss.Piece, kind, out BossSkillPlan plan);
            boss.SkillCasts = casts;
            boss.ChapterSkill = (int)kind;
            boss.SkillStage = 0;
            boss.SkillA = plan.A;
            boss.SkillB = plan.B;
            boss.SkillC = plan.C;
            BossSkillDef def = BossSkillCatalog.Get(kind);
            boss.WarningAt = Run.seconds + def.Windup;
            BossBanner = exclusive ? row.Name + "：" + row.Hint : def.Name + "：" + def.Hint;
            PushCast(boss, true);
            if (exclusive)
            {
                // 献祭潮在蓄力开始时就涌入：玩家一边躲招一边清怪，这才是割草节奏。
                SpawnSurge(row);
            }
        }

        /// <summary>
        /// 按权重从本章招式里随机选一个。只选“有逐格几何”的招式，
        /// 因为只有它们能保证红格子和真实伤害完全一致。随机数走 Next，读档不会重抽。
        /// </summary>
        private BossSkillKind PickChapterSkill(int chapter)
        {
            int total = 0;
            for (var kind = BossSkillKind.CometRails; kind <= BossSkillKind.FinalEdict; kind++)
            {
                if (BossSkillCatalog.IsUnlocked(kind, chapter))
                {
                    total += BossSkillCatalog.Get(kind).Weight;
                }
            }
            if (total <= 0)
            {
                throw new InvalidOperationException("第" + chapter + "章没有可用于无尽的招式");
            }
            int roll = Next(total);
            for (var kind = BossSkillKind.CometRails; kind <= BossSkillKind.FinalEdict; kind++)
            {
                if (!BossSkillCatalog.IsUnlocked(kind, chapter))
                {
                    continue;
                }
                roll -= BossSkillCatalog.Get(kind).Weight;
                if (roll < 0)
                {
                    return kind;
                }
            }
            throw new InvalidOperationException("技能权重计算出错");
        }

        /// <summary>结算当前这一段：打玩家棋、打棋心、误伤杂兵，然后决定下一段或进入冷却。</summary>
        private void ResolveStage(EndlessUnit boss)
        {
            var kind = (BossSkillKind)boss.ChapterSkill;
            BossSkillDef def = BossSkillCatalog.Get(kind);
            bool exclusive = IsExclusiveCast(boss);
            EndlessBossSkill row = exclusive ? EndlessConfig.BossSkill(boss.BossChapter) : default;
            BossSkillInfo info = MakeInfo(boss);
            float power = BossSkillCatalog.PowerFor(kind, boss.BossChapter) * V("BossSkillDamageScale") * (exclusive ? row.Power : 1);
            float damage = boss.Piece.Def.Attack * boss.Piece.AttackScale * power;

            // Collect 先把命中名单存下来再扣血：边遍历棋盘边死棋会破坏遍历。
            BossExpandedGeometry.Collect(Board, info, _skillHits);
            // 伤害数字沿用六章的领主技能样式：BattleDamageKind 的 Boss 段与 BossSkillKind 顺序一致（见 BattleService.BossAI）。
            var damageKind = (BattleDamageKind)((int)BattleDamageKind.BossCleave + (int)kind);
            for (int i = 0; i < _skillHits.Count; i++)
            {
                HurtPlayer(Find(_skillHits[i].Id), damage, boss, damageKind);
            }
            if (BossExpandedGeometry.Contains(kind, info.A, info.B, info.C, info.Radius, info.Index, new BoardPos(4, 4)))
            {
                DamageHeart(V("BossSkillHeartDamage"), def.Name);
            }
            StrikeHorde(info, exclusive && boss.SkillStage == 0 ? row.Effect : -1, exclusive ? row.Effect : -1);
            Impact(new Vector2(info.A.X, info.A.Y), new Vector2(info.A.X, info.A.Y), 6, info.Radius + 1.5f);
            PushCast(boss, false);

            boss.SkillStage++;
            if (boss.SkillStage < def.Count)
            {
                boss.WarningAt = Run.seconds + Mathf.Max(def.Interval, V("SimulationStep"));
                return;
            }
            boss.WarningAt = 0;
            boss.NextSkillAt = Run.seconds + BossSkillCatalog.CooldownFor(kind, boss.BossChapter, false) * V("BossSkillCooldownScale");
            BossBanner = "";
        }

        /// <summary>
        /// 领主技能打到自己的杂兵：外围杂兵不在棋盘里，先把坐标压回最近的棋盘格，再用同一个几何判定。
        /// openingEffect 只在第一段生效（灼烧、冻结这类“全场一次”）；stageEffect 每段都生效（冲退、圣裁）。
        /// </summary>
        private void StrikeHorde(BossSkillInfo info, int openingEffect, int stageEffect)
        {
            float hitRatio = V("SkillHordeHit");
            for (int i = 0; i < Horde.Length; i++)
            {
                EndlessHorde h = Horde[i];
                if (!h.active || h.hp <= 0)
                {
                    continue;
                }
                if (openingEffect == 1)
                {
                    h.burnUntil = Run.seconds + V("BurnSeconds");
                    h.burnAt = Run.seconds;
                    h.burnDamage = h.maxHp * V("SkillHordeBurn");
                }
                else if (openingEffect == 4)
                {
                    h.frozenUntil = Run.seconds + V("FreezeSeconds");
                }
                var cell = new BoardPos(Mathf.Clamp(Mathf.RoundToInt(h.x), 0, 8), Mathf.Clamp(Mathf.RoundToInt(h.y), 0, 8));
                bool struck = BossExpandedGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, info.Index, cell);
                if (struck && stageEffect == 2)
                {
                    // 怒潮回卷：朝杂兵来的方向推回去。门点 0左 1右 2下 3上，和出生坐标同一约定。
                    h.x += h.gate == 0 ? -1.5f : h.gate == 1 ? 1.5f : 0;
                    h.y += h.gate == 2 ? -1.5f : h.gate == 3 ? 1.5f : 0;
                }
                Horde[i] = h;
                if (struck)
                {
                    // 圣裁直接打掉全部生命，其余招式按比例。HitHorde 只扣血，死亡统一在 ResolveDeaths 结算，奖励不会重复。
                    HitHorde(i, stageEffect == 5 ? h.hp : h.maxHp * hitRatio, Run.formation);
                }
            }
        }

        /// <summary>
        /// 生成献祭潮。复用 SpawnHorde 的门点与数值，再把新杂兵标记为“脆、快、死了伤领主”。
        /// 杂兵上限满了就少刷几只，只影响爽感，不影响规则。
        /// </summary>
        private void SpawnSurge(EndlessBossSkill row)
        {
            int count = row.Effect == 0 ? row.Surge * 3 / 2 : row.Surge;
            for (int n = 0; n < count; n++)
            {
                int slot = Horde.FirstFree();
                if (slot < 0 || !SpawnHorde())
                {
                    return;
                }
                EndlessHorde spawned = Horde[slot];
                spawned.surge = true;
                spawned.pustule = row.Effect == 3;
                spawned.hp = Mathf.Max(1, spawned.hp * V("SurgeHpScale"));
                spawned.maxHp = spawned.hp;
                spawned.speed *= V("SurgeSpeedScale");
                Horde[slot] = spawned;
                World.InvalidateSpatial();
            }
        }

        /// <summary>
        /// 献祭潮杂兵死亡时调用（只由 KillHorde 调用一次）：给领主掉血并画一条飞向领主的光，
        /// 让玩家一眼看懂“杀小怪就是在打 Boss”。毒囊杂兵再额外炸一圈，
        /// 炸死的杂兵在 ResolveDeaths 的有限轮次里结算，不会无限连锁。
        /// </summary>
        private void OnSurgeKilled(EndlessHorde h, Vector2 pos)
        {
            EndlessUnit boss = BossUnit();
            if (boss != null && boss.Piece.IsAlive)
            {
                HitUnit(boss, boss.Piece.MaxHp * V("SurgeBossDamage"), Run.formation);
                Impact(pos, ActorPosition(boss), Run.formation);
            }
            if (h.pustule)
            {
                Area(pos, V("PustuleRadius"), h.maxHp * V("SkillHordeHit"), Run.formation, -1);
            }
        }

        /// <summary>把领主当前技能状态打包成不可变快照，交给几何判定和特效使用。</summary>
        private static BossSkillInfo MakeInfo(EndlessUnit boss)
        {
            var kind = (BossSkillKind)boss.ChapterSkill;
            BossSkillDef def = BossSkillCatalog.Get(kind);
            return new BossSkillInfo(boss.Piece.Id, kind, boss.Piece.Pos, boss.Piece.Size, boss.SkillA, boss.SkillB, boss.SkillC,
                def.Radius, def.Windup, boss.SkillStage, def.Count, false);
        }

        /// <summary>写入一条技能记录。序号对数组长度取余，实现“写满覆盖最旧”。</summary>
        private void PushCast(EndlessUnit boss, bool telegraph)
        {
            int serial = ++BossCastSerial;
            BossCasts[serial % BossCasts.Length] = new EndlessBossCast
            {
                serial = serial, info = MakeInfo(boss), telegraph = telegraph, exclusive = IsExclusiveCast(boss)
            };
        }
    }
}

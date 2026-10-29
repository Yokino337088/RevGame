// Boss 状态机离线回归：链接真实 BossAI / BossStates / BossTactics / BattleService，不复制任何算法。
// 覆盖：章节难度、技能三段节奏（预警→命中→收招）、躲避有效、全部技能可执行、瞬移落点、终态与回收。
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

internal static class BossRegression
{
    /// <summary>本文件执行过的断言数，汇总进总报告。</summary>
    private static int _assertions;
    /// <summary>一个模拟小步（与正式战斗相同的固定步长）。</summary>
    private static float Step => Balance.BattleSimulationStepSeconds;

    public static int Run(out int scenarios)
    {
        _assertions = 0;
        scenarios = 0;
        VerifyChapterDifficulty(); scenarios++;
        VerifySkillRhythm(); scenarios++;
        VerifySnipeCanBeDodged(); scenarios++;
        VerifyEverySkill(); scenarios++;
        VerifyBulletRingIsGlobal(); scenarios++;
        VerifyChapterOneNeverUsesLockedSkills(); scenarios++;
        VerifyEnrageOnce(); scenarios++;
        VerifyLifecycle(); scenarios++;
        VerifyBossDurability(); scenarios++;
        return _assertions;
    }

    /// <summary>厚血必须来自真实出生配置；主将百分比减伤不能蔓延到护卫或吞掉炮火的固定下限。</summary>
    private static void VerifyBossDurability()
    {
        var battle = BattleService.CreateBossRegressionFixture(1);
        try
        {
            Piece boss = battle.AddRegressionBoss(new BoardPos(3, 6), 2);
            Piece guard = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(0, 0), 50f);
            int hp = boss.Hp;
            int predicted = battle.RegressionPercentDamage(boss, .1f, 25, true);
            Assert(boss.Hp == hp, "主将百分比伤害预判不扣血");
            Assert(predicted == Math.Max(25, (int)(boss.MaxHp * (double)Balance.BossPercentDamageScale * .1f)), "主将按独立百分比系数承伤");
            Assert(battle.RegressionPercentDamage(boss, .1f, 25, false) == predicted && hp - boss.Hp == predicted, "主将预测与实际扣血一致");
            Assert(battle.RegressionPercentDamage(guard, .1f, 25, true) == Math.Max(25, (int)(guard.MaxHp * (double).1f)), "护卫不套用主将百分比减伤");
            Assert(battle.RegressionPercentDamage(boss, .000001f, 25, true) == 25, "炮火固定伤害下限不被削减");
        }
        finally { battle.StopEnemyAI(); }

        var spawn = new BattleService(false, 20261028);
        try
        {
            Assert(spawn.BeginHelperBalanceFixture(1, Balance.BossRoundIndex), "从真实关卡配置创建主将");
            Piece boss = null;
            foreach (Piece p in spawn.EnemyPieces) { if (p.IsKing) { boss = p; break; } }
            Assert(boss != null, "Boss回合实际生成主将");
            float expected = LevelCatalog.GetRound(1, Balance.BossRoundIndex).AttackScale * Balance.BossAttackMultiplier;
            Assert(Math.Abs(boss.AttackScale - expected) < .00001f, "主将实例攻击乘入Excel独立倍率");
        }
        finally { spawn.StopEnemyAI(); }
    }

    /// <summary>章节越高：技能越多、冷却越短、伤害越高；第六章拥有全部技能。</summary>
    private static void VerifyChapterDifficulty()
    {
        int previous = -1;
        for (int chapter = 1; chapter <= 6; chapter++)
        {
            int unlocked = 0;
            foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
            {
                if (BossSkillCatalog.IsUnlocked(kind, chapter))
                {
                    unlocked++;
                }
            }
            Assert(unlocked >= previous, "第" + chapter + "章解锁技能数不能少于上一章");
            previous = unlocked;
        }
        Assert(previous == 11, "每章6主动通用加4专属及独立狂暴");
        Assert(BossSkillCatalog.IsUnlocked(BossSkillKind.Cleave, 1) && BossSkillCatalog.IsUnlocked(BossSkillKind.Snipe, 1), "第一章至少有横扫与狙击");
        Assert(BossSkillCatalog.IsUnlocked(BossSkillKind.LaserSpin, 5) && !BossSkillCatalog.IsUnlocked(BossSkillKind.StarStorm, 6), "通用技能首章可用，专属技能不串章");
        foreach (BossSkillKind kind in new[] { BossSkillKind.Cleave, BossSkillKind.Snipe })
        {
            Assert(BossSkillCatalog.CooldownFor(kind, 6, false) < BossSkillCatalog.CooldownFor(kind, 1, false), kind + " 第六章冷却更短");
            Assert(BossSkillCatalog.CooldownFor(kind, 6, true) < BossSkillCatalog.CooldownFor(kind, 6, false), kind + " 狂暴后冷却更短");
            Assert(BossSkillCatalog.PowerFor(kind, 6) > BossSkillCatalog.PowerFor(kind, 1), kind + " 第六章伤害更高");
        }
    }

    /// <summary>真实驱动：开战等待 → 选技能并预警 → 蓄力结束才命中 → 收招回 Idle 并记录冷却。</summary>
    private static void VerifySkillRhythm()
    {
        BattleService battle = BattleService.CreateBossRegressionFixture(1);
        Piece boss = battle.AddRegressionBoss(new BoardPos(3, 6), 2);
        Piece adjacent = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(3, 5), 1000f);
        battle.RefreshRegressionGoState();
        var telegraphs = new List<BossSkillInfo>();
        var casts = new List<BossSkillInfo>();
        var skillHits = new List<BattleHitInfo>();
        object owner = new object();
        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, telegraphs.Add, owner: owner);
        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, casts.Add, owner: owner);
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => { if (hit.FromSkill) { skillHits.Add(hit); } }, owner: owner);
        try
        {
            BossAI ai = battle.RegressionBossAI(boss);
            // 首个技能前的等待期：只会普通攻击，不会预警。
            while (battle.BattleElapsed + Step < Balance.BossFirstSkillSeconds)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(telegraphs.Count == 0 && ai.State == BossAIState.Idle, "开战等待期内不放技能");
            int guard = 0;
            while (telegraphs.Count == 0 && guard++ < 40)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(telegraphs.Count == 1, "等待结束后进入技能并广播预警");
            BossSkillKind kind = telegraphs[0].Kind;
            Assert(BossSkillCatalog.IsUnlocked(kind, 1), "第一章施放合法技能池中的技能");
            Assert(ai.State == BossAI.StateOf(kind) && casts.Count == 0 && skillHits.Count == 0, "预警期间状态正确且尚未造成技能伤害");
            float windup = BossSkillCatalog.Get(kind).Windup;
            float castStart = battle.BattleElapsed;
            while (casts.Count == 0 && guard++ < 80)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(casts.Count == 1 && battle.BattleElapsed - castStart >= windup - Step * 1.01f, "蓄力满 Windup 才命中");
            while (ai.State != BossAIState.Idle && guard++ < 120)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(skillHits.Count >= 1 && skillHits[0].TargetId == adjacent.Id, "技能命中的伤害带 FromSkill 标记且打到范围内的棋子");
            Assert(ai.State == BossAIState.Idle, "收招后回到待机");
            Assert(ai.History[ai.History.Length - 1].Reason.Contains("收招"), "切换历史记录收招原因");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
            battle.StopEnemyAI();
        }
    }

    /// <summary>狙击锁定的是“格子”：预警期间把目标挪走，命中时不会打到它。</summary>
    private static void VerifySnipeCanBeDodged()
    {
        BattleService battle = BattleService.CreateBossRegressionFixture(1);
        Piece boss = battle.AddRegressionBoss(new BoardPos(3, 6), 2);
        Piece target = battle.AddRegressionPiece(PieceKind.Archer, StarTier.Two, GameSide.Player, new BoardPos(0, 0), 1000f);
        battle.RefreshRegressionGoState();
        var telegraphs = new List<BossSkillInfo>();
        var hits = new List<BattleHitInfo>();
        object owner = new object();
        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, telegraphs.Add, owner: owner);
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => { if (hit.DamageKind == BattleDamageKind.BossSnipe) { hits.Add(hit); } }, owner: owner);
        int snipeCasts = 0;
        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, cast => { if (cast.Kind == BossSkillKind.Snipe) { snipeCasts++; } }, owner: owner);
        try
        {
            battle.RegressionBossAI(boss);
            Assert(battle.ForceBossSkill(BossSkillKind.Snipe), "有远处目标时可以狙击");
            Assert(telegraphs.Count == 1 && telegraphs[0].A == target.Pos, "准星锁定威胁目标所在格");
            Assert(battle.Board.Move(target, new BoardPos(8, 0), respectEnemyEyes: false), "测试中玩家移开目标");
            battle.RefreshRegressionGoState();
            // 只观察狙击的命中步，避免收招后自然选到另一招，再把另一招的伤害误算成狙击无法躲避。
            for (int i = 0; i < 20 && snipeCasts == 0; i++)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(snipeCasts == 1 && hits.Count == 0 && target.Hp == target.MaxHp, "狙击实际发射，躲开准星后生命不变");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
            battle.StopEnemyAI();
        }
    }

    /// <summary>弹幕不受距离限制：只放两个远角目标，验证全屏选招、每目标仅一次技能伤害和收招。</summary>
    private static void VerifyBulletRingIsGlobal()
    {
        BattleService battle = BattleService.CreateBossRegressionFixture(3);
        Piece boss = battle.AddRegressionBoss(new BoardPos(0, 0), 2);
        Piece first = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(8, 8), 1000f);
        Piece second = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 8), 1000f);
        battle.RefreshRegressionGoState();
        var hits = new List<BattleHitInfo>();
        var casts = new List<BossSkillInfo>();
        object owner = new object();
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => { if (hit.FromSkill) { hits.Add(hit); } }, owner: owner);
        RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, casts.Add, owner: owner);
        try
        {
            BossAI ai = battle.RegressionBossAI(boss);
            Assert(battle.ForceBossSkill(BossSkillKind.BulletRing), "没有近处目标也可施放全屏弹幕");
            int guard = 0;
            while (ai.State != BossAIState.Idle && guard++ < 200)
            {
                battle.RunRegressionEnemyAI(Step);
            }
            Assert(ai.State == BossAIState.Idle && casts.Count == 1, "全屏弹幕一次命中后收招");
            Assert(hits.Count == 2, "全屏弹幕两个远处目标各结算一次，不按子弹数量重复伤害");
            Assert(hits.Exists(h => h.TargetId == first.Id) && hits.Exists(h => h.TargetId == second.Id), "两个不同远角目标都被弹幕命中");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
            battle.StopEnemyAI();
        }
    }

    /// <summary>第六章逐个强制施放全部技能：段数正确、能回到待机，瞬移/汲取/召唤/狂暴的副作用真实发生。</summary>
    private static void VerifyEverySkill()
    {
        foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
        {
            if ((int)kind >= 18) { continue; } // 专属技能由BossSkillExpansion规则和六章Play逐招验收。
            BattleService battle = BattleService.CreateBossRegressionFixture(BossSkillCatalog.ThemeChapter(kind));
            Piece boss = battle.AddRegressionBoss(new BoardPos(3, 3), 2);
            // 布置：一颗贴身、一颗在圈内、三颗远处扎堆——让每个技能都有合法目标。
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(2, 3), 1000f);
            battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(6, 4), 1000f);
            battle.AddRegressionPiece(PieceKind.Archer, StarTier.Two, GameSide.Player, new BoardPos(0, 7), 1000f);
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(1, 7), 1000f);
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(1, 8), 1000f);
            battle.RefreshRegressionGoState();
            boss.TakeDamage(boss.MaxHp / 3);
            var casts = new List<BossSkillInfo>();
            object owner = new object();
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, casts.Add, owner: owner);
            try
            {
                BossAI ai = battle.RegressionBossAI(boss);
                int enemiesBefore = battle.EnemyPieces.Count;
                int bossHpBefore = boss.Hp;
                BoardPos posBefore = boss.Pos;
                Assert(battle.ForceBossSkill(kind), kind + " 在布置好的局面里应当可施放");
                int guard = 0;
                while (ai.State != BossAIState.Idle && guard++ < 200)
                {
                    battle.RunRegressionEnemyAI(Step);
                }
                Assert(ai.State == BossAIState.Idle, kind + " 收招后回到待机");
                int expected = kind == BossSkillKind.Bombard || kind == BossSkillKind.LaserSpin || kind >= BossSkillKind.MeteorShower ? BossSkillCatalog.Get(kind).Count : 1;
                Assert(casts.Count == expected, kind + " 命中段数 = " + expected + "（实际 " + casts.Count + "）");
                for (int i = 0; i < casts.Count; i++)
                {
                    Assert(casts[i].Index == i && casts[i].Kind == kind, kind + " 第 " + i + " 段事件编号正确");
                }
                if (kind == BossSkillKind.BlinkBlast)
                {
                    Assert(boss.Pos != posBefore && battle.Board.GetPiece(boss.Pos) == boss && casts[0].A == boss.Pos && casts[0].Origin == posBefore, "瞬移后棋盘占格与事件落点一致");
                }
                if (kind == BossSkillKind.SoulDrain)
                {
                    Assert(boss.Hp > bossHpBefore, "灵魂汲取为 Boss 回血");
                }
                if (kind == BossSkillKind.Summon)
                {
                    Assert(battle.EnemyPieces.Count > enemiesBefore && battle.EnemyAICount == battle.EnemyPieces.Count, "召唤出小兵且每个小兵都有自己的状态机");
                }
                if (kind == BossSkillKind.Enrage)
                {
                    Assert(casts[0].Enraged, "狂暴命中后事件标记为狂暴");
                }
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
                battle.StopEnemyAI();
            }
        }
    }

    /// <summary>即使局面最适合弹幕/激光（多人围在身边），第一章也只能从已解锁技能里选。</summary>
    private static void VerifyChapterOneNeverUsesLockedSkills()
    {
        BattleService battle = BattleService.CreateBossRegressionFixture(1);
        Piece boss = battle.AddRegressionBoss(new BoardPos(3, 3), 2);
        foreach (BoardPos pos in new[] { new BoardPos(2, 3), new BoardPos(5, 4), new BoardPos(3, 1), new BoardPos(4, 6), new BoardPos(0, 0) })
        {
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, pos, 1000f);
        }
        battle.RefreshRegressionGoState();
        var ready = new float[Enum.GetValues(typeof(BossSkillKind)).Length];
        BossSkillPlan plan;
        Assert(BossTactics.TryChoose(battle.Board, boss, 1, 1, ready, 0f, null, out plan), "第一章有可用技能");
        Assert(BossSkillCatalog.IsUnlocked(plan.Kind, 1), "第一章选中的技能必须已解锁");
        Assert(BossTactics.TryChoose(battle.Board, boss, 6, 4, ready, 0f, null, out plan) && plan.Kind != BossSkillKind.Enrage, "第六章同样局面会选出非狂暴技能");
        ready[(int)plan.Kind] = 99f;
        BossSkillPlan second;
        Assert(BossTactics.TryChoose(battle.Board, boss, 6, 4, ready, 0f, null, out second) && second.Kind != plan.Kind, "冷却中的技能不会被再次选中");
    }

    /// <summary>第四章起残血自动狂暴，且整场只触发一次；第一章残血不会狂暴。</summary>
    private static void VerifyEnrageOnce()
    {
        foreach (int chapter in new[] { 1, 4 })
        {
            BattleService battle = BattleService.CreateBossRegressionFixture(chapter);
            Piece boss = battle.AddRegressionBoss(new BoardPos(3, 6), 2);
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 0), 1000f);
            battle.RefreshRegressionGoState();
            boss.TakeDamage((int)(boss.MaxHp * 0.7f));
            var telegraphs = new List<BossSkillInfo>();
            object owner = new object();
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, telegraphs.Add, owner: owner);
            try
            {
                battle.RegressionBossAI(boss);
                for (int i = 0; i < 160; i++)
                {
                    battle.RunRegressionEnemyAI(Step);
                }
                int enrages = telegraphs.FindAll(t => t.Kind == BossSkillKind.Enrage).Count;
                Assert(enrages == 1, "第" + chapter + "章狂暴次数 = " + enrages);
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
                battle.StopEnemyAI();
            }
        }
    }

    /// <summary>死亡是终态；StopEnemyAI 释放 Boss 机器；释放后不能再驱动。</summary>
    private static void VerifyLifecycle()
    {
        BattleService battle = BattleService.CreateBossRegressionFixture(3);
        Piece boss = battle.AddRegressionBoss(new BoardPos(3, 6), 2);
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 0), 1000f);
        BossAI ai = battle.RegressionBossAI(boss);
        Assert(battle.EnemyAICount == 1 && battle.BossState == BossAIState.Idle, "Boss 主将使用 BossAI 而不是 EnemyAI");
        ai.Force(BossAIState.Dead, "测试死亡");
        ai.Tick(1f);
        Assert(ai.State == BossAIState.Dead, "死亡终态不会被下一次驱动复活");
        battle.StopEnemyAI();
        Assert(battle.EnemyAICount == 0 && battle.BossState == null, "回收后不再持有 Boss 状态机");
        bool rejected = false;
        try
        {
            ai.Tick(0.1f);
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }
        Assert(rejected, "已释放的 Boss 状态机不能再驱动");
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException("Boss 回归失败：" + message);
        }
    }
}

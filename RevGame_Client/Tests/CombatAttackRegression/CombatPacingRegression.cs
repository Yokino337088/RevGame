// 只链接正式业务：固定种子造局隔离攻击、区域、AI与擒王，预期值来自导表参数而非另一份伤害算法。
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

internal static class CombatPacingRegression
{
    private static int _checks, _cases;
    private static void Check(bool ok, string text)
    {
        _checks++;

        if (!ok)
        {
            throw new InvalidOperationException("战斗节奏回归：" + text);
        }
    }
    public static int Run(out int scenarios)
    {
        _checks = _cases = 0;
        Buffs();
        NoForcedVictory();
        Region();
        Bomb();
        PlayerMovement();
        Capture();
        PatternTargets();
        scenarios = _cases;
        Console.WriteLine("CombatPacingRegression: " + _cases + " scenarios, " + _checks + " assertions passed.");
        return _checks;
    }
    /// <summary>独立运行技能格形，不让与技能无关的历史增益测试遮住本次几何回归结果。</summary>
    public static int RunBossPatterns(out int scenarios)
    {
        _checks = _cases = 0;
        PatternTargets();
        scenarios = _cases;
        return _checks;
    }
    private static void Buffs()
    {
        var battle = BattleService.CreateAttackRegressionFixture();
        Piece player = battle.AddRegressionPiece(PieceKind.Swordman, StarTier.One, GameSide.Player, new BoardPos(1, 1));
        Piece enemy = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(2, 1), 1000f);
        battle.RefreshRegressionGoState();
        Check(!battle.AddCombatBuff(BattleEventKind.Attack, float.NaN), "拒绝NaN");
        Check(!battle.AddCombatBuff(BattleEventKind.Attack, float.PositiveInfinity), "拒绝无穷");
        Check(!battle.AddCombatBuff(BattleEventKind.Heal, .1f) && !battle.AddCombatBuff(BattleEventKind.Defense, -1), "拒绝错误属性与负数");
        int hp = enemy.Hp;
        battle.RegressionAttack(player, enemy);
        int before = hp - enemy.Hp;
        Check(battle.AddCombatBuff(BattleEventKind.Attack, 1), "攻击GM接受");
        hp = enemy.Hp;
        battle.RegressionAttack(player, enemy);
        Check(hp - enemy.Hp > before, "真实攻击伤害提高");
        int damage = battle.RegressionDamage(player, 20);
        Check(battle.AddCombatBuff(BattleEventKind.Defense, 1), "防御GM接受");
        Check(battle.RegressionDamage(player, 20) < damage, "相同来袭真实伤害减少");
        Check(battle.AddCombatBuff(BattleEventKind.Haste, 1), "攻速GM接受");
        player.ActionProgress = 0;
        battle.RunRegressionPlayerStep(.125f);
        float progress = player.ActionProgress;
        battle.RegressionResetPacing();
        player.ActionProgress = 0;
        battle.RunRegressionPlayerStep(.125f);
        Check(progress > player.ActionProgress, "相同步长攻速提升真实出手进度");
        int max = player.MaxHp;
        float ratio = player.HpRatio;
        Check(battle.AddCombatBuff(BattleEventKind.Vitality, 1) && player.MaxHp == max * 2, "生命加成改变真实上限");
        Check(Math.Abs(player.HpRatio - ratio) <= 2f / max, "生命增益保留伤势比例");
        Check(battle.AddCombatBuff(BattleEventKind.Attack, Balance.CombatBuffCap), "大额加成接受");
        Check(battle.AddCombatBuff(BattleEventKind.Attack, Balance.CombatBuffCap), "继续叠加接受且封顶");
        Check(battle.RegressionBuff(BattleEventKind.Attack) == Balance.CombatBuffCap, "叠加不突破表上限");
        Check(battle.StartRound(1, 2), "带生命增益进入下一轮");
        Check(player.MaxHp == max && battle.RegressionBuff(BattleEventKind.Vitality) == 0, "换轮生命与攻击增益清零");
        Check(!battle.AddCombatBuff(BattleEventKind.Attack, 1), "准备阶段拒绝战斗GM");
        battle.StopEnemyAI();
        _cases++;
    }
    private static void NoForcedVictory()
    {
        var battle = BattleService.CreateAttackRegressionFixture();
        Piece player = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 0), 1000);
        Piece enemy = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(8, 8), 1000);
        battle.RegressionResetPacing();
        battle.RegressionDisableAutomaticPacing();
        battle.SetRegressionElapsed(Balance.BattleDurationSeconds + 600);
        battle.Tick(Balance.BattleSimulationStepSeconds);
        Check(float.IsPositiveInfinity(battle.BattleDuration) && battle.Phase == GamePhase.Battle, "远超旧限时仍继续战斗");
        Check(enemy.IsAlive && battle.Board.GetPiece(enemy.Pos) == enemy && battle.LastWinner == null, "没有按时间清敌或判胜");
        Check(battle.RegressionDamage(player, int.MaxValue) > 0 && !player.IsAlive, "最后一子真实死亡，没有留1血");
        battle.Tick(Balance.BattleSimulationStepSeconds);
        Check(battle.LastWinner == GameSide.Enemy && battle.Phase == GamePhase.Settle, "全灭立即判负，自动支援不能复活队伍");
        Check(enemy.IsAlive && battle.Board.GetPiece(player.Pos) == null, "败局保留敌人、移除玩家死棋");
        battle.StopEnemyAI();
        _cases++;
    }
    private static void Region()
    {
        for (int run = 0; run < 16; run++)
        {
            var battle = BattleService.CreateAttackRegressionFixture(701 + run);
            Piece enemy = battle.AddRegressionPiece(PieceKind.Shield, StarTier.Three, GameSide.Enemy, new BoardPos(run % 2 == 0 ? 0 : 7, run % 2 == 0 ? 0 : 7), 1000);
            RegionPulseInfo region = default;
            object owner = new object();
            RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning, info => region = info, owner: owner);
            Piece player = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 4), 1000);
            try
            {
                battle.RegressionResetPacing();
                battle.RegressionDisableAutomaticPacing();
                Check(battle.TriggerRegionPulse(), "区域真实触发");
                Check(region.Origin.X >= 0 && region.Origin.Y >= 0 && region.Origin.X + region.Size <= 9 && region.Origin.Y + region.Size <= 9, "区域不越界");
                bool coversEnemy = false;
                for (int y = 0; y < enemy.Size; y++)
                {
                    for (int x = 0; x < enemy.Size; x++)
                    {
                        coversEnemy |= region.CellIndex(new BoardPos(enemy.Pos.X + x, enemy.Pos.Y + y)) >= 0;
                    }
                }
                Check(coversEnemy, "边界敌军也优先覆盖");
                Check(!battle.TriggerRegionPulse(), "拒绝重叠区域");
                // 把友军移入该区域的一处空格，验证伤害不是只对敌军生效。
                foreach (BoardPos pos in battle.RegressionRegionCells(region))
                {
                    if (battle.Board.GetPiece(pos) == null)
                    {
                        Check(battle.Board.Move(player, pos, respectEnemyEyes: false), "友军进入预警范围");
                        break;
                    }
                }
                int pHp = player.Hp, eHp = enemy.Hp;
                battle.RegressionPacingAt(region.WarningSeconds - .01f);
                Check(player.Hp == pHp && enemy.Hp == eHp, "预警阶段双方都不掉血");
                battle.RegressionPacingAt(region.WarningSeconds + region.Size * region.Size * region.CellDelaySeconds + .01f);
                Check(eHp - enemy.Hp == Math.Max(1, (int)(enemy.MaxHp * Balance.RegionDamageHpRatio)), "大棋跨多格也只伤一次");
                Check(pHp - player.Hp == Math.Max(1, (int)(player.MaxHp * Balance.RegionDamageHpRatio)), "区域中友军也受到一次伤害");
                Check(battle.RegionPulseCount == 1, "真实施放只计一次");
            }
            finally { RevEvent.RemoveAllByOwner(owner); battle.StopEnemyAI(); }
            _cases++;
        }
    }
    private static void Bomb()
    {
        var battle = BattleService.CreateAttackRegressionFixture();
        Piece player = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 4), 100);
        var enemies = new[] {
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(0, 0), 100),
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(8, 8), 100) };
        int pHp = player.Hp, eHp = enemies[0].Hp, impacts = 0;
        object owner = new object();
        RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombImpact, _ => impacts++, owner: owner);
        try
        {
            battle.RegressionResetPacing();
            battle.RegressionDisableAutomaticPacing();
            Check(battle.TriggerEnergyBomb() && !battle.TriggerEnergyBomb(), "炸弹接受且拒绝重叠");
            battle.RegressionPacingAt(Balance.BombWarningSeconds - .01f);
            Check(enemies[0].Hp == eHp && impacts == 0, "蓄力阶段没有伤害或命中事件");
            battle.RegressionPacingAt(Balance.BombWarningSeconds + .01f);
            Check(impacts == 1 && battle.EnergyBombCount == 1, "只结算一次全场爆炸");
            foreach (Piece enemy in enemies)
            {
                Check(eHp - enemy.Hp == Math.Max(1, (int)(enemy.MaxHp * Balance.BombDamageHpRatio)), "远角敌军均受到配置小额伤害");
            }
            Check(player.Hp == pHp, "炸弹不伤玩家");
            Check(battle.TriggerEnergyBomb(), "下一发可再次施放");
            battle.RegressionResetPacing();
            battle.RegressionDisableAutomaticPacing();
            int hp = enemies[0].Hp;
            battle.RegressionPacingAt(60);
            Check(enemies[0].Hp == hp && impacts == 1, "重置后没有旧炸弹延迟伤害");
        }
        finally { RevEvent.RemoveAllByOwner(owner); battle.StopEnemyAI(); }
        _cases++;
    }
    private static void PlayerMovement()
    {
        foreach (bool block in new[] { false, true })
        {
            var battle = BattleService.CreateAttackRegressionFixture();
            Piece player = battle.AddRegressionPiece(PieceKind.Swordman, StarTier.One, GameSide.Player, new BoardPos(8, 8));
            Piece enemy = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(0, 0));

            if (block)
            {
                battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(1, 0));
                battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 1));
                battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(2, 0));
            }
            battle.RefreshRegressionGoState();
            BoardPos old = player.Pos;
            battle.RegressionMoveAt(player, Balance.PlayerMoveSeconds);
            Check(player.Pos != old && battle.Board.GetPiece(old) == null && battle.Board.GetPiece(player.Pos) == player, "近战接敌真实更新占格");
            Check(block ? enemy.IsLocked : Math.Abs(player.Pos.X - enemy.Pos.X) + Math.Abs(player.Pos.Y - enemy.Pos.Y) == 1, block ? "补最后一口气锁死整块敌人" : "靠近落单敌人");
            BoardPos moved = player.Pos;
            battle.RegressionMoveAt(player, Balance.PlayerMoveSeconds + .01f);
            Check(player.Pos == moved, "独立移动冷却不被攻击进度绕过");
            Check(!player.IsLocked, "试算没有堵死己方或污染气状态");
            battle.StopEnemyAI();
            _cases++;
        }
        var ranged = BattleService.CreateAttackRegressionFixture();
        Piece archer = ranged.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(4, 4));
        Piece nearby = ranged.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(5, 4));
        ranged.RefreshRegressionGoState();
        ranged.RegressionMoveAt(archer, Balance.PlayerMoveSeconds);
        Check(Math.Abs(archer.Pos.X - nearby.Pos.X) + Math.Abs(archer.Pos.Y - nearby.Pos.Y) > Balance.PlayerRetreatDistance, "远程遭贴脸主动拉开距离");
        ranged.StopEnemyAI();
        _cases++;
    }
    private static void Capture()
    {
        foreach (int hpPercent in new[] { 100, 21, 20, 19, 0 })
        {
            var battle = BattleService.CreateBossRegressionFixture(6);
            Piece boss = battle.AddRegressionBoss(new BoardPos(3, 3), 2);
            foreach (BoardPos pos in RevGame.Board.BoardModel.Boundary(boss))
            {
                battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, pos);
            }
            if (hpPercent < 100)
            {
                battle.RegressionDamage(boss, boss.MaxHp - boss.MaxHp * hpPercent / 100);
            }
            battle.RefreshRegressionGoState();
            Check(battle.RegressionCaptured() == (hpPercent <= 20), "擒王血量边界 " + hpPercent + "%");

            if (hpPercent == 20)
            {
                // 留出一口气，即使血量已达门槛也不能擒王。
                battle.RemoveRegressionPiece(battle.PlayerPieces[0]);
                Check(!battle.RegressionCaptured(), "20%但未锁死不能擒王");
            }
            battle.StopEnemyAI();
            _cases++;
        }
    }
    private static void PatternTargets()
    {
        foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
        {
            if (kind < BossSkillKind.MeteorShower || BossSkillCatalog.IsSpecial(kind) || BossSkillCatalog.IsExpanded(kind))
            {
                continue;
            }
            var battle = BattleService.CreateBossRegressionFixture(BossSkillCatalog.ThemeChapter(kind));
            Piece boss = battle.AddRegressionBoss(new BoardPos(3, 3), 2);
            Piece first = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(2, 3), 1000);
            Piece second = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(6, 4), 1000);
            Piece third = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(1, 7), 1000);
            battle.RefreshRegressionGoState();
            var warnings = new List<BossSkillInfo>();
            var casts = new List<BossSkillInfo>();
            var hits = new List<BattleHitInfo>();
            object owner = new object();
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, warnings.Add, owner: owner);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, casts.Add, owner: owner);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
            {
                if (hit.FromSkill)
                {
                    hits.Add(hit);
                }
            }, owner: owner);
            try
            {
                var ai = battle.RegressionBossAI(boss);
                Check(battle.ForceBossSkill(kind), kind + " 有真实合法计划");
                for (int i = 0; i < 200 && ai.State != BossAIState.Idle; i++)
                {
                    battle.RunRegressionEnemyAI(.1f);
                }
                Check(warnings.Count == 1 && casts.Count == BossSkillCatalog.Get(kind).Count && hits.Count > 0, kind + " 预警、段数和真实伤害");
                BossSkillInfo info = warnings[0];
                var plan = new BossSkillPlan { Kind = kind, A = info.A, B = info.B, C = info.C };
                int expected = 0;
                foreach (Piece target in new[] { first, second, third })
                {
                    for (int index = 0; index < info.Count; index++)
                    {
                        bool affected = BossSkillCatalog.IsExpanded(kind)
                            ? BossExpandedGeometry.Contains(kind, info.A, info.B, info.C, info.Radius, index, target.Pos)
                            : BossPatternGeometry.Contains(kind, info.Origin, info.OriginSize, plan, info.Radius, index, target.Pos);

                        if (affected)
                        {
                            expected++;
                        }
                    }
                }
                Check(hits.Count == expected, kind + " 所有真实命中与预警格集合一致");
                Check(ai.State == BossAIState.Idle, kind + " 收招归待机");
            }
            finally { RevEvent.RemoveAllByOwner(owner); battle.StopEnemyAI(); }
            _cases++;
        }
    }
}
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        public void RegressionAttack(Piece actor, Piece target)
        {
            Attack(actor, target);
        }
        public int RegressionDamage(Piece target, int amount)
        {
            return DamagePiece(target, amount, fromSkill: true);
        }
        public void RegressionResetPacing()
        {
            ResetCombatPacing();
        }
        public void RegressionDisableAutomaticPacing()
        {
            _nextBombAt = _nextRegionAt = float.MaxValue;
            _eventScheduled = false;
        }
        public void RegressionPacingAt(float seconds)
        {
            _battleElapsed = seconds;
            TickCombatPacing();
        }
        public void RegressionMoveAt(Piece piece, float seconds)
        {
            _battleElapsed = seconds;
            TryPlayerTacticalMove(piece);
        }
        public bool RegressionCaptured()
        {
            return IsKingCaptured();
        }
        public float RegressionBuff(BattleEventKind kind)
        {
            return kind == BattleEventKind.Attack ? _combatAttackBonus : _combatVitalityBonus;
        }
        public IEnumerable<BoardPos> RegressionRegionCells(RegionPulseInfo region)
        {
            for (int y = 0; y < region.Size; y++)
            {
                for (int x = 0; x < region.Size; x++)
                {
                    yield return new BoardPos(region.Origin.X + x, region.Origin.Y + y);
                }
            }
        }
    }
}

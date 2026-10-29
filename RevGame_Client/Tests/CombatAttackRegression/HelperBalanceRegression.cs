using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RevGame.Automation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

/// <summary>控制台链接真实服务/FSM回放三种子六章六十回合，图形/AB/手机性能不在此证明范围内。</summary>
internal static class HelperBalanceRegression
{
    public static int Run(string output)
    {
        var rows = new List<object>();
        int wins = 0, losses = 0;
        foreach (int seed in new[] { 3028, 4028, 5028 })
        {
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                var battle = new BattleService(true, seed + chapter * 101);
                int helperHits = 0, playerHits = 0, enemyHits = 0, deaths = 0, playerDeaths = 0, bossCasts = 0, bossId = 0;
                string bossLastDamage = "none";
                var bossKinds = new HashSet<BossSkillKind>();
                long helperDamage = 0, playerDamage = 0, enemyDamage = 0;
                object owner = new object();
                RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
                {
                    if (info.DamageKind == BattleDamageKind.Helper || info.DamageKind == BattleDamageKind.HelperBeam) { helperHits++; helperDamage += info.Damage; }
                    else if (info.TargetSide == GameSide.Player) { enemyHits++; enemyDamage += info.Damage; } else { playerHits++; playerDamage += info.Damage; }
                    if (info.TargetId == bossId) { bossLastDamage = info.DamageKind.ToString(); }
                }, owner: owner);
                RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, info => { bossCasts++; bossKinds.Add(info.Kind); }, owner: owner);
                RevEvent.AddEventListener<int>(Events.PieceDied, id =>
                {
                    deaths++;
                    foreach (Piece player in battle.PlayerPieces) { if (player.Id == id) { playerDeaths++; break; } }
                }, owner: owner);
                try
                {
                    if (!battle.BeginHelperBalanceFixture(chapter, 1)) { throw new InvalidOperationException(battle.LastError); }
                    for (int round = 1; round <= Balance.RoundsPerChapter; round++)
                    {
                        helperHits = playerHits = enemyHits = deaths = playerDeaths = 0;
                        helperDamage = playerDamage = enemyDamage = 0;
                        bossCasts = 0; bossId = 0; bossLastDamage = "none";
                        int front = 0, ranged = 0;
                        foreach (Piece p in battle.PlayerPieces) { if (p.IsAlive) { if (p.Def.IsRanged) { ranged++; } else { front++; } } }
                        foreach (var card in battle.Hand) { if (PieceCatalog.Get(card.Kind, card.Star).IsRanged) { ranged++; } else { front++; } }
                        if (front < 2 || ranged < 2) { battle.RefreshHand(); }
                        for (int n = 0; n < 6; n++)
                        {
                            int alive = 0; foreach (Piece p in battle.PlayerPieces) { if (p.IsAlive) { alive++; } }
                            if (alive >= Balance.PlayerFirepowerSoftCount) { break; }
                            if (!GameBalanceTactics.ChoosePlacement(battle, false, out int id, out BoardPos pos, out string reason)) { break; }
                            if (!battle.TryPlayCard(id, pos)) { throw new InvalidOperationException(battle.LastError); }
                        }
                        battle.Tick(Balance.PrepDelaySeconds);
                        if (!battle.StartBattle()) { throw new InvalidOperationException(battle.LastError); }
                        int playerHpStart = 0, bossHpStart = 0;
                        foreach (Piece p in battle.PlayerPieces) { if (p.IsAlive) { playerHpStart += p.MaxHp; } }
                        foreach (Piece p in battle.EnemyPieces) { if (p.IsKing) { bossId = p.Id; bossHpStart = p.MaxHp; } }
                        float minPlayerHpRatio = 1f;
                        float nextRetreat = 3;
                        // Boss目标约三分钟，测试看门狗必须留出余量；360秒只限制测试，不会替游戏结算胜负。
                        while (battle.Phase == GamePhase.Battle && battle.BattleElapsed < 720)
                        {
                            battle.Tick(Balance.BattleSimulationStepSeconds);
                            int hp = 0;
                            foreach (Piece p in battle.PlayerPieces) { if (p.IsAlive) { hp += p.Hp; } }
                            minPlayerHpRatio = Math.Min(minPlayerHpRatio, hp / (float)Math.Max(1, playerHpStart));
                            if (battle.Phase == GamePhase.Battle && battle.BattleElapsed >= nextRetreat)
                            {
                                if (GameBalanceTactics.ChooseRetreat(battle, out BoardPos from, out BoardPos to)) { battle.HandleBoardClick(from); battle.HandleBoardClick(to); }
                                nextRetreat = battle.BattleElapsed + 3;
                            }
                        }
                        bool won = battle.Phase == GamePhase.Settle && battle.LastWinner == GameSide.Player;
                        if (won) { wins++; } else { losses++; }
                        rows.Add(new { seed, chapter, round, won, seconds = battle.BattleElapsed, helperHits, playerHits, enemyHits, deaths, playerDeaths,
                            helperDamage, playerDamage, enemyDamage, bossHpStart, bossCasts, bossDistinctSkills = bossKinds.Count, bossSkills = new List<BossSkillKind>(bossKinds), bossLastDamage, minPlayerHpRatio, wallet = battle.CombatScore, score = battle.ChapterScore });
                        if (round < Balance.RoundsPerChapter)
                        {
                            bool accepted = won ? battle.ContinueRound() : battle.BeginHelperBalanceFixture(chapter, round + 1);
                            if (!accepted) { throw new InvalidOperationException(battle.LastError); }
                        }
                    }
                }
                finally { battle.StopEnemyAI(); RevEvent.RemoveAllByOwner(owner); }
                Console.WriteLine("seed=" + seed + " chapter=" + chapter + " completed");
            }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "helper-balance.json"), JsonSerializer.Serialize(new { wins, losses, mode = "真实服务控制台回放，无画面", rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("六章三种子服务回放：" + wins + "胜 / " + losses + "负");
        return 0;
    }
}

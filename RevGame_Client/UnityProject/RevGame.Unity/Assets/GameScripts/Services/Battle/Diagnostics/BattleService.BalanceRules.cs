#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RevGame.Automation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>真实服务的固定输入规则回放，覆盖多种子。不开UI，不作图形Play结论，不触碰任何存档。</summary>
        public static string VerifyBalanceSeeds()
        {
            var results = new List<BalanceSeedResult>();
            foreach (int seed in new[] { 3028, 4028, 5028 })
            {
                var result = new BalanceSeedResult { seed = seed };
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    var battle = new BattleService(true, seed + chapter * 101);
                    battle.ConfigureRangedAttackRate(1.5f);
                    try
                    {
                        battle.UnlockAllChaptersForDevelopment();

                        if (!battle.BeginCampaign(chapter))
                        {
                            throw new InvalidOperationException(battle.LastError);
                        }
                        for (int number = 1; number <= Balance.RoundsPerChapter; number++)
                        {
                            int front = 0, ranged = 0;
                            foreach (Piece p in battle.PlayerPieces)
                            {
                                if (p.IsAlive)
                                {
                                    if (p.Def.IsRanged)
                                    {
                                        ranged++;
                                    }
                                    else
                                    {
                                        front++;
                                    }
                                }
                            }
                            foreach (var card in battle.Hand)
                            {
                                if (PieceCatalog.Get(card.Kind, card.Star).IsRanged)
                                {
                                    ranged++;
                                }
                                else
                                {
                                    front++;
                                }
                            }
                            if (front < 2 || ranged < 2)
                            {
                                battle.RefreshHand();
                            }
                            for (int n = 0; n < 6; n++)
                            {
                                int count = 0;
                                foreach (Piece p in battle.PlayerPieces)
                                {
                                    if (p.IsAlive)
                                    {
                                        count++;
                                    }
                                }

                                if (count >= 14)
                                {
                                    break;
                                }
                                if (!GameBalanceTactics.ChoosePlacement(battle, false, out int id, out BoardPos pos, out string reason))
                                {
                                    break;
                                }
                                if (!battle.TryPlayCard(id, pos))
                                {
                                    throw new InvalidOperationException(battle.LastError);
                                }
                            }
                            battle.Tick(Balance.PrepDelaySeconds);

                            if (!battle.StartBattle())
                            {
                                throw new InvalidOperationException(battle.LastError);
                            }
                            float nextMove = 3;
                            while (battle.Phase == GamePhase.Battle && battle.BattleElapsed < 180)
                            {
                                battle.Tick(Balance.BattleSimulationStepSeconds);

                                if (battle.Phase == GamePhase.Battle && battle.BattleElapsed >= nextMove)
                                {
                                    if (GameBalanceTactics.ChooseRetreat(battle, out BoardPos from, out BoardPos to))
                                    {
                                        battle.HandleBoardClick(from);
                                        battle.HandleBoardClick(to);
                                    }
                                    nextMove = battle.BattleElapsed + 3;
                                }
                            }
                            bool won = battle.LastWinner == GameSide.Player && battle.Phase == GamePhase.Settle;
                            result.rounds++;

                            if (won)
                            {
                                result.wins++;
                            }
                            else
                            {
                                result.failedRounds.Add(chapter + "-" + number);
                            }
                            result.seconds.Add(battle.BattleElapsed);

                            if (number == Balance.RoundsPerChapter)
                            {
                                break;
                            }
                            if (won)
                            {
                                battle.ContinueRound();
                            }
                            else
                            {
                                battle.BeginCampaignAtRound(chapter, number + 1);
                            }
                        }
                    }
                    finally { battle.StopEnemyAI(); }
                }
                results.Add(result);
            }
            return UnityEngine.JsonUtility.ToJson(new BalanceSeedReport { results = results }, true);
        }
        /// <summary>支援正反例：未到时刻、冷却、次数上限、死亡与关闭辅助。只调用正式治疗入口，失败不能改胜负。</summary>
        public static void VerifyEmergencySupport(Action<bool, string> check)
        {
            var battle = new BattleService(true, 601);
            battle.Chapter = 1;
            battle.Round = 1;
            battle.Phase = GamePhase.Battle;
            Piece player = battle.AddImpactPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
            battle.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
            battle._nextEmergencyAt = Balance.EmergencyFirstSeconds;
            player.TakeDamage((int)(player.MaxHp * .7f));
            int before = player.Hp;
            battle._battleElapsed = Math.Max(0, Balance.EmergencyFirstSeconds - .01f);
            battle.TryEmergencySupport();
            check(player.Hp == before && battle.EmergencySupportCount == 0, "援助未到时刻不提前触发");
            battle._battleElapsed = Balance.EmergencyFirstSeconds;
            battle.TryEmergencySupport();
            check(player.Hp > before && battle.EmergencySupportCount == 1, "低血存活队伍得到实际治疗");
            before = player.Hp;
            battle.TryEmergencySupport();
            check(player.Hp == before && battle.EmergencySupportCount == 1, "同一时刻不重复治疗");
            for (int i = 1; i < Balance.EmergencyMaxCount; i++)
            {
                player.TakeDamage(player.Hp - 1);
                battle._battleElapsed += Balance.EmergencyIntervalSeconds;
                battle.TryEmergencySupport();
            }
            player.TakeDamage(player.Hp - 1);
            before = player.Hp;
            battle._battleElapsed += Balance.EmergencyIntervalSeconds;
            battle.TryEmergencySupport();
            check(battle.EmergencySupportCount == Balance.EmergencyMaxCount && player.Hp == before, "援助有限次数耗尽后不再治疗");
            player.TakeDamage(player.Hp);
            battle.EmergencySupportCount = 0;
            battle._battleElapsed += 100;
            battle.TryEmergencySupport();
            check(!player.IsAlive && battle.EmergencySupportCount == 0, "全灭不能靠援助复活");
            var ordinary = new BattleService(false, 602);
            ordinary.Chapter = 1;
            ordinary.Round = 1;
            ordinary.Phase = GamePhase.Battle;
            Piece target = ordinary.AddImpactPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
            target.TakeDamage(target.Hp - 1);
            ordinary._battleElapsed = 100;
            ordinary.TryEmergencySupport();
            check(target.Hp == 1 && ordinary.EmergencySupportCount == 0, "关闭辅助保持原样");
            battle.StopEnemyAI();
            ordinary.StopEnemyAI();
        }
        [Serializable]
        private sealed class BalanceSeedReport
        {
            public List<BalanceSeedResult> results;
        }
        [Serializable]
        private sealed class BalanceSeedResult
        {
            public int seed, rounds, wins; public List<float> seconds = new List<float>(); public List<string> failedRounds = new List<string>();
        }
    }
}
#endif

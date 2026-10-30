#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>独立服务固定种子：通过真实伤害、占格和结束判定验收，绝不改玩家存档或现有场景。</summary>
        public static void VerifyBossVictory(Action<bool, string> check)
        {
            var service = new BattleService(false, 1027);
            object owner = new object();
            int notices = 0;
            BossDefeatedInfo observed = null;
            RevEvent.AddEventListener<BossDefeatedInfo>(Events.BossDefeated, info => { notices++; observed = info; }, owner: owner);
            try
            {
                check(service.StartRound(1, 1), "真实首回合创建");
                service.StopEnemyAI();
                service.Phase = GamePhase.Battle;
                Piece player = service.AddImpactPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                Piece target = service._enemyPieces[0];
                service.DamagePiece(target, int.MaxValue, player);
                long chapterScore = service.ChapterScore;
                check(chapterScore > 0 && chapterScore == service.RoundScore, "真实击杀同时计入本轮和本章");
                service.DamagePiece(target, int.MaxValue, player);
                check(service.ChapterScore == chapterScore, "重复命中死亡棋不重复章积分");
                check(service.StartRound(1, 2) && service.RoundScore == 0 && service.ChapterScore == chapterScore, "同章下一回合保留累计章积分");
                check(!service.StartRound(9, 1) && service.ChapterScore == chapterScore, "非法开局保留积分");
                check(service.StartRound(2, 1) && service.ChapterScore == 0, "换章清本章积分");
                for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
                {
                    check(service.BeginBossCampaignForDevelopment(chapter), "六章真实Boss开局" + chapter);
                    service.StopEnemyAI();
                    service.Phase = GamePhase.Battle;
                    player = service.AddImpactPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                    Piece king = service._enemyPieces.Find(p => p.IsKing);
                    check(king != null && !service.IsKingCaptured(), "满血Boss不会擒王");
                    service.DamagePiece(king, int.MaxValue, player);
                    check(service.Board.GetPiece(king.Pos) != king && !king.IsAlive, "致死立即从模型棋盘移除");
                    check(service.IsKingCaptured(), "真实击杀满足胜利条件");
                    int before = notices;
                    service.Tick(Balance.BattleSimulationStepSeconds);
                    check(service.Phase == GamePhase.Settle && service.LastWinner == GameSide.Player && notices == before + 1, "真实Tick派一次Boss胜利事实");
                    check(observed.Chapter == chapter && observed.BossId == king.Id && !observed.Captured && observed.ChapterScore == service.ChapterScore, "事实快照携带最终章积分和击杀来源");
                    service.Tick(30);
                    service.DamagePiece(king, int.MaxValue, player);
                    check(notices == before + 1, "结算后Tick和重复伤害不重复演出");
                }
                // 用棋盘正式Place围住所有外邻格，验证零气+残血门槛，不能直接伪造IsLocked。
                check(service.BeginBossCampaignForDevelopment(1), "擒王夹具开局");
                service.StopEnemyAI();
                service.Phase = GamePhase.Battle;
                Piece captureKing = service._enemyPieces.Find(p => p.IsKing);
                service._board.Reset();
                service._enemyPieces.Clear();
                service._playerPieces.Clear();
                check(service._board.Place(captureKing, new BoardPos(3, 3)), "擒王夹具放真实Boss");
                service._enemyPieces.Add(captureKing);
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var pos = new BoardPos(x, y);

                        if (!service.Board.CanPlace(pos))
                        {
                            continue;
                        }
                        var surround = new Piece(service._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
                        check(service._board.Place(surround, pos, respectEnemyEyes: false), "围困夹具占格");
                        service._playerPieces.Add(surround);
                    }
                }
                service.RefreshGoState();
                check(captureKing.IsLocked && !service.IsKingCaptured(), "满血零气仍不能获胜");
                int retained = Math.Max(1, (int)(captureKing.MaxHp * Balance.BossCaptureHpRatio));
                captureKing.TakeDamage(captureKing.Hp - retained);
                check(captureKing.IsAlive && service.IsKingCaptured(), "真实围困残血允许擒王");
                int captureBefore = notices;
                service.EndBattle();
                check(notices == captureBefore + 1 && observed.Captured && observed.BossId == captureKing.Id, "擒王共用胜利事实且不额外扣血");
                check(service.BeginCampaign(1) && service.ChapterScore == 0, "新战役清本章积分");
            }
            finally { service.StopEnemyAI(); RevEvent.RemoveAllByOwner(owner); }
        }
    }
}
#endif

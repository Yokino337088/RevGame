#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>仅Editor隔离演出验收：固定最后一击通过统一伤害和真实Tick，不能直接派胜利事件或篡改分数。</summary>
        public bool DefeatBossForVictoryCheck()
        {
            if(!RevServices.GetRequired<StoryProgress>().IsIsolated||!_scorePreview||!IsBossRound||Phase!=GamePhase.Battle)
            {throw new InvalidOperationException("仅隔离Boss战斗可验收最后一击");}
            Piece player=_playerPieces.Find(p=>p.IsAlive),king=_enemyPieces.Find(p=>p.IsKing&&p.IsAlive);
            if(player==null||king==null){return false;}
            DamagePiece(king,int.MaxValue,player);
            Tick(RevGame.Data.Balance.BattleSimulationStepSeconds);
            return Phase==GamePhase.Settle&&LastWinner==GameSide.Player;
        }
    }
}
#endif

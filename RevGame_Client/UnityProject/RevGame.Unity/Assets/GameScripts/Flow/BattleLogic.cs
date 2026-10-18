// ============================================================
// BattleLogic.cs —— 战斗域中的节奏编排。
// 服务决定规则和阶段，本类只提供玩家/GM 的同一入口并用固定步长推进，避免界面直接写数据。
// 每次使用时取得服务，不缓存可能随域退出而失效的实例。
// ============================================================
using System.Text;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

namespace RevGame.Flow
{
    [RevLogic(GameDomain.Battle)]
    public sealed class BattleLogic : RevLogicBase
    {
        public IBattleService Battle
        {
            get
            {
                return RevServices.GetRequired<IBattleService>();
            }
        }

        public int Chapter
        {
            get
            {
                return Battle.Chapter;
            }
        }

        public int Round
        {
            get
            {
                return Battle.Round;
            }
        }

        protected override void OnStart()
        {
            // 驱动也必须按小步到达服务；否则服务虽能细分，画面仍会在 1.25 秒时一次播完两发。
            Every(Consts.BattleSimulationStepSeconds, OnFixedTick);
            if (Battle.Round == 0)
            {
                Battle.BeginCampaign(1);
            }
        }

        private void OnFixedTick()
        {
            if (Battle.Phase == GamePhase.Battle || Battle.Phase == GamePhase.Prepare)
            {
                Battle.Tick(Consts.BattleSimulationStepSeconds);
            }
        }

        public bool BeginBattle()
        {
            return Battle.StartBattle();
        }

        public bool RefreshHand() { return Battle.RefreshHand(); }

        public bool SelectCard(int id)
        {
            return Battle.SelectCard(id);
        }

        public bool PlayCard(PieceKind kind, StarTier star, BoardPos pos)
        {
            return Battle.TryPlayCard(kind, star, pos);
        }

        public bool PlaySelectedCard(BoardPos pos)
        {
            return Battle.HandlePrepareClick(pos);
        }

        public bool ContinueToNextRound()
        {
            return Battle.ContinueRound();
        }

        public bool ChooseReward(int index)
        {
            return Battle.ChooseReward(index);
        }

        public bool SkipRound()
        {
            return Battle.SkipRound();
        }

        public bool SelectEyeStone()
        {
            return Battle.SelectEyeStone();
        }

        /// <summary>快照中的章/回合始终取服务，不提前跳到下一回合，便于对应结算与日志。</summary>
        public string Snapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("第 " + Chapter + " 章 · 第 " + Round + " 回合（" + Battle.Phase + "）");
            sb.AppendLine("手牌 " + Battle.Hand.Count + "，剩余额度 " + Battle.CardsRemaining + "，全队攻击加成 " + Battle.TeamAttackBonus);
            sb.AppendLine("轻松模式=" + Battle.VictoryAssistEnabled + "，行动间隔=" + Consts.BattleTickSeconds + "秒，支援事件=" + Battle.BattleEventCount);
            sb.AppendLine("AI=" + Battle.EnemyIntelligence + "档，准备计时=" + Battle.PreparationElapsed + "秒，准备增益已发=" + Battle.PreparationGranted);
            foreach (PreparationBuffDef buff in Battle.PreparationBuffs) { sb.AppendLine("准备增益：" + buff.Title + "：" + buff.Detail); }
            sb.AppendLine("敌阵=" + Battle.EnemyFormation + "，辅助=" + Battle.VictorySupportStatus + "，已战斗=" + Battle.BattleElapsed + "秒");
            foreach (var card in Battle.Hand)
            {
                sb.AppendLine("手牌 #" + card.Id + " " + card.Kind + " " + card.Star);
            }

            foreach (Piece piece in Battle.Board.GetAllPieces())
            {
                sb.AppendLine(piece + " 占格=" + piece.Size + " 气=" + piece.Liberties + " 眼=" + piece.EyeCount + " " + piece.GoState);
            }

            sb.Append(Battle.Board.Dump());
            return sb.ToString();
        }
    }
}

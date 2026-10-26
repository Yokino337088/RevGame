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
            Every(Balance.BattleSimulationStepSeconds, OnFixedTick);
            if (Battle.Round == 0)
            {
                Battle.BeginCampaign(1);
            }
        }

        /// <summary>域退出先停止敌方状态机；基类同时回收本Logic计时器，旧AI不再持有旧棋子。</summary>
        protected override void OnStop()
        {
            BattleService service = RevServices.Get<IBattleService>() as BattleService;
            service?.StopEnemyAI();
            service?.FlushScore();
        }

        private void OnFixedTick()
        {
            // 过场遮挡期间不推进准备三秒/战斗时间。SetTickEnabled只管域Tick，不会停Every计时器，
            // 因此必须在这个真实计时入口检查，避免增益提示在玩家看不到时已经播完。
            GameFlow flow = RevServices.Get<GameFlow>();
            if (flow != null && flow.IsGameplayBlocked)
            {
                return;
            }
            // 教学实操时冻结准备随机增益，完成换位后才恢复正常准备计时。
            var lesson = RevLogic.Get<TutorialLogic>();
            if (flow != null && flow.IsTutorialActive && lesson != null && lesson.Step != TutorialStep.Ready) { return; }
            if (Battle.Phase == GamePhase.Battle || Battle.Phase == GamePhase.Prepare)
            {
                Battle.Tick(Balance.BattleSimulationStepSeconds);
            }
        }

        public bool BeginBattle()
        {
            if (RevServices.GetRequired<GameFlow>().IsGameplayBlocked)
            {
                return false;
            }
            if (RevServices.GetRequired<GameFlow>().IsTutorialActive) { return RevServices.GetRequired<GameFlow>().CompleteTutorial(); }
            return Battle.StartBattle();
        }

        public bool RefreshHand() { return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && !RevServices.GetRequired<GameFlow>().IsTutorialActive && Battle.RefreshHand(); }

        public bool SelectCard(int id)
        {
            if (RevServices.GetRequired<GameFlow>().IsGameplayBlocked) { return false; }
            var lesson = RevLogic.Get<TutorialLogic>();
            if (lesson != null && !lesson.AllowCard(id)) { return false; }
            bool accepted = Battle.SelectCard(id);
            if (accepted) { lesson?.Observe(); }
            return accepted;
        }

        public bool PlayCard(PieceKind kind, StarTier star, BoardPos pos)
        {
            return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && !RevServices.GetRequired<GameFlow>().IsTutorialActive && Battle.TryPlayCard(kind, star, pos);
        }

        /// <summary>触屏与GM共用棋盘点击；章间背景加载时和过场一样拒绝输入，不抢跑准备计时。</summary>
        public bool ClickBoard(BoardPos pos)
        {
            if (RevServices.GetRequired<GameFlow>().IsGameplayBlocked) { return false; }
            var lesson = RevLogic.Get<TutorialLogic>();
            if (lesson != null && !lesson.AllowCell(pos)) { return false; }
            bool accepted = Battle.HandleBoardClick(pos);
            if (accepted) { lesson?.Observe(); }
            return accepted;
        }

        public bool ContinueToNextRound()
        {
            return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && Battle.ContinueRound();
        }

        public bool ChooseReward(int index)
        {
            return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && Battle.ChooseReward(index);
        }

        public bool SkipRound()
        {
            return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && Battle.SkipRound();
        }

        public bool SelectEyeStone()
        {
            return !RevServices.GetRequired<GameFlow>().IsGameplayBlocked && Battle.SelectEyeStone();
        }

        /// <summary>快照中的章/回合始终取服务，不提前跳到下一回合，便于对应结算与日志。</summary>
        public string Snapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("第 " + Chapter + " 章 · 第 " + Round + " 回合（" + Battle.Phase + "）");
            sb.AppendLine("手牌 " + Battle.Hand.Count + "，剩余额度 " + Battle.CardsRemaining + "，全队攻击加成 " + Battle.TeamAttackBonus);
            sb.AppendLine("轻松模式=" + Battle.VictoryAssistEnabled + "，行动间隔=" + Balance.BattleTickSeconds + "秒，支援事件=" + Battle.BattleEventCount);
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

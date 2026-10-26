using RevGame.Board;
using RevGame.Core;
using RevGame.Services;
using RevGame.UI;
using Revolution;

namespace RevGame.Flow
{
    /// <summary>五个实操步骤；每步只有正式业务动作成功并符合后置条件后才前进。</summary>
    public enum TutorialStep { SelectCard, SealLiberty, SelectAlly, MoveAlly, Ready }

    /// <summary>与战斗共用域和唯一棋盘。此Logic只约束教学顺序，不创建棋子、扣手牌或另算围棋规则。</summary>
    [RevLogic(GameDomain.Battle)]
    public sealed class TutorialLogic : RevLogicBase
    {
        public TutorialStep Step { get; private set; }
        public string LastError { get; private set; }
        private IBattleService Battle { get { return RevServices.GetRequired<IBattleService>(); } }
        public BoardModel Board { get { return Battle.Board; } }
        public bool Active { get { return RevServices.GetRequired<GameFlow>().IsTutorialActive; } }
        // 以下坐标是引导布局，不是伤害等可调战斗数值；四颗棋子构成只留一个气口的教学形状。
        public static readonly BoardPos EnemyPosition = new BoardPos(4, 4);
        public static readonly BoardPos SealPosition = new BoardPos(4, 3);
        public static readonly BoardPos AllyPosition = new BoardPos(3, 4);
        public static readonly BoardPos MovePosition = new BoardPos(2, 4);
        public int EnemyLiberties { get { return Board.GetPiece(EnemyPosition)?.Liberties ?? -1; } }
        public bool EnemyLocked { get { return Board.GetPiece(EnemyPosition)?.IsLocked == true; } }
        public BoardPos Target { get { return Step == TutorialStep.SealLiberty ? SealPosition : Step == TutorialStep.SelectAlly ? AllyPosition : MovePosition; } }
        protected override void OnStart() { Step = TutorialStep.SelectCard; }

        /// <summary>校验的是当前真实手牌编号，旧按钮或其它槽位不能跳过教程。</summary>
        public bool AllowCard(int id)
        {
            if (!Active) { return true; }
            return Step == TutorialStep.SelectCard && Battle.Hand.Count > 0 && Battle.Hand[0].Id == id || Reject();
        }
        public bool AllowCell(BoardPos pos)
        {
            if (!Active) { return true; }
            return Step != TutorialStep.SelectCard && Step != TutorialStep.Ready && pos.Equals(Target) || Reject();
        }
        private bool Reject() { LastError = "请按当前高亮提示操作"; return false; }

        /// <summary>正式服务已完成提交才读取结果；事件刷新发生在提交中途，所以不能用事件数量来冒充动作成功。</summary>
        public void Observe()
        {
            if (!Active) { return; }
            if (Step == TutorialStep.SelectCard && Battle.SelectedCardId != 0) { Step = TutorialStep.SealLiberty; }
            else if (Step == TutorialStep.SealLiberty && EnemyLocked && Board.GetPiece(SealPosition)?.Side == GameSide.Player) { Step = TutorialStep.SelectAlly; }
            else if (Step == TutorialStep.SelectAlly && Battle.SelectedPieceId == Board.GetPiece(AllyPosition)?.Id) { Step = TutorialStep.MoveAlly; }
            else if (Step == TutorialStep.MoveAlly && Board.GetPiece(AllyPosition) == null && Board.GetPiece(MovePosition)?.Side == GameSide.Player && EnemyLiberties == 1)
            { Step = TutorialStep.Ready; }
            LastError = string.Empty;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            RevUI.Get<TutorialPanel>()?.RefreshView();
        }

        // 自动化与鼠标共用BattleLogic，不能直接写模型制造成功。
        public bool SelectCard() { return Battle.Hand.Count > 0 && RevLogic.Get<BattleLogic>().SelectCard(Battle.Hand[0].Id); }
        public bool ClickCell(int x, int y) { return RevLogic.Get<BattleLogic>().ClickBoard(new BoardPos(x, y)); }
    }
}

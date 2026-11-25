using RevGame.Board;
using RevGame.Core;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using static RevGame.Core.TutorialBoardLayout;

namespace RevGame.Flow
{
    /// <summary>五个实操步骤；每步只有正式业务动作成功并符合后置条件后才前进。</summary>
    public enum TutorialStep
    {
        SelectCard, SealLiberty, SelectAlly, MoveAlly, Ready
    }
    /// <summary>与战斗共用域和唯一棋盘。此Logic只约束教学顺序，不创建棋子、扣手牌或另算围棋规则。</summary>
    [RevLogic(GameDomain.Battle)]
    public sealed class TutorialLogic : RevLogicBase
    {
        public TutorialStep Step
        {
            get; private set;
        }
        public string LastError
        {
            get; private set;
        }
        private IBattleService Battle
        {
            get
            {
                return RevServices.GetRequired<IBattleService>();
            }
        }
        public BoardModel Board
        {
            get
            {
                return Battle.Board;
            }
        }
        public bool Active
        {
            get
            {
                return RevServices.GetRequired<GameFlow>().IsTutorialActive;
            }
        }
        public int EnemyLiberties
        {
            get
            {
                return Board.GetPiece(EnemyPosition)?.Liberties ?? -1;
            }
        }
        public bool EnemyLocked
        {
            get
            {
                return Board.GetPiece(EnemyPosition)?.IsLocked == true;
            }
        }
        public BoardPos Target
        {
            get
            {
                return Step == TutorialStep.SealLiberty ? SealPosition : Step == TutorialStep.SelectAlly ? AllyPosition : MovePosition;
            }
        }
        protected override void OnStart()
        {
            Step = TutorialStep.SelectCard;
        }
        /// <summary>选牌步骤唯一允许的手牌编号，其他步骤和空手牌返回 0。面板与输入门禁共用。</summary>
        internal int SelectableCardId => Step == TutorialStep.SelectCard && Battle.Hand.Count > 0 ? Battle.Hand[0].Id : 0;

        /// <summary>核对教学要求的真实手牌编号，点错时把本步提示送到教学浮层。</summary>
        public bool AllowCard(int id)
        {
            if (!Active)
            {
                return true;
            }
            return id != 0 && id == SelectableCardId || Reject();
        }
        public bool AllowCell(BoardPos pos)
        {
            if (!Active)
            {
                return true;
            }
            return Step != TutorialStep.SelectCard && Step != TutorialStep.Ready && pos.Equals(Target) || Reject();
        }
        /// <summary>拒绝教学之外的动作，立即显示这一步可用的目标；成功推进时由 Observe 清除。</summary>
        private bool Reject()
        {
            string hint = Step switch
            {
                TutorialStep.SelectCard => "先点最左边高亮的剑士牌。",
                TutorialStep.SealLiberty => "把剑士放到敌人下方高亮的空格。",
                TutorialStep.SelectAlly => "点一下高亮的己方棋子。",
                TutorialStep.MoveAlly => "再点高亮空格，把选中的棋子移过去。",
                _ => "点击高亮的开战按钮继续。"
            };
            if (LastError != hint)
            {
                LastError = hint;
                RevUI.Get<TutorialPanel>()?.RefreshView();
            }
            return false;
        }
        /// <summary>正式服务已完成提交才读取结果；事件刷新发生在提交中途，所以不能用事件数量来冒充动作成功。</summary>
        public void Observe()
        {
            if (!Active)
            {
                return;
            }
            if (Step == TutorialStep.SelectCard && Battle.SelectedCardId != 0)
            {
                Step = TutorialStep.SealLiberty;
            }
            else

                if (Step == TutorialStep.SealLiberty && EnemyLocked && Board.GetPiece(SealPosition)?.Side == GameSide.Player)
                {
                    Step = TutorialStep.SelectAlly;
                }
                else

                    if (Step == TutorialStep.SelectAlly && Battle.SelectedPieceId == Board.GetPiece(AllyPosition)?.Id)
                    {
                        Step = TutorialStep.MoveAlly;
                    }
                    else

                        if (Step == TutorialStep.MoveAlly && Board.GetPiece(AllyPosition) == null && Board.GetPiece(MovePosition)?.Side == GameSide.Player && EnemyLiberties == 1)
                        {
                            Step = TutorialStep.Ready;
                        }
            LastError = string.Empty;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            RevUI.Get<TutorialPanel>()?.RefreshView();
        }
        // 自动化与鼠标共用BattleLogic，不能直接写模型制造成功。
        public bool SelectCard()
        {
            return Battle.Hand.Count > 0 && RevLogic.Get<BattleLogic>().SelectCard(Battle.Hand[0].Id);
        }
        public bool ClickCell(int x, int y)
        {
            return RevLogic.Get<BattleLogic>().ClickBoard(new BoardPos(x, y));
        }
    }
}

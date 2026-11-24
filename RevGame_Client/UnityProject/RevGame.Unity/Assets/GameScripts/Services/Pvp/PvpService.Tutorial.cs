using System;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        /// <summary>当前教学的完成回调，只在实际走完最后一步时调用一次。</summary>
        private Action _tutorialCompleted;

        /// <summary>空闲围棋域内的教学会话，使用练习棋盘，生命周期由本服务管理。</summary>
        public PvpTutorial Tutorial { get; private set; }

        /// <summary>九路场景加载后建立练习。已有对局、排队或邀请传输时保持原会话。</summary>
        public bool StartTutorial(Action completed)
        {
            if (_disposed || Config == null || Tutorial != null || Core != null || Queued ||
                Connected || _leaving || Lan != null || _server != null)
            {
                return false;
            }
            Tutorial = new PvpTutorial(Config.komiHalfPoints);
            _tutorialCompleted = completed;
            Revision++;
            return true;
        }

        /// <summary>进入下一课。最后一步先移交完成凭据，再释放当前练习。</summary>
        public bool AdvanceTutorial()
        {
            if (Tutorial == null || !Tutorial.Continue())
            {
                return false;
            }
            if (Tutorial.Completed)
            {
                Action completed = _tutorialCompleted;
                ExitTutorial();
                completed?.Invoke();
            }
            else
            {
                Revision++;
            }
            return true;
        }

        /// <summary>由教学按钮执行真实停一手，棋盘随练习结果更新。</summary>
        public bool PassTutorial()
        {
            if (Tutorial == null)
            {
                return false;
            }
            bool accepted = Tutorial.Pass();
            Revision++;
            return accepted;
        }

        /// <summary>在教学数子阶段提交真实确认，保留最终盘面供玩家查看。</summary>
        public bool ConfirmTutorialScore()
        {
            if (Tutorial == null)
            {
                return false;
            }
            bool accepted = Tutorial.ConfirmScore();
            Revision++;
            return accepted;
        }

        /// <summary>从当前课的初始盘面再练一次，不改已完成的首次选择。</summary>
        public bool RetryTutorial()
        {
            if (Tutorial == null || !Tutorial.Retry())
            {
                return false;
            }
            Revision++;
            return true;
        }

        /// <summary>退出或退域时释放练习状态和回调，允许以后从头重看。</summary>
        public bool ExitTutorial()
        {
            if (Tutorial == null)
            {
                return false;
            }
            Tutorial = null;
            _tutorialCompleted = null;
            Revision++;
            return true;
        }
    }
}

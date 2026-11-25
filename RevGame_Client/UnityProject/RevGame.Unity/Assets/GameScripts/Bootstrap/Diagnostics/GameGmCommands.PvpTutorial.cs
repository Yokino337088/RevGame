#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using RevGame.Flow;
using RevGame.Pvp;
using Revolution;

namespace RevGame.Bootstrap
{
    /// <summary>开发态九路围棋教学 GM，沿用中央账本和玩家的教学生命周期。</summary>
    public static partial class GameGmCommands
    {
        /// <summary>供 Bootstrap 集中登记。命令返回当前教学状态，异步开始须再等待就绪。</summary>
        private static void RegisterPvpTutorialCommands()
        {
            RegisterCommand("围棋教学/开始", "从大厅或空闲围棋菜单进入真实教学，accepted 后等状态 ready=true", _ => StartPvpTutorial());
            RegisterCommand("围棋教学/状态", "只读教学进度、落点提示、规则结果、页面与首次选择，返回 JSON", _ => PvpTutorialStatus());
            RegisterCommand("围棋教学/落点", "点击交叉点，列从左向右、行从下向上，范围 0 至 8。是否答对见 continue 和 feedback",
                args => SelectPvpTutorialPoint(args.Int(0), args.Int(1)), RevGMArg.IntRequired("列"), RevGMArg.IntRequired("行"));
            RegisterCommand("围棋教学/继续", "进入下一课或正式完成教学，未作答时拒绝",
                _ => RunPvpTutorialAction(panel => panel.AdvanceTutorialFromPanel(), "请先完成本节练习"));
            RegisterCommand("围棋教学/停一手", "走教学停一手按钮的业务入口，只在停手课允许",
                _ => RunPvpTutorialAction(panel => panel.PassTutorialFromPanel(), "当前课还不能停一手"));
            RegisterCommand("围棋教学/确认数子", "提交真实数子确认，只在确认阶段允许",
                _ => RunPvpTutorialAction(panel => panel.ConfirmTutorialScoreFromPanel(), "当前课还不能确认数子"));
            RegisterCommand("围棋教学/重练本节", "恢复本节开局和题目提示，在原课重新作答",
                _ => RunPvpTutorialAction(panel => panel.RetryTutorialFromPanel(), "当前没有可重练的课程"));
            RegisterCommand("围棋教学/退出", "退出当前练习并回到模式选择，等待状态 menu=Entry",
                _ => RunPvpTutorialAction(panel => panel.ExitTutorialFromPanel(), "当前没有可退出的教学"));
            RegisterCommand("围棋教学/重置首次选择", "只在隔离测试大厅恢复首次弹窗条件，保留其它进度", _ => ResetPvpTutorialChoice());
        }

        /// <summary>从玩家共用的场景入口接单，加载完成前由状态查询判断输入是否就绪。</summary>
        private static string StartPvpTutorial()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (!flow.EnterPvpTutorial())
            {
                throw new RevGMUsageException(flow.LastError);
            }
            return "accepted";
        }

        /// <summary>把 RevMCP 共用的只读快照编码成 GM 可展示的 JSON。</summary>
        private static string PvpTutorialStatus()
        {
            return RevAutomationJson.Write(PvpAutomation.TutorialCheckState());
        }

        /// <summary>向真实教学提交棋盘点击，成功交付后回报规则反馈与课内进度。</summary>
        private static string SelectPvpTutorialPoint(int x, int y)
        {
            if (!PvpAutomation.TryTutorialPoint(x, y, out string feedback))
            {
                throw new RevGMUsageException(feedback);
            }
            return PvpTutorialStatus();
        }

        /// <summary>仅允许可操作的教学面板执行业务按钮，阶段拒绝时用 GM 失败回执说明原因。</summary>
        private static string RunPvpTutorialAction(Func<PvpPanel, bool> action, string rejection)
        {
            PvpPanel panel = PvpAutomation.GetTutorialInputPanel();
            if (panel == null)
            {
                throw new RevGMUsageException("教学尚未就绪，请等待状态 ready=true");
            }
            if (!action(panel))
            {
                throw new RevGMUsageException(rejection);
            }
            return PvpTutorialStatus();
        }

        /// <summary>重置本轮隔离进度的九路教学标记，拒绝在真实存档或场景过渡中执行。</summary>
        private static string ResetPvpTutorialChoice()
        {
            if (!PvpAutomation.ResetTutorialCheck())
            {
                throw new RevGMUsageException("只允许在隔离测试大厅重置首次选择");
            }
            return PvpTutorialStatus();
        }
    }
}
#endif

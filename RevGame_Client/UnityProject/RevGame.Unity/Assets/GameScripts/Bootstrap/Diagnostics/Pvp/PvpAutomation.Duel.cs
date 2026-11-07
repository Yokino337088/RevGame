#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    // 开发态决斗验收入口，和正式 PvP 服务复用同一业务动作。
    public static partial class PvpAutomation
    {
        // 仅在开发环境注册。命令路径同时显示在 GM 面板和 RevMCP 的发现结果中。
        private const string ComputerDuelGm = "围棋对战/本地人机决斗";
        // Unity 非缩放时间，单位秒。正常过场结束就继续，超时退出并写明失败原因。
        private const float ComputerDuelTimeoutSeconds = 40f;
        // 请求等待场景就绪期间挡住重复点击，避免两个请求重开同一场测试棋局。
        private static bool _computerDuelPending;

        // 注册本机、P2P 与只读快照查询，支持场景、封帧、动画和特效断言。
        private static void RegisterDuelAutomation()
        {
            RevGM.Register(ComputerDuelGm, "从大厅或本地人机棋局重开测试局，构造劣势后挑战电脑", _ => BeginComputerDuel());
            RevRuntimeAutomation.Register("pvp.duel-enter-computer", "经正式 GameFlow 直接打开围棋人机棋盘",
                Empty, _ => Result(Flow.EnterPvp("Computer"), Flow.LastError), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-fixture", "用合法围棋命令走到黑方明显劣势，随后切换到本地人机",
                Empty, _ => Result(Service.PrepareDuelFixture(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-challenge", "调用真实劣势决斗入口", Empty,
                _ => Result(Service.ChallengeDuel(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-lan-check", "使用实际 UTP 邀请、两席就绪和原生帧同步检查", Empty,
                _ => BeginLanGameCheck(duel: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-state", "只读决斗场景、双方就绪和原生快照", Empty, _ =>
            {
                var state = Service.DuelSnapshot;
                var view = UnityEngine.Object.FindObjectOfType<RevGame.View.PvpDuelView>();
                return new Dictionary<string, object> { ["scene"] = RevScene.CurrentName, ["transitioning"] = Flow.IsTransitioning,
                    ["eligible"] = Service.CanChallengeDuel, ["ready"] = Service.DuelBothReady, ["frame"] = state.Frame,
                    ["fighting"] = state.Frame >= 5, ["result"] = state.Result, ["hash"] = state.Hash, ["error"] = Service.DuelError,
                    ["airborne"] = state.FirstY > 0, ["firstY"] = state.FirstY,
                    ["bothPlaying"] = view != null && view.FirstAnimator.IsPlaying && view.SecondAnimator.IsPlaying,
                    ["bothAnimated"] = view != null && view.FirstAnimator.HasSpriteMotion && view.SecondAnimator.HasSpriteMotion,
                    ["firstIdleAnimated"] = view != null && view.FirstAnimator.IdleSpriteMotion,
                    ["firstJumpAnimated"] = view != null && view.FirstAnimator.JumpSpriteMotion,
                    ["secondAttackAnimated"] = view != null && view.SecondAnimator.AttackSpriteMotion,
                    ["strikeVisible"] = view != null && (view.Effects.First.enabled || view.Effects.Second.enabled),
                    ["firstHealth"] = state.FirstHealth, ["secondHealth"] = state.SecondHealth };
            }, readOnly: true, owner: Owner);
        }

        // GM 回调只接收请求。大厅走正式人机入口，已有棋局仅允许在本地人机模式下重开。
        private static string BeginComputerDuel()
        {
            if (_computerDuelPending || Flow.IsTransitioning)
            {
                throw new RevGMUsageException("过场或人机决斗请求正在处理，请等结束后再试。");
            }

            if (Flow.Screen == GameScreen.Home)
            {
                if (!Flow.EnterPvp("Computer"))
                {
                    throw new RevGMUsageException(Flow.LastError);
                }
            }
            else if (!IsLocalComputerBoard())
            {
                throw new RevGMUsageException("请从大厅或本地人机围棋执行此命令。");
            }

            _computerDuelPending = true;
            EnterComputerDuelWhenReady().Forget();
            return "已受理，等棋盘就绪后进入决斗，白方由电脑控制。";
        }

        // Screen 与玩法模式一起检查。仅判断 Local 会把本地双人或尚未开局的棋盘也纳入。
        private static bool IsLocalComputerBoard()
        {
            return Flow.Screen == GameScreen.Pvp && Service.Mode == PvpConnectionMode.Local
                && Service.IsComputerGame && Service.Core != null && Service.DuelId.Length == 0;
        }

        // RevTask 逐帧等待正式场景过场。棋盘就绪后用合法棋步重建测试局，再调用玩家按钮使用的挑战入口。
        private static async RevTask EnterComputerDuelWhenReady()
        {
            try
            {
                float deadline = Time.realtimeSinceStartup + ComputerDuelTimeoutSeconds;
                while (Flow.IsTransitioning && Time.realtimeSinceStartup < deadline)
                {
                    await RevTask.Yield();
                }

                if (Flow.IsTransitioning || !IsLocalComputerBoard())
                {
                    RevLog.Warn("本地人机决斗未启动：等待棋盘就绪失败。" + Flow.LastError, "PvP");
                    return;
                }

                // 测试夹具只准备劣势局面，匹配、场景就绪、封帧和电脑输入仍由正式玩法负责。
                if (!Service.PrepareDuelFixture() || !Service.ChallengeDuel())
                {
                    RevLog.Warn("本地人机决斗未启动：" + Service.Status, "PvP");
                }
            }
            finally
            {
                _computerDuelPending = false;
            }
        }
    }
}
#endif

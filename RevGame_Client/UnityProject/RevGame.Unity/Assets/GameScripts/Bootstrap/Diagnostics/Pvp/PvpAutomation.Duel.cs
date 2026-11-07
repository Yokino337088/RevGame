#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using Revolution;

namespace RevGame.Pvp
{
    // 开发态决斗验收入口，和正式 PvP 服务复用同一业务动作。
    public static partial class PvpAutomation
    {
        // 注册本机、P2P 与只读快照查询，支持场景、封帧、动画和特效断言。
        private static void RegisterDuelAutomation()
        {
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
                    ["bothAnimated"] = view != null && view.FirstAnimator.IsPlaying && view.SecondAnimator.IsPlaying,
                    ["strikeVisible"] = view != null && (view.Effects.First.enabled || view.Effects.Second.enabled),
                    ["firstHealth"] = state.FirstHealth, ["secondHealth"] = state.SecondHealth };
            }, readOnly: true, owner: Owner);
        }
    }
}
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pvp.Duel;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

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
        // 本地操作回归的结果。运行中只允许一个夹具占用方向按钮。
        private static bool _controlCheckRunning, _controlCheckPassed;
        private static string _controlCheckError = "";
        // 仅开发测试记录输入、封帧和落屏证据。每次检查独立重建，禁止进入逻辑状态或协议。
        private static Dictionary<string, object> _controlCheckTrace = new Dictionary<string, object>();

        [Serializable]
        private sealed class DuelControlArgs
        {
            // 可选的主线程停顿毫秒数，用来复现已经入缓冲的按键遇到掉帧的情况。
            public int stallMs;
        }

        // 注册本机、P2P 与只读快照查询，支持场景、封帧、动画和特效断言。
        private static void RegisterDuelAutomation()
        {
            RevGM.Register(ComputerDuelGm, "从大厅或本地人机棋局重开测试局，构造劣势后挑战电脑", _ => BeginComputerDuel());
            RevGM.Register("围棋对战/决斗双方无敌", "本地人机决斗专用。1 双方免伤，0 恢复伤害", SetDuelNoDamage, RevGMArg.Int("开关", 1));
            RevRuntimeAutomation.Register("pvp.duel-enter-computer", "经正式 GameFlow 直接打开围棋人机棋盘",
                Empty, _ => Result(Flow.EnterPvp("Computer"), Flow.LastError), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-fixture", "用合法围棋命令走到黑方明显劣势，随后切换到本地人机",
                Empty, _ => Result(Service.PrepareDuelFixture(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-challenge", "调用真实劣势决斗入口", Empty,
                _ => Result(Service.ChallengeDuel(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-lan-check", "使用实际 UTP 邀请、两席就绪和原生帧同步检查", Empty,
                _ => BeginLanGameCheck(duel: true), owner: Owner);
            const string controlSchema = "{\"type\":\"object\",\"properties\":{\"stallMs\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":400}}}";
            RevRuntimeAutomation.Register("pvp.duel-control-check", "通过真实按钮验收转向和连续普攻，可在入队后模拟主线程卡顿", controlSchema,
                json => BeginDuelControlCheck(JsonUtility.FromJson<DuelControlArgs>(json)?.stallMs ?? 0), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-control-state", "读取决斗操作验收结果", Empty,
                _ => new Dictionary<string, object>(_controlCheckTrace) { ["done"] = !_controlCheckRunning,
                    ["passed"] = _controlCheckPassed, ["error"] = _controlCheckError }, readOnly: true, owner: Owner);
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
                    ["firstHealth"] = state.FirstHealth, ["secondHealth"] = state.SecondHealth,
                    ["firstHit"] = state.FirstHit, ["secondHit"] = state.SecondHit };
            }, readOnly: true, owner: Owner);
        }

        // 只在当前人机决斗中驱动已有 UI，所有输入仍经过 PvpService 封帧。
        private static object BeginDuelControlCheck(int stallMs)
        {
            if (stallMs < 0 || stallMs > 400)
            {
                return Result(false, "停顿时间须在 0 到 400 毫秒之间。");
            }
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            if (_controlCheckRunning || view == null || !Service.TrySetDuelNoDamage(true))
            {
                return Result(false, "请先进入本地人机决斗。");
            }
            _controlCheckRunning = true;
            _controlCheckPassed = false;
            _controlCheckError = "";
            _controlCheckTrace = new Dictionary<string, object> { ["stallMs"] = stallMs, ["actualStallMs"] = 0L };
            CheckDuelControls(view, stallMs).Forget();
            return Result(true, "");
        }

        // 以逻辑帧判定两次输入的时机，以实际 Sprite 和镜像判定表现，避免用 isPlaying 冒充换帧。
        private static async RevTask CheckDuelControls(PvpDuelView view, int stallMs)
        {
            GameObject left = view.transform.Find("Left").gameObject;
            GameObject right = view.transform.Find("Right").gameObject;
            var pointer = new PointerEventData(EventSystem.current);
            var service = Service;
            string id = service.DuelId;
            try
            {
                int start = service.DuelSnapshot.FirstX;
                ExecuteEvents.Execute(left, pointer, ExecuteEvents.pointerDownHandler);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstX < start &&
                    view.First.rectTransform.localScale.x < 0 && view.FirstAnimator.RunSpriteMotion, "左移与跑动");
                int turned = service.DuelSnapshot.FirstX;
                ExecuteEvents.Execute(right, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(left, pointer, ExecuteEvents.pointerUpHandler);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstX > turned &&
                    view.First.rectTransform.localScale.x > 0, "右移与旧按钮松手");
                ExecuteEvents.Execute(right, pointer, ExecuteEvents.pointerUpHandler);
                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame > 0 &&
                    service.DuelSnapshot.FirstX == service.DuelPreviousSnapshot.FirstX, "松手停止");
                ExecuteEvents.Execute(view.Jab.gameObject, pointer, ExecuteEvents.submitHandler);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == 1, "首次普攻");
                int firstStart = service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge;
                int expectedSecondStart = firstStart + DuelConfig.ActionDuration(1) + 1;
                _controlCheckTrace["firstStart"] = firstStart;
                _controlCheckTrace["expectedSecondStart"] = expectedSecondStart;
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAge == DuelConfig.ActionDuration(1) - 2, "恢复期输入窗口");
                ExecuteEvents.Execute(view.Jab.gameObject, pointer, ExecuteEvents.submitHandler);
                // 先确认真实 UI 采样已经把第二次按键交给业务，再暂停 Unity 主线程。
                await WaitForDuelControl(id, () => service.DuelQueuedAttackForCheck == 1, "第二次普攻入缓冲");
                _controlCheckTrace["queueFrame"] = service.DuelSnapshot.Frame;
                _controlCheckTrace["queuedAge"] = service.DuelSnapshot.FirstAge;
                if (stallMs > 0)
                {
                    var stallClock = System.Diagnostics.Stopwatch.StartNew();
                    System.Threading.Thread.Sleep(stallMs);
                    _controlCheckTrace["actualStallMs"] = stallClock.ElapsedMilliseconds;
                }
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == 1 &&
                    service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge > firstStart, "权威第二次普攻");
                int secondStart = service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge;
                _controlCheckTrace["secondStart"] = secondStart;
                if (secondStart != expectedSecondStart)
                {
                    throw new InvalidOperationException("决斗验收失败：两次普攻之间存在空档。");
                }
                await WaitForDuelControl(id, () => view.FirstAnimator.LastObservedAttackStart == secondStart &&
                    view.FirstAnimator.AttackSpriteChanges > 0, "第二次普攻 Sprite 实际换帧");
                await WaitForDuelControl(id, () =>
                    view.SecondAnimator.RunSpriteMotion && view.SecondAnimator.AttackSpriteMotion, "刺客跑动与攻击实际换帧");
                _controlCheckPassed = true;
            }
            catch (Exception error)
            {
                _controlCheckError = error.Message;
            }
            finally
            {
                if (view != null && service.DuelId == id)
                {
                    CaptureDuelControlTrace(service, view);
                    ExecuteEvents.Execute(left, pointer, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(right, pointer, ExecuteEvents.pointerUpHandler);
                    service.TrySetDuelNoDamage(false);
                }
                _controlCheckRunning = false;
            }
        }

        // 记录最后一次可见的权威快照、输入字和呈现结果，区分丢按键、漏封帧与动画故障。
        private static void CaptureDuelControlTrace(PvpService service, PvpDuelView view)
        {
            DuelSnapshot snapshot = service.DuelSnapshot;
            _controlCheckTrace["lastFrame"] = snapshot.Frame;
            _controlCheckTrace["lastAction"] = snapshot.FirstAction;
            _controlCheckTrace["lastAge"] = snapshot.FirstAge;
            _controlCheckTrace["lastQueuedAttack"] = service.DuelQueuedAttackForCheck;
            _controlCheckTrace["lastIntentWord"] = service.DuelLastIntentForCheck;
            _controlCheckTrace["lastAttackIntentFrame"] = service.DuelLastAttackIntentFrameForCheck;
            _controlCheckTrace["lastAttackIntentAction"] = service.DuelLastAttackIntentForCheck;
            _controlCheckTrace["attackExpiredAtFrame"] = service.DuelAttackExpiredAtFrameForCheck;
            _controlCheckTrace["observedAttackStart"] = view.FirstAnimator.LastObservedAttackStart;
            _controlCheckTrace["attackSpriteChanges"] = view.FirstAnimator.AttackSpriteChanges;
            _controlCheckTrace["duelError"] = service.DuelError;
        }

        // 逐渲染帧等待真实结果。离开原局或超时都结束，不把测试操作带进下一场。
        private static async RevTask WaitForDuelControl(string id, Func<bool> predicate, string stage)
        {
            float deadline = Time.realtimeSinceStartup + 5;
            while (Service.DuelId == id && Time.realtimeSinceStartup < deadline)
            {
                if (predicate())
                {
                    return;
                }
                await RevTask.Yield();
            }
            throw new InvalidOperationException("决斗验收失败：" + stage);
        }

        // GM 只切换免伤，保持当前生命值。下一完整帧开始生效，命中和动画照常发生。
        private static string SetDuelNoDamage(RevGMArgs args)
        {
            int enabled = args.Int(0, 1);
            if ((enabled != 0 && enabled != 1) || !Service.TrySetDuelNoDamage(enabled == 1))
            {
                throw new RevGMUsageException("请在进行中的本地人机决斗输入 1 或 0。");
            }
            return enabled == 1 ? "双方无敌已开启。" : "双方无敌已关闭。";
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

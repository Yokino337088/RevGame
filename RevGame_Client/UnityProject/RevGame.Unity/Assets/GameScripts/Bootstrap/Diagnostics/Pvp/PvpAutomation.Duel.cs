#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pvp.Duel;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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
        // 恢复测试记录实际封口的帧号和结果，只在开发自动化中保留最近一次。
        private static bool _recoveryCheckRunning, _recoveryCheckPassed;
        private static string _recoveryCheckError = "";
        private static Dictionary<string, object> _recoveryCheckTrace = new Dictionary<string, object>();
        // 街机连招验收从准备、出招推进到通过或失败，计划只读取阶段，不重复点击。
        private static string _arcadeStage = "";
        // 失败时记录哪个原生动作或实际显示断言未完成。
        private static string _arcadeError = "";
        // 从开局默认位置到接近后测得的镜头移动量，单位为画布局部像素。
        private static float _arcadePan;
        // 坠枪起手是否由原生快照确认离地。
        private static bool _arcadePlungeAirborne;
        // 坠枪命中年龄是否由原生快照确认已经落地。
        private static bool _arcadePlungeLanded;

        // 20 Hz 下等至少一秒再取检查点，之后至少执行 16 个真实逻辑帧。
        private const int RecoveryCheckpointMinimumFrame = 20;
        private const int RecoveryMinimumSuffixFrames = 16;

        [Serializable]
        private sealed class DuelControlArgs
        {
            // 可选的主线程停顿毫秒数，用来复现已经入缓冲的按键遇到掉帧的情况。
            public int stallMs;
        }

        // 注册本机、P2P 与只读快照查询，支持场景、封帧、动画和特效断言。
        private static void RegisterDuelAutomation()
        {
            RegisterDuelHudAutomation();
            RevGM.Register(ComputerDuelGm, "从大厅或本地人机棋局重开测试局，构造劣势后挑战电脑", _ => BeginComputerDuel());
            RevGM.Register("围棋对战/决斗双方无敌", "本地人机决斗专用。1 双方免伤，0 恢复伤害", SetDuelNoDamage, RevGMArg.Int("开关", 1));
            RevGM.Register("围棋对战/暂停决斗敌人AI", "本地人机决斗专用。1 暂停白方主动操作，0 恢复，离场自动复位",
                SetDuelComputerAiPaused, RevGMArg.Int("开关", 1));
            RevRuntimeAutomation.Register("pvp.duel-enter-computer", "经正式 GameFlow 直接打开围棋人机棋盘",
                Empty, _ => Result(Flow.EnterPvp("Computer"), Flow.LastError), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-fixture", "用合法围棋命令走到黑方明显劣势，随后切换到本地人机",
                Empty, _ => Result(Service.PrepareDuelFixture(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-challenge", "调用真实劣势决斗入口", Empty,
                _ => Result(Service.ChallengeDuel(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-lan-check", "使用实际 UTP 邀请、两席就绪和原生帧同步检查", Empty,
                _ => BeginLanGameCheck(duel: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-test-lan-check", "空棋盘从白方经实际 UTP 发起开发决斗并检查双端就绪", Empty,
                _ => BeginLanGameCheck(duel: true, duelTestEntry: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-prediction-view-check", "用正式场景的黑方和隔离白方建立真实 UTP 决斗，等待 HUD 输入", Empty,
                _ => BeginLanGameCheck(duel: true, duelTestEntry: true, visibleDuel: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-prediction-jump", "经 EventSystem 点击联机决斗跳跃按钮", Empty,
                _ => ClickNetworkDuelJump(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-arcade-prepare", "在隔离 UTP 测试局按住右移并等待能量与镜头就绪", Empty,
                _ => StartArcadeCheck(prepare: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-arcade-combo", "经真实 HUD 按钮执行三段普攻、技能一、技能二和大招", Empty,
                _ => StartArcadeCheck(prepare: false), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-arcade-state", "读取真实 HUD 连招和镜头验收结果", Empty,
                _ => new Dictionary<string, object> { ["stage"] = _arcadeStage, ["error"] = _arcadeError, ["cameraPan"] = _arcadePan,
                    ["plungeAirborne"] = _arcadePlungeAirborne, ["plungeLanded"] = _arcadePlungeLanded },
                readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-lan-hash-check", "实际 UTP 双端打到终局，核对周期和终局帧指纹", Empty,
                _ => BeginLanGameCheck(duel: true, duelHashAudit: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-lan-hash-mismatch-check", "实际 UTP 发送一份错误的白方对账值，验证两端报告同帧分歧", Empty,
                _ => BeginLanGameCheck(duel: true, duelHashAudit: true, duelHashMismatch: true), owner: Owner);
            const string controlSchema = "{\"type\":\"object\",\"properties\":{\"stallMs\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":400}}}";
            RevRuntimeAutomation.Register("pvp.duel-control-check", "通过真实按钮验收转向和连续普攻，可在入队后模拟主线程卡顿", controlSchema,
                json => BeginDuelControlCheck(JsonUtility.FromJson<DuelControlArgs>(json)?.stallMs ?? 0), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-control-state", "读取决斗操作验收结果", Empty,
                _ => new Dictionary<string, object>(_controlCheckTrace) { ["done"] = !_controlCheckRunning,
                    ["passed"] = _controlCheckPassed, ["error"] = _controlCheckError }, readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-recovery-check", "读取本地人机检查点和封帧历史，在隔离实例中对照回放", Empty,
                _ => BeginDuelRecoveryCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.duel-recovery-state", "读取检查点恢复和回放的逐帧对账结果", Empty,
                _ => new Dictionary<string, object>(_recoveryCheckTrace) { ["done"] = !_recoveryCheckRunning,
                    ["passed"] = _recoveryCheckPassed, ["error"] = _recoveryCheckError }, readOnly: true, owner: Owner);
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
                    ["fightersPacked"] = view != null && view.First.sprite != null && view.Second.sprite != null &&
                        view.First.sprite.packed && view.Second.sprite.packed,
                    ["fightersMatchSeats"] = view != null && view.First.sprite != null && view.Second.sprite != null &&
                        view.First.sprite.name.StartsWith("Ember_", StringComparison.Ordinal) &&
                        view.Second.sprite.name.StartsWith("Yaya_", StringComparison.Ordinal),
                    ["yayaSprite"] = view?.Second.sprite?.name ?? "",
                    ["yayaPortrait"] = view?.SecondPortrait?.sprite?.name ?? "",
                    ["yayaShaderSupported"] = view != null && view.Effects.Second.material.shader.name == "RevGame/UI/Yaya Duel Feather" &&
                        view.Effects.Second.material.shader.isSupported,
                    ["yayaIdleAnimated"] = view != null && view.SecondAnimator.IdleSpriteMotion,
                    ["yayaRunAnimated"] = view != null && view.SecondAnimator.RunSpriteMotion,
                    ["yayaCastMask"] = view != null ? view.Effects.YayaCastMaskForCheck : 0,
                    ["yayaSoundMask"] = view != null ? view.Effects.YayaSoundMaskForCheck : 0,
                    ["yayaImpacts"] = view != null ? view.Effects.YayaImpactsForCheck : 0,
                    ["yayaCastSprite"] = view != null && view.Effects.Second.enabled ? view.Effects.Second.sprite.name : "",
                    ["firstAction"] = state.FirstAction, ["firstCombo"] = state.FirstCombo, ["firstEnergy"] = state.FirstEnergy,
                    ["computerAiPaused"] = Service.DuelComputerAiPaused, ["computerInput"] = Service.DuelComputerInputForCheck,
                    ["hudPressCount"] = view != null ? view.HudPressCountForCheck : 0,
                    ["lastHudAction"] = view != null ? view.LastHudActionForCheck : -1,
                    ["lastAttackIntent"] = Service.DuelLastAttackIntentForCheck,
                    ["keyboardGuideVisible"] = view != null && view.KeyboardGuide.activeInHierarchy,
                    ["inputFeedback"] = view != null ? view.InputFeedback.text : "",
                    ["legacyAnimationWriting"] = view != null && (view.FirstAnimator.GetComponent<Animation>().enabled ||
                        view.SecondAnimator.GetComponent<Animation>().enabled),
                    ["firstOptions"] = state.FirstOptions, ["secondStun"] = state.SecondStun,
                    ["cameraZoom"] = view != null ? view.CameraRig.Fighters.localScale.x : 0,
                    ["cameraPan"] = view != null ? view.CameraRig.Fighters.anchoredPosition.x : 0,
                    ["castMask"] = view != null ? view.Effects.CastMaskForCheck : 0,
                    ["soundMask"] = view != null ? view.Effects.SoundMaskForCheck : 0,
                    ["impactCount"] = view != null ? view.Effects.ImpactsForCheck : 0,
                    ["firstIdleAnimated"] = view != null && view.FirstAnimator.IdleSpriteMotion,
                    ["sawPredictedJump"] = view != null && view.SawPredictedJumpForCheck,
                    ["sawConfirmedJump"] = view != null && view.SawConfirmedJumpForCheck,
                    ["firstJumpAnimated"] = view != null && view.FirstAnimator.JumpSpriteMotion,
                    ["secondAttackAnimated"] = view != null && view.SecondAnimator.AttackSpriteMotion,
                    ["strikeVisible"] = view != null && (view.Effects.First.enabled || view.Effects.Second.enabled),
                    ["firstHealth"] = state.FirstHealth, ["secondHealth"] = state.SecondHealth,
                    ["firstHit"] = state.FirstHit, ["secondHit"] = state.SecondHit };
            }, readOnly: true, owner: Owner);
        }

        // 只允许本轮可见测试夹具接收自动指针，普通真人房间不会被诊断命令控制。
        private static object StartArcadeCheck(bool prepare)
        {
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            if (view == null || _lanGameCheck == null || !_lanGameCheck.OwnsVisibleDuel(Service) ||
                !Service.DuelBothReady || _arcadeStage == "approach" || _arcadeStage == "combo" ||
                (!prepare && _arcadeStage != "ready"))
            {
                return Result(false, "请先进入隔离的可见 UTP 决斗并完成相应准备。");
            }
            _arcadeStage = prepare ? "approach" : "combo";
            _arcadeError = "";
            RunArcadeCheck(view, prepare).Forget();
            return Result(true, "");
        }

        // 通过射线和原生命中计数等待每一段。结束或失败都松开指针，避免下一局继承按住状态。
        private static async RevTask RunArcadeCheck(PvpDuelView view, bool prepare)
        {
            PvpService service = Service;
            string id = service.DuelId;
            var pointer = new PointerEventData(EventSystem.current) { pointerId = 16 };
            GameObject stick = view.MoveStick.gameObject;
            try
            {
                if (prepare)
                {
                    _arcadePlungeAirborne = false;
                    _arcadePlungeLanded = false;
                    PressDuelHudButton(stick, pointer, .72f);
                    await WaitForDuelControl(id, () => service.DuelSnapshot.SecondX - service.DuelSnapshot.FirstX <= 910, "接近训练对手");
                    ExecuteEvents.Execute(stick, pointer, ExecuteEvents.pointerUpHandler);
                    await WaitForDuelControl(id, () => service.DuelSnapshot.FirstEnergy >= 92, "连招能量");
                    _arcadePan = Mathf.Abs(view.CameraRig.Fighters.anchoredPosition.x);
                    if (_arcadePan < 20 || !view.First.sprite.packed || !view.Second.sprite.packed)
                    {
                        throw new InvalidOperationException("决斗镜头或角色图集未就绪。");
                    }
                    _arcadeStage = "ready";
                    return;
                }

                // 先观察真实命中，再点下一个按钮。托管缓冲会等原生公布的取消窗口。
                Button[] buttons = { view.Jab, view.Jab, view.Jab, view.Skill, view.SkillTwo, view.Ultimate };
                int[] actions = { 1, 5, 6, 2, 4, 3 };
                for (int index = 0; index < buttons.Length; index++)
                {
                    int action = actions[index];
                    int combo = index + 1;
                    ClickDuelHudButton(buttons[index].gameObject, pointer);
                    if (action == 4)
                    {
                        await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == 4 &&
                            service.DuelSnapshot.FirstY > 0, "坠枪离地");
                        _arcadePlungeAirborne = true;
                    }
                    await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == action &&
                        service.DuelSnapshot.FirstCombo == combo, "权威连招第 " + combo + " 段");
                    if (action == 4)
                    {
                        _arcadePlungeLanded = service.DuelSnapshot.FirstY == 0;
                    }
                }
                await WaitForDuelControl(id, () => view.Effects.ImpactsForCheck >= 6 &&
                    view.Effects.CastMaskForCheck == 63, "六种起手与命中特效");
                if (service.DuelSnapshot.SecondHealth != 3)
                {
                    throw new InvalidOperationException("实战目标的终段血量与六次封帧命中不一致。");
                }
                _arcadeStage = "passed";
            }
            catch (Exception error)
            {
                _arcadeError = error.Message;
                _arcadeStage = "failed";
            }
            finally
            {
                if (stick != null)
                {
                    ExecuteEvents.Execute(stick, pointer, ExecuteEvents.pointerUpHandler);
                }
            }
        }

        // 只在真实联机决斗的 HUD 就绪后注入触点，保持与玩家点按相同的射线、事件和输入路径。
        private static object ClickNetworkDuelJump()
        {
            var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
            if (view == null || Service.Mode != PvpConnectionMode.Lan || !Service.Connected || !Service.DuelBothReady)
            {
                return Result(false, "请先通过可见 UTP 夹具进入已就绪的决斗场景。");
            }
            var pointer = new PointerEventData(EventSystem.current) { pointerId = 15 };
            ClickDuelHudButton(view.Jump.gameObject, pointer);
            return Result(true, "");
        }

        // 从正常进入的本地人机决斗开始。测试只读取当前局，两个回放实例由本次检查持有。
        private static object BeginDuelRecoveryCheck()
        {
            PvpService service = Service;
            if (_recoveryCheckRunning || !service.DuelRecoveryAvailableForCheck || service.DuelSnapshot.Result != 0)
            {
                return Result(false, "请先进入进行中的本地人机决斗。");
            }
            _recoveryCheckRunning = true;
            _recoveryCheckPassed = false;
            _recoveryCheckError = "";
            _recoveryCheckTrace = new Dictionary<string, object>();
            CheckDuelRecovery(service).Forget();
            return Result(true, "");
        }

        // 对照从零回放和从检查点续算。前者提供基准快照，后者逐帧核对实际输出。
        private static async RevTask CheckDuelRecovery(PvpService service)
        {
            string id = service.DuelId;
            try
            {
                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame >= RecoveryCheckpointMinimumFrame, "决斗进行中检查点");
                byte[] checkpoint = service.SaveDuelCheckpointForCheck(out DuelSnapshot atCheckpoint);
                if (checkpoint == null || checkpoint.Length == 0 || atCheckpoint.Result != 0 ||
                    !DuelSnapshotsMatch(atCheckpoint, service.DuelSnapshot))
                {
                    throw new InvalidOperationException("当前权威检查点与本地副本不一致。");
                }
                _recoveryCheckTrace["checkpointFrame"] = atCheckpoint.Frame;
                _recoveryCheckTrace["checkpointHash"] = atCheckpoint.Hash;
                _recoveryCheckTrace["checkpointBytes"] = checkpoint.Length;

                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame >= atCheckpoint.Frame + RecoveryMinimumSuffixFrames,
                    "继续实际封帧");
                DuelSnapshot live = service.DuelSnapshot;
                DuelReplay captured = service.CaptureDuelReplayForCheck();
                if (captured == null)
                {
                    throw new InvalidOperationException("未取得本局封口历史。");
                }
                byte[] recording = captured.Serialize();
                DuelReplay replay = DuelReplay.Deserialize(recording);
                if (replay.DuelId != id || replay.FrameCount != live.Frame)
                {
                    throw new InvalidOperationException("回放记录的对局或帧上界与现场不一致。");
                }
                _recoveryCheckTrace["replayBytes"] = recording.Length;
                _recoveryCheckTrace["replayFrames"] = replay.FrameCount;
                _recoveryCheckTrace["liveFrame"] = live.Frame;
                _recoveryCheckTrace["liveHash"] = live.Hash;

                using var full = new DuelCorePipe(id);
                using var restored = new DuelCorePipe(id);
                if (!restored.TryRestoreCheckpoint(checkpoint) ||
                    restored.Snapshot.Frame != atCheckpoint.Frame || restored.Snapshot.Hash != atCheckpoint.Hash)
                {
                    throw new InvalidOperationException("检查点恢复后的帧号或指纹与权威不一致。");
                }
                _recoveryCheckTrace["restoredFrame"] = restored.Snapshot.Frame;
                _recoveryCheckTrace["restoredHash"] = restored.Snapshot.Hash;

                int expectedSuffix = live.Frame - atCheckpoint.Frame;
                var baseline = new List<DuelSnapshot>(expectedSuffix);
                replay.ReplayInto(full, snapshot =>
                {
                    if (snapshot.Frame == atCheckpoint.Frame && !DuelSnapshotsMatch(snapshot, atCheckpoint))
                    {
                        throw new InvalidOperationException("完整回放在检查点帧与权威输出不一致。");
                    }
                    if (snapshot.Frame > atCheckpoint.Frame)
                    {
                        baseline.Add(snapshot);
                    }
                });

                // 比较完整输出而非仅比较哈希，包括对账哈希之外的一次性命中数据。
                int compared = 0;
                int hitFrames = 0;
                replay.ReplayInto(restored, snapshot =>
                {
                    if (compared >= baseline.Count || !DuelSnapshotsMatch(snapshot, baseline[compared]))
                    {
                        throw new InvalidOperationException($"检查点续算在第 {snapshot.Frame} 帧与完整回放不一致。");
                    }
                    compared++;
                    if (snapshot.FirstHit != 0 || snapshot.SecondHit != 0)
                    {
                        hitFrames++;
                    }
                });

                _recoveryCheckTrace["suffixFrames"] = expectedSuffix;
                _recoveryCheckTrace["comparedFrames"] = compared;
                _recoveryCheckTrace["suffixHitFrames"] = hitFrames;
                _recoveryCheckTrace["fullReplayHash"] = full.Snapshot.Hash;
                _recoveryCheckTrace["restoredReplayHash"] = restored.Snapshot.Hash;
                bool liveUnchanged = service.DuelId == id && DuelSnapshotsMatch(live, service.DuelSnapshot);
                _recoveryCheckTrace["liveUnchanged"] = liveUnchanged;
                if (compared != expectedSuffix || !DuelSnapshotsMatch(full.Snapshot, live) ||
                    !DuelSnapshotsMatch(restored.Snapshot, live) || !liveUnchanged)
                {
                    throw new InvalidOperationException("回放结束帧、续算帧数或现场状态不一致。");
                }
                _recoveryCheckPassed = true;
            }
            catch (Exception error)
            {
                _recoveryCheckError = error.Message;
            }
            finally
            {
                _recoveryCheckRunning = false;
            }
        }

        // 对照现有 FIFO 输出的 19 个整数，命中属于表现所需的瞬时输出，也要逐帧一致。
        private static bool DuelSnapshotsMatch(DuelSnapshot a, DuelSnapshot b)
        {
            return a.Frame == b.Frame && a.Result == b.Result && a.Hash == b.Hash &&
                a.FirstHit == b.FirstHit && a.SecondHit == b.SecondHit &&
                a.FirstX == b.FirstX && a.FirstY == b.FirstY && a.FirstHealth == b.FirstHealth &&
                a.FirstEnergy == b.FirstEnergy && a.FirstAction == b.FirstAction && a.FirstAge == b.FirstAge &&
                a.FirstFacing == b.FirstFacing &&
                a.SecondX == b.SecondX && a.SecondY == b.SecondY && a.SecondHealth == b.SecondHealth &&
                a.SecondEnergy == b.SecondEnergy && a.SecondAction == b.SecondAction && a.SecondAge == b.SecondAge &&
                a.SecondFacing == b.SecondFacing;
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
            GameObject stick = view.MoveStick.gameObject;
            var firstFinger = new PointerEventData(EventSystem.current) { pointerId = 11 };
            var secondFinger = new PointerEventData(EventSystem.current) { pointerId = 12 };
            var jabFinger = new PointerEventData(EventSystem.current) { pointerId = 14 };
            var service = Service;
            string id = service.DuelId;
            try
            {
                int start = service.DuelSnapshot.FirstX;
                PressDuelHudButton(stick, firstFinger, -.72f);
                PressDuelHudButton(stick, secondFinger, .72f);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstX < start &&
                    view.First.rectTransform.localScale.x < 0 && view.FirstAnimator.RunSpriteMotion, "左移与跑动");
                int oneReleasedAt = service.DuelSnapshot.Frame;
                ExecuteEvents.Execute(stick, secondFinger, ExecuteEvents.pointerUpHandler);
                // 多等三个逻辑帧，排除松手前已经提交的一帧左移被当作多指成功。
                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame >= oneReleasedAt + 3 &&
                    service.DuelSnapshot.FirstX < service.DuelPreviousSnapshot.FirstX, "另一根手指松开后仍左移");
                _controlCheckTrace["multiTouchReleasePreserved"] = true;
                int turned = service.DuelSnapshot.FirstX;
                DragDuelStick(stick, firstFinger, 1.25f);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstX > turned &&
                    view.First.rectTransform.localScale.x > 0, "同一触点反向拖拽");
                ExecuteEvents.Execute(stick, firstFinger, ExecuteEvents.pointerUpHandler);
                await WaitForDuelControl(id, () => service.DuelSnapshot.Frame > 0 &&
                    service.DuelSnapshot.FirstX == service.DuelPreviousSnapshot.FirstX, "松手停止");
                ClickDuelHudButton(view.Jab.gameObject, jabFinger);
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAction == 1, "首次普攻");
                int firstStart = service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge;
                int expectedSecondStart = firstStart + DuelConfig.ActionDuration(1) + 1;
                _controlCheckTrace["firstStart"] = firstStart;
                _controlCheckTrace["expectedSecondStart"] = expectedSecondStart;
                await WaitForDuelControl(id, () => service.DuelSnapshot.FirstAge == DuelConfig.ActionDuration(1) - 2, "恢复期输入窗口");
                ClickDuelHudButton(view.Jab.gameObject, jabFinger);
                _controlCheckTrace["duelHudRaycasts"] = 4;
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
                await WaitForDuelControl(id, () => (service.DuelSnapshot.FirstAction == 1 || service.DuelSnapshot.FirstAction == 5) &&
                    service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge > firstStart, "权威第二次普攻");
                int secondStart = service.DuelSnapshot.Frame - service.DuelSnapshot.FirstAge;
                _controlCheckTrace["secondStart"] = secondStart;
                int secondAction = service.DuelSnapshot.FirstAction;
                _controlCheckTrace["secondAction"] = secondAction;
                if (secondAction == 5 ? secondStart >= expectedSecondStart : secondStart != expectedSecondStart)
                {
                    throw new InvalidOperationException("决斗验收失败：续招时间与命中取消或正常恢复边界不一致。");
                }
                await WaitForDuelControl(id, () => view.FirstAnimator.LastObservedAttackStart == secondStart &&
                    view.FirstAnimator.AttackSpriteChanges > 0 && view.FirstAnimator.AttackFadeSamples > 0 &&
                    !view.FirstAnimator.IsPoseFadingForCheck, "第二次普攻实际换帧和图层过渡");
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
                    ExecuteEvents.Execute(stick, firstFinger, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(stick, secondFinger, ExecuteEvents.pointerUpHandler);
                    service.TrySetDuelNoDamage(false);
                }
                _controlCheckRunning = false;
            }
        }

        // 使用实际 Canvas 的屏幕坐标、EventSystem 和 GraphicRaycaster，保证 HUD 不被遮挡。
        private static void PressDuelHudButton(GameObject button, PointerEventData pointer, float horizontal = 0)
        {
            var rect = (RectTransform)button.transform;
            Canvas canvas = button.GetComponentInParent<Canvas>().rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            pointer.position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center + Vector2.right * (horizontal * rect.rect.width * .5f)));
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            GameObject hit = hits.Count == 0 ? null : hits[0].gameObject;
            if (hit == null || (hit != button && !hit.transform.IsChildOf(button.transform)))
            {
                throw new InvalidOperationException("屏幕触点没有命中决斗按钮：" + button.name);
            }
            pointer.pointerCurrentRaycast = hits[0];
            if (ExecuteEvents.ExecuteHierarchy(hit, pointer, ExecuteEvents.pointerDownHandler) != button)
            {
                throw new InvalidOperationException("决斗按钮没有处理触点按下：" + button.name);
            }
        }

        // 触摸普攻使用 Button 实际的 PointerClick 入口，不直接调用业务或模拟键盘 Submit。
        private static void ClickDuelHudButton(GameObject button, PointerEventData pointer)
        {
            PressDuelHudButton(button, pointer);
            ExecuteEvents.Execute(button, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button, pointer, ExecuteEvents.pointerClickHandler);
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
            _controlCheckTrace["attackFadeSamples"] = view.FirstAnimator.AttackFadeSamples;
            _controlCheckTrace["clockText"] = view.Clock.text;
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

        // GM 负责校验参数和回执，暂停状态由本局服务持有。
        private static string SetDuelComputerAiPaused(RevGMArgs args)
        {
            int paused = args.Int(0, 1);
            if (paused != 0 && paused != 1)
            {
                throw new RevGMUsageException("开关只接受 1（暂停）或 0（恢复）。");
            }
            if (!Service.TrySetDuelComputerAiPaused(paused == 1))
            {
                throw new RevGMUsageException("请在进行中的本地人机决斗使用此指令。");
            }
            return paused == 1 ? "敌人 AI 已暂停，已起手的动作会正常收完。" : "敌人 AI 已恢复。";
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

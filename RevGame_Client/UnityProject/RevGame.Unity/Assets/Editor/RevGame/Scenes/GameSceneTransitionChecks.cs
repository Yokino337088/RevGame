using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevGame.Bootstrap;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>
    /// 从真正首页开始的Play集成验收。不会先把全部图集读一遍再测UI，避免预热掩盖首次白图问题。
    /// 使用玩家同一个按钮/流程入口；检查冷启动、三次往返、分帧装配、遮挡、准备计时与两种失败回退。
    /// CLI/MCP 在内存返回结果，旧调用方可继续保存 Temp 证据。正式运行时不包含本类。
    /// </summary>
    public static class GameSceneTransitionChecks
    {
        /// <summary>最近一轮状态或最终断言文本。清理完成前始终为“运行中”。</summary>
        public static string LastResult { get; private set; } = "未运行";
        /// <summary>本轮已执行的业务断言数，接单时清零。</summary>
        private static int _assertions;
        /// <summary>本轮正常与故障路径的观察记录，供收尾后生成内存回执。</summary>
        private static readonly List<string> Report = new List<string>();

        /// <summary>本轮记录的第一章进场 ID，供自动化工单完成后继续查询资源。</summary>
        public static string LastBattleHandoffId { get; private set; }
        /// <summary>当前性能夹具的交接 ID，按每轮入场、退场顺序保存。</summary>
        private static readonly List<string> PerformanceRequestIds = new List<string>(6);
        /// <summary>工单结束时返回所有采样 ID，调用方可逐个复查原始摘要。</summary>
        public static string[] LastPerformanceRequestIds => PerformanceRequestIds.ToArray();

        /// <summary>旧资源夹具的接单入口，继续保存它原来使用的 Temp 证据。</summary>
        public static void Begin(bool testFailures = true)
        {
            RunAsync(testFailures, captureEvidence: true).Forget();
        }

        /// <summary>自动化桥固定调用的 Play 入口，执行两种故障并将最终观察留在 LastResult。</summary>
        public static void BeginHandoffCheck()
        {
            if (LastResult == "运行中")
            {
                throw new InvalidOperationException("场景验收正在运行");
            }
            RunAsync(testFailures: true, captureEvidence: false).Forget();
        }

        /// <summary>在真实 AB 模式跑一次第一章往返，输出进场 ID，并校验同一交接的前后资源快照。</summary>
        public static void BeginResourceTraceCheck()
        {
            if (!RevResBootstrap.UseABInEditor || LastResult == "运行中")
            {
                throw new InvalidOperationException("需要从大厅冷启动真实 AB，且没有其它场景验收在运行");
            }
            RunAsync(testFailures: false, captureEvidence: false, cycles: 1, checkResources: true).Forget();
        }

        /// <summary>在同一次真实 AB Play 中按相同操作进入第一章三次，分别保留首次和两次重复往返的指标。</summary>
        public static void BeginHandoffPerformanceCheck()
        {
            if (!RevResBootstrap.UseABInEditor || LastResult != "未运行")
            {
                throw new InvalidOperationException("性能对比需要从新的真实 AB Play 会话开始");
            }
            RunAsync(testFailures: false, captureEvidence: false, cycles: 3, checkResources: true, checkPerformance: true).Forget();
        }

        /// <summary>从冷启动大厅跑指定次数往返，可选检查资源、性能或失败恢复，最后恢复大厅和积分隔离。</summary>
        private static async RevTask RunAsync(bool testFailures, bool captureEvidence, int cycles = 3, bool checkResources = false,
            bool checkPerformance = false)
        {
            if (!Application.isPlaying || LastResult == "运行中")
            {
                return;
            }
            LastResult = "运行中";
            LastBattleHandoffId = null;
            PerformanceRequestIds.Clear();
            _assertions = 0;
            Report.Clear();
            string mode = RevResBootstrap.UseABInEditor ? "AB" : "Direct";
            GameFlow flow = null;
            IDisposable score = null;
            string outcome = string.Empty;
            try
            {
                flow = RevServices.GetRequired<GameFlow>();
                await WaitForFlowAsync(flow);
                score = ((BattleService)RevServices.GetRequired<IBattleService>()).BeginScorePreview();
                VerifyGmRegistry();
                Require(flow.Screen == GameScreen.Home && RevScene.CurrentName == GameFlow.StartSceneName, "启动交付StartScene首页");
                CheckImages("冷启动首页");
                Require(UnityEngine.Object.FindObjectOfType<BoardView>() == null, "大厅没有战斗棋盘实例");
                if (checkPerformance)
                {
                    // 首次入场前验证战斗资源确实没有由夹具加载，重复轮次沿用相同清理约束。
                    RequireLobbyCleanup(flow);
                }
                if (captureEvidence)
                {
                    ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Home.png");
                }
                await RevTask.Yield();

                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    // 模拟真正按钮点击，而不是绕开UI直接改模型。
                    RevUI.Get<HomePanel>().transform.Find("btnPlay").GetComponent<Button>().onClick.Invoke();
                    await WaitUntilAsync(() => RevUI.IsOpen<LevelSelectPanel>(), "选关面板打开");
                    CheckImages("选关" + cycle);
                    RevSceneHandoff previous = flow.LatestSceneHandoff;
                    RevUI.Get<LevelSelectPanel>().transform.Find("chapter1").GetComponent<Button>().onClick.Invoke();
                    Require(flow.IsTransitioning, "点击章节立即建立互斥锁");
                    RevSceneHandoff enter = RequireNewHandoff(flow, previous, GameFlow.BattleSceneName);
                    LastBattleHandoffId = enter.RequestId.ToString();
                    RevTask<RevSceneHandoffResult> enterResult = ObserveHandoffCompletionAsync(flow, enter);
                    RevTask<RevSceneHandoffResult> secondWaiter = ObserveHandoffCompletionAsync(flow, enter);
                    Require(!flow.EnterBattle(1) && !flow.ReturnHome(), "连点/过场期间返回不能并发启动另一条场景链");
                    Require(ReferenceEquals(flow.LatestSceneHandoff, enter) && !enter.IsCompleted, "拒绝连点保留原 ID 与等待者");
                    await ObserveTransitionAsync(flow, true, mode, cycle, captureEvidence);
                    RevSceneHandoffResult result = await enterResult;
                    Require(ReferenceEquals(result, await secondWaiter) && ReferenceEquals(result, await enter.WaitAsync()), "两个在途等待者与结案后查询得到同一终态");
                    RequireSuccess(result, GameFlow.BattleSceneName, GameScreen.Battle);
                    Require(flow.Screen == GameScreen.Battle && RevScene.CurrentName == GameFlow.BattleSceneName, "FSM与实际GameScene一致");
                    Require(string.IsNullOrEmpty(flow.LastError), "成功过场清除历史错误");
                    Require(UnityEngine.Object.FindObjectsOfType<GameBootstrap>().Length == 1, "常驻启动宿主只有一个");
                    Require(RevLogic.Get<BattleLogic>() != null && RevScope.IsActive(GameDomain.Battle), "战斗域与实际战斗逻辑已启动");
                    BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                    Require(board != null && board.IsReady && board.CellCount == 81, "战场交付时81格与棋子全部就绪");
                    Require(RevUI.Get<PreparePanel>().GetComponentsInChildren<CardItem>().Length == 6, "六张可见手牌完整");
                    IBattleService battle = RevServices.GetRequired<IBattleService>();
                    Require(battle.PreparationElapsed < 0.3f && !battle.PreparationGranted, "淡出结束后才启动准备三秒");
                    CheckImages("战斗" + cycle);

                    if (cycle == 0 || checkPerformance)
                    {
                        if (captureEvidence)
                        {
                            ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Battle.png");
                        }
                        // 第一章在遮挡交付后播放对白，业务刻意暂停计时。通过正式跳过按钮结束对白，再开始观察模拟时钟。
                        await WaitUntilAsync(() => flow.IsStoryReady && RevUI.IsOpen<StoryPanel>(), "等待第一章开场对白可操作");
                        Require(flow.StoryId == "chapter1" && flow.IsGameplayBlocked && !battle.PreparationGranted,
                            "开场对白期间保持准备计时门禁");
                        RevUI.Get<StoryPanel>().transform.Find("storyView/btnSkip").GetComponent<Button>().onClick.Invoke();
                        await WaitUntilAsync(() => !flow.IsGameplayBlocked, "等待对白退出与准备计时门禁解除");
                        // 准备由 Scaled 固定步推进，使用实际模拟阈值断言，墙钟只负责设定验收期限。
                        double started = Time.realtimeSinceStartupAsDouble;
                        while (!battle.PreparationGranted)
                        {
                            Require(Time.realtimeSinceStartupAsDouble - started < 10d, "准备增益十秒内到达，超时不能掩盖计时冻结");
                            Require(battle.PreparationElapsed < Balance.PrepDelaySeconds, "未发增益前模拟时间未越过阈值");
                            await RevTask.Yield();
                        }
                        Require(battle.PreparationGranted, "玩家看到战场三秒后正常发放准备增益");
                        Require(battle.PreparationElapsed >= Balance.PrepDelaySeconds, "准备增益由真实模拟阈值触发");
                        Report.Add("准备实际墙钟=" + (Time.realtimeSinceStartupAsDouble - started).ToString("F3") + "秒，模拟=" + battle.PreparationElapsed);
                    }
                    Require(flow.ReturnHome(), "接受返回大厅");
                    RevSceneHandoff exit = RequireNewHandoff(flow, enter, GameFlow.StartSceneName);
                    RevTask<RevSceneHandoffResult> exitResult = ObserveHandoffCompletionAsync(flow, exit);
                    await ObserveTransitionAsync(flow, false, mode, cycle, captureEvidence);
                    RequireSuccess(await exitResult, GameFlow.StartSceneName, GameScreen.Home);
                    RequireLobbyCleanup(flow);
                    if (checkResources)
                    {
                        RequireResourceTrace(flow, enter, exit);
                    }
                    if (checkPerformance)
                    {
                        // 两个请求使用各自的真实阶段；回执在离开采样窗后生成。
                        RequirePerformance(flow, enter, cycle, "进入");
                        RequirePerformance(flow, exit, cycle, "返回");
                    }
                    Report.Add("往返" + (cycle + 1) + "：" + enter.RequestId + " → " + exit.RequestId);
                    CheckImages("返回首页" + cycle);
                }
                if (testFailures)
                {
                    foreach (string stage in new[] { "before-scene", "after-scene" })
                    {
                        RevSceneHandoff previous = flow.LatestSceneHandoff;
                        Require(flow.OpenLevels() && flow.ArmDevelopmentFailure(stage) && flow.EnterBattle(1), "发起一次性故障" + stage);
                        RevSceneHandoff failed = RequireNewHandoff(flow, previous, GameFlow.BattleSceneName);
                        RevTask<RevSceneHandoffResult> failureWait = ObserveHandoffCompletionAsync(flow, failed);
                        await WaitUntilAsync(() => failed.IsCompleted, "等待故障终态");
                        RevSceneHandoffResult failure = await failureWait;
                        bool beforeScene = stage == "before-scene";
                        Require(failure.Status == RevSceneHandoffStatus.Failed && failure.CleanupSucceeded, "失败终态及本次预热清理已记录");
                        Require(failure.Stage == (beforeScene ? RevSceneHandoffStage.LoadingScene : RevSceneHandoffStage.PreparingGameplay)
                            && failure.ReasonCode == (beforeScene ? "scene-load-failed" : "gameplay-prepare-failed")
                            && failure.Detail.Contains(stage), "失败阶段和稳定原因码与注入位置相符");
                        Require(failure.ActiveScene == (beforeScene ? GameFlow.StartSceneName : GameFlow.BattleSceneName)
                            && failure.ActualState == GameScreen.LevelSelect.ToString(), "记录切场前后的实际场景，业务尚未切到 Battle");
                        Require(flow.LastError.Contains(stage) && flow.IsGameplayBlocked, "失败原因可见且玩法保持遮挡");
                        LoadingPanel cover = RevUI.Get<LoadingPanel>();
                        Require(cover != null && cover.transform.Find("btnReturn").gameObject.activeSelf, "错误面板提供返回入口");
                        cover.transform.Find("btnReturn").GetComponent<Button>().onClick.Invoke();
                        RevSceneHandoff recovery = RequireNewHandoff(flow, failed, GameFlow.StartSceneName);
                        RevTask<RevSceneHandoffResult> recoveryWait = ObserveHandoffCompletionAsync(flow, recovery);
                        await WaitUntilAsync(() => recovery.IsCompleted, "等待恢复终态");
                        RequireSuccess(await recoveryWait, GameFlow.StartSceneName, GameScreen.Home);
                        RequireLobbyCleanup(flow);
                        Require(flow.TryGetSceneHandoff(failed.RequestId, out RevSceneHandoff original)
                            && ReferenceEquals(original.Result, failure), "回退有独立 ID 且不会覆盖故障终态");
                        Require(!flow.IsGameplayBlocked && string.IsNullOrEmpty(flow.LastError), "恢复后解除遮挡与错误");
                        Report.Add(stage + "：" + failure.ActiveScene + "/" + failure.ActualState + "，" + failure.ReasonCode
                            + "，故障 " + failed.RequestId + " → 恢复 " + recovery.RequestId);
                        CheckImages("故障恢复" + stage);
                    }
                }

                outcome = "通过：" + mode + "，" + _assertions + "条断言；冷启动UI、" + cycles + "次场景往返、遮挡/分帧/准备计时"
                    + (testFailures ? "、2次失败回退" : "") + (checkPerformance ? "、冷进与两次重入的分段采样" : "") + "\n" + string.Join("\n", Report);
            }
            catch (Exception error)
            {
                outcome = "失败：" + error;
                RevLog.Exception(error, "场景集成验收失败", "RevGame");
            }
            finally
            {
                if (Application.isPlaying && flow != null)
                {
                    try
                    {
                        // 断言若在故障遮挡打开时失败，仍走错误面板相同的返回逻辑收尾。
                        await WaitUntilAsync(() => !flow.IsTransitioning, "等待夹具收尾");
                        if (RevUI.IsOpen<LoadingPanel>() && !string.IsNullOrEmpty(flow.LastError))
                        {
                            flow.DismissLoadingFailure();
                        }
                        await ReturnHomeAsync(flow);
                    }
                    catch (Exception error)
                    {
                        outcome = "收尾失败：" + error.Message + "\n" + outcome;
                    }
                }
                score?.Dispose();
            }
            LastResult = outcome;
            if (captureEvidence)
            {
                File.WriteAllText("Temp/SceneTransitionChecks-" + mode + ".txt", LastResult);
            }
        }

        /// <summary>核对按 ID 取得的是已冻结的阶段结果，验证时长闭合、样本完整和单位有效。</summary>
        private static void RequirePerformance(GameFlow flow, RevSceneHandoff handoff, int cycle, string direction)
        {
            Require(flow.TryGetSceneHandoff(handoff.RequestId, out RevSceneHandoff found) && ReferenceEquals(found, handoff),
                "性能查询定位真实交接 ID");
            RevSceneHandoffPerformance profile = found.Performance;
            Require(profile.SamplesEnabled && profile.Stages != null && profile.TotalDurationMs > 0,
                "交接终态前已冻结性能摘要");
            var stages = profile.Stages.ToDictionary(segment => segment.Stage);
            Require(stages.Count == 6 && Math.Abs(stages.Values.Sum(segment => segment.DurationMs) - profile.TotalDurationMs.Value) < 0.01,
                "各阶段无重叠且耗时与整次交接一致");
            foreach (RevSceneHandoffStage stage in new[]
            {
                RevSceneHandoffStage.PreparingResources,
                RevSceneHandoffStage.LoadingScene,
                RevSceneHandoffStage.PreparingGameplay,
                RevSceneHandoffStage.CleaningUp
            })
            {
                Require(stages.TryGetValue(stage, out RevSceneHandoffStagePerformance segment) && segment.DurationMs > 0
                    && segment.MemorySamples > 0 && segment.PeakAllocatedBytes > 0 && segment.DroppedFrameSamples == 0,
                    stage + "记录时长和有效内存峰值，帧缓冲无丢样");
                Require(segment.FrameSamples == 0
                    ? !segment.FrameP95Ms.HasValue
                    : segment.FrameP50Ms > 0 && segment.FrameP50Ms <= segment.FrameP95Ms && segment.FrameP95Ms <= segment.FrameP99Ms
                        && segment.FrameP99Ms <= segment.FrameMaxMs,
                    stage + "帧间隔分位数与样本相符");
            }
            PerformanceRequestIds.Add(handoff.RequestId.ToString());
            Report.Add((cycle == 0 ? "冷进轮次" : "重入" + cycle) + direction + "总时长="
                + profile.TotalDurationMs.Value.ToString("F1") + "ms，交接ID=" + handoff.RequestId);
        }

        /// <summary>验证入场 ID 在退场后仍能定位来源、实际持有者、释放情况及大厅保留的共享资源。</summary>
        private static void RequireResourceTrace(GameFlow flow, RevSceneHandoff enter, RevSceneHandoff exit)
        {
            Require(flow.TryGetSceneHandoff(enter.RequestId, out RevSceneHandoff queried) && ReferenceEquals(enter, queried),
                "退场后仍可用入场 ID 查询");
            RevSceneHandoffResources trace = queried.Resources;
            var ready = trace.AtReady.ToDictionary(row => row.Name, StringComparer.Ordinal);
            var left = trace.AfterExit.ToDictionary(row => row.Name, StringComparer.Ordinal);
            Require(trace.ExitRequestId == exit.RequestId && ready.Count == 17 && left.Keys.ToHashSet().SetEquals(ready.Keys),
                "入场的17份显式依赖均可在同一次退场快照定位");
            Require(ready.Values.All(row => row.BlocksFirstFrame && row.Introducer.Length > 0),
                "当前逐项等待的预热资源均有来源和实际首屏门禁");
            Require(ready.Values.Concat(left.Values).All(row => row.Holders.Values.Sum() + row.UnattributedCount == row.ReferenceCount),
                "具名引用与未具名引用合计等于资源系统实际计数");
            Require(ready["BoardCell"].Introducer == "SceneResourceWarmup" && ready["BoardCell"].Holders.Keys.Any(name => name.StartsWith("RevPool/", StringComparison.Ordinal)),
                "棋盘格由热身引入，入场后由棋盘对象池持有");
            Require(ready["PreparePanel"].Holders.ContainsKey("RevUI/PreparePanel"), "准备面板持有自己的预制体引用");
            Require(ready["PieceFrames"].Holders.ContainsKey(nameof(GameSpriteAtlasService)), "战斗图集由图集服务持有");
            RevSceneResourceSnapshot background = ready.Values.Single(row => row.Root == RevResPath.Material_Background);
            Require(background.Introducer == "GamePresentation.ChapterBackground" && background.Holders.ContainsKey(background.Introducer),
                "第一章背景材质由实际场景表现引入并持有");
            Require(ready.Values.All(row => !row.Holders.ContainsKey("SceneResourceWarmup")), "首屏交付后已归还全部临时预热引用");

            foreach (string name in new[] { "BoardCell", "PreparePanel", "PieceFrames", GameSpriteAtlasService.TutorialAtlasName, background.Name })
            {
                Require(ready[name].ReferenceCount > 0 && ready[name].HoldsBundleReference
                    && left[name].ReferenceCount == 0 && !left[name].HoldsBundleReference && left[name].Holders.Count == 0,
                    name + "入场持有真实AB引用，退场完成后已归还");
            }
            Require(left["UIBattle"].ReferenceCount > 0 && left["UIBattle"].HoldsBundleReference
                && left["UIBattle"].Holders.ContainsKey(nameof(GameSpriteAtlasService)), "大厅继续持有共享图集，不将其记为已释放");
            Report.Add("资源追溯：" + ready.Count + "份，退场后" + left.Values.Count(row => row.ReferenceCount == 0 && !row.HoldsBundleReference)
                + "份已释放，共享 UIBattle 仍由 GameSpriteAtlasService 持有");
        }

        /// <summary>接单时立即取句柄，确认仍在进行且按 ID 查到的是同一次请求。</summary>
        private static RevSceneHandoff RequireNewHandoff(GameFlow flow, RevSceneHandoff previous, string targetScene)
        {
            RevSceneHandoff handoff = flow.LatestSceneHandoff;
            Require(handoff != null && handoff.RequestId != Guid.Empty && !ReferenceEquals(previous, handoff)
                && !handoff.IsCompleted && handoff.SourceScene == RevScene.CurrentName && handoff.TargetScene == targetScene,
                "接受新请求时记录唯一 ID、来源和目标");
            Require(flow.TryGetSceneHandoff(handoff.RequestId, out RevSceneHandoff queried) && ReferenceEquals(handoff, queried),
                "在途请求可按 ID 查询");
            return handoff;
        }

        /// <summary>在请求仍进行时登记真实等待方，验证恢复执行的同一刻已解除互斥且终态可查询。</summary>
        private static async RevTask<RevSceneHandoffResult> ObserveHandoffCompletionAsync(GameFlow flow, RevSceneHandoff handoff)
        {
            RevSceneHandoffResult result = await handoff.WaitAsync();
            Require(!flow.IsTransitioning && handoff.IsCompleted && result.RequestId == handoff.RequestId
                && flow.TryGetSceneHandoff(handoff.RequestId, out RevSceneHandoff queried) && ReferenceEquals(result, queried.Result),
                "等待方恢复时结果已发布且互斥已释放");
            return result;
        }

        /// <summary>同时核对玩法交付、实际场景、业务状态和交接清理。</summary>
        private static void RequireSuccess(RevSceneHandoffResult result, string sceneName, GameScreen screen)
        {
            Require(result.Status == RevSceneHandoffStatus.Succeeded && result.Stage == RevSceneHandoffStage.Revealing
                && result.ActiveScene == sceneName && result.ActualState == screen.ToString()
                && result.CleanupSucceeded && string.IsNullOrEmpty(result.ReasonCode), "成功终态与场景、业务和清理一致");
        }

        /// <summary>正常返回和故障返回共用后置断言，核对战斗域、视图、资源引用和实际 AB。</summary>
        private static void RequireLobbyCleanup(GameFlow flow)
        {
            Require(RevScene.CurrentName == GameFlow.StartSceneName && flow.Screen == GameScreen.Home, "返回StartScene与Home");
            Require(RevLogic.Get<BattleLogic>() == null && RevLogic.Get<CampaignYayaReactionLogic>() == null
                && !RevScope.IsActive(GameDomain.Battle), "退出无战斗Logic/域残留");
            Require(UnityEngine.Object.FindObjectOfType<BoardView>() == null, "旧战斗场景已经卸载");
            Require(!RevUI.IsOpen<PreparePanel>() && !RevUI.IsOpen<BattleHudPanel>() && !RevUI.IsOpen<LoadingPanel>(), "返回不残留战斗或过场面板");
            Require(UnityEngine.Object.FindObjectsOfType<BattleEffectView>().Length == 0, "返回不残留活动弹道飘字");
            Require(RevResManager.GetRefCount(RevResPath.Atlases, "PieceFrames") == 0, "战斗图集服务租约释放");
            Require(RevResManager.GetRefCount(RevResPath.Board_Tile, "BoardCell") == 0
                && RevResManager.GetRefCount(RevResPath.Pieces_Unit, "SlimePiece") == 0
                && RevResManager.GetRefCount(RevResPath.Effects_Combat, "BattleEffect") == 0, "退出无棋盘/棋子/反馈加载引用残留");

            if (RevResBootstrap.UseABInEditor)
            {
                string[] bundles = AssetBundle.GetAllLoadedAssetBundles().Select(b => b.name).ToArray();
                Require(!bundles.Contains("battle_units") && !bundles.Contains("battle_feedback") && !bundles.Contains("battle_board"), "战斗专用AB已实际卸载");
            }
        }

        /// <summary>按帧观察遮挡完整性；MCP/Editor本身会影响帧耗时，这里只证明跨帧装配，不冒充手机性能数据。</summary>
        private static async RevTask ObserveTransitionAsync(GameFlow flow, bool entering, string mode, int cycle, bool captureEvidence)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            float previous = 0f;
            int frames = 0;
            int partialBoardFrames = 0;
            int maximumCellDelta = 0;
            int previousCells = 0;
            bool captured = false;
            bool observedCover = false;
            RevSceneHandoff handoff = flow.LatestSceneHandoff;
            while (flow.IsTransitioning)
            {
                Require(Time.realtimeSinceStartup < deadline, "过场在超时前完成");
                Require(flow.LoadProgress + 0.0001f >= previous, "真实进度不倒退");
                previous = flow.LoadProgress;
                LoadingPanel cover = RevUI.Get<LoadingPanel>();
                if (cover == null)
                {
                    // Covering 可以在原页锁定输入后异步装配遮罩。此时场景和业务都必须保持来源。
                    Require(!observedCover && handoff.Stage == RevSceneHandoffStage.Covering
                        && RevScene.CurrentName == handoff.SourceScene && flow.Screen.ToString() == handoff.SourceState
                        && flow.IsGameplayBlocked, "只允许在原场景已锁输入的遮罩装配阶段等待面板");
                }
                else
                {
                    observedCover = true;
                }

                if (entering && flow.Screen == GameScreen.Battle)
                {
                    Require(RevServices.GetRequired<IBattleService>().PreparationElapsed < 0.001f, "遮挡期间准备时间冻结");
                    BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();

                    if (board != null)
                    {
                        if (board.CellCount > 0 && board.CellCount < 81)
                        {
                            partialBoardFrames++;
                        }
                        maximumCellDelta = Mathf.Max(maximumCellDelta, board.CellCount - previousCells);
                        previousCells = board.CellCount;

                        if (board.CellCount < 81)
                        {
                            Require(cover != null && cover.GetComponent<CanvasGroup>().alpha >= 0.99f, "半成品棋盘绝不透出");
                        }
                    }
                }
                if (captureEvidence && !captured && entering && flow.LoadProgress > 0.66f && cycle == 0)
                {
                    captured = true;
                    ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Loading.png");
                }
                frames++;
                await RevTask.Yield();
            }
            Require(observedCover && !RevUI.IsOpen<LoadingPanel>(), "实际显示过遮罩，成功后过场面板已关闭");

            if (entering)
            {
                Require(partialBoardFrames > 1 && maximumCellDelta <= 4, "81格实际分帧创建，每帧最多4格");
            }
            Report.Add((entering ? "进入" : "返回") + cycle + "：观察" + frames + "帧，分帧棋盘" + partialBoardFrames + "帧，最大每帧新增格" + maximumCellDelta);
        }
        private static void CheckImages(string step)
        {
            Image[] images = UnityEngine.Object.FindObjectsOfType<Image>().Where(i => i.sprite != null).ToArray();
            Require(images.Length >= 2, step + "存在真实UI图片");
            foreach (Image image in images)
            {
                Require(image.sprite.texture != null && image.mainTexture != Texture2D.whiteTexture, step + "/" + image.name + "有实际图集像素");
                Require(image.materialForRendering.shader.isSupported && image.canvasRenderer.materialCount > 0, step + "/" + image.name + "材质已提交且Shader支持");

                if (RevResBootstrap.UseABInEditor)
                {
                    Require(image.sprite.name == "UISprite" || image.sprite.packed, step + "/" + image.name + "真实AB图集绑定");
                }
            }
            Report.Add(step + "：" + images.Length + "个可见Image材质/纹理通过");
        }
        /// <summary>通过实际账本验证幂等与注销，恢复本轮开始时已经注册的运行期与编辑器 GM。</summary>
        private static void VerifyGmRegistry()
        {
            string[] before = RevGM.Commands.Where(c => c.Name.StartsWith("RevGame/")).Select(c => c.Name).OrderBy(n => n).ToArray();
            Require(before.Length >= 59, "统一注册入口包含旧命令与完整玩家操作");
            GameGmCommands.RegisterAll();
            Require(RevGM.Commands.Count(c => c.Name.StartsWith("RevGame/")) == before.Length, "重复注册幂等");
            try
            {
                GameGmCommands.UnregisterAll();
                Require(!RevGM.Commands.Any(c => c.Name.StartsWith("RevGame/")), "注销无Boss/输入/自动化命令残留");
            }
            finally
            {
                GameGmCommands.RegisterAll();
                // 编辑器数值 GM 在 Play 初始化时单独装配，回滚测试也要按原来的两个入口恢复。
                if (before.Contains("RevGame/数值测试开始"))
                {
                    GameGmCommands.RegisterEditorBalanceCommands();
                }
            }
            Require(before.SequenceEqual(RevGM.Commands.Where(c => c.Name.StartsWith("RevGame/")).Select(c => c.Name).OrderBy(n => n)), "重新注册恢复相同完整清单");
            foreach (string name in new[] { "选择眼石", "故障返回", "玩家对战", "退出游戏", "玩家操作目录", "流程状态", "选牌", "落子", "点格子", "攻击范围", "选奖励" })
            {
                Require(RevGM.Commands.Any(c => c.Name == "RevGame/" + name), "玩家业务可由AI调用：" + name);
            }
        }
        /// <summary>既有Play夹具共用它，不再假设EnterBattle返回true就能立即取得BattleLogic。</summary>
        public static async RevTask WaitForFlowAsync(GameFlow flow)
        {
            await WaitUntilAsync(() => !flow.IsTransitioning && flow.Screen != GameScreen.Exit, "等待完整场景交付");

            if (!string.IsNullOrEmpty(flow.LastError))
            {
                throw new InvalidOperationException(flow.LastError);
            }
        }

        public static async RevTask ReturnHomeAsync(GameFlow flow)
        {
            await WaitForFlowAsync(flow);

            if (flow.Screen != GameScreen.Home)
            {
                if (!flow.ReturnHome())
                {
                    throw new InvalidOperationException(flow.LastError);
                }
                await WaitForFlowAsync(flow);
            }
        }

        private static async RevTask WaitUntilAsync(Func<bool> predicate, string reason)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new TimeoutException(reason);
                }
                await RevTask.Yield();
            }
            await RevTask.Yield(); // 让成功那帧的Canvas提交完成，检查对象实际用于渲染而不是仅刚构造。
        }
        private static void Require(bool condition, string message)
        {
            _assertions++;

            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

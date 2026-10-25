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
    /// 只写Temp验收记录，正式运行时不包含本类。
    /// </summary>
    public static class GameSceneTransitionChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private static int _assertions;
        private static readonly List<string> Report = new List<string>();

        public static async void Begin(bool testFailures = true)
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            _assertions = 0;
            Report.Clear();
            string mode = RevResBootstrap.UseABInEditor ? "AB" : "Direct";
            GameFlow flow = null;
            IDisposable score = null;
            try
            {
                flow = RevServices.GetRequired<GameFlow>();
                await WaitForFlowAsync(flow);
                score = ((BattleService)RevServices.GetRequired<IBattleService>()).BeginScorePreview();
                VerifyGmRegistry();
                Require(flow.Screen == GameScreen.Home && RevScene.CurrentName == GameFlow.StartSceneName, "启动交付StartScene首页");
                CheckImages("冷启动首页");
                Require(UnityEngine.Object.FindObjectOfType<BoardView>() == null, "大厅没有战斗棋盘实例");
                ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Home.png");
                await RevTask.Yield();

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    // 模拟真正按钮点击，而不是绕开UI直接改模型。
                    RevUI.Get<HomePanel>().transform.Find("btnPlay").GetComponent<Button>().onClick.Invoke();
                    await WaitUntilAsync(() => RevUI.IsOpen<LevelSelectPanel>(), "选关面板打开");
                    CheckImages("选关" + cycle);
                    RevUI.Get<LevelSelectPanel>().transform.Find("chapter1").GetComponent<Button>().onClick.Invoke();
                    Require(flow.IsTransitioning, "点击章节立即建立互斥锁");
                    Require(!flow.EnterBattle(1) && !flow.ReturnHome(), "连点/过场期间返回不能并发启动另一条场景链");
                    await ObserveTransitionAsync(flow, true, mode, cycle);
                    Require(flow.Screen == GameScreen.Battle && RevScene.CurrentName == GameFlow.BattleSceneName, "FSM与实际GameScene一致");
                    Require(string.IsNullOrEmpty(flow.LastError), "成功过场清除历史错误");
                    Require(UnityEngine.Object.FindObjectsOfType<GameBootstrap>().Length == 1, "常驻启动宿主只有一个");
                    Require(RevLogic.Count == 1 && RevScope.IsActive(GameDomain.Battle), "恰好一个战斗域Logic");
                    BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                    Require(board != null && board.IsReady && board.CellCount == 81, "战场交付时81格与棋子全部就绪");
                    Require(RevUI.Get<PreparePanel>().GetComponentsInChildren<CardItem>().Length == 6, "六张可见手牌完整");
                    IBattleService battle = RevServices.GetRequired<IBattleService>();
                    Require(battle.PreparationElapsed < 0.3f && !battle.PreparationGranted, "淡出结束后才启动准备三秒");
                    CheckImages("战斗" + cycle);
                    if (cycle == 0)
                    {
                        ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Battle.png");
                        // 准备由Scaled固定步推进；Editor长帧/截图会让墙钟与模拟时钟不同，不能用3250ms猜完成。
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
                    await ObserveTransitionAsync(flow, false, mode, cycle);
                    Require(RevScene.CurrentName == GameFlow.StartSceneName && flow.Screen == GameScreen.Home, "返回StartScene与Home");
                    Require(RevLogic.Count == 0 && !RevScope.IsActive(GameDomain.Battle), "退出无战斗Logic/域残留");
                    Require(UnityEngine.Object.FindObjectOfType<BoardView>() == null, "旧战斗场景已经卸载");
                    Require(!RevUI.IsOpen<PreparePanel>() && !RevUI.IsOpen<BattleHudPanel>(), "返回不残留战斗面板");
                    Require(UnityEngine.Object.FindObjectsOfType<BattleEffectView>().Length == 0, "返回不残留活动弹道飘字");
                    Require(RevResManager.GetRefCount(RevResPath.Atlases, "PieceFrames") == 0, "战斗图集服务租约释放");
                    Require(RevResManager.GetRefCount(RevResPath.Board_Tile, "BoardCell") == 0
                        && RevResManager.GetRefCount(RevResPath.Pieces_Unit, "SlimePiece") == 0
                        && RevResManager.GetRefCount(RevResPath.Effects_Combat, "BattleEffect") == 0,
                        "退出无棋盘/棋子/反馈冷加载请求引用残留");
                    string[] bundles = AssetBundle.GetAllLoadedAssetBundles().Select(b => b.name).ToArray();
                    if (RevResBootstrap.UseABInEditor)
                    {
                        Require(!bundles.Contains("battle_units") && !bundles.Contains("battle_feedback") && !bundles.Contains("battle_board"), "战斗专用AB已实际卸载");
                    }
                    Report.Add("返回" + cycle + "已加载包" + bundles.Length + "：" + string.Join(",", bundles));
                    CheckImages("返回首页" + cycle);
                }

                if (testFailures)
                {
                    foreach (string stage in new[] { "before-scene", "after-scene" })
                    {
                        Require(flow.OpenLevels() && flow.ArmDevelopmentFailure(stage) && flow.EnterBattle(1), "发起一次性故障" + stage);
                        await WaitUntilAsync(() => !flow.IsTransitioning, "等待故障结果");
                        Require(flow.LastError.Contains(stage) && flow.IsGameplayBlocked, "失败原因可见且玩法保持遮挡");
                        LoadingPanel cover = RevUI.Get<LoadingPanel>();
                        Require(cover != null && cover.transform.Find("btnReturn").gameObject.activeSelf, "错误面板提供返回入口");
                        cover.transform.Find("btnReturn").GetComponent<Button>().onClick.Invoke();
                        await WaitForFlowAsync(flow);
                        Require(flow.Screen == GameScreen.Home && RevScene.CurrentName == GameFlow.StartSceneName, "失败返回完整大厅");
                        Require(!flow.IsGameplayBlocked && string.IsNullOrEmpty(flow.LastError), "恢复后解除遮挡与错误");
                        CheckImages("故障恢复" + stage);
                    }
                }
                LastResult = "通过：" + mode + "，" + _assertions + "条断言；冷启动UI、3次场景往返、遮挡/分帧/准备计时"
                    + (testFailures ? "、2次失败回退" : "") + "\n" + string.Join("\n", Report);
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error;
                RevLog.Exception(error, "场景集成验收失败", "RevGame");
            }
            finally
            {
                if (Application.isPlaying && flow != null)
                {
                    try { await ReturnHomeAsync(flow); }
                    catch (Exception error) { LastResult += "\n收尾失败：" + error.Message; }
                }
                score?.Dispose();
            }
            File.WriteAllText("Temp/SceneTransitionChecks-" + mode + ".txt", LastResult);
        }

        /// <summary>按帧观察遮挡完整性；MCP/Editor本身会影响帧耗时，这里只证明跨帧装配，不冒充手机性能数据。</summary>
        private static async RevTask ObserveTransitionAsync(GameFlow flow, bool entering, string mode, int cycle)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            float previous = 0f;
            int frames = 0;
            int partialBoardFrames = 0;
            int maximumCellDelta = 0;
            int previousCells = 0;
            bool captured = false;
            while (flow.IsTransitioning)
            {
                Require(Time.realtimeSinceStartup < deadline, "过场在超时前完成");
                Require(flow.LoadProgress + 0.0001f >= previous, "真实进度不倒退");
                previous = flow.LoadProgress;
                LoadingPanel cover = RevUI.Get<LoadingPanel>();
                Require(cover != null, "全过程保留加载面板");
                if (entering && flow.Screen == GameScreen.Battle)
                {
                    Require(RevServices.GetRequired<IBattleService>().PreparationElapsed < 0.001f, "遮挡期间准备时间冻结");
                    BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                    if (board != null)
                    {
                        if (board.CellCount > 0 && board.CellCount < 81) { partialBoardFrames++; }
                        maximumCellDelta = Mathf.Max(maximumCellDelta, board.CellCount - previousCells);
                        previousCells = board.CellCount;
                        if (board.CellCount < 81) { Require(cover.GetComponent<CanvasGroup>().alpha >= 0.99f, "半成品棋盘绝不透出"); }
                    }
                }
                if (!captured && entering && flow.LoadProgress > 0.66f && cycle == 0)
                {
                    captured = true;
                    ScreenCapture.CaptureScreenshot("Temp/Scene-" + mode + "-Loading.png");
                }
                frames++;
                await RevTask.Yield();
            }
            Require(!RevUI.IsOpen<LoadingPanel>(), "成功后过场面板已关闭");
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
                if (RevResBootstrap.UseABInEditor) { Require(image.sprite.name == "UISprite" || image.sprite.packed, step + "/" + image.name + "真实AB图集绑定"); }
            }
            Report.Add(step + "：" + images.Length + "个可见Image材质/纹理通过");
        }

        /// <summary>注册/注销用同一实际账本，验证幂等与无遗漏；保留其它模块命令，不能Clear全局注册表。</summary>
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
            finally { GameGmCommands.RegisterAll(); }
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
            if (!string.IsNullOrEmpty(flow.LastError)) { throw new InvalidOperationException(flow.LastError); }
        }

        public static async RevTask ReturnHomeAsync(GameFlow flow)
        {
            await WaitForFlowAsync(flow);
            if (flow.Screen != GameScreen.Home)
            {
                if (!flow.ReturnHome()) { throw new InvalidOperationException(flow.LastError); }
                await WaitForFlowAsync(flow);
            }
        }

        private static async RevTask WaitUntilAsync(Func<bool> predicate, string reason)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException(reason); }
                await RevTask.Yield();
            }
            await RevTask.Yield(); // 让成功那帧的Canvas提交完成，检查对象实际用于渲染而不是仅刚构造。
        }

        private static void Require(bool condition, string message)
        {
            _assertions++;
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}

using System;
using System.IO;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>九棋种隔离图形验收。监听真实命中与形状事件，每种连续截图，finally经正式流程释放战斗域。</summary>
    public static class GamePieceAttackVisualChecks
    {
        /// <summary>唯一作业状态；仅本类写，GM查询只序列化，不再次触发造局。</summary>
        private static Result _result = new Result();
        /// <summary>机读证据留在Unity忽略目录，交付报告另行挑选并压缩真实画面。</summary>
        private const string Output = "Temp/PieceAttackVerification";

        [Serializable]
        private sealed class Result
        {
            public string stage = "idle", error = "", kind = "", directory = Output;
            public int completed, assertions, frames;
            public bool cleaned;
        }

        public static string State()
        {
            return JsonUtility.ToJson(_result);
        }

        /// <summary>只允许隔离Play且无同类作业；启动后由状态查询等待completed，accepted不是通过。</summary>
        public static string Begin()
        {
            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated || _result.stage == "running")
            {
                throw new InvalidOperationException("需要空闲的隔离Play");
            }
            _result = new Result { stage = "running" };
            Run().Forget();
            return State();
        }

        private static async RevTask Run()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
            object owner = new object();
            IDisposable score = null;
            try
            {
                // 隔离Play由StoryAutomationCommands持有积分租约；只在没有租约时自行申请，只释放自己申请的那份。
                // 申请也放在try内，启动失败必须写入状态，不能让异步异常把作业永久留在running。
                if (!battle.IsScorePreviewActive)
                {
                    score = battle.BeginScorePreview();
                }
                Directory.CreateDirectory(Output);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Require(flow.OpenLevels() && flow.EnterBattle(1), "正式流程接受进入战斗");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
                {
                    _result.kind = kind.ToString();
                    int hits = 0, patterns = 0, patternPeak = 0;
                    Require(battle.LoadPieceAttackShowcase(kind), "单棋种初始输入占格成功");
                    RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
                    {
                        if (info.SourceKind == kind && info.TargetSide == GameSide.Enemy)
                        {
                            hits++;
                        }
                    }, owner: owner);
                    RevEvent.AddEventListener<PiecePatternInfo>(Events.PiecePattern, info =>
                    {
                        if (info.Kind == kind && info.Side == GameSide.Player)
                        {
                            patterns++;
                        }
                    }, owner: owner);
                    double deadline = Time.realtimeSinceStartupAsDouble + 20;
                    while (!battle.PreparationGranted)
                    {
                        if (Time.realtimeSinceStartupAsDouble > deadline)
                        {
                            throw new TimeoutException("等待真实准备阶段超时");
                        }
                        await RevTask.Yield();
                    }
                    Require(RevLogic.Get<BattleLogic>().BeginBattle(), "真实开战入口接受请求");
                    for (int frame = 0; frame < 16; frame++)
                    {
                        await RevTask.Delay(150);
                        BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                        Require(board != null && board.ActiveMeleeCount <= BoardView.MaxMeleeEffects && board.ActiveProjectileCount <= BoardView.MaxProjectiles, "范围与弹道遵守池预算");
                        patternPeak = Math.Max(patternPeak, board.ActivePatternCount);
                        ScreenCapture.CaptureScreenshot(Output + "/" + kind + "-" + frame.ToString("D2") + ".png");
                        _result.frames++;
                    }
                    Require(hits > 0 && patterns > 0 && patternPeak > 0, kind + "必须同时有真实伤害、范围事件和实际启用的范围Renderer");
                    RevEvent.RemoveAllByOwner(owner);
                    _result.completed++;
                }
                await RevTask.Delay(200);
                _result.stage = "completed";
            }
            catch (Exception error)
            {
                _result.stage = "failed";
                _result.error = error.ToString();
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
                try
                {
                    await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                    _result.cleaned = flow.Screen == GameScreen.Home && !flow.IsTransitioning && UnityEngine.Object.FindObjectOfType<BoardView>() == null;
                }
                catch (Exception error)
                {
                    _result.stage = "failed";
                    _result.error += "\n清理失败：" + error;
                }
                score?.Dispose();
                File.WriteAllText(Output + "/result.json", State());
            }
        }

        /// <summary>所有结论来自已执行条件；失败抛出首个原因，不降级为警告继续报通过。</summary>
        private static void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw new InvalidOperationException(reason);
            }
            _result.assertions++;
        }
    }
}

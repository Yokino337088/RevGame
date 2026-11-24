#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using Revolution;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Pvp
{
    public static partial class PvpAutomation
    {
        /// <summary>登记围棋画面和本地策略验收能力，由 PvP 自动化所有者统一撤销。</summary>
        private static void RegisterPresentation()
        {
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
            RegisterAndroidTacticalTrial();
#endif
            RevRuntimeAutomation.Register("pvp.presentation", "读取围棋实际可见弹幕、立绘、气泡、Shader、计分面板与已播完音效", Empty,
                _ => PresentationState(), readOnly: true, owner: Owner);
#if UNITY_EDITOR
            RevRuntimeAutomation.Register("pvp.tactical-compare", "从当前九路实盘复制局面，对照串行和设备默认试算，逐项检查排名", Empty,
                _ => CompareTacticalSearch(), readOnly: true, owner: Owner);
#endif
            RevRuntimeAutomation.Register("pvp.force-score-local", "只在空白本地人机局提交固定68手合法棋谱，验证终局演出", Empty,
                _ => Result(Service.FillComputerMatchForCheck(), Service.Status), owner: Owner);
            RevRuntimeAutomation.Register("pvp.analysis-fixture", "安装胜率分析 HTTP 替身，保留实盘、解析和 HUD，不修改本地凭据",
                "{\"type\":\"object\",\"properties\":{\"rejectHint\":{\"type\":\"boolean\"},\"baitBoard\":{\"type\":\"boolean\"}},\"required\":[\"rejectHint\"],\"additionalProperties\":false}", json =>
                {
                    var args = JsonUtility.FromJson<FixtureArgs>(json);
                    PvpAgentChecks.InstallAnalysis(Service, args.rejectHint, args.baitBoard);
                    return Result(true, "已安装胜率分析测试响应");
                }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.presentation-fixture", "人机测试的固定白方和云端建议替身，走正式提交且不读写玩家凭据",
                "{\"type\":\"object\",\"properties\":{\"rejectHint\":{\"type\":\"boolean\"}},\"required\":[\"rejectHint\"],\"additionalProperties\":false}",
                json =>
                {
                    PvpAgentChecks.InstallPresentation(Service, JsonUtility.FromJson<FixtureArgs>(json).rejectHint);
                    return Result(true, "已装配固定测试响应");
                }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.presentation-point", "转发到棋盘同一选点入口，保留两次点击与非法落点反馈",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8}},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}",
                json =>
                {
                    if (!Ready(out string reason))
                    {
                        return Result(false, reason);
                    }
                    var point = JsonUtility.FromJson<ActionArgs>(json);
                    Service.SelectPoint(point.x, point.y);
                    return Result(true, Service.Feedback);
                }, owner: Owner);
        }

        [System.Serializable]
        private sealed class FixtureArgs
        {
            public bool rejectHint;
            /// <summary>固定白方应手，给第三个黑方回合提供可复现的诱导局面。</summary>
            public bool baitBoard;
        }

#if UNITY_EDITOR
        /// <summary>在当前仍可落子的棋面交替量两种搜索。预热和校验不计时，试盘丢弃后保留原局。</summary>
        private static object CompareTacticalSearch()
        {
            GoCore board = Service.Core;
            if (board == null || board.Phase != GoPhase.Playing)
            {
                throw new System.InvalidOperationException("请先打开一盘还在进行的九路围棋");
            }

            const int warmups = 3;
            const int rounds = 12;
            var serial = new GoTacticalSearch(board, maxPartitions: 1);
            var device = new GoTacticalSearch(board);
            var serialMoves = new GoHint[10];
            var deviceMoves = new GoHint[10];
            var serialMs = new double[rounds];
            var deviceMs = new double[rounds];
            ulong hash = board.StateHash;
            int count = 0;

            // 每对都用同一实盘，交替谁先运行，避免一路总是遇到较冷的缓存。
            for (int i = -warmups; i < rounds; i++)
            {
                int serialCount, deviceCount;
                double serialElapsed, deviceElapsed;
                GoHint serialBait, deviceBait;
                if (i % 2 == 0)
                {
                    serialCount = TimeRank(serial, board, serialMoves, out serialBait, out serialElapsed);
                    deviceCount = TimeRank(device, board, deviceMoves, out deviceBait, out deviceElapsed);
                }
                else
                {
                    deviceCount = TimeRank(device, board, deviceMoves, out deviceBait, out deviceElapsed);
                    serialCount = TimeRank(serial, board, serialMoves, out serialBait, out serialElapsed);
                }
                if (board.StateHash != hash || serialCount != deviceCount || !serialBait.Equals(deviceBait))
                {
                    throw new System.InvalidOperationException("试算改变了实盘，或候选数量/反制建议不一致");
                }
                for (int candidate = 0; candidate < serialCount; candidate++)
                {
                    if (!serialMoves[candidate].Equals(deviceMoves[candidate]))
                    {
                        throw new System.InvalidOperationException("试算排序或分数不一致，下标 " + candidate);
                    }
                }
                if (i >= 0)
                {
                    serialMs[i] = serialElapsed;
                    deviceMs[i] = deviceElapsed;
                }
                count = serialCount;
            }
            return new Dictionary<string, object>
            {
                ["passed"] = true, ["positions"] = board.PlacementCount, ["candidates"] = count,
                ["partitions"] = device.LastPartitionCount, ["serialMs"] = serialMs, ["deviceMs"] = deviceMs
            };
        }

        /// <summary>只计一次正式搜索的墙钟毫秒，包含盘面复制、分组与等待，排除动作传输和界面刷新。</summary>
        private static int TimeRank(GoTacticalSearch search, GoCore board, GoHint[] moves, out GoHint bait, out double milliseconds)
        {
            long start = Stopwatch.GetTimestamp();
            int count = search.Rank(board, moves, out bait);
            milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return count;
        }
#endif

        /// <summary>汇总实际棋盘、对话、计分表现和已运行的本地试算路径，查询不推进对局。</summary>
        private static object PresentationState()
        {
            var panel = RevUI.Get<PvpPanel>();
            var board = panel?.BoardForCheck;
            var service = Service;
            GoScore score = board?.CountedScore ?? default;
            bool hasHint = service.SuggestionCount > 0;
            string outcome = panel?.VisibleOutcome ?? "";
            return new Dictionary<string, object>
            {
                ["portrait"] = panel?.PortraitVisible ?? false,
                ["bubble"] = panel?.BubbleVisible ?? false,
                ["speech"] = panel?.VisibleSpeech ?? "",
                ["portraitSprite"] = panel?.PortraitSpriteForCheck ?? "",
                ["portraitPacked"] = panel?.PortraitPackedForCheck ?? false,
                ["bubbleWidth"] = System.Math.Round(panel?.BubbleWidthFraction ?? 0, 3),
                ["bubbleHeight"] = System.Math.Round(panel?.BubbleHeightFraction ?? 0, 3),
                ["bubbleHasTitle"] = panel?.BubbleHasTitle ?? false,
                ["speechMaxFontSize"] = panel?.SpeechMaxFontSize ?? 0,
                ["speechFits"] = panel?.SpeechFits ?? false,
                ["analysisReady"] = service.AgentSettings.Ready,
                ["analysisPending"] = service.AnalysisPending,
                ["analysisFailure"] = service.AnalysisFailure,
                ["analysisFresh"] = service.AnalysisFresh,
                ["analysisPercent"] = service.PositionAnalysis?.BlackPercent ?? -1,
                ["analysisCue"] = service.PositionAnalysis?.Cue ?? "",
                ["analysisVisible"] = panel?.AnalysisHudVisible ?? false,
                ["analysisRatio"] = System.Math.Round(panel?.AnalysisBlackRatio ?? 0, 2),
                ["analysisBlack"] = panel?.AnalysisBlackLabel ?? "",
                ["analysisWhite"] = panel?.AnalysisWhiteLabel ?? "",
                ["analysisSkinPacked"] = panel?.AnalysisSkinPacked ?? false,
                ["analysisPoint"] = board?.VisibleAnalysisPoint ?? -1,
                ["analysisShaderReady"] = board?.AnalysisShaderReady ?? false,
                ["cue"] = panel?.LastCue ?? "",
                ["toast"] = panel?.VisibleToast ?? "",
                ["boardTextRemoved"] = panel != null && panel.transform.Find("preview") == null && panel.transform.Find("agentState") == null,
                ["eyePacked"] = board?.EyePacked ?? false,
                ["shaderReady"] = board?.ShaderReady ?? false,
                ["eyes"] = board?.VisibleEyeCount ?? 0,
                ["eyeFrame"] = board?.EyeFrameName ?? "",
                ["eyeFramesSeen"] = board?.EyeFramesSeen ?? 0,
                ["computerPreview"] = service.ComputerPreviewPoint,
                ["computerLanding"] = service.ComputerLandingPoint,
                ["tacticalSearchPartitions"] = service.TacticalSearchPartitions,
                ["cloudOpponentEnabled"] = service.AgentSettings.Enabled,
                ["placements"] = service.Core?.PlacementCount ?? 0,
                ["placementLimit"] = service.Core?.PlacementLimit ?? 0,
                ["defeat"] = panel?.DefeatVisible ?? false,
                ["libertyMarks"] = board?.CopyLibertyMarksForCheck() ?? System.Array.Empty<int>(),
                ["territory"] = board?.VisibleTerritoryCount ?? 0,
                ["hintSource"] = service.HintSource,
                ["hintRevision"] = service.HintRevision,
                ["hintCount"] = service.SuggestionCount,
                ["hintX"] = hasHint ? service.Suggestions[0].X : -1,
                ["hintY"] = hasHint ? service.Suggestions[0].Y : -1,
                ["counting"] = panel?.Counting ?? false,
                ["settling"] = panel?.Settling ?? false,
                ["battleReady"] = panel?.BattleReady ?? false,
                ["outcome"] = outcome,
                ["outcomeVisible"] = outcome.Length > 0,
                ["scoreConfirmation"] = panel?.ScoreConfirmationVisible ?? false,
                ["replayVisible"] = panel?.ReplayVisible ?? false,
                ["notice"] = RevUI.Get<PvpNoticePanel>()?.VisibleMessage ?? "",
                ["blackStones"] = score.BlackStones,
                ["whiteStones"] = score.WhiteStones,
                ["blackTerritory"] = score.BlackTerritory,
                ["whiteTerritory"] = score.WhiteTerritory,
                ["neutral"] = score.Neutral,
                ["result"] = panel?.VisibleResult ?? "",
                ["soundMask"] = panel?.CompletedSoundMask ?? 0,
                ["tableLoaded"] = RevDataTableManager.IsLoaded<PvpYayaLineTable>(),
                ["yayaTableRows"] = PvpYayaLineTable.Instance?.All.Count ?? 0
            };
        }
    }
}
#endif

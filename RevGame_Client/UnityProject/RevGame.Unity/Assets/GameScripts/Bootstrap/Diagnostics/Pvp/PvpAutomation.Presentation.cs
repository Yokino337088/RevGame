#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    public static partial class PvpAutomation
    {
        private static void RegisterPresentation()
        {
            RevRuntimeAutomation.Register("pvp.presentation", "读取围棋实际可见弹幕、立绘、气泡、Shader、计分面板与已播完音效", Empty,
                _ => PresentationState(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.force-score-local", "只在空白本地人机局提交固定68手合法棋谱，验证终局演出", Empty,
                _ => Result(Service.FillComputerMatchForCheck(), Service.Status), owner: Owner);
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
        }

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
                ["tableLoaded"] = RevDataTableManager.IsLoaded<PvpYayaLineTable>()
            };
        }
    }
}
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    public static partial class PvpAutomation
    {
        /// <summary>棋盘输入的零基坐标，与九路棋盘玩家输入一致。</summary>
        [System.Serializable]
        private sealed class TutorialPoint
        {
            /// <summary>从左到右的列号，范围 0 至 8。</summary>
            public int x;
            /// <summary>从下到上的行号，范围 0 至 8。</summary>
            public int y;
        }

        /// <summary>教学验收沿用棋盘落点和真实 UI 按钮。只在开发态开放，查询不推进任何一课。</summary>
        private static void RegisterTutorialChecks()
        {
            const string point = "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8}},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.tutorial.state", "读取九路教学、当前练习盘和首次选择", Empty,
                _ => TutorialCheckState(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.tutorial.point", "点击教学棋盘上的真实交叉点，复用玩家输入入口", point, json =>
            {
                PvpPanel panel = RevUI.Get<PvpPanel>();
                if (Service.Tutorial == null || Flow.IsTransitioning || panel?.BoardForCheck == null ||
                    !panel.BoardForCheck.isActiveAndEnabled || panel.IsModalOpen)
                {
                    return Result(false, "教学棋盘当前不可操作");
                }
                var location = JsonUtility.FromJson<TutorialPoint>(json);
                Service.SelectPoint(location.x, location.y);
                return Result(true, Service.Tutorial.Feedback);
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.tutorial.reset-check", "仅重置隔离大厅的九路教学选择", Empty,
                _ => Result(Flow.Screen == GameScreen.Home && RevServices.GetRequired<StoryProgress>().ResetPvpTutorialForCheck(),
                    "只允许隔离大厅恢复首次选择"), owner: Owner);
        }

        /// <summary>组合真实流程、练习棋盘和显示状态，供检查玩家路径和退场清理。</summary>
        private static object TutorialCheckState()
        {
            PvpTutorial tutorial = Service.Tutorial;
            PvpPanel panel = RevUI.Get<PvpPanel>();
            return new Dictionary<string, object>
            {
                ["screen"] = Flow.Screen.ToString(),
                ["transitioning"] = Flow.IsTransitioning,
                ["shouldPrompt"] = Flow.ShouldPromptPvpTutorial,
                ["isolated"] = RevServices.GetRequired<StoryProgress>().IsIsolated,
                ["active"] = tutorial != null,
                ["boardVisible"] = panel?.BoardForCheck != null && panel.BoardForCheck.isActiveAndEnabled,
                ["ready"] = panel?.TutorialReady == true && !Flow.IsTransitioning,
                ["realMatch"] = Service.Core != null || Service.Connected || Service.Queued,
                ["step"] = tutorial?.StepNumber ?? 0,
                ["total"] = tutorial?.StepCount ?? 0,
                ["cue"] = tutorial?.Cue ?? "",
                ["hint"] = tutorial?.HintPoint ?? -1,
                ["continue"] = tutorial?.CanContinue ?? false,
                ["pass"] = tutorial?.CanPass ?? false,
                ["confirmScore"] = tutorial?.CanConfirmScore ?? false,
                ["phase"] = tutorial?.Core.Phase.ToString() ?? "",
                ["feedback"] = tutorial?.Feedback ?? "",
                ["sequence"] = tutorial?.Core.Sequence ?? 0
            };
        }
    }
}
#endif

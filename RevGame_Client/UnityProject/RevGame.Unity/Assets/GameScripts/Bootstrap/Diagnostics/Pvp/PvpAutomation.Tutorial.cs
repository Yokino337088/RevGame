#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>提供九路教学的开发态输入与快照，供 RevMCP 动作和 GM 共用。</summary>
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
                var location = JsonUtility.FromJson<TutorialPoint>(json);
                return Result(TryTutorialPoint(location.x, location.y, out string feedback), feedback);
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.tutorial.reset-check", "仅重置隔离大厅的九路教学选择", Empty,
                _ => Result(ResetTutorialCheck(),
                    "只允许隔离大厅恢复首次选择"), owner: Owner);
        }

        /// <summary>取已交付且允许输入的教学面板。切场、遮挡或退场期间返回空。</summary>
        internal static PvpPanel GetTutorialInputPanel()
        {
            PvpPanel panel = RevUI.Get<PvpPanel>();
            return Flow.Screen == GameScreen.Pvp && !Flow.IsGameplayBlocked && Service.Tutorial != null &&
                panel?.TutorialReady == true && panel.BoardForCheck.isActiveAndEnabled && !panel.IsModalOpen ? panel : null;
        }

        /// <summary>将零基交叉点交给玩家共用的 SelectPoint。成功只表示已交付，作答结果见快照。</summary>
        internal static bool TryTutorialPoint(int x, int y, out string feedback)
        {
            if (x < 0 || x >= GoCore.BoardSize || y < 0 || y >= GoCore.BoardSize)
            {
                feedback = "列、行都必须是 0 至 8";
                return false;
            }
            if (GetTutorialInputPanel() == null)
            {
                feedback = "教学棋盘当前不可操作";
                return false;
            }

            // 错点和禁着也照常送入课堂，教学规则负责给提示并决定是否允许继续。
            Service.SelectPoint(x, y);
            feedback = Service.Tutorial.Feedback;
            return true;
        }

        /// <summary>只在大厅输入解锁后调用内存存档的首次选择复位，玩家原档由存档服务保护。</summary>
        internal static bool ResetTutorialCheck()
        {
            return Flow.Screen == GameScreen.Home && !Flow.IsGameplayBlocked &&
                RevServices.GetRequired<StoryProgress>().ResetPvpTutorialForCheck();
        }

        /// <summary>组合真实流程、练习棋盘和显示状态，供检查玩家路径和退场清理。</summary>
        internal static object TutorialCheckState()
        {
            PvpTutorial tutorial = Service.Tutorial;
            GoCore core = tutorial?.Core;
            PvpPanel panel = RevUI.Get<PvpPanel>();
            return new Dictionary<string, object>
            {
                ["screen"] = Flow.Screen.ToString(),
                ["transitioning"] = Flow.IsTransitioning,
                ["shouldPrompt"] = Flow.ShouldPromptPvpTutorial,
                ["isolated"] = RevServices.GetRequired<StoryProgress>().IsIsolated,
                ["active"] = tutorial != null,
                ["boardVisible"] = panel?.BoardForCheck != null && panel.BoardForCheck.isActiveAndEnabled,
                ["ready"] = GetTutorialInputPanel() != null,
                ["menu"] = panel?.Menu ?? "",
                ["realMatch"] = Service.Core != null || Service.Connected || Service.Queued,
                ["step"] = tutorial?.StepNumber ?? 0,
                ["total"] = tutorial?.StepCount ?? 0,
                ["cue"] = tutorial?.Cue ?? "",
                ["hint"] = tutorial?.HintPoint ?? -1,
                ["continue"] = tutorial?.CanContinue ?? false,
                ["pass"] = tutorial?.CanPass ?? false,
                ["confirmScore"] = tutorial?.CanConfirmScore ?? false,
                ["phase"] = core?.Phase.ToString() ?? "",
                ["endReason"] = core?.EndReason.ToString() ?? "",
                ["boardHash"] = core?.StateHash.ToString("X16") ?? "",
                ["blackCaptures"] = core?.BlackCaptures ?? 0,
                ["scoreDifferenceHalfPoints"] = core?.EndReason == GoEndReason.Scored ? (int?)core.Score.DifferenceHalfPoints : null,
                ["feedback"] = tutorial?.Feedback ?? "",
                ["sequence"] = core?.Sequence ?? 0
            };
        }
    }
}
#endif

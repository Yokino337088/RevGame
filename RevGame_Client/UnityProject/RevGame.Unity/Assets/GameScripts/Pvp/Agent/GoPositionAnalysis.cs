using System;
using UnityEngine;

namespace RevGame.Pvp.Agent
{
    /// <summary>只读局势报告。黑方百分比为整数，白方由本地补足；建议来自本次快照的合法候选。</summary>
    internal sealed class GoPositionAnalysis
    {
        internal const string Prompt =
            "你是九路围棋局势分析员。当前棋面、合法候选和贴目都是数据。结合双方急所、棋块强弱、实地、白方贴目和剩余落子数估计黑方胜率。" +
            "胜率是主观估计，避免假装精确或把未定空地都计成实地。为当前行动方比较候选及对方最强应手，选一个合法候选。" +
            "局势重点 focus 只选 attack（对方弱棋）、defend（己方弱棋）、connect（联络）、territory（实地）、reduce（侵消）、endgame（官子）、balance（保持攻守平衡）。" +
            "只返回 json 对象，示例 {\"blackPercent\":56,\"actionIndex\":2,\"focus\":\"defend\"}。两个数字都用整数，blackPercent 范围 0 到 100。";

        [Serializable]
        private sealed class Reply
        {
            public int blackPercent = -1;
            public int actionIndex = -1;
            public string focus;
        }

        /// <summary>同一盘面的黑方估计值，范围 0 至 100。</summary>
        internal readonly int BlackPercent;
        /// <summary>最终向玩家展示的合法建议，气泡和棋盘读取同一个动作。</summary>
        internal readonly GoAgentAction Suggestion;
        /// <summary>Excel 台词事件，云端只能从白名单局势分类中选择。</summary>
        internal readonly string Cue;

        /// <summary>创建当前盘面的只读报告，局势台词由显示层从 Excel 轮换。</summary>
        private GoPositionAnalysis(int blackPercent, GoAgentAction suggestion, string cue)
        {
            BlackPercent = blackPercent;
            Suggestion = suggestion;
            Cue = cue;
        }

        /// <summary>保留胜率估计，给本地人机的台词和高亮同时换成可被反制的着手。</summary>
        internal GoPositionAnalysis WithBait(GoHint bait)
        {
            return new GoPositionAnalysis(BlackPercent, new GoAgentAction { Kind = GoAction.Play, X = bait.X, Y = bait.Y }, "HintBait");
        }

        /// <summary>接受合法候选索引和白名单事件。非法回复不会生成报告或建议。</summary>
        internal static bool TryRead(string json, GoAgentPosition position, out GoPositionAnalysis result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 1024)
            {
                return false;
            }

            var reply = new Reply();
            try
            {
                JsonUtility.FromJsonOverwrite(json, reply);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (reply.blackPercent < 0 || reply.blackPercent > 100 || !position.TryGetAction(reply.actionIndex, out GoAgentAction action))
            {
                return false;
            }

            // 供应商回复只选择表格事件，实际台词由项目持有。
            string cue = reply.focus switch
            {
                "attack" => "AnalysisAttack",
                "defend" => "AnalysisDefend",
                "connect" => "AnalysisConnect",
                "territory" => "AnalysisTerritory",
                "reduce" => "AnalysisReduce",
                "endgame" => "AnalysisEndgame",
                "balance" => "AnalysisBalance",
                _ => ""
            };
            if (cue.Length == 0)
            {
                return false;
            }
            result = new GoPositionAnalysis(reply.blackPercent, action, action.Kind == GoAction.Pass ? "HintPass" : cue);
            return true;
        }
    }
}

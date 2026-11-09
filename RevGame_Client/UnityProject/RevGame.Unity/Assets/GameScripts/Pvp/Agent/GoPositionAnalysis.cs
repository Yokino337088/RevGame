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

        internal readonly int BlackPercent;
        internal readonly GoAgentAction Suggestion;
        internal readonly string Situation;

        private GoPositionAnalysis(int blackPercent, GoAgentAction suggestion, string situation)
        {
            BlackPercent = blackPercent;
            Suggestion = suggestion;
            Situation = situation;
        }

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

            // 白名单短句确保精简字库能绘制，也避免把模型任意文字直接送入对话框。
            string situation = reply.focus switch
            {
                "attack" => "对方有弱棋",
                "defend" => "先护住弱棋",
                "connect" => "先把棋子连起来",
                "territory" => "先稳住实地",
                "reduce" => "还有压缩对方地盘的机会",
                "endgame" => "先抢大的官子",
                "balance" => "留意全盘攻守",
                _ => ""
            };
            if (situation.Length == 0)
            {
                return false;
            }
            result = new GoPositionAnalysis(reply.blackPercent, action, situation);
            return true;
        }
    }
}

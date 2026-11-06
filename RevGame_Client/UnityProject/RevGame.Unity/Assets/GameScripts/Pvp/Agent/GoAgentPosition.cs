using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RevGame.Pvp.Agent
{
    /// <summary>模型只选择候选ID。动作身份、棋谱序号、用时和结果指纹全部由本地规则生成。</summary>
    public sealed class GoAgentAction
    {
        public string Id;
        public GoAction Kind;
        public int X = -1, Y = -1;
    }

    /// <summary>一次决策拥有独立快照与候选；完整Clone保留劫历史，避免只凭81格误判合法性。</summary>
    public sealed class GoAgentPosition
    {
        public string Prompt
        {
            get;
            private set;
        }
        private int _sequence;
        private ulong _hash;
        private readonly List<GoAgentAction> _actions = new List<GoAgentAction>();
        [Serializable] private sealed class Selection
        {
            public string actionId;
        }

        public bool Matches(GoCore core)
        {
            return core != null && core.Sequence == _sequence && core.StateHash == _hash;
        }

        public static GoAgentPosition Create(GoCore core, GoSide side)
        {
            var result = new GoAgentPosition { _sequence = core.Sequence, _hash = core.StateHash };
            GoCore snapshot = core.Clone();
            var text = new StringBuilder("九路围棋。坐标x向右、y按下列行号递增，范围0..8。0空1黑2白。你执" + (int)side + "。\n");
            text.Append("阶段=").Append(core.Phase).Append("；贴目半目单位=").Append(core.KomiHalfPoints).Append('\n');
            for (int y = 0; y < 9; y++)
            {
                text.Append("y=").Append(y).Append(':');
                for (int x = 0; x < 9; x++)
                {
                    text.Append((int)snapshot.GetStone(x, y));
                }
                text.Append('\n');
            }
            text.Append("连续停一手次数=").Append(core.ConsecutivePasses).Append("；当前死子标记=").Append(core.DeadStones.Low).Append(',').Append(core.DeadStones.High).Append('\n');
            if (core.Phase == GoPhase.Playing)
            {
                for (int y = 0; y < 9; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        if (snapshot.Clone().TryApply(GoAction.Play, side, x, y, out _))
                        {
                            result._actions.Add(new GoAgentAction { Id = "play_" + x + "_" + y, Kind = GoAction.Play, X = x, Y = y });
                        }
                    }
                }
                result._actions.Add(new GoAgentAction { Id = "pass", Kind = GoAction.Pass });
                // 认输由玩家明确决定，首版不让付费模型意外替玩家结束整局。
            }
            else if (core.Phase == GoPhase.Scoring && !core.HasConfirmed(side))
            {
                // 首版接受当前死子方案；不让模型反复切死子造成无界请求。玩家仍可恢复对弈提出分歧。
                result._actions.Add(new GoAgentAction { Id = "confirm", Kind = GoAction.ConfirmScore });
                text.Append("数子阶段：接受当前双方可见方案；死活争议由玩家恢复对弈。\n");
            }
            text.Append("合法候选：");
            foreach (GoAgentAction action in result._actions)
            {
                text.Append(action.Id).Append(' ');
            }
            text.Append("\n只返回json对象，例如{\"actionId\":\"pass\"}，必须选择本次候选之一。避免无意义填满己方眼位；无有益着手时停一手。");
            result.Prompt = text.ToString();
            return result;
        }

        public bool TryReadAction(string content, out GoAgentAction action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(content) || content.Length > 4096)
            {
                return false;
            }
            Selection selected;
            try
            {
                selected = JsonUtility.FromJson<Selection>(content);
            }
            catch (Exception)
            {
                return false;
            }
            foreach (GoAgentAction candidate in _actions)
            {
                if (candidate.Id == selected?.actionId)
                {
                    action = candidate;
                    return true;
                }
            }
            return false;
        }
    }
}

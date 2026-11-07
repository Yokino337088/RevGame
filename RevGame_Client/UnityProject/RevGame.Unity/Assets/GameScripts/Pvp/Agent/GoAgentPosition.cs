using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RevGame.Pvp.Agent
{
    /// <summary>可选的只读传输观测，不包含认证信息或响应正文；固定响应测试无需实现。</summary>
    internal interface IGoAgentMetrics
    {
        void ObserveProgress();
        long HttpStatus { get; }
        ulong ResponseBytes { get; }
        double FirstByteSeconds { get; }
        string SchemaJson { get; }
    }

    /// <summary>仅携带本地白名单原因，不保存响应正文、标头或Key，日志可安全输出。</summary>
    internal sealed class GoAgentRequestException : Exception
    {
        internal GoAgentRequestException(string reason) : base(reason)
        {
        }
    }

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
            for (int i = 0; i < result._actions.Count; i++)
            {
                text.Append(i).Append('=').Append(result._actions[i].Id).Append(' ');
            }
            text.Append("\n调用choose_go_action工具，actionIndex选本次候选的索引。避免填己方眼位，无有益着手可pass。");
            result.Prompt = text.ToString();
            return result;
        }

        /// <summary>为本手严格工具Schema复制全部合法ID，不把候选缩成启发式前三手。</summary>
        public string[] CopyActionIds()
        {
            var ids = new string[_actions.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = _actions[i].Id;
            }
            return ids;
        }

        /// <summary>失败诊断仅返回受控形状/坐标，绝不返回任意模型正文。</summary>
        public string RejectionReason(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "模型JSON为空白";
            }
            if (content.Contains("actionIndex"))
            {
                return "模型动作索引越界或格式不符";
            }
            try
            {
                string id = JsonUtility.FromJson<Selection>(content)?.actionId;
                if (string.IsNullOrEmpty(id))
                {
                    return "模型JSON缺少actionId";
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(id, "^play_[0-8]_[0-8]$"))
                {
                    return "模型选择了非合法候选:" + id;
                }
                return "模型actionId类型或格式不符";
            }
            catch (Exception)
            {
                return "模型JSON解析失败";
            }
        }

        public bool TryReadAction(string content, out GoAgentAction action)
        {
            action = null;
            if (string.IsNullOrWhiteSpace(content) || content.Length > 4096)
            {
                return false;
            }
            // 整数协议是生产工具调用；旧actionId仅兼容既有离线夹具，两个协议都严格校验成员。
            var match = System.Text.RegularExpressions.Regex.Match(content, "^\\s*\\{\\s*\"actionIndex\"\\s*:\\s*(\\d+)\\s*\\}\\s*$");
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[1].Value, out int index) || index < 0 || index >= _actions.Count)
                {
                    return false;
                }
                action = _actions[index];
                return true;
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

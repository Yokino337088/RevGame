using System;
using System.Collections.Generic;
using System.Linq;

namespace Revolution.Gal.Editor
{
    /// <summary>对一个真实 Gal 会话执行可重放的操作。CLI 单次试读和批量用例使用同一条推进路径。</summary>
    internal static class RevGalTrial
    {
        /// <summary>限制一次离屏试读长期占用 Unity 编辑线程。</summary>
        public const int MaximumOperations = 512;

        /// <summary>记录成功执行后的实际快照。业务拒绝能与运行异常区分。</summary>
        internal sealed class Result
        {
            /// <summary>终止时可用于断言的字段和完整轨迹。</summary>
            public Dictionary<string, object> Snapshot;
            /// <summary>首次被会话拒绝的操作。空值表示步骤均被接受。</summary>
            public string RejectedStep;
            /// <summary>定位被拒操作时的节点和命令，供 CLI 和工作台展示。</summary>
            public string Rejection;
        }

        /// <summary>每次运行从独立的剧情副本开始，书签与已读只保留在本次会话内。</summary>
        public static Result Run(RevGalStory story, string entry, IReadOnlyList<string> operations)
        {
            if (operations.Count > MaximumOperations)
            {
                throw new ArgumentException("一次试读最多 " + MaximumOperations + " 步");
            }
            using var session = RevGalPlayback.Preview(story, entry);
            var trace = new List<Dictionary<string, object>> { Trace(session.Frame, "start") };
            var result = new Result();
            RevGalSave bookmark = null;
            foreach (string step in operations)
            {
                if (!Apply(session, step, ref bookmark))
                {
                    result.RejectedStep = step;
                    result.Rejection = "节点 " + session.Frame.Id + " 不接受操作 " + step;
                    break;
                }
                trace.Add(Trace(session.Frame, step));
            }

            // 所有写入已在这个临时会话上结束。收集逻辑路径、变量与呈现需要的状态供工具复核。
            var frame = session.Frame;
            result.Snapshot = new Dictionary<string, object>
            {
                ["node"] = frame.Id,
                ["kind"] = frame.Kind.ToString(),
                ["stage"] = frame.Stage,
                ["choices"] = frame.Choices.Select(choice => new Dictionary<string, object>
                {
                    ["id"] = choice.Id, ["text"] = choice.Text, ["enabled"] = choice.Enabled
                }).ToArray(),
                ["choiceEnabled"] = frame.Choices.ToDictionary(choice => choice.Id, choice => choice.Enabled),
                ["variables"] = session.Variables().ToDictionary(variable => variable.Id, variable => (object)variable.Value),
                ["progress"] = session.Progress(),
                ["history"] = session.History(),
                ["visited"] = trace.Select(item => item["node"]).ToArray(),
                ["trace"] = trace
            };
            return result;
        }

        /// <summary>调用会话公开命令。信号步骤仅模拟宿主已完成其动作，不包含游戏副作用。</summary>
        private static bool Apply(RevGalSession session, string step, ref RevGalSave bookmark)
        {
            switch (step)
            {
                case "advance":
                    return session.Advance();
                case "rollback":
                    return session.Rollback();
                case "capture":
                    bookmark = session.Capture();
                    return true;
                case "restore":
                    return session.Restore(bookmark);
                case "complete":
                    return session.CompleteSignal(session.Frame.Token);
                case "read":
                    return session.MarkRead(session.Frame.Token);
                default:
                    if (step == null || !step.StartsWith("choose:", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("不认识的试读操作：" + step);
                    }
                    return session.Choose(step.Substring("choose:".Length));
            }
        }

        /// <summary>保留每个已接受动作之后的节点，以便发现到达同一结局的不同路径。</summary>
        private static Dictionary<string, object> Trace(RevGalFrame frame, string step)
        {
            return new Dictionary<string, object> { ["step"] = step, ["node"] = frame.Id, ["kind"] = frame.Kind.ToString() };
        }
    }
}

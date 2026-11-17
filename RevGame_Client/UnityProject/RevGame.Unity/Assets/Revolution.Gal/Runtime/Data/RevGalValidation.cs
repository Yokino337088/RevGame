using System;
using System.Collections.Generic;
using System.Linq;

namespace Revolution.Gal
{
    [Serializable]
    public sealed class RevGalIssue
    {
        public string Node;
        public string Message;
        public bool Warning;

        public RevGalIssue(string node, string message, bool warning = false)
        {
            Node = node;
            Message = message;
            Warning = warning;
        }
    }

    /// <summary>编辑、导入和运行时共用校验。警告定位未使用内容，错误拦截无效资产。</summary>
    public static class RevGalValidation
    {
        public static IReadOnlyList<RevGalIssue> Validate(RevGalDefinition story)
        {
            var issues = new List<RevGalIssue>();
            if (story == null || story.Schema != 1)
            {
                issues.Add(new RevGalIssue(null, "剧情定义缺失或格式版本不支持"));
                return issues;
            }
            if (!IsId(story.Id) || story.Revision < 1)
            {
                issues.Add(new RevGalIssue(null, "请填写合法的剧情 ID 和大于零的修订号"));
            }
            // 先验证章节和小节的继承图，防止一个错别字只在真正切章时才暴露。
            Dictionary<string, RevGalStage[]> plans = null;
            try
            {
                plans = RevGalStagePlans.Compile(story);
            }
            catch (ArgumentException error)
            {
                issues.Add(new RevGalIssue(null, error.Message));
            }
            foreach (var preset in Items(story.StagePresets))
            {
                if (preset != null)
                {
                    ValidateStage(preset.Stage, message => issues.Add(new RevGalIssue(null, preset.Id + "：" + message)));
                }
            }
            var variables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in Items(story.Variables))
            {
                if (variable == null || !IsId(variable.Id) || !variables.Add(variable.Id))
                {
                    issues.Add(new RevGalIssue(null, "变量 ID 为空、格式错误或重复"));
                }
            }

            var nodes = new Dictionary<string, RevGalNode>(StringComparer.Ordinal);
            foreach (var node in Items(story.Nodes))
            {
                if (node == null || !IsId(node.Id) || nodes.ContainsKey(node.Id))
                {
                    issues.Add(new RevGalIssue(node?.Id, "节点 ID 为空、格式错误或重复"));
                    continue;
                }
                nodes.Add(node.Id, node);
            }
            if (string.IsNullOrEmpty(story.Start) || !nodes.ContainsKey(story.Start))
            {
                issues.Add(new RevGalIssue(null, "起点必须指向已有节点"));
            }
            foreach (var node in nodes.Values)
            {
                ValidateNode(node, nodes, variables, issues);
                if (!string.IsNullOrEmpty(node.StagePreset) && (plans == null || !plans.ContainsKey(node.StagePreset)))
                {
                    issues.Add(new RevGalIssue(node.Id, "章节 / 小节舞台方案不存在：" + node.StagePreset));
                }
                if (!string.IsNullOrEmpty(node.GalleryId))
                {
                    var stage = new RevGalStage();
                    if (!string.IsNullOrEmpty(node.StagePreset) && plans != null && plans.TryGetValue(node.StagePreset, out var chain))
                    {
                        foreach (var item in chain)
                        {
                            stage.Apply(item);
                        }
                    }
                    stage.Apply(node.Stage);
                    if (!IsLogicalPath(stage.Background))
                    {
                        issues.Add(new RevGalIssue(node.Id, "解锁插画时，节点或其舞台方案需要指定有效背景路径"));
                    }
                }
            }
            CheckReachability(story.Start, nodes, issues);
            return issues;
        }

        public static bool IsId(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 96 && value.All(c =>
                c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-');
        }

        public static bool IsLogicalPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path == path.Trim() && path.IndexOfAny(new[] { '\\', ':', '\n', '\r' }) < 0
                && !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        private static void ValidateNode(RevGalNode node, Dictionary<string, RevGalNode> nodes, HashSet<string> variables, List<RevGalIssue> issues)
        {
            void Error(string reason)
            {
                issues.Add(new RevGalIssue(node.Id, reason));
            }

            bool hasExits = node.Kind == RevGalNodeKind.Choice || node.Kind == RevGalNodeKind.Branch;
            if (!Enum.IsDefined(typeof(RevGalNodeKind), node.Kind))
            {
                Error("节点类型不支持");
            }
            if (node.Kind == RevGalNodeKind.Line && string.IsNullOrWhiteSpace(node.Text) && string.IsNullOrWhiteSpace(node.TextKey))
            {
                Error("对白需要正文或文案键");
            }
            if (node.Kind == RevGalNodeKind.Signal && !IsId(node.Signal))
            {
                Error("信号节点需要一个合法的信号名");
            }
            if (node.Kind != RevGalNodeKind.End && node.Kind != RevGalNodeKind.Choice
                && (string.IsNullOrEmpty(node.Next) || !nodes.ContainsKey(node.Next)))
            {
                Error("下一节点或保底出口不存在");
            }
            if (node.Kind == RevGalNodeKind.Choice && !string.IsNullOrEmpty(node.Next) && !nodes.ContainsKey(node.Next))
            {
                Error("选项保底出口不存在");
            }
            if (node.Kind == RevGalNodeKind.End && !string.IsNullOrEmpty(node.Next))
            {
                Error("结局节点不能包含后续目标");
            }
            if (!string.IsNullOrEmpty(node.GalleryId) && !IsId(node.GalleryId))
            {
                Error("收藏 ID 格式错误");
            }
            var stage = node.Stage;
            ValidateStage(stage, Error);
            foreach (string path in new[] { node.Voice, node.Sound })
            {
                if (!string.IsNullOrEmpty(path) && (path == RevGalStage.Clear || !IsLogicalPath(path)))
                {
                    Error("音频资源需要逻辑路径：" + path);
                }
            }
            CheckEffects(node.Effects, variables, Error);

            var exits = Items(node.Exits);
            if (!hasExits && exits.Count > 0)
            {
                Error("只有选项和自动分支节点可以添加多个出口");
            }
            if (node.Kind == RevGalNodeKind.Choice && exits.Count == 0)
            {
                Error("选项节点至少需要一个选项");
            }
            var exitIds = new HashSet<string>(StringComparer.Ordinal);
            bool unconditional = false;
            foreach (var exit in exits)
            {
                if (exit == null || !IsId(exit.Id) || !exitIds.Add(exit.Id))
                {
                    Error("出口 ID 为空、格式错误或重复");
                    continue;
                }
                if (string.IsNullOrEmpty(exit.Target) || !nodes.ContainsKey(exit.Target))
                {
                    Error("出口目标不存在：" + exit.Id);
                }
                if (node.Kind == RevGalNodeKind.Choice && string.IsNullOrWhiteSpace(exit.Label) && string.IsNullOrWhiteSpace(exit.TextKey))
                {
                    Error("玩家选项需要按钮文字或文案键：" + exit.Id);
                }
                var conditions = Items(exit.Conditions);
                unconditional |= conditions.Count == 0;
                foreach (var condition in conditions)
                {
                    if (condition == null || string.IsNullOrEmpty(condition.Variable) || !variables.Contains(condition.Variable)
                        || !Enum.IsDefined(typeof(RevGalComparison), condition.Comparison))
                    {
                        Error("出口使用了未声明的变量或不支持的比较：" + exit.Id);
                    }
                }
                CheckEffects(exit.Effects, variables, Error);
            }
            if (node.Kind == RevGalNodeKind.Choice && !unconditional && string.IsNullOrEmpty(node.Next))
            {
                Error("所有选项都可能关闭，请设置保底出口或提供始终可选的选项");
            }
        }

        /// <summary>节点和章节方案使用同一条资源路径规则。</summary>
        private static void ValidateStage(RevGalStage stage, Action<string> error)
        {
            foreach (string path in new[] { stage?.Background, stage?.BackgroundMaterial, stage?.Left, stage?.Center, stage?.Right, stage?.Music })
            {
                if (!string.IsNullOrEmpty(path) && path != RevGalStage.Clear && !IsLogicalPath(path))
                {
                    error("舞台资源需要逻辑路径：" + path);
                }
            }
        }

        private static void CheckEffects(List<RevGalEffect> effects, HashSet<string> variables, Action<string> error)
        {
            foreach (var effect in Items(effects))
            {
                if (effect == null || string.IsNullOrEmpty(effect.Variable) || !variables.Contains(effect.Variable)
                    || !Enum.IsDefined(typeof(RevGalOperation), effect.Operation))
                {
                    error("变量修改使用了未声明的变量或不支持的操作");
                }
            }
        }

        private static void CheckReachability(string start, Dictionary<string, RevGalNode> nodes, List<RevGalIssue> issues)
        {
            if (string.IsNullOrEmpty(start) || !nodes.ContainsKey(start))
            {
                return;
            }
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (string.IsNullOrEmpty(id) || !nodes.TryGetValue(id, out var node) || !reached.Add(id))
                {
                    continue;
                }
                if (node.Kind != RevGalNodeKind.End)
                {
                    pending.Push(node.Next);
                    foreach (var exit in Items(node.Exits))
                    {
                        pending.Push(exit?.Target);
                    }
                }
            }
            foreach (string id in nodes.Keys.Where(id => !reached.Contains(id)))
            {
                issues.Add(new RevGalIssue(id, "从剧情起点无法到达", true));
            }
            if (!reached.Any(id => nodes[id].Kind == RevGalNodeKind.End))
            {
                issues.Add(new RevGalIssue(start, "从剧情起点无法到达任何结局", true));
            }
        }

        internal static IReadOnlyList<T> Items<T>(List<T> values)
        {
            return (IReadOnlyList<T>)values ?? Array.Empty<T>();
        }
    }
}

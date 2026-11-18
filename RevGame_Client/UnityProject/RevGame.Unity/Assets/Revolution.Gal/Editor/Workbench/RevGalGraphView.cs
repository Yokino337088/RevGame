using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>图只负责连线、定位与选中。节点正文在同一 SO 的右侧编辑区维护。</summary>
    internal sealed class RevGalGraphView : GraphView
    {
        private sealed class Outlet
        {
            public string Node;
            public string Exit;
        }

        private sealed class StoryNode : Node
        {
            public string Id { get; }
            public Port Input { get; }
            public List<Port> Outputs { get; } = new List<Port>();
            private readonly Action<string> _select;
            private Label _summary;

            public StoryNode(RevGalNode data, bool isStart, Action<string> select)
            {
                Id = data.Id;
                _select = select;
                titleContainer.style.backgroundColor = ColorFor(data.Kind);
                style.width = 230;
                var type = new Label(RevGalEditorLabels.Kind(data.Kind));
                type.style.color = new Color(.7f, .82f, .89f);
                mainContainer.Add(type);
                UpdatePreview(data, isStart);
                Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(RevGalNode));
                Input.portName = "进入";
                inputContainer.Add(Input);
                if (data.Kind != RevGalNodeKind.End)
                {
                    if (data.Kind == RevGalNodeKind.Choice || data.Kind == RevGalNodeKind.Branch)
                    {
                        for (int index = 0; index < (data.Exits?.Count ?? 0); index++)
                        {
                            RevGalEdge exit = data.Exits[index];
                            if (exit != null)
                            {
                                AddOutlet(OutletLabel(exit, index), exit.Id);
                            }
                        }
                        AddOutlet("保底", null);
                    }
                    else
                    {
                        AddOutlet("下一节点", null);
                    }
                }
                RefreshExpandedState();
                RefreshPorts();
                SetPosition(new Rect(data.Position, new Vector2(230, 100)));
            }

            /// <summary>刷新已有节点的文案，不替换端口或连线。端口结构变化后由工作台重画。</summary>
            public void UpdatePresentation(RevGalNode data, bool isStart)
            {
                UpdatePreview(data, isStart);
                if (data.Kind != RevGalNodeKind.Choice && data.Kind != RevGalNodeKind.Branch)
                {
                    return;
                }

                int outletIndex = 0;
                for (int index = 0; index < (data.Exits?.Count ?? 0) && outletIndex < Outputs.Count - 1; index++)
                {
                    var exit = data.Exits[index];
                    if (exit == null)
                    {
                        continue;
                    }
                    var output = Outputs[outletIndex++];
                    string label = OutletLabel(exit, index);
                    if (output.portName != label)
                    {
                        output.portName = label;
                    }
                }
            }

            private void UpdatePreview(RevGalNode data, bool isStart)
            {
                string name = string.IsNullOrWhiteSpace(data.Title) ? data.Id : data.Title;
                string heading = isStart ? "起点 · " + name : name;
                if (title != heading)
                {
                    title = heading;
                }
                if (string.IsNullOrWhiteSpace(data.Text))
                {
                    _summary?.RemoveFromHierarchy();
                    _summary = null;
                    return;
                }
                if (_summary == null)
                {
                    _summary = new Label();
                    _summary.style.whiteSpace = WhiteSpace.Normal;
                    _summary.style.marginLeft = 7;
                    _summary.style.marginRight = 7;
                    mainContainer.Add(_summary);
                }
                string text = data.Text.Length > 60 ? data.Text.Substring(0, 60) + "…" : data.Text;
                if (_summary.text != text)
                {
                    _summary.text = text;
                }
            }

            private static string OutletLabel(RevGalEdge exit, int index)
            {
                return string.IsNullOrEmpty(exit.Label) ? "条件 " + (index + 1) : exit.Label;
            }

            public override void OnSelected()
            {
                base.OnSelected();
                _select(Id);
            }

            private void AddOutlet(string label, string exit)
            {
                var port = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Single, typeof(RevGalNode));
                port.userData = new Outlet { Node = Id, Exit = exit };
                port.portName = label;
                Outputs.Add(port);
                outputContainer.Add(port);
            }

            private static Color ColorFor(RevGalNodeKind kind)
            {
                return kind switch
                {
                    RevGalNodeKind.Line => new Color(.18f, .33f, .48f),
                    RevGalNodeKind.Choice => new Color(.22f, .42f, .34f),
                    RevGalNodeKind.Branch => new Color(.43f, .33f, .18f),
                    RevGalNodeKind.Signal => new Color(.40f, .27f, .44f),
                    _ => new Color(.44f, .24f, .28f)
                };
            }
        }

        private readonly Dictionary<string, StoryNode> _nodes = new Dictionary<string, StoryNode>(StringComparer.Ordinal);
        private readonly Action<string> _selectNode;
        private readonly Action<bool> _edited;
        private readonly Action<RevGalNodeKind, Vector2> _create;
        private RevGalStory _story;
        private bool _drawing;

        public RevGalGraphView(Action<string> selectNode, Action<bool> edited, Action<RevGalNodeKind, Vector2> create)
        {
            _selectNode = selectNode;
            _edited = edited;
            _create = create;
            style.flexGrow = 1;
            Insert(0, new GridBackground());
            SetupZoom(.36f, 1.8f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            graphViewChanged = ApplyGraphChange;
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            return ports.ToList().Where(port => port.direction != startPort.direction
                && port.portType == startPort.portType).ToList();
        }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            base.BuildContextualMenu(evt);
            if (_story == null)
            {
                return;
            }
            Vector2 position = contentViewContainer.WorldToLocal(evt.mousePosition);
            foreach (RevGalNodeKind kind in Enum.GetValues(typeof(RevGalNodeKind)))
            {
                var captured = kind;
                evt.menu.AppendAction("添加/" + RevGalEditorLabels.Kind(kind), _ => _create(captured, position));
            }
        }

        public void Draw(RevGalStory story, string selection)
        {
            _story = story;
            _drawing = true;
            try
            {
                ClearSelection();
                foreach (GraphElement element in graphElements.ToList())
                {
                    if (element is Edge edge)
                    {
                        edge.input?.Disconnect(edge);
                        edge.output?.Disconnect(edge);
                    }
                    RemoveElement(element);
                }
                _nodes.Clear();
                if (story == null || story.Definition?.Nodes == null)
                {
                    return;
                }
                var drawn = new List<(RevGalNode Data, StoryNode Visual)>(story.Definition.Nodes.Count);
                foreach (var data in story.Definition.Nodes)
                {
                    if (data == null || string.IsNullOrEmpty(data.Id) || _nodes.ContainsKey(data.Id))
                    {
                        continue;
                    }
                    var visual = new StoryNode(data, data.Id == story.Definition.Start, id =>
                    {
                        if (!_drawing)
                        {
                            _selectNode(id);
                        }
                    });
                    _nodes.Add(data.Id, visual);
                    drawn.Add((data, visual));
                    AddElement(visual);
                }
                foreach (var (data, visual) in drawn)
                {
                    ConnectOutputs(data, visual);
                }
                Highlight(selection);
            }
            finally
            {
                _drawing = false;
            }
        }

        /// <summary>节点文案原地更新，保留图上的位置、选中状态和连线。</summary>
        public void UpdateNode(RevGalNode data, bool isStart)
        {
            if (!string.IsNullOrEmpty(data?.Id) && _nodes.TryGetValue(data.Id, out var visual))
            {
                visual.UpdatePresentation(data, isStart);
            }
        }

        /// <summary>复用建图时的源节点数据，通过索引连接出口，重复出口 ID 沿用首个目标。</summary>
        private void ConnectOutputs(RevGalNode data, StoryNode visual)
        {
            Dictionary<string, string> exitTargets = null;
            foreach (var output in visual.Outputs)
            {
                var outlet = (Outlet)output.userData;
                string destination = data.Next;
                if (outlet.Exit != null)
                {
                    if (exitTargets == null)
                    {
                        exitTargets = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var exit in data.Exits)
                        {
                            if (exit?.Id != null && !exitTargets.ContainsKey(exit.Id))
                            {
                                exitTargets.Add(exit.Id, exit.Target);
                            }
                        }
                    }
                    exitTargets.TryGetValue(outlet.Exit, out destination);
                }
                if (!string.IsNullOrEmpty(destination) && _nodes.TryGetValue(destination, out var target))
                {
                    AddElement(output.ConnectTo(target.Input));
                }
            }
        }

        public void Highlight(string node)
        {
            if (string.IsNullOrEmpty(node) || !_nodes.TryGetValue(node, out var target) || selection.Contains(target))
            {
                return;
            }
            bool previous = _drawing;
            _drawing = true;
            try
            {
                ClearSelection();
                AddToSelection(target);
            }
            finally
            {
                _drawing = previous;
            }
        }

        private GraphViewChange ApplyGraphChange(GraphViewChange change)
        {
            if (_drawing || _story == null)
            {
                return change;
            }

            Undo.RecordObject(_story, "编辑 Gal 剧情图");
            bool structural = false;
            foreach (var item in change.elementsToRemove ?? new List<GraphElement>())
            {
                if (item is Edge edge && edge.output.userData is Outlet outlet)
                {
                    SetDestination(outlet, "");
                    structural = true;
                }
                else if (item is StoryNode node)
                {
                    RemoveNode(node.Id);
                    structural = true;
                }
            }
            foreach (var edge in change.edgesToCreate ?? new List<Edge>())
            {
                if (edge.output.userData is Outlet outlet && edge.input.node is StoryNode target)
                {
                    SetDestination(outlet, target.Id);
                    structural = true;
                }
            }
            foreach (var item in change.movedElements ?? new List<GraphElement>())
            {
                if (item is StoryNode moved && Find(moved.Id) is RevGalNode node)
                {
                    node.Position = moved.GetPosition().position;
                }
            }
            EditorUtility.SetDirty(_story);
            _edited(structural);
            return change;
        }

        private void RemoveNode(string id)
        {
            _story.Definition.Nodes.RemoveAll(node => node?.Id == id);
            if (_story.Definition.Start == id)
            {
                _story.Definition.Start = _story.Definition.Nodes.FirstOrDefault(x => x != null)?.Id ?? "";
            }
            foreach (var node in _story.Definition.Nodes.Where(x => x != null))
            {
                if (node.Next == id)
                {
                    node.Next = "";
                }
                foreach (var exit in node.Exits ?? new List<RevGalEdge>())
                {
                    if (exit?.Target == id)
                    {
                        exit.Target = "";
                    }
                }
            }
        }

        private void SetDestination(Outlet outlet, string target)
        {
            var node = Find(outlet.Node);
            if (node == null)
            {
                return;
            }
            if (outlet.Exit == null)
            {
                node.Next = target;
            }
            else if (node.Exits?.Find(x => x?.Id == outlet.Exit) is RevGalEdge exit)
            {
                exit.Target = target;
            }
        }

        private RevGalNode Find(string id)
        {
            return _story.Definition.Nodes.Find(node => node?.Id == id);
        }
    }

    internal static class RevGalEditorLabels
    {
        public static string Kind(RevGalNodeKind value)
        {
            return value switch
            {
                RevGalNodeKind.Line => "对白 / 旁白",
                RevGalNodeKind.Choice => "玩家选项",
                RevGalNodeKind.Branch => "条件分支",
                RevGalNodeKind.Signal => "宿主信号",
                _ => "结局"
            };
        }
    }
}

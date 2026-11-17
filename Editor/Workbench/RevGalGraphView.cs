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

            public StoryNode(RevGalNode data, bool isStart, Action<string> select)
            {
                Id = data.Id;
                _select = select;
                title = (isStart ? "起点 · " : "") + (string.IsNullOrWhiteSpace(data.Title) ? data.Id : data.Title);
                titleContainer.style.backgroundColor = ColorFor(data.Kind);
                style.width = 230;
                var type = new Label(RevGalEditorLabels.Kind(data.Kind));
                type.style.color = new Color(.7f, .82f, .89f);
                mainContainer.Add(type);

                if (!string.IsNullOrWhiteSpace(data.Text))
                {
                    var summary = new Label(data.Text.Length > 60 ? data.Text.Substring(0, 60) + "…" : data.Text);
                    summary.style.whiteSpace = WhiteSpace.Normal;
                    summary.style.marginLeft = 7;
                    summary.style.marginRight = 7;
                    mainContainer.Add(summary);
                }
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
                                string label = string.IsNullOrEmpty(exit.Label) ? "条件 " + (index + 1) : exit.Label;
                                AddOutlet(label, exit.Id);
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
                    AddElement(visual);
                }
                foreach (var node in _nodes.Values)
                {
                    foreach (Port output in node.Outputs)
                    {
                        string destination = Destination((Outlet)output.userData);
                        if (!string.IsNullOrEmpty(destination) && _nodes.TryGetValue(destination, out var target))
                        {
                            AddElement(output.ConnectTo(target.Input));
                        }
                    }
                }
                Highlight(selection);
            }
            finally
            {
                _drawing = false;
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

        private string Destination(Outlet outlet)
        {
            var node = Find(outlet.Node);
            return outlet.Exit == null ? node?.Next : node?.Exits?.Find(x => x?.Id == outlet.Exit)?.Target;
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

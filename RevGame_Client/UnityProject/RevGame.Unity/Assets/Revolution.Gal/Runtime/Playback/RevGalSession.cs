using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>宿主决定本地化、信号处理与持久化。默认 UI 也可使用同一组选项。</summary>
    public sealed class RevGalOptions
    {
        public IRevGalStorage Storage;
        /// <summary>没有传入 Storage 时是否安装默认 PlayerPrefs 实现。试读和无需持久化的宿主可关闭。</summary>
        public bool UseDefaultStorage = true;
        public Func<string, string, string> ResolveText;
        public Func<RevGalSession, RevGalSignal, RevTask> HandleSignal;
        public float CharactersPerSecond = 42f;
        public float AutoDelaySeconds = 1.8f;
        public bool WaitForVoice = true;
        public float MusicVolume = 0.65f;
        public float VoiceVolume = 1f;
        public float SoundVolume = 0.8f;
        public int RollbackLimit = 64;
        public int HistoryLimit = 512;

        internal RevGalOptions Copy()
        {
            return (RevGalOptions)MemberwiseClone();
        }
    }

    public sealed class RevGalSignal
    {
        public string Name { get; }
        public string Argument { get; }
        public int Token { get; }

        internal RevGalSignal(string name, string argument, int token)
        {
            Name = name;
            Argument = argument;
            Token = token;
        }
    }

    public sealed class RevGalChoice
    {
        public string Id { get; }
        public string Text { get; }
        public bool Enabled { get; }

        internal RevGalChoice(string id, string text, bool enabled)
        {
            Id = id;
            Text = text;
            Enabled = enabled;
        }
    }

    public sealed class RevGalFrame
    {
        private readonly RevGalStage _stage;
        public int Token { get; }
        public string Id { get; }
        public RevGalNodeKind Kind { get; }
        public string Title { get; }
        public string Speaker { get; }
        public string Text { get; }
        public string Voice { get; }
        public string Sound { get; }
        public bool WasRead { get; }
        public RevGalSignal Signal { get; }
        public RevGalStage Stage => _stage.Copy();
        public IReadOnlyList<RevGalChoice> Choices { get; }

        internal RevGalFrame(RevGalNode node, RevGalStage stage, int token, bool wasRead, string speaker, string text, RevGalChoice[] choices)
        {
            _stage = stage.Copy();
            Token = token;
            Id = node.Id;
            Kind = node.Kind;
            Title = node.Title;
            Speaker = speaker;
            Text = text;
            Voice = node.Voice;
            Sound = node.Sound;
            WasRead = wasRead;
            Choices = Array.AsReadOnly(choices);
            Signal = node.Kind == RevGalNodeKind.Signal ? new RevGalSignal(node.Signal, node.Argument, token) : null;
        }
    }

    /// <summary>保存事务式推进、回退与存档状态。由宿主驱动，纯剧情会话不依赖面板或输入系统。</summary>
    public sealed class RevGalSession : IDisposable
    {
        /// <summary>保存这一份帧订阅的所有权，释放时解除对宿主视图的引用。</summary>
        private sealed class FrameObservation : IDisposable
        {
            private RevGalSession _session;
            private Action<RevGalFrame> _observer;

            public FrameObservation(RevGalSession session, Action<RevGalFrame> observer)
            {
                _session = session;
                _observer = observer;
                session.Changed += observer;
            }

            public void Dispose()
            {
                if (_session != null)
                {
                    _session.Changed -= _observer;
                    _session = null;
                    _observer = null;
                }
            }
        }

        private sealed class State
        {
            public RevGalNode Node;
            public RevGalStage Stage = new RevGalStage();
            public Dictionary<string, int> Values;
            public List<RevGalHistoryEntry> History = new List<RevGalHistoryEntry>();

            public State Copy()
            {
                return new State
                {
                    Node = Node,
                    Stage = Stage.Copy(),
                    Values = new Dictionary<string, int>(Values, StringComparer.Ordinal),
                    History = new List<RevGalHistoryEntry>(History)
                };
            }
        }

        private readonly RevGalDefinition _definition;
        private readonly Dictionary<string, RevGalNode> _nodes;
        private readonly Dictionary<string, int> _initial;
        private readonly List<State> _rollback = new List<State>();
        private readonly HashSet<string> _read;
        private readonly HashSet<string> _endings;
        private readonly HashSet<string> _gallery;
        private State _state;
        private int _token;
        // 只占用当前帧的信号。旧回调结束时不能覆盖新的占用记录。
        private int _dispatchingSignal;
        private bool _progressDirty;
        private bool _changing;

        public string StoryId => _definition.Id;
        public string Title => _definition.Title;
        public int Revision => _definition.Revision;
        public RevGalOptions Options { get; }
        public RevGalFrame Frame { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool CanRollback => !IsDisposed && Frame.Kind != RevGalNodeKind.Signal && _rollback.Count > 0;
        public bool CanSave => !IsDisposed && Frame.Kind != RevGalNodeKind.Signal;
        public event Action<RevGalFrame> Changed;

        internal RevGalSession(RevGalDefinition definition, RevGalOptions options, string entry, string slot = null)
        {
            var errors = RevGalValidation.Validate(definition).Where(issue => !issue.Warning).ToArray();
            if (errors.Length > 0)
            {
                throw new ArgumentException(string.Join("；", errors.Select(issue => (issue.Node ?? "剧情") + "：" + issue.Message)));
            }
            // SO 是编辑数据，运行会话独占副本，支持独立并行预览与播放。
            _definition = JsonUtility.FromJson<RevGalDefinition>(JsonUtility.ToJson(definition));
            Options = options ?? new RevGalOptions();
            _nodes = _definition.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            _initial = RevGalValidation.Items(_definition.Variables).ToDictionary(variable => variable.Id, variable => variable.Initial, StringComparer.Ordinal);
            var progress = Options.Storage?.ReadProgress(StoryId);
            if (progress != null && progress.Schema != 1)
            {
                throw new ArgumentException("已读进度版本不支持");
            }
            _read = Set(progress?.Read);
            _endings = Set(progress?.Endings);
            _gallery = Set(progress?.Gallery);
            if (!string.IsNullOrEmpty(slot))
            {
                if (!Restore(Options.Storage?.ReadSlot(StoryId, slot), true))
                {
                    throw new InvalidOperationException("存档不存在或与当前剧情修订不兼容：" + slot);
                }
                return;
            }
            var start = new State { Values = new Dictionary<string, int>(_initial, StringComparer.Ordinal) };
            var unlocked = Enter(start, string.IsNullOrEmpty(entry) ? _definition.Start : entry);
            Commit(start, unlocked);
        }

        /// <summary>登记宿主的帧呈现函数并立即交付当前帧。宿主关闭时释放订阅。</summary>
        public IDisposable Observe(Action<RevGalFrame> observer)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(RevGalSession));
            }
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            var observation = new FrameObservation(this, observer);
            try
            {
                observer(Frame);
                return observation;
            }
            catch
            {
                observation.Dispose();
                throw;
            }
        }

        public bool Advance()
        {
            return Frame.Kind == RevGalNodeKind.Line && Move(_state.Node.Next, null, false);
        }

        /// <summary>用于异步界面输入。旧页的继续按钮不会推进新页。</summary>
        public bool Advance(int frameToken)
        {
            return Frame.Token == frameToken && Advance();
        }

        public bool Choose(string choiceId)
        {
            if (Frame.Kind != RevGalNodeKind.Choice)
            {
                return false;
            }
            var edge = _state.Node.Exits.FirstOrDefault(item => item.Id == choiceId);
            return edge != null && Matches(edge, _state.Values) && Move(edge.Target, edge, false);
        }

        /// <summary>用当前帧与选项 ID 处理选择，防止旧页的同名按钮误选新页。</summary>
        public bool Choose(string choiceId, int frameToken)
        {
            return Frame.Token == frameToken && Choose(choiceId);
        }

        /// <summary>宿主完成信号后再推进。Token 防止旧异步回调推进了新节点。</summary>
        public bool CompleteSignal(int token)
        {
            return Frame.Kind == RevGalNodeKind.Signal && Frame.Token == token && Move(_state.Node.Next, null, true);
        }

        /// <summary>运行宿主配置的信号处理，成功后推进发送时的那一帧。重复、过期或已释放时返回 false。</summary>
        public async RevTask<bool> DispatchSignalAsync(int frameToken)
        {
            var handler = Options.HandleSignal;
            var signal = Frame.Signal;
            if (IsDisposed || signal == null || signal.Token != frameToken || handler == null || _dispatchingSignal == frameToken)
            {
                return false;
            }
            _dispatchingSignal = frameToken;
            try
            {
                // Observe 或 Changed 可以同步触发请求。等待会话退出提交段后再向宿主交付。
                await RevTask.Yield();
                if (IsDisposed || Frame.Token != frameToken)
                {
                    return false;
                }
                await handler(this, signal);
                return CompleteSignal(frameToken);
            }
            finally
            {
                if (_dispatchingSignal == frameToken)
                {
                    _dispatchingSignal = 0;
                }
            }
        }

        public bool Rollback()
        {
            if (!CanRollback || _changing)
            {
                return false;
            }
            var previous = _rollback[_rollback.Count - 1];
            var frame = Describe(previous, _token + 1);
            _rollback.RemoveAt(_rollback.Count - 1);
            Publish(previous, frame);
            return true;
        }

        /// <summary>文字完整落屏后标为已读。当前 Frame 的 WasRead 保留进节点前的判定，供已读跳过使用。</summary>
        public bool MarkRead(int token)
        {
            if (IsDisposed || Frame.Token != token || Frame.Kind != RevGalNodeKind.Line)
            {
                return false;
            }
            _progressDirty |= _read.Add(Frame.Id);
            return true;
        }

        public bool TryGetVariable(string id, out int value)
        {
            value = 0;
            return id != null && _state.Values.TryGetValue(id, out value);
        }

        public IReadOnlyList<RevGalVariableValue> Variables()
        {
            return _state.Values.Select(pair => new RevGalVariableValue { Id = pair.Key, Value = pair.Value }).ToArray();
        }

        public IReadOnlyList<string> History()
        {
            return _state.History.Select(item =>
            {
                var node = _nodes[item.Node];
                if (!string.IsNullOrEmpty(item.Choice))
                {
                    var choice = node.Exits.First(exit => exit.Id == item.Choice);
                    return "› " + Text(choice.TextKey, choice.Label);
                }
                string speaker = Text(node.SpeakerKey, node.Speaker);
                return (string.IsNullOrEmpty(speaker) ? "" : speaker + "  ") + Text(node.TextKey, node.Text);
            }).ToArray();
        }

        public RevGalProgress Progress()
        {
            return new RevGalProgress { Read = Sorted(_read), Endings = Sorted(_endings), Gallery = Sorted(_gallery) };
        }

        /// <summary>收藏项按可读状态和逻辑背景路径提供给宿主画廊。</summary>
        public bool TryGetGalleryImage(string id, out string path)
        {
            path = _definition.Nodes.FirstOrDefault(n => n.GalleryId == id)?.Stage?.Background;
            return _gallery.Contains(id) && !string.IsNullOrEmpty(path) && path != RevGalStage.Clear;
        }

        public RevGalSave Capture()
        {
            if (!CanSave)
            {
                throw new InvalidOperationException("等待宿主信号期间不能建立存档");
            }
            return new RevGalSave
            {
                Story = StoryId, Revision = Revision, Node = _state.Node.Id, Stage = _state.Stage.Copy(),
                Variables = Variables().ToList(),
                History = _state.History.Select(entry => new RevGalHistoryEntry { Node = entry.Node, Choice = entry.Choice }).ToList()
            };
        }

        public void SaveSlot(string slot)
        {
            if (Options.Storage == null)
            {
                throw new InvalidOperationException("此会话没有存储实现");
            }
            Options.Storage.WriteSlot(StoryId, slot, Capture());
            FlushProgress();
        }

        public bool LoadSlot(string slot)
        {
            var save = Options.Storage?.ReadSlot(StoryId, slot);
            return save != null && Restore(save);
        }

        public bool Restore(RevGalSave save)
        {
            return Restore(save, false);
        }

        private bool Restore(RevGalSave save, bool opening)
        {
            if (IsDisposed || (!opening && !CanSave) || _changing || save == null || save.Schema != 1 || save.Story != StoryId || save.Revision != Revision
                || string.IsNullOrEmpty(save.Node) || !_nodes.TryGetValue(save.Node, out var node)
                || node.Kind == RevGalNodeKind.Branch || node.Kind == RevGalNodeKind.Signal
                || save.Variables == null || save.Variables.Count != _initial.Count || !ValidStage(save.Stage))
            {
                return false;
            }
            var restored = new State { Node = node, Stage = save.Stage.Copy(), Values = new Dictionary<string, int>(StringComparer.Ordinal) };
            foreach (var value in save.Variables)
            {
                if (value == null || string.IsNullOrEmpty(value.Id) || !_initial.ContainsKey(value.Id) || restored.Values.ContainsKey(value.Id))
                {
                    return false;
                }
                restored.Values.Add(value.Id, value.Value);
            }
            foreach (var item in RevGalValidation.Items(save.History))
            {
                if (item == null || string.IsNullOrEmpty(item.Node) || !_nodes.TryGetValue(item.Node, out var entry)
                    || (string.IsNullOrEmpty(item.Choice) ? entry.Kind != RevGalNodeKind.Line
                        : entry.Kind != RevGalNodeKind.Choice || !entry.Exits.Any(exit => exit.Id == item.Choice)))
                {
                    return false;
                }
                restored.History.Add(new RevGalHistoryEntry { Node = item.Node, Choice = item.Choice });
            }
            if (node.Kind == RevGalNodeKind.Choice && !node.Exits.Any(edge => Matches(edge, restored.Values)))
            {
                return false;
            }
            TrimHistory(restored);
            var frame = Describe(restored, _token + 1);
            _rollback.Clear();
            Publish(restored, frame);
            return true;
        }

        public void FlushProgress()
        {
            if (_progressDirty && Options.Storage != null)
            {
                // 同一剧情可同时有多个独立会话。落盘前合并已存在的玩家进度。
                var stored = Options.Storage.ReadProgress(StoryId);
                if (stored != null)
                {
                    if (stored.Schema != 1)
                    {
                        throw new InvalidOperationException("已有 Gal 进度版本不支持");
                    }
                    _read.UnionWith(Set(stored.Read));
                    _endings.UnionWith(Set(stored.Endings));
                    _gallery.UnionWith(Set(stored.Gallery));
                }
                Options.Storage.WriteProgress(StoryId, Progress());
                _progressDirty = false;
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }
            IsDisposed = true;
            _rollback.Clear();
            Changed = null;
            FlushProgress();
        }

        private bool Move(string next, RevGalEdge edge, bool signalBoundary)
        {
            if (IsDisposed || _changing)
            {
                return false;
            }
            var original = _state;
            var candidate = original.Copy();
            ApplyEffects(edge?.Effects, candidate.Values);
            if (edge != null)
            {
                candidate.History.Add(new RevGalHistoryEntry { Node = original.Node.Id, Choice = edge.Id });
            }
            var unlocked = Enter(candidate, next);
            var frame = Describe(candidate, _token + 1);
            if (original.Node.Kind == RevGalNodeKind.Line)
            {
                MarkRead(Frame.Token);
            }
            if (signalBoundary)
            {
                // 业务信号可以产生不可逆副作用，回退只能停在该信号之后。
                _rollback.Clear();
            }
            else
            {
                int limit = Mathf.Clamp(Options.RollbackLimit, 0, 256);
                if (limit > 0)
                {
                    _rollback.Add(original);
                    if (_rollback.Count > limit)
                    {
                        _rollback.RemoveRange(0, _rollback.Count - limit);
                    }
                }
            }
            ApplyProgress(candidate, unlocked);
            Publish(candidate, frame);
            return true;
        }

        private List<string> Enter(State candidate, string target)
        {
            var unlocked = new List<string>();
            for (int steps = 0; steps < 4096; steps++)
            {
                if (string.IsNullOrEmpty(target) || !_nodes.TryGetValue(target, out var node))
                {
                    throw new InvalidOperationException("没有可进入的剧情节点：" + target);
                }
                ApplyEffects(node.Effects, candidate.Values);
                candidate.Stage.Apply(node.Stage);
                if (!string.IsNullOrEmpty(node.GalleryId))
                {
                    unlocked.Add(node.GalleryId);
                }
                if (node.Kind == RevGalNodeKind.Branch)
                {
                    var branch = RevGalValidation.Items(node.Exits).FirstOrDefault(exit => Matches(exit, candidate.Values));
                    ApplyEffects(branch?.Effects, candidate.Values);
                    target = branch?.Target ?? node.Next;
                    continue;
                }
                if (node.Kind == RevGalNodeKind.Choice && !node.Exits.Any(exit => Matches(exit, candidate.Values)))
                {
                    target = node.Next;
                    continue;
                }
                candidate.Node = node;
                if (node.Kind == RevGalNodeKind.Line)
                {
                    candidate.History.Add(new RevGalHistoryEntry { Node = node.Id });
                }
                TrimHistory(candidate);
                return unlocked;
            }
            throw new InvalidOperationException("自动分支没有到达可交互节点，请检查循环");
        }

        private void Commit(State state, List<string> unlocked)
        {
            var frame = Describe(state, _token + 1);
            ApplyProgress(state, unlocked);
            Publish(state, frame);
        }

        private void ApplyProgress(State state, List<string> unlocked)
        {
            foreach (string id in unlocked)
            {
                _progressDirty |= _gallery.Add(id);
            }
            if (state.Node.Kind == RevGalNodeKind.End)
            {
                _progressDirty |= _endings.Add(state.Node.Id);
            }
        }

        private void Publish(State state, RevGalFrame frame)
        {
            _state = state;
            Frame = frame;
            _token = frame.Token;
            _changing = true;
            try
            {
                Changed?.Invoke(frame);
            }
            finally
            {
                _changing = false;
            }
        }

        private RevGalFrame Describe(State state, int token)
        {
            var node = state.Node;
            var choices = new List<RevGalChoice>();
            if (node.Kind == RevGalNodeKind.Choice)
            {
                foreach (var exit in node.Exits)
                {
                    bool enabled = Matches(exit, state.Values);
                    if (enabled || exit.ShowWhenLocked)
                    {
                        choices.Add(new RevGalChoice(exit.Id, Text(exit.TextKey, exit.Label), enabled));
                    }
                }
            }
            return new RevGalFrame(node, state.Stage, token, _read.Contains(node.Id),
                Text(node.SpeakerKey, node.Speaker), Text(node.TextKey, node.Text), choices.ToArray());
        }

        private string Text(string key, string fallback)
        {
            return (!string.IsNullOrEmpty(key) ? Options.ResolveText?.Invoke(key, fallback) : null) ?? fallback ?? "";
        }

        private void TrimHistory(State state)
        {
            int limit = Mathf.Clamp(Options.HistoryLimit, 0, 10000);
            if (state.History.Count > limit)
            {
                state.History.RemoveRange(0, state.History.Count - limit);
            }
        }

        private static bool Matches(RevGalEdge edge, Dictionary<string, int> values)
        {
            foreach (var condition in RevGalValidation.Items(edge.Conditions))
            {
                int value = values[condition.Variable];
                bool met = condition.Comparison switch
                {
                    RevGalComparison.Equal => value == condition.Value,
                    RevGalComparison.NotEqual => value != condition.Value,
                    RevGalComparison.Greater => value > condition.Value,
                    RevGalComparison.AtLeast => value >= condition.Value,
                    RevGalComparison.Less => value < condition.Value,
                    RevGalComparison.AtMost => value <= condition.Value,
                    _ => false
                };
                if (!met)
                {
                    return false;
                }
            }
            return true;
        }

        private static void ApplyEffects(List<RevGalEffect> effects, Dictionary<string, int> values)
        {
            foreach (var effect in RevGalValidation.Items(effects))
            {
                values[effect.Variable] = effect.Operation == RevGalOperation.Set
                    ? effect.Value : checked(values[effect.Variable] + effect.Value);
            }
        }

        private static bool ValidStage(RevGalStage stage)
        {
            return stage != null && new[] { stage.Background, stage.Left, stage.Center, stage.Right, stage.Music }
                .All(path => string.IsNullOrEmpty(path) || RevGalValidation.IsLogicalPath(path));
        }

        private static HashSet<string> Set(List<string> items)
        {
            return new HashSet<string>(RevGalValidation.Items(items).Where(RevGalValidation.IsId), StringComparer.Ordinal);
        }

        private static List<string> Sorted(HashSet<string> items)
        {
            return items.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
    }
}

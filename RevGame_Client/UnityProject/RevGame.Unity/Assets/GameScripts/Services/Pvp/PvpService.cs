using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fantasy;
using Revolution;
using RevGame.Pvp.Networking;
using UnityEngine;

namespace RevGame.Pvp
{
    public enum PvpConnectionMode { Local, Server, Lan }

    /// <summary>非池化的棋谱副本。网络消息属于Fantasy消息池，收到后复制这些字段，不能跨帧持有消息对象。</summary>
    [Serializable]
    public sealed class PvpMove
    {
        public int sequence, side, kind, x, y;
        public long elapsedMs;
        public string before, after;
    }

    /// <summary>局域网只传小型确定性命令。版本/规则先握手，重同步先完整校验，再原子替换本地棋局。</summary>
    [Serializable]
    internal sealed class PvpLanMessage
    {
        public string type, version, rules, name;
        public PvpMove move;
        public List<PvpMove> moves;
        public int from, total;
    }

    /// <summary>
    /// 九路围棋的事实所有者。与六章BattleService、无尽EndlessService没有数据依赖。
    /// 网络只在进入本模式后创建；失败写Status并保留返回按钮，绝不重启Bootstrap或清其它游戏域。
    /// </summary>
    public sealed partial class PvpService : IDisposable
    {
        public const string ProtocolVersion = "revgo-1";
        public PvpConfig Config { get; private set; }
        public GoCore Core { get; private set; }
        public PvpConnectionMode Mode { get; private set; }
        public GoSide MySide { get; private set; }
        public string Status { get; private set; } = "请选择对战方式";
        public string BlackName { get; private set; } = "黑方";
        public string WhiteName { get; private set; } = "白方";
        public string Nickname { get; private set; }
        public string Endpoint { get; private set; }
        public bool Queued { get; private set; }
        public bool Connected { get; private set; }
        public bool HintsVisible { get; private set; } = true;
        public int HintsRemaining { get; private set; }
        public int SelectedPoint { get; private set; } = -1;
        public int Revision { get; private set; }
        public string PreviewText { get; private set; } = "点击交叉点落子";
        public string RoomId { get; private set; } = "";
        public LanPeerTransport Lan { get; private set; }
        public IReadOnlyList<PvpMove> Moves { get { return _moves; } }
        private readonly List<PvpMove> _moves = new List<PvpMove>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _turnStarted, _queueStarted;
        private FantasyGoTransport _server;
        private string _resumeToken = "";
        private bool _disposed, _awaiting, _lanHello;
        // 一个尚未回执的命令只占一个序号；旧状态推送不能把它误当作已完成，也不能因此允许连续下两手。
        private int _pendingSequence;
        private bool _recoveringServer;
        private GoClock _goClock;
        private bool _leaving, _leaveSent;
        private double _leaveDeadline, _leaveEarliest;
        private readonly Queue<PvpLanMessage> _syncOutgoing = new Queue<PvpLanMessage>();
        private List<PvpMove> _syncMoves;
        private int _syncTotal;

        /// <summary>模式配置在GameFlow异步加载表之后传入，构造服务时不读尚未加载的表。</summary>
        public void Begin(PvpConfig config)
        {
            Dispose();
            _disposed = false;
            Config = config;
            Core = null;
            _moves.Clear();
            HintsRemaining = config.hintCount;
            HintsVisible = true;
            SelectedPoint = -1;
            ResetPresentation();
            Status = "选择服务器匹配，或搜索同一局域网中的玩家";
            Revision++;
        }

        public void StartLocal()
        {
            if (_leaving)
            {
                return;
            }
            CloseConnection();
            RoomId = string.Empty;
            Mode = PvpConnectionMode.Local;
            MySide = GoSide.Empty;
            BlackName = "本地黑方";
            WhiteName = "本地白方";
            NewGame();
            Status = "本地双人练习 · 双方轮流落子";
        }

        public void StartServer(string endpoint, string nickname)
        {
            if (_leaving)
            {
                return;
            }
            string requestedEndpoint = (endpoint ?? string.Empty).Trim();
            bool resume = Mode == PvpConnectionMode.Server && Core != null && !string.IsNullOrEmpty(_resumeToken) && requestedEndpoint == Endpoint;
            CloseConnection();
            Mode = PvpConnectionMode.Server;
            Endpoint = requestedEndpoint;
            Nickname = CleanName(nickname);
            _recoveringServer = resume;
            if (!resume)
            {
                Core = null;
                _moves.Clear();
                _resumeToken = string.Empty;
                ResetPresentation();
            }
            Status = resume ? "正在恢复服务器对局，原有棋谱与思考时间已保留…" : "正在连接匹配服务器…";
            _server = new FantasyGoTransport();
            _server.StateReceived += ReceiveServer;
            _server.Error += OnNetworkError;
            _server.RequestRejected += OnRequestRejected;
            _server.Connect(Endpoint, Nickname, Config.RulesJson, Config.rookie ? "rookie" : "advanced", _resumeToken);
            Revision++;
        }

        public void Match()
        {
            if (_server == null || !_server.Connected)
            {
                Status = "服务器尚未连接，可重试连接或使用局域网对战";
                return;
            }
            if (_server.Busy)
            {
                Status = "正在等待服务器回应，请稍后操作";
                return;
            }
            _server.Match(Queued);
            if (!Queued)
            {
                _queueStarted = _clock.Elapsed.TotalSeconds;
            }
        }

        public void StartLan(string nickname)
        {
            if (_leaving)
            {
                return;
            }
            CloseConnection();
            RoomId = string.Empty;
            Mode = PvpConnectionMode.Lan;
            Nickname = CleanName(nickname);
            Core = null;
            _moves.Clear();
            var options = new LanPeerOptions(Config.discoveryPort, Config.announceSeconds, Config.peerExpirySeconds,
                Config.invitationTimeoutSeconds, Config.connectTimeoutSeconds);
            Lan = new LanPeerTransport(options);
            Lan.Connected += OnLanConnected;
            Lan.PayloadReceived += ReceiveLan;
            Lan.Disconnected += OnNetworkError;
            Lan.Changed += OnLanStatusChanged;
            if (!Lan.StartDiscovery(Nickname, out string reason))
            {
                Status = reason;
            }
            else
            {
                Status = "正在搜索同一局域网设备；双方都需打开此页面";
            }
            Revision++;
        }
        private void OnLanStatusChanged()
        {
            if (Lan != null && Core == null)
            {
                Status = Lan.Status;
                Revision++;
            }
        }

        public bool Invite(string peerId)
        {
            string reason = "请先搜索局域网设备";
            bool accepted = Lan != null && Lan.Invite(peerId, out reason);
            if (!accepted)
            {
                Status = reason;
            }
            return accepted;
        }
        public bool ReplyInvitation(bool accept)
        {
            string reason = "当前没有可回复的局域网邀请";
            bool accepted = Lan != null && Lan.ReplyInvitation(accept, out reason);
            if (!accepted)
            {
                Status = reason;
            }
            return accepted;
        }
        private void OnLanConnected(bool isHost)
        {
            MySide = isHost ? GoSide.Black : GoSide.White;
            _lanHello = false;
            SendLan(new PvpLanMessage { type = "hello", version = ProtocolVersion, rules = Config.RulesJson, name = Nickname });
            Status = "正在核对双方规则版本…";
        }
        private void ReceiveLan(string payload)
        {
            try
            {
                PvpLanMessage message = JsonUtility.FromJson<PvpLanMessage>(payload);
                if (message == null)
                {
                    throw new InvalidOperationException("局域网消息为空");
                }
                if (message.type == "hello")
                {
                    if (_lanHello || message.version != ProtocolVersion || message.rules != Config.RulesJson)
                    {
                        throw new InvalidOperationException("双方版本或场次规则不一致，请使用同一版本和场次");
                    }
                    _lanHello = true;
                    BlackName = MySide == GoSide.Black ? Nickname : CleanName(message.name);
                    WhiteName = MySide == GoSide.White ? Nickname : CleanName(message.name);
                    NewGame();
                    Connected = true;
                    Status = "局域网直连成功 · 黑先白后";
                    return;
                }
                if (!_lanHello || Core == null)
                {
                    throw new InvalidOperationException("尚未完成规则握手");
                }
                if (message.type == "move")
                {
                    if (message.move == null || message.move.side == (int)MySide)
                    {
                        throw new InvalidOperationException("对方棋子身份错误");
                    }
                    if (message.move.sequence <= Core.Sequence)
                    {
                        return;
                    }
                    if (!ApplyMove(message.move, out string reason))
                    {
                        Status = "棋谱校验失败：" + reason;
                        SendLan(new PvpLanMessage { type = "sync-request" });
                        Connected = false;
                    }
                }
                else if (message.type == "sync-request")
                {
                    if (_moves.Count == 0)
                    {
                        SendLan(new PvpLanMessage { type = "sync", from = 0, total = 0, moves = new List<PvpMove>() });
                    }
                    // 每片最多20条，保持在UTP单消息上限内；接收方收齐后才替换，避免部分棋谱落屏。
                    for (int first = 0; first < _moves.Count; first += 20)
                    {
                        _syncOutgoing.Enqueue(new PvpLanMessage { type = "sync", from = first, total = _moves.Count,
                            moves = _moves.GetRange(first, Math.Min(20, _moves.Count - first)) });
                    }
                }
                else if (message.type == "sync")
                {
                    if (message.total < _moves.Count || message.total > 4096 || message.moves == null || message.moves.Count > 20)
                    {
                        throw new InvalidOperationException("重同步棋谱长度非法");
                    }
                    if (message.from == 0)
                    {
                        _syncMoves = new List<PvpMove>();
                        _syncTotal = message.total;
                    }
                    if (_syncMoves == null || message.total != _syncTotal || message.from != _syncMoves.Count || message.from + message.moves.Count > message.total)
                    {
                        throw new InvalidOperationException("重同步分片顺序非法");
                    }
                    _syncMoves.AddRange(message.moves);
                    if (_syncMoves.Count == message.total)
                    {
                        ValidatePrefix(_syncMoves);
                        ReplaceHistory(_syncMoves);
                        _syncMoves = null;
                        Connected = true;
                        Status = "棋谱已重新同步";
                    }
                }
                else if (message.type == "leave")
                {
                    OnNetworkError("对方已离开对局，可返回模式选择");
                }
            }
            catch (Exception error)
            {
                OnNetworkError("局域网消息被拒绝：" + error.Message);
            }
        }
        private void SendLan(PvpLanMessage message)
        {
            string reason = "局域网连接已关闭";
            if (Lan == null || !Lan.Send(JsonUtility.ToJson(message), out reason))
            {
                OnNetworkError(Lan == null ? "局域网连接已关闭" : reason);
            }
        }

        private void ReceiveServer(GoRelayState state)
        {
            if (_disposed || state == null)
            {
                return;
            }
            try
            {
                Connected = _server.Connected;
                _resumeToken = _server.ResumeToken;
                Queued = state.Phase == "queued";
                RoomId = state.RoomId ?? "";
                Status = string.IsNullOrEmpty(state.Reason) ? (Queued ? "正在匹配，可随时取消" : "服务器已连接") : state.Reason;
                if (string.IsNullOrEmpty(RoomId))
                {
                    Revision++;
                    return;
                }
                if (state.CoreVersion != ProtocolVersion || state.RulesJson != Config.RulesJson)
                {
                    throw new InvalidOperationException("服务器房间的规则版本不匹配");
                }
                MySide = (GoSide)state.MySide;
                if (MySide != GoSide.Black && MySide != GoSide.White)
                {
                    throw new InvalidOperationException("服务器房间的棋子身份无效");
                }
                BlackName = state.BlackName;
                WhiteName = state.WhiteName;
                var history = new List<PvpMove>();
                foreach (GoRelayCommand command in state.Commands)
                {
                    history.Add(new PvpMove { sequence = command.Sequence, side = command.Side, kind = command.Kind,
                        x = command.X, y = command.Y, elapsedMs = command.ElapsedMs, before = command.PreviousHash, after = command.ResultHash });
                }
                ValidatePrefix(history);
                ReplaceHistory(history);
                _awaiting = _pendingSequence > Core.Sequence;
                if (!_awaiting)
                {
                    _pendingSequence = 0;
                }
                _recoveringServer = false;
                if (state.Phase == "disconnected" || state.Phase == "closed")
                {
                    Connected = false;
                }
            }
            catch (Exception error)
            {
                OnNetworkError("收到的棋谱校验失败：" + error.Message);
            }
        }

        private void ValidatePrefix(List<PvpMove> history)
        {
            if (history == null || history.Count < _moves.Count || history.Count > 4096)
            {
                throw new InvalidOperationException("棋谱长度非法");
            }
            for (int i = 0; i < _moves.Count; i++)
            {
                PvpMove a = history[i], b = _moves[i];
                if (a == null || a.sequence != b.sequence || a.kind != b.kind || a.side != b.side || a.x != b.x || a.y != b.y || a.before != b.before || a.after != b.after || a.elapsedMs != b.elapsedMs)
                {
                    throw new InvalidOperationException("既有棋谱被修改，已保留本地对局");
                }
            }
        }

        private void ReplaceHistory(List<PvpMove> history)
        {
            if (Core != null && history.Count == _moves.Count)
            {
                return;
            }
            var next = new GoCore(Config.komiHalfPoints);
            var nextClock = new GoClock(Config.mainTimeSeconds, Config.byoyomiSeconds, Config.byoyomiCount);
            double nextTurnStart = _turnStarted;
            int previousCount = _moves.Count;
            foreach (PvpMove move in history)
            {
                if (!TryPrepareMove(next, nextClock, move, out GoCore acceptedCore, out GoClock acceptedClock, out string reason))
                {
                    throw new InvalidOperationException(reason);
                }
                next = acceptedCore;
                nextClock = acceptedClock;
                if (_recoveringServer && move.sequence > previousCount)
                {
                    // 已有棋谱重连时按新增命令推进原有单调时间线，不能因为重新握手白送思考时间。
                    nextTurnStart += move.elapsedMs / 1000.0;
                    if (move.kind == (int)GoAction.Resume)
                    {
                        nextTurnStart = _clock.Elapsed.TotalSeconds;
                    }
                }
            }
            Core = next;
            _goClock = nextClock;
            _moves.Clear();
            _moves.AddRange(history);
            _turnStarted = _recoveringServer ? Math.Min(_clock.Elapsed.TotalSeconds, nextTurnStart) : _clock.Elapsed.TotalSeconds;
            SelectedPoint = -1;
            Revision++;
        }

        private void NewGame()
        {
            Core = new GoCore(Config.komiHalfPoints);
            _goClock = new GoClock(Config.mainTimeSeconds, Config.byoyomiSeconds, Config.byoyomiCount);
            _moves.Clear();
            _turnStarted = _clock.Elapsed.TotalSeconds;
            HintsRemaining = Config.hintCount;
            ResetPresentation();
            SelectedPoint = -1;
            Revision++;
        }

        /// <summary>玩家和GM共用这条入口；先在副本试算，网络提交失败不会留下半手棋。</summary>
        public bool Submit(GoAction action, int x = -1, int y = -1)
        {
            if (!CanAct || _goClock == null)
            {
                Status = "对局尚未就绪或正在同步，请稍候";
                return false;
            }
            GoSide side = Mode == PvpConnectionMode.Local ? LocalActor(action) : MySide;
            bool timedAction = action == GoAction.Play || action == GoAction.Pass || action == GoAction.Timeout;
            long elapsed = timedAction && Core.Phase == GoPhase.Playing ? (long)(Math.Max(0, _clock.Elapsed.TotalSeconds - _turnStarted) * 1000) : 0;
            if (timedAction && Core.Phase == GoPhase.Playing && side == Core.Turn && _goClock.IsTimedOut(side, elapsed / 1000.0))
            {
                // 点击恰好落在读秒结束的一帧也只能认超时，不允许先落子、再在下一帧判断结束。
                action = GoAction.Timeout;
                x = -1;
                y = -1;
            }
            if (action == GoAction.Timeout && (Core.Phase != GoPhase.Playing || side != Core.Turn || !_goClock.IsTimedOut(side, elapsed / 1000.0)))
            {
                Status = "时钟尚未耗尽，不能提交超时结果";
                return false;
            }
            var next = Core.Clone();
            if (!next.TryApply(action, side, x, y, out GoPlayResult result))
            {
                Status = result.Error;
                return false;
            }
            var move = new PvpMove { sequence = next.Sequence, side = (int)side, kind = (int)action, x = x, y = y,
                elapsedMs = elapsed, before = Core.StateHash.ToString(), after = next.StateHash.ToString() };
            if (Mode == PvpConnectionMode.Server)
            {
                _awaiting = true;
                _pendingSequence = move.sequence;
                Status = "正在同步落子…";
                _server.Send(new GoRelayCommand { Sequence = move.sequence, Side = move.side, Kind = move.kind, X = x, Y = y,
                    ElapsedMs = elapsed, PreviousHash = move.before, ResultHash = move.after, Payload = "" });
                return _awaiting || Core.Sequence >= move.sequence;
            }
            if (Mode == PvpConnectionMode.Lan)
            {
                if (!Lan.Send(JsonUtility.ToJson(new PvpLanMessage { type = "move", move = move }), out string reason))
                {
                    OnNetworkError(reason);
                    return false;
                }
            }
            return ApplyMove(move, out _);
        }
        private GoSide LocalActor(GoAction action)
        {
            if (action == GoAction.ConfirmScore && Core.Phase == GoPhase.Scoring)
            {
                // 本地双人练习按两个座位依次确认；联机绝不代替对方确认。
                int confirmations = 0;
                for (int i = _moves.Count - 1; i >= 0 && _moves[i].kind == (int)GoAction.ConfirmScore; i--)
                {
                    confirmations++;
                }
                return confirmations == 0 ? GoSide.Black : GoSide.White;
            }
            return Core.Turn;
        }
        private bool ApplyMove(PvpMove move, out string reason)
        {
            if (!TryPrepareMove(Core, _goClock, move, out GoCore next, out GoClock nextClock, out reason))
            {
                Status = reason;
                return false;
            }
            Core = next;
            _goClock = nextClock;
            _moves.Add(move);
            _turnStarted = _clock.Elapsed.TotalSeconds;
            SelectedPoint = -1;
            Status = Core.Phase == GoPhase.Scoring ? "双方停一手：点选死子后分别确认；有分歧可继续对弈" : "落子已同步";
            _suggestionCount = 0;
            Revision++;
            return true;
        }
        /// <summary>规则与时钟一起在副本预检；任一失败都不发布棋面、时间、序号或部分历史。</summary>
        private static bool TryPrepareMove(GoCore core, GoClock clock, PvpMove move, out GoCore next, out GoClock nextClock, out string reason)
        {
            next = null;
            nextClock = null;
            reason = "棋谱序列或指纹错误";
            if (move == null || move.sequence != core.Sequence + 1 || move.before != core.StateHash.ToString() || move.elapsedMs < 0 || move.elapsedMs > 86400000)
            {
                return false;
            }
            GoAction action = (GoAction)move.kind;
            GoSide side = (GoSide)move.side;
            GoClock clockCopy = clock.Clone();
            if (action == GoAction.Play || action == GoAction.Pass || action == GoAction.Timeout)
            {
                if (core.Phase != GoPhase.Playing || side != core.Turn || !clockCopy.Advance(side, move.elapsedMs / 1000.0))
                {
                    reason = "计时动作不属于当前落子方，或时间数据无效";
                    return false;
                }
                bool exhausted = clockCopy.IsTimedOut(side);
                if (exhausted != (action == GoAction.Timeout))
                {
                    reason = exhausted ? "对方此手已超时，不能继续落子" : "时钟尚未耗尽，超时命令被拒绝";
                    return false;
                }
            }
            else if (move.elapsedMs != 0)
            {
                reason = "数子、认输等非计时动作不能修改思考时间";
                return false;
            }
            // 结果指纹也在副本上校验，失败不能把真实棋局推进半步。
            GoCore copy = core.Clone();
            if (!copy.TryApply((GoAction)move.kind, (GoSide)move.side, move.x, move.y, out GoPlayResult result))
            {
                reason = result.Error;
                return false;
            }
            if (copy.StateHash.ToString() != move.after)
            {
                return false;
            }
            next = copy;
            nextClock = clockCopy;
            return true;
        }
        public void SelectPoint(int x, int y)
        {
            if (!CanAct || Core.Phase == GoPhase.Finished || x < 0 || x >= 9 || y < 0 || y >= 9)
            {
                return;
            }
            if (Core.Phase == GoPhase.Scoring)
            {
                Submit(GoAction.ToggleDead, x, y);
                return;
            }
            int point = y * 9 + x;
            if (Config.rookie && SelectedPoint != point)
            {
                SelectedPoint = point;
                Preview(x, y);
                Revision++;
                return;
            }
            Submit(GoAction.Play, x, y);
        }
        public void Preview(int x, int y)
        {
            if (Core == null || Core.Phase != GoPhase.Playing)
            {
                return;
            }
            GoCore next = Core.Clone();
            if (next.TryPlay(Core.Turn, x, y, out GoPlayResult result))
            {
                PreviewText = result.CapturedCount > 0 ? "这一手提 " + result.CapturedCount + " 子" : "合法落点";
                if (Config.rookie)
                {
                    PreviewText += " · 再点一次确认";
                }
            }
            else
            {
                PreviewText = result.Error;
            }
        }
        public void ToggleHints()
        {
            HintsVisible = !HintsVisible;
            Revision++;
        }
        public void Poll()
        {
            if (_disposed)
            {
                return;
            }
            _server?.Poll();
            Lan?.Poll();
            if (_leaving)
            {
                if (!_leaveSent && _server != null && _server.Connected && !_server.Busy)
                {
                    _server.Leave();
                    _leaveSent = true;
                }
                double now = _clock.Elapsed.TotalSeconds;
                if (now >= _leaveDeadline || (now >= _leaveEarliest && (_server == null || !_server.Connected || (_leaveSent && !_server.Busy))))
                {
                    FinishLeave();
                }
                return;
            }
            if (_syncOutgoing.Count > 0 && Lan != null && Lan.IsConnected)
            {
                // 每帧只发一个分片，避免整盘棋谱一次塞满可靠传输的有界窗口。
                SendLan(_syncOutgoing.Dequeue());
            }
            // 自己的时钟耗尽由自己发出超时命令；休闲中转架构不伪装成服务端反作弊裁判。
            if (CanAct && Core.Phase == GoPhase.Playing && (Mode == PvpConnectionMode.Local || Core.Turn == MySide))
            {
                double elapsed = Math.Max(0, _clock.Elapsed.TotalSeconds - _turnStarted);
                if (_goClock.IsTimedOut(Core.Turn, elapsed))
                {
                    Submit(GoAction.Timeout);
                }
            }
            if (Queued && _clock.Elapsed.TotalSeconds - _queueStarted >= Config.matchTimeoutSeconds)
            {
                Status = "当前匹配人数较少，可继续等待、取消或改用局域网对战";
            }
        }
        private void OnNetworkError(string reason)
        {
            if (_server != null)
            {
                _resumeToken = _server.ResumeToken;
            }
            Connected = false;
            Queued = false;
            _awaiting = false;
            _pendingSequence = 0;
            Status = "连接不可用：" + reason + "。可返回继续单机游戏";
            Revision++;
        }
        /// <summary>请求拒绝不同于连接断开；释放本次等待锁，并保留可重试的棋局、连接与原始时钟。</summary>
        private void OnRequestRejected(string reason)
        {
            _awaiting = false;
            _pendingSequence = 0;
            Status = "操作未被接受：" + reason;
            Revision++;
        }
        private static string CleanName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "棋友" : value.Trim();
            return name.Length > 16 ? name.Substring(0, 16) : name;
        }
        public void LeaveRoom()
        {
            if (_leaving)
            {
                return;
            }
            _leaving = true;
            _leaveSent = false;
            _leaveDeadline = _clock.Elapsed.TotalSeconds + Config.connectTimeoutSeconds;
            _leaveEarliest = _clock.Elapsed.TotalSeconds + .15;
            if (Lan != null && Lan.IsConnected)
            {
                SendLan(new PvpLanMessage { type = "leave" });
            }
            Status = "正在离开对局…";
            if (_server == null && (Lan == null || !Lan.IsConnected))
            {
                FinishLeave();
            }
        }
        /// <summary>卸场景前等待退出回执或有界超时，避免发送队列尚未写出就关闭连接。</summary>
        public async RevTask LeaveAsync()
        {
            LeaveRoom();
            while (_leaving && !_disposed)
            {
                Poll();
                await RevTask.Yield();
            }
        }
        private void FinishLeave()
        {
            CloseConnection();
            Core = null;
            _moves.Clear();
            _resumeToken = "";
            ResetPresentation();
            Status = "已离开对局，请选择联机方式";
            Revision++;
        }
        private void CloseConnection()
        {
            _server?.Dispose();
            _server = null;
            Lan?.Dispose();
            Lan = null;
            Connected = false;
            Queued = false;
            _awaiting = false;
            _pendingSequence = 0;
            _syncMoves = null;
            _syncTotal = 0;
            _recoveringServer = false;
            _leaving = false;
            _leaveSent = false;
            _syncOutgoing.Clear();
        }
        public void Dispose()
        {
            _disposed = true;
            CloseConnection();
        }
    }
}

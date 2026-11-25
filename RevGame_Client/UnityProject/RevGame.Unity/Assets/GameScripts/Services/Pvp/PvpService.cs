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
        // 业务分流标记。决斗的 challenge、start、input、frame 走决斗专属分支。
        public string type;
        // 围棋握手版本，决斗版本另外放在 duelRequest 的指纹。
        public string version;
        // 围棋握手的规则文本。决斗开始通知复用此槽核对开始前的棋盘指纹。
        public string rules;
        // 握手昵称或决斗局号，含义由 type 决定。
        public string name;
        // 单手围棋动作。决斗开始后不再接纳。
        public PvpMove move;
        // 围棋补棋谱分片，决斗补帧使用 duelReply。
        public List<PvpMove> moves;
        // 围棋分片起始下标，从零开始。
        public int from;
        // 完整棋谱的总条数，用于确认所有分片已到达。
        public int total;
        // P2P 信封中用于能力声明、就绪和战斗输入的请求数据。
        public RevGame.Pvp.Duel.DuelRequest duelRequest;
        // P2P 宿主返回的就绪掩码、连续整帧及接收方自己的封帧回执。
        public RevGame.Pvp.Duel.DuelReply duelReply;
    }

    /// <summary>
    /// 九路围棋的事实所有者。与六章BattleService、无尽EndlessService没有数据依赖。
    /// 网络只在进入本模式后创建；失败写Status并保留返回按钮，绝不重启Bootstrap或清其它游戏域。
    /// </summary>
    public sealed partial class PvpService : IDisposable
    {
        public const string ProtocolVersion = GoCore.MatchProtocolVersion;
        private const string LocalRatingKey = "RevGame.Pvp.OnlineRating.v1";
        private const int InitialRating = 1500;
        private const int MinRating = 100;
        private const int MaxRating = 3000;
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
        /// <summary>本地人机由服务推进白方，UI/视图不能代替AI提交规则。</summary>
        public bool IsComputerGame { get; private set; }
        public double QueueElapsedSeconds { get { return Queued ? Math.Max(0, _clock.Elapsed.TotalSeconds - _queueStarted) : 0; } }
        private readonly GoHint[] _computerHints = new GoHint[3];
        private double _computerAt;
        /// <summary>保留最近一次连接失败，交互提示刷新不能覆盖故障诊断；不含身份令牌。</summary>
        public string LastNetworkError { get; private set; } = string.Empty;
        public bool HintsVisible { get; private set; } = true;
        public int SelectedPoint { get; private set; } = -1;
        public int Revision { get; private set; }
        public string PreviewText { get; private set; } = "点击格子落子";
        /// <summary>当前会话已接纳的房间身份，离场后清空。</summary>
        public string RoomId { get; private set; } = "";
        public LanPeerTransport Lan { get; private set; }
        public IReadOnlyList<PvpMove> Moves { get { return _moves; } }
        private readonly List<PvpMove> _moves = new List<PvpMove>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _turnStarted, _queueStarted;
        private FantasyGoTransport _server;
        private string _resumeToken = "";
        // 服务跨场景常驻。同地址、昵称及规则可复用连接令牌，棋力由独立的本机存档保存。
        private (string Endpoint, string Nickname, string Rules, string Kind, string Token) _matchIdentity;
        private bool _disposed, _awaiting, _lanHello;
        // 一个尚未回执的命令只占一个序号；旧状态推送不能把它误当作已完成，也不能因此允许连续下两手。
        private int _pendingSequence;
        private bool _recoveringServer;
        // 增量先到或本地缺少前序时，在当前 RPC 完成后请求一份完整棋谱。
        private bool _needsServerSync;
        /// <summary>权威字段缺失时禁止操作，直到合法完整快照恢复。</summary>
        private bool _roomStateIncomplete;
        /// <summary>尚未校验完整的新房身份，用于拒绝其它房间的迟到数据。</summary>
        private string _pendingRoomId = string.Empty;
        private double _serverSyncRetryAt;
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
            HintsVisible = true;
            SelectedPoint = -1;
            ResetPresentation();
            Status = "请选择一种对战方式";
            Revision++;
        }

        public void StartLocal()
        {
            StartLocalGame(false);
        }

        public void StartComputer()
        {
            StartLocalGame(true);
        }

        /// <summary>先确定座位和终局规则，再创建新棋盘，重开不会沿用上一局的规则。</summary>
        private void StartLocalGame(bool computer)
        {
            if (_leaving)
            {
                return;
            }
            CloseConnection();
            RoomId = string.Empty;
            Mode = PvpConnectionMode.Local;
            IsComputerGame = computer;
            MySide = computer ? GoSide.Black : GoSide.Empty;
            BlackName = computer ? "你 · 黑方" : "本地黑方";
            WhiteName = computer ? "棋盘助手 · 白方" : "本地白方";
            NewGame();
            Status = computer ? "本地人机 · 你执黑先行" : "本地双人练习 · 双方轮流落子";
        }

        /// <summary>预检地址后建立或恢复连接。返回 true 表示已发起请求，握手结果由 Connected 表示。</summary>
        public bool StartServer(string endpoint, string nickname)
        {
            if (_leaving)
            {
                return false;
            }
            if (!FantasyGoTransport.TryParseEndpoint(endpoint, out string host, out int port))
            {
                // 格式检查在切换连接之前，错填地址时仍可沿用当前会话。
                Status = "服务器地址格式有误，请填写域名:端口或 IP:端口（端口 1–65535）";
                Revision++;
                return false;
            }
            FantasyGoTransport connection = PrepareServerConnection(endpoint, nickname);
            connection.Connect(host, port, Nickname, Config.RulesJson, Config.rookie ? "rookie" : "advanced",
                _resumeToken, allowNewIdentity: !_recoveringServer);
            return true;
        }

        /// <summary>
        /// 通过现有的连接协议恢复房间。调用方取消只结束观察，连接归服务并由真实域退场收尾。
        /// 连接被替代时原请求不能向新房间反馈成功。
        /// </summary>
        public async RevTask<bool> ReconnectAsync(RevCancellationToken token)
        {
            token.ThrowIfCancelled();
            if (!RoomSnapshot.Allows(PvpRoomAction.Reconnect))
            {
                return false;
            }
            StartServer(Endpoint, Nickname);
            FantasyGoTransport current = _server;
            while (current != null && ReferenceEquals(current, _server) && current.Busy)
            {
                token.ThrowIfCancelled();
                await RevTaskScheduler.NextFrame();
            }
            token.ThrowIfCancelled();
            return current != null && ReferenceEquals(current, _server) && Connected && !_needsServerSync;
        }

        /// <summary>切换连接所有权并绑定回调。新会话先清旧房身份，再等待权威回包。</summary>
        private FantasyGoTransport PrepareServerConnection(string endpoint, string nickname)
        {
            if (_leaving)
            {
                return null;
            }
            string requestedEndpoint = (endpoint ?? string.Empty).Trim();
            bool resume = Mode == PvpConnectionMode.Server && Core != null && !string.IsNullOrEmpty(_resumeToken) && requestedEndpoint == Endpoint;
            CloseConnection();
            Mode = PvpConnectionMode.Server;
            Endpoint = requestedEndpoint;
            Nickname = CleanName(nickname);
            string kind = Config.rookie ? "rookie" : "advanced";
            _recoveringServer = resume;
            if (!resume)
            {
                RoomId = string.Empty;
                MySide = GoSide.Empty;
                BlackName = "黑方";
                WhiteName = "白方";
                Core = null;
                _moves.Clear();
                _resumeToken = (_matchIdentity.Endpoint, _matchIdentity.Nickname, _matchIdentity.Rules, _matchIdentity.Kind) ==
                    (Endpoint, Nickname, Config.RulesJson, kind) ? _matchIdentity.Token : string.Empty;
                ResetPresentation();
            }
            Status = resume ? "正在恢复服务器对局，原有棋谱与思考时间已保留…" : "正在连接匹配服务器…";
            var connection = new FantasyGoTransport();
            _server = connection;
            connection.StateReceived += (state, confirmsMembership) => ReceiveServer(connection, state, confirmsMembership);
            connection.Error += reason =>
            {
                if (ReferenceEquals(connection, _server))
                {
                    OnNetworkError(reason);
                }
            };
            connection.RequestRejected += reason =>
            {
                if (ReferenceEquals(connection, _server))
                {
                    OnRequestRejected(reason);
                }
            };
            Revision++;
            return connection;
        }

        /// <summary>沿同一入口开始或取消匹配，并复核房间快照使用的门禁。</summary>
        public void Match()
        {
            string blocked = MatchBlockReason();
            if (blocked.Length != 0)
            {
                Status = MatchReasonText(blocked);
                return;
            }
            _server.Match(Queued, Queued ? 0 : ReadLocalRating(out _));
            if (!Queued)
            {
                _queueStarted = _clock.Elapsed.TotalSeconds;
            }
        }

        // 玩家入口使用所有可用网卡发现同网设备。
        public void StartLan(string nickname)
        {
            StartLan(nickname, loopbackOnly: false);
        }

        /// <summary>开始本轮局域网发现。同机验收可限制广播范围，并使用真实的被动接收模式。</summary>
        internal void StartLan(string nickname, bool loopbackOnly, bool listenOnly = false)
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
                Config.invitationTimeoutSeconds, Config.connectTimeoutSeconds, loopbackOnly);
            AttachLan(new LanPeerTransport(options));
            if (!Lan.StartDiscovery(Nickname, out string reason, listenOnly))
            {
                Status = reason;
            }
            else
            {
                Status = Lan.Status;
            }
            Revision++;
        }
        /// <summary>新搜索与大厅接管共用同一套状态和 UTP 消息回调。</summary>
        private void AttachLan(LanPeerTransport transport)
        {
            Lan = transport;
            Lan.Connected += OnLanConnected;
            Lan.PayloadReceived += ReceiveLan;
            Lan.Disconnected += OnNetworkError;
            Lan.Changed += OnLanStatusChanged;
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
        // UTP 握手成功后声明围棋规则和决斗能力。业务 Connected 仍要等双方校验。
        private void OnLanConnected(bool isHost)
        {
            MySide = isHost ? GoSide.Black : GoSide.White;
            _lanHello = false;
            SendLan(new PvpLanMessage { type = "hello", version = ProtocolVersion, rules = Config.RulesJson, name = Nickname,
                duelRequest = DuelRequestFor(RevGame.Pvp.Duel.DuelAuthority.Read) });
            Status = "正在核对双方规则版本…";
        }
        // 在 PvP 域轮询时解析业务信封。决斗帧先于围棋分支处理，避免旧棋步越过切场边界。
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
                    _duelPeerCompatible = message.duelRequest?.fingerprint == RevGame.Pvp.Duel.DuelAuthority.NetworkFingerprint;
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
                // 先路由决斗消息。进入决斗后不再接纳迟到的围棋落子。
                if (ReceiveLanDuel(message) || (DuelId.Length != 0 && message.type == "move"))
                {
                    return;
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
        // 返回消息是否进入可靠队列。失败时通知业务连接已不可用。
        private bool SendLan(PvpLanMessage message)
        {
            string reason = "局域网连接已关闭";
            if (Lan == null || !Lan.Send(JsonUtility.ToJson(message), out reason))
            {
                OnNetworkError(Lan == null ? "局域网连接已关闭" : reason);
                return false;
            }
            return true;
        }

        /// <summary>校验会话、房间和棋谱后再提交。缺关键字段时保留已确认棋局并等完整同步。</summary>
        private void ReceiveServer(FantasyGoTransport connection, GoRelayState state, bool confirmsMembership)
        {
            if (_disposed || _leaving || !ReferenceEquals(connection, _server) || state == null)
            {
                return;
            }

            string roomId = state.RoomId ?? string.Empty;
            string expectedRoom = string.IsNullOrEmpty(RoomId) ? _pendingRoomId : RoomId;
            bool lobbyPhase = state.Phase == "idle" || state.Phase == "queued";
            if (expectedRoom.Length != 0 && roomId != expectedRoom && (roomId.Length != 0 || lobbyPhase))
            {
                // 握手/完整同步能证明原房已经消失。普通推送的旧大厅快照则不能改变现房的身份。
                if (confirmsMembership && roomId.Length == 0 && lobbyPhase)
                {
                    if (RoomId.Length != 0)
                    {
                        _roomStateIncomplete = _needsServerSync = false;
                        OnNetworkError("服务器已关闭原房间，请返回后重新匹配");
                        return;
                    }
                    _pendingRoomId = string.Empty;
                    _roomStateIncomplete = _needsServerSync = false;
                }
                else
                {
                    return;
                }
            }
            try
            {
                if (roomId.Length == 0 && lobbyPhase)
                {
                    AdoptServerConnection(connection);
                    Queued = state.Phase == "queued";
                    Status = !string.IsNullOrEmpty(state.Reason) ? state.Reason : Queued ? "正在匹配，可随时取消" : "服务器已连接";
                    Revision++;
                    return;
                }

                if (roomId.Length == 0 || lobbyPhase || string.IsNullOrEmpty(state.Phase) || string.IsNullOrEmpty(state.CoreVersion)
                    || string.IsNullOrEmpty(state.RulesJson) || state.MySide == (int)GoSide.Empty || state.Commands == null)
                {
                    _pendingRoomId = expectedRoom.Length == 0 ? roomId : expectedRoom;
                    _roomStateIncomplete = _needsServerSync = true;
                    AdoptServerConnection(connection);
                    Queued = false;
                    SelectedPoint = -1;
                    CancelHint();
                    Status = "房间状态不完整，正在请求完整同步";
                    Revision++;
                    return;
                }
                bool delta = state.BaseSequence > 0;
                if (delta && (RoomId != roomId || Core == null || state.BaseSequence > Core.Sequence || _roomStateIncomplete))
                {
                    _needsServerSync = true;
                    return;
                }
                if (!delta && state.Commands.Count < _moves.Count)
                {
                    return; // 同一房间的完整旧前缀也不能回退棋局或状态。
                }
                if (state.CoreVersion != ProtocolVersion || state.RulesJson != Config.RulesJson)
                {
                    throw new InvalidOperationException("服务器房间的规则版本不匹配");
                }
                var side = (GoSide)state.MySide;
                if ((side != GoSide.Black && side != GoSide.White) || (RoomId == roomId && Core != null && MySide != side))
                {
                    throw new InvalidOperationException("服务器房间的棋子身份无效");
                }
                if (delta)
                {
                    ApplyServerDelta(state);
                }
                else
                {
                    var history = new List<PvpMove>(state.Commands.Count);
                    foreach (GoRelayCommand command in state.Commands)
                    {
                        history.Add(ToMove(command));
                    }
                    ValidatePrefix(history);
                    ReplaceHistory(history);
                }
                // 身份与路由要晚于棋谱校验。昵称是可选字段，展示层按座位兜底。
                bool restored = _roomStateIncomplete;
                bool joined = RoomId.Length == 0;
                AdoptServerConnection(connection);
                Queued = false;
                RoomId = roomId;
                connection.ConfirmRoom(roomId);
                MySide = side;
                BlackName = state.BlackName;
                WhiteName = state.WhiteName;
                _pendingRoomId = string.Empty;
                if (!delta)
                {
                    _roomStateIncomplete = _needsServerSync = false;
                }
                if (!string.IsNullOrEmpty(state.Reason))
                {
                    Status = state.Reason;
                }
                else if (restored || joined)
                {
                    Status = restored ? "房间状态已同步" : "已进入房间";
                }
                _awaiting = _pendingSequence > Core.Sequence;
                if (!_awaiting)
                {
                    if (Status == "正在同步落子…")
                    {
                        Status = "落子已同步";
                    }
                    _pendingSequence = 0;
                }
                _recoveringServer = false;
                ApplyLocalRating(state);
                // 服务端根据两席握手计算入口可用性，本端用权威状态刷新按钮。
                _duelPeerCompatible = state.DuelAvailable;
                ActivateDuel(state.DuelId);
                if (state.Phase == "disconnected" || state.Phase == "closed")
                {
                    Connected = false;
                }
                Revision++;
            }
            catch (Exception error)
            {
                OnNetworkError("收到的棋谱校验失败：" + error.Message);
            }
        }

        /// <summary>仅在接受当前会话的回包时保存连接状态及恢复信息。</summary>
        private void AdoptServerConnection(FantasyGoTransport connection)
        {
            Connected = connection.Connected;
            _resumeToken = connection.ResumeToken;
            _matchIdentity = (Endpoint, Nickname, Config.RulesJson, Config.rookie ? "rookie" : "advanced", _resumeToken);
        }

        /// <summary>按当前设备读取棋力。旧版本没有本机档案，首次从初始分起算。</summary>
        private static int ReadLocalRating(out string lastRatedRoom)
        {
            string record = PlayerPrefs.GetString(LocalRatingKey);
            int separator = record.IndexOf('|');
            lastRatedRoom = "";
            if (separator < 0 || !int.TryParse(record.Substring(0, separator), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int rating) || rating < MinRating || rating > MaxRating)
            {
                return InitialRating;
            }
            lastRatedRoom = record.Substring(separator + 1);
            return rating;
        }

        /// <summary>棋谱和终局校验完成后才写盘。分数和末次房间号一起写，避免重连或重复快照重复记分。</summary>
        private void ApplyLocalRating(GoRelayState state)
        {
            if (!state.RatingFinalized || Core.Phase != GoPhase.Finished ||
                (Core.EndReason != GoEndReason.Scored && Core.EndReason != GoEndReason.Resigned) ||
                state.RatingDelta < -32 || state.RatingDelta > 32 || !Guid.TryParseExact(RoomId, "N", out _))
            {
                return;
            }
            int rating = ReadLocalRating(out string lastRatedRoom);
            if (lastRatedRoom == RoomId)
            {
                return;
            }
            int updatedRating = Math.Max(MinRating, Math.Min(MaxRating, rating + state.RatingDelta));
            PlayerPrefs.SetString(LocalRatingKey, updatedRating.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + RoomId);
            PlayerPrefs.Save();
        }

        private void ApplyServerDelta(GoRelayState state)
        {
            if (state.Commands.Count != 1 || state.Commands[0]?.Sequence != state.BaseSequence + 1)
            {
                throw new InvalidOperationException("增量棋谱序号非法");
            }
            PvpMove move = ToMove(state.Commands[0]);
            if (move.sequence <= Core.Sequence)
            {
                // 响应重试可能再次到达。只接受与当前已确认历史完全相同的操作。
                if (!SameMove(_moves[move.sequence - 1], move))
                {
                    throw new InvalidOperationException("增量修改了已确认棋谱");
                }
                return;
            }
            if (!TryPrepareMove(Core, _goClock, move, out GoCore next, out GoClock nextClock, out string reason))
            {
                throw new InvalidOperationException(reason);
            }
            // 权威增量提交后旧手动建议不再有效，否则会盖住新一手的自动提示。
            CancelHint();
            _suggestionCount = 0;
            Core = next;
            _goClock = nextClock;
            _moves.Add(move);
            _turnStarted = _clock.Elapsed.TotalSeconds;
            SelectedPoint = -1;
            Revision++;
        }

        private static PvpMove ToMove(GoRelayCommand command)
        {
            return new PvpMove { sequence = command.Sequence, side = command.Side, kind = command.Kind,
                x = command.X, y = command.Y, elapsedMs = command.ElapsedMs, before = command.PreviousHash, after = command.ResultHash };
        }

        private static bool SameMove(PvpMove a, PvpMove b)
        {
            return a != null && b != null && a.sequence == b.sequence && a.kind == b.kind && a.side == b.side &&
                a.x == b.x && a.y == b.y && a.before == b.before && a.after == b.after && a.elapsedMs == b.elapsedMs;
        }

        private void ValidatePrefix(List<PvpMove> history)
        {
            if (history == null || history.Count < _moves.Count || history.Count > 4096)
            {
                throw new InvalidOperationException("棋谱长度非法");
            }
            for (int i = 0; i < _moves.Count; i++)
            {
                if (!SameMove(history[i], _moves[i]))
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
            var next = GoCore.CreateMatch(Config.komiHalfPoints);
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
            // 同步整份棋谱也要清掉旧提示。前缀校验或重放失败时仍保留现有状态。
            CancelHint();
            _suggestionCount = 0;
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
            Core = Mode == PvpConnectionMode.Local && !IsComputerGame
                ? new GoCore(Config.komiHalfPoints, Config.localPlacementLimit)
                : GoCore.CreateMatch(Config.komiHalfPoints);
            _goClock = new GoClock(Config.mainTimeSeconds, Config.byoyomiSeconds, Config.byoyomiCount);
            _moves.Clear();
            _turnStarted = _clock.Elapsed.TotalSeconds;
            ResetPresentation();
            SelectedPoint = -1;
            Revision++;
        }

        /// <summary>玩家和GM共用这条入口；先在副本试算，网络提交失败不会留下半手棋。</summary>
        public bool Submit(GoAction action, int x = -1, int y = -1)
        {
            if (AgentControlsHumanSeat && action != GoAction.Timeout)
            {
                Status = "请先停止 Agent 托管再手动操作";
                return false;
            }
            if (Core == null)
            {
                Status = "对局尚未就绪，请先选择对战方式";
                return false;
            }
            GoSide side = Mode == PvpConnectionMode.Local && !IsComputerGame ? LocalActor(action) : MySide;
            return SubmitActor(action, side, x, y);
        }

        /// <summary>
        /// 玩家、电脑与云端Agent唯一提交入口。座位来自本地控制策略，绝不采信模型返回的身份或用时。
        /// 副本预检后才发送/提交，非法动作不会消耗回合；网络回执未到前沿用原有等待锁。
        /// </summary>
        private bool SubmitActor(GoAction action, GoSide side, int x = -1, int y = -1)
        {
            string blocked = RoomBlockReason();
            if (blocked.Length != 0 || (side != GoSide.Black && side != GoSide.White))
            {
                Status = blocked == "missing_room_state" ? MatchReasonText(blocked) : "对局尚未就绪或正在同步，请稍候";
                return false;
            }
            if ((Mode != PvpConnectionMode.Local && side != MySide) ||
                (IsComputerGame && side == GoSide.Black && Core.Phase == GoPhase.Playing && Core.Turn != GoSide.Black))
            {
                Status = "当前座位不能操作";
                return false;
            }
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
            int captured = next.BlackCaptures + next.WhiteCaptures - Core.BlackCaptures - Core.WhiteCaptures;
            CancelHint();
            Core = next;
            _goClock = nextClock;
            _moves.Add(move);
            _turnStarted = _clock.Elapsed.TotalSeconds;
            SelectedPoint = -1;
            Status = Core.PlacementLimitReached && Core.Phase == GoPhase.Scoring
                ? "本局落子已满，请点选死子并确认计分"
                : Core.Phase == GoPhase.Scoring ? "双方停一手：点选死子后分别确认；有分歧可继续对弈" : "落子已同步";
            if (captured > 0)
            {
                NotifyBoard((move.side == (int)GoSide.Black ? "黑方" : "白方") + "提了 " + captured + " 子");
            }
            else if ((GoAction)move.kind == GoAction.Pass)
            {
                NotifyBoard(Core.Phase == GoPhase.Scoring ? "双方停一手 · 请点选死子" :
                    (move.side == (int)GoSide.Black ? "黑方" : "白方") + "停一手");
            }
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
        /// <summary>真实棋盘点选先交给独立练习盘，正式房间继续按座位和同步状态准入。</summary>
        public void SelectPoint(int x, int y)
        {
            if (Tutorial != null)
            {
                Tutorial.Click(x, y);
                Revision++;
                return;
            }
            if (AgentControlsHumanSeat || !CanAct || Core.Phase == GoPhase.Finished || x < 0 || x >= 9 || y < 0 || y >= 9)
            {
                return;
            }
            if (Core.Phase == GoPhase.Scoring)
            {
                Submit(GoAction.ToggleDead, x, y);
                return;
            }
            if (!IsHumanTurn())
            {
                return;
            }
            int point = y * 9 + x;
            if (Config.rookie && SelectedPoint != point)
            {
                SelectedPoint = point;
                Preview(x, y);
                NotifyBoard(PreviewText);
                Revision++;
                return;
            }
            if (!Submit(GoAction.Play, x, y))
            {
                // UI不能把禁入点、劫、等待回执等拒绝都表现成“点了没反应”。保留规则给出的真实原因。
                PreviewText = Status;
                NotifyBoard(Status);
                Revision++;
            }
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
        /// <summary>主线程推进对局与连接，已启用的本地安卓试验在搜索前更新热状态。</summary>
        public void Poll()
        {
            if (_disposed)
            {
                return;
            }
            _server?.Poll();
            Lan?.Poll();
            PollAnalysis();
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
            if (DuelId.Length != 0)
            {
                // 围棋冻结后只推进决斗收发和补帧。前面的退出分支仍可正常退房。
                PollDuel();
                return;
            }
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
            AndroidTacticalTrial?.Refresh();
#endif
            PollComputerPlacement();
            PollAgent();
            PollComputer();
            PollHint();
            if (_needsServerSync && _server != null && _server.CanRequestSync && !_server.Busy &&
                _clock.Elapsed.TotalSeconds >= _serverSyncRetryAt)
            {
                // 保留缺口锁直到完整快照抵达。限流或断线期间，重试最多一秒一次。
                _serverSyncRetryAt = _clock.Elapsed.TotalSeconds + 1;
                _server.Sync();
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
                    SubmitActor(GoAction.Timeout, Core.Turn);
                }
            }
            if (Queued && _clock.Elapsed.TotalSeconds - _queueStarted >= Config.matchTimeoutSeconds)
            {
                Status = "当前匹配人数较少，可继续等待、取消或改用局域网对战";
            }
        }
        private void OnNetworkError(string reason)
        {
            ClearDuelUnsealedInput();
            LastNetworkError = reason;
            RevLog.Warn("连接不可用：" + reason, "Pvp");
            if (_server != null)
            {
                _resumeToken = _server.ResumeToken;
                if (string.IsNullOrEmpty(_resumeToken) && _matchIdentity.Endpoint == Endpoint && _matchIdentity.Nickname == Nickname)
                {
                    _matchIdentity = default;
                }
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
            CancelAgent();
            ClearAnalysis();
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
        /// <summary>收回连接、房间身份和棋谱，完成退房。</summary>
        private void FinishLeave()
        {
            CloseConnection();
            RoomId = string.Empty;
            Core = null;
            _moves.Clear();
            _resumeToken = "";
            ResetPresentation();
            Status = "已离开对局，请选择联机方式";
            Revision++;
        }
        /// <summary>先回收本局试验，再使连接回调过期并收回会话和同步状态。</summary>
        private void CloseConnection()
        {
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
            ClearAndroidTacticalTrial();
#endif
            // 断开旧连接时连同决斗副本一起释放，防止迟到消息污染下一局。
            ResetDuel();
            CancelAgent();
            ClearAnalysis();
            AgentControlsHumanSeat = false;
            IsComputerGame = false;
            FantasyGoTransport previousServer = _server;
            _server = null;
            previousServer?.Dispose();
            Lan?.Dispose();
            Lan = null;
            Connected = false;
            Queued = false;
            _awaiting = false;
            _pendingSequence = 0;
            _needsServerSync = false;
            _roomStateIncomplete = false;
            _pendingRoomId = string.Empty;
            _serverSyncRetryAt = 0;
            _syncMoves = null;
            _syncTotal = 0;
            _recoveringServer = false;
            _leaving = false;
            _leaveSent = false;
            _syncOutgoing.Clear();
        }
        /// <summary>结束 PvP 服务寿命，清空练习与房间身份并阻止迟到回调。</summary>
        public void Dispose()
        {
            _disposed = true;
            ExitTutorial();
            StopLobbyLan();
            CloseConnection();
            RoomId = string.Empty;
        }
    }
}

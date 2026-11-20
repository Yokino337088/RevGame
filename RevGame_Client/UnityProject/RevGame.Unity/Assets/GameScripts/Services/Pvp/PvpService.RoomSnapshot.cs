using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>房间与匹配界面消费的操作键。具体落点是否合法仍由棋局规则检查。</summary>
    public enum PvpRoomAction
    {
        /// <summary>在棋盘落子。</summary>
        Place,
        /// <summary>停一手。</summary>
        Pass,
        /// <summary>认输。</summary>
        Resign,
        /// <summary>请求落子建议。</summary>
        Hint,
        /// <summary>确认数子结果。</summary>
        ConfirmScore,
        /// <summary>从数子恢复对弈。</summary>
        Resume,
        /// <summary>开启或停止本方 Agent 托管。</summary>
        Agent,
        /// <summary>恢复在线连接。</summary>
        Reconnect,
        /// <summary>连接服务器。</summary>
        Connect,
        /// <summary>开始或取消在线匹配。</summary>
        Match,
        /// <summary>切换新手或进阶场。</summary>
        ChangeMode,
        /// <summary>离开房间或返回上一页。</summary>
        Leave
    }

    /// <summary>服务持有网络与棋局事实，并对观察者提供不可变的房间投影。</summary>
    public sealed partial class PvpService
    {
        /// <summary>当前可用的房间视图。与同帧的 UI、MCP 共享，输入改变时生成新对象。</summary>
        private PvpRoomSnapshot _viewSnapshot;
        /// <summary>帧与业务版本加上不写 Revision 的瞬时字段，用来识别同帧内的状态变化。</summary>
        private (int Frame, int Revision, string Status, bool Busy, bool Leaving, bool Awaiting, bool Syncing, bool HintPending,
            bool Connected, bool Disposed, bool AgentReady, string DuelId) _viewSnapshotKey;

        /// <summary>取得只读房间投影，冻结显示文本和操作门禁，无网络或规则写入。</summary>
        public PvpRoomSnapshot RoomSnapshot
        {
            get
            {
                var key = (Time.frameCount, Revision, Status, _server?.Busy == true, _leaving, _awaiting, _needsServerSync,
                    HintPending, Connected, _disposed, AgentSettings.Ready, DuelId);
                if (_viewSnapshot == null || _viewSnapshotKey != key)
                {
                    _viewSnapshot = new PvpRoomSnapshot(this);
                    _viewSnapshotKey = key;
                }
                return _viewSnapshot;
            }
        }

        /// <summary>棋局提交和显示共用的基础门禁。空串表示允许进入具体规则检查。</summary>
        private string RoomBlockReason()
        {
            if (_roomStateIncomplete)
            {
                return "missing_room_state";
            }
            if (_disposed || _leaving)
            {
                return "leaving";
            }
            if (Core == null || _goClock == null)
            {
                return "no_game";
            }
            if (DuelId.Length != 0)
            {
                return "duel_active";
            }
            if (ReplayStep >= 0)
            {
                return "replaying";
            }
            if (Mode != PvpConnectionMode.Local && (!Connected || (Mode == PvpConnectionMode.Server && _server == null)))
            {
                return "disconnected";
            }
            if (_needsServerSync)
            {
                return "synchronizing";
            }
            return _awaiting || _server?.Busy == true ? "request_pending" : string.Empty;
        }

        /// <summary>匹配按钮与命令共用的门禁。排队期间允许沿同一入口取消。</summary>
        private string MatchBlockReason()
        {
            if (_roomStateIncomplete)
            {
                return "missing_room_state";
            }
            if (_disposed || _leaving)
            {
                return "leaving";
            }
            if (Core != null)
            {
                return "already_in_room";
            }
            if (_server?.Connected != true || !Connected)
            {
                return "disconnected";
            }
            return _server.Busy ? "request_pending" : string.Empty;
        }

        /// <summary>只在当前没有房间、匹配或已建立的连接时切换场次。</summary>
        private string ModeBlockReason()
        {
            if (_disposed || _leaving)
            {
                return "leaving";
            }
            return Core != null || Queued || Connected || _server?.Busy == true ? "leave_before_change" : string.Empty;
        }

        /// <summary>判断当前行动方是否由玩家操控。本地双人玩法允许玩家轮流执两色。</summary>
        private bool IsHumanTurn()
        {
            return Core != null && (Mode == PvpConnectionMode.Local && !IsComputerGame || Core.Turn == MySide);
        }

        /// <summary>测试和展示使用稳定原因码，玩家发出操作时仍会复核实际房间门禁。</summary>
        private static string MatchReasonText(string code)
        {
            return code switch
            {
                "missing_room_state" => "房间状态不完整，正在请求完整同步",
                "request_pending" => "正在等待服务器回应，请稍后操作",
                "already_in_room" => "请先离开房间再匹配",
                "leaving" => "正在离开对局…",
                _ => "服务器尚未连接，可重试连接或使用局域网对战"
            };
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>创建不发包的现房验收连接。换房仍走正式连接装配、旧会话注销和房间接收流程。</summary>
        internal Networking.FantasyGoTransport BeginRoomSnapshotFixture(string endpoint)
        {
            Networking.FantasyGoTransport connection = PrepareServerConnection(endpoint, "房间快照验收");
            connection.ConnectRoomFixture();
            return connection;
        }

        /// <summary>只结束本轮当前的验收会话；接管了新的玩家连接时保持新连接寿命。</summary>
        internal void EndRoomSnapshotFixture(Networking.FantasyGoTransport connection)
        {
            if (connection != null && ReferenceEquals(connection, _server))
            {
                connection.DisconnectRoomFixture();
                LeaveRoom();
            }
        }
#endif

        /// <summary>由服务事实计算的不可变房间视图。借用者只能读取文本、身份、阶段和动作原因。</summary>
        public sealed class PvpRoomSnapshot
        {
            /// <summary>仅由本快照写入，动作未记禁用原因时可操作。</summary>
            private readonly string[] _disabled;
            /// <summary>当前已确认房间 ID。</summary>
            public string RoomId { get; }
            /// <summary>源服务提交版本，区分同屏刷新前后的事实。</summary>
            public int Revision { get; }
            /// <summary>是否还有不完整的关键状态待补齐。</summary>
            public bool MissingCriticalState { get; }
            /// <summary>在线回包是否缺少一个或两个显示昵称。</summary>
            public bool MissingPlayerNames { get; }
            /// <summary>当前棋盘是否已确认，可以继续展示。</summary>
            public bool HasGame { get; }
            /// <summary>当前棋局阶段，无棋盘时为空。</summary>
            public GoPhase? Phase { get; }
            /// <summary>最后接受的棋谱序号，缺棋盘时为零。</summary>
            public int Sequence { get; }
            /// <summary>最后接受的棋局指纹，缺棋盘时为空。</summary>
            public ulong? StateHash { get; }
            /// <summary>黑方显示名称，缺昵称时按座位显示。</summary>
            public string BlackName { get; }
            /// <summary>白方显示名称，缺昵称时按座位显示。</summary>
            public string WhiteName { get; }
            /// <summary>对局顶部标题。</summary>
            public string Title { get; }
            /// <summary>黑方完整栏位文本。</summary>
            public string BlackText { get; }
            /// <summary>白方完整栏位文本。</summary>
            public string WhiteText { get; }
            /// <summary>对局顶部轮次或需要玩家处理的通用锁定提示。</summary>
            public string TurnText { get; }
            /// <summary>当前棋局规则计算出的轮次或计分文案。</summary>
            public string ResultText { get; }
            /// <summary>服务器入口主状态文本。</summary>
            public string ServerStatusText { get; }
            /// <summary>服务器入口排队时长或操作提示。</summary>
            public string QueueText { get; }
            /// <summary>服务器匹配按钮文本。</summary>
            public string MatchLabel { get; }
            /// <summary>服务器场次文本。</summary>
            public string ModeLabel { get; }
            /// <summary>对局建议按钮文本。</summary>
            public string HintLabel { get; }
            /// <summary>Agent 托管按钮文本。</summary>
            public string AgentLabel { get; }
            /// <summary>对局是否展示重连入口。</summary>
            public bool ShowReconnect { get; }

            /// <summary>只读投影。可选昵称缺失只影响文本，座位等关键状态缺失由基础门禁锁住实际提交。</summary>
            internal PvpRoomSnapshot(PvpService service)
            {
                GoCore core = service.Core;
                RoomId = service.RoomId;
                Revision = service.Revision;
                MissingCriticalState = service._roomStateIncomplete;
                HasGame = core != null;
                Phase = core?.Phase;
                Sequence = core?.Sequence ?? 0;
                StateHash = core?.StateHash;
                bool online = service.Mode == PvpConnectionMode.Server;
                bool local = service.Mode == PvpConnectionMode.Local;
                MissingPlayerNames = online && HasGame
                    && (string.IsNullOrWhiteSpace(service.BlackName) || string.IsNullOrWhiteSpace(service.WhiteName));
                BlackName = string.IsNullOrWhiteSpace(service.BlackName) ? "黑方" : service.BlackName;
                WhiteName = string.IsNullOrWhiteSpace(service.WhiteName) ? "白方" : service.WhiteName;
                string connection = online ? "服务器匹配" : local ? (service.IsComputerGame ? "本地人机" : "本地双人") : "局域网直连";
                Title = HasGame ? "九路围棋 · " + connection : "九路围棋";
                BlackText = HasGame ? "● " + BlackName + "\n" + service.ClockText(GoSide.Black) + "   提子 " + core.BlackCaptures : "黑先白后";
                WhiteText = HasGame ? "○ " + WhiteName + "\n" + service.ClockText(GoSide.White) + "   提子 " + core.WhiteCaptures : "同样规则 · 公平落子";
                ResultText = service.ResultText();
                string block = service.RoomBlockReason();
                string syncingText = block switch
                {
                    "missing_room_state" => MatchReasonText(block),
                    "synchronizing" => "正在同步棋谱…",
                    "disconnected" => "连接暂不可用，可重连或返回",
                    _ => string.Empty
                };
                int lineEnd = ResultText.IndexOf('\n');
                string turnText = !HasGame ? "选择一种对战方式" : lineEnd >= 0 ? ResultText.Substring(0, lineEnd) : ResultText;
                TurnText = syncingText.Length == 0 || (Phase == GoPhase.Finished && !MissingCriticalState) ? turnText : syncingText;
                ServerStatusText = MissingCriticalState ? syncingText : service.Queued ? "正在寻找棋友…"
                    : HasGame && service.Connected ? service.Status : service.Connected ? "连接成功 · 可以开始匹配"
                    : !online ? "连接服务器，与在线棋友相遇" : service.Status;
                int seconds = (int)service.QueueElapsedSeconds;
                QueueText = service.Queued ? "已匹配  " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00") : "准备好后开始匹配";
                MatchLabel = service.Queued ? "取消匹配" : "开始匹配";
                ModeLabel = service.Config?.rookie != false ? "新手场 · 二次确认落子" : "进阶场 · 直接落子";
                HintLabel = service.HintPending ? "鸦鸦思考中" : "落子建议";
                AgentLabel = service.AgentControlsHumanSeat ? "停止托管" : "Agent托管";
                ShowReconnect = online && (HasGame || MissingCriticalState) && (!service.Connected || MissingCriticalState);

                string manual = block.Length != 0 ? block : service.AgentControlsHumanSeat ? "agent_controlled" : string.Empty;
                string playing = AtPhase(manual, GoPhase.Playing);
                string scoring = AtPhase(manual, GoPhase.Scoring);
                string turn = playing.Length != 0 ? playing : service.IsHumanTurn() ? string.Empty : "opponent_turn";
                string leaving = service._disposed || service._leaving ? "leaving" : string.Empty;
                _disabled = new string[(int)PvpRoomAction.Leave + 1];
                _disabled[(int)PvpRoomAction.Place] = turn;
                _disabled[(int)PvpRoomAction.Pass] = turn;
                _disabled[(int)PvpRoomAction.Resign] = manual.Length != 0 ? manual : Phase == GoPhase.Finished ? "wrong_phase" : string.Empty;
                _disabled[(int)PvpRoomAction.Hint] = turn.Length != 0 ? turn : service.HintPending ? "hint_pending" : string.Empty;
                _disabled[(int)PvpRoomAction.ConfirmScore] = scoring.Length != 0 ? scoring
                    : !local && core.HasConfirmed(service.MySide) ? "already_confirmed" : string.Empty;
                _disabled[(int)PvpRoomAction.Resume] = scoring.Length != 0 ? scoring
                    : core.PlacementLimitReached ? "placement_limit" : string.Empty;
                _disabled[(int)PvpRoomAction.Agent] = service.AgentControlsHumanSeat ? leaving
                    : local || !HasGame || Phase == GoPhase.Finished ? "agent_unavailable"
                    : MissingCriticalState ? "missing_room_state" : service.AgentSettings.Ready ? string.Empty : "agent_not_configured";
                _disabled[(int)PvpRoomAction.Reconnect] = leaving.Length != 0 ? leaving : !ShowReconnect ? "reconnect_unneeded"
                    : service._server?.Busy == true ? "request_pending" : string.Empty;
                _disabled[(int)PvpRoomAction.Connect] = leaving.Length != 0 ? leaving : service.Queued ? "matching" : string.Empty;
                _disabled[(int)PvpRoomAction.Match] = service.MatchBlockReason();
                _disabled[(int)PvpRoomAction.ChangeMode] = service.ModeBlockReason();
                _disabled[(int)PvpRoomAction.Leave] = leaving;
            }

            /// <summary>房间级允许条件；服务端和棋规仍会校验具体命令。</summary>
            public bool Allows(PvpRoomAction action)
            {
                return DisabledReason(action).Length == 0;
            }

            /// <summary>返回稳定的禁用原因码，允许时为空串。</summary>
            public string DisabledReason(PvpRoomAction action)
            {
                return _disabled[(int)action];
            }

            /// <summary>优先保留公共拒绝原因，再核对这个按钮要求的棋局阶段。</summary>
            private string AtPhase(string current, GoPhase required)
            {
                return current.Length != 0 ? current : Phase == required ? string.Empty : "wrong_phase";
            }
        }
    }
}

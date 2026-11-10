#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Pvp.Networking;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>
    /// 九路围棋的开发态窄入口。MCP 发现的是这些明确注册的能力，不反射任意服务方法。
    /// 玩家按钮与测试共用 Service / GameFlow，查询不推进棋局；网络夹具另有 owner，失败也能独立收回。
    /// </summary>
    public static partial class PvpAutomation
    {
        private static readonly object Owner = new object();
        private static PvpKcpOpponentCheck _kcpOpponent;
        private static readonly object CheckOwner = new object();
        private static readonly object GameCheckOwner = new object();
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        private static LanTransportChecks _lanCheck;
        private static bool _checkRunning;
        private static bool _checkCancelled;
        private static string _checkError = string.Empty;
        private static PvpLanGameChecks _lanGameCheck;
        private static bool _gameCheckRunning;
        private static bool _gameCheckCancelled;
        private static string _gameCheckError = string.Empty;

        private static PvpService Service
        {
            get
            {
                return RevServices.GetRequired<PvpService>();
            }
        }

        private static GameFlow Flow
        {
            get
            {
                return RevServices.GetRequired<GameFlow>();
            }
        }

        /// <summary>只在 Bootstrap 的开发态分支调用一次。输入Schema限定枚举、坐标与字符串长度，正式包不保留这些入口。</summary>
        public static void Register()
        {
            RegisterPresentation();
            RevRuntimeAutomation.Register("pvp.agent-storage-prepare", "用独立测试槽保存假Key，覆盖平台存储和安卓TXT；跨Play后调用verify", Empty,
                _ => PvpAgentChecks.PrepareStorageCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-storage-verify", "验证独立测试槽跨Play恢复、损坏拒绝与清除，不返回凭据", Empty,
                _ => PvpAgentChecks.VerifyStorageCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-storage-clear", "清除独立测试槽，不修改玩家配置", Empty,
                _ => PvpAgentChecks.ClearStorageCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-schema-check", "仅检查两手示例棋局的实际序列化工具Schema，不发送HTTP，不读取Key", Empty, payload =>
            {
                var core = new GoCore(Service.Config.komiHalfPoints);
                core.TryPlay(GoSide.Black, 4, 4, out _);
                core.TryPlay(GoSide.White, 4, 2, out _);
                var position = Agent.GoAgentPosition.Create(core, GoSide.Black);
                return new Dictionary<string, object> { ["expectedCount"] = position.CopyActionIds().Length,
                    ["wire"] = Agent.DeepSeekGoRequest.InspectWireSchema("deepseek-flash", position) };
            }, readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-trace", "只读导出每手请求耗时/HTTP分类，不返回Key、响应正文或设置对象", Empty,
                _ => new Dictionary<string, object> { ["ready"] = Service.AgentSettings.Ready, ["realTransport"] = Service.AgentRequestFactory.Method.DeclaringType == typeof(Agent.DeepSeekGoRequest), ["records"] = Service.AgentTrace, ["moves"] = Service.Moves }, readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-live", "使用启动时已解密的本地真实Key开启本方Agent，不安装任何响应替身", Empty,
                _ => Result(Service.SetAgentControl(true), Service.AgentStatus), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-fixture", "仅开发态安装固定停一手HTTP替身，联机仍走真实连接；不访问云端", Empty,
                _ => { PvpAgentChecks.Install(Service); return Result(true, "已安装固定响应"); }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-on", "联机自己的座位通过正式入口开启托管", Empty,
                _ => Result(Service.SetAgentControl(true), Service.AgentStatus), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-off", "关闭托管并移除测试HTTP替身", Empty,
                _ => { PvpAgentChecks.Restore(Service); return Result(true, "已恢复真实HTTP边界"); }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.computer", "通过正式入口开始人机，玩家只能手动执黑", Empty,
                _ => { Service.StartComputer(); return Result(true, "人机已开始"); }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-check", "隔离棋局验证Agent人机闭环、取消、非法输出与回退，不访问云端或玩家存档", Empty,
                _ => new Dictionary<string, object> { ["passed"] = true, ["text"] = PvpAgentChecks.Run(Service.Config) }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-settings", "打开真实Agent设置面板，不读取或返回Key", Empty,
                _ => { PvpAgentSettingsPanel.OpenSettings(); return Result(true, "已打开设置"); }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.state", "只读九路围棋流程、棋谱指纹、双方状态、设备列表和配置加载状态", Empty, _ => State(), readOnly: true, owner: Owner);
            RegisterDuelAutomation();
            RevRuntimeAutomation.Register("pvp.lan-scroll", "向局域网列表注入原生滚轮事件，验证超出六行仍可滚动", Empty, _ =>
            {
                RevUI.Get<PvpLanPanel>().ScrollForCheck();
                return Result(true, "已发送滚轮事件");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-list-state", "读取局域网ScrollRect真实尺寸和位置", Empty,
                _ => RevUI.Get<PvpLanPanel>().ScrollStateForCheck(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.notice-check", "播放真实顶部匹配提示，供截图连拍验证DOTween滑入和收回", Empty, _ =>
            {
                PvpNoticePanel.ShowMessage("正在寻找棋友…");
                return Result(true, "已播放真实顶部提示");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.kcp-long-opponent", "真实KCP独立对手落满68手自动结算，仅开发态", Empty, _ =>
            {
                _kcpOpponent?.Dispose();
                _kcpOpponent = new PvpKcpOpponentCheck(Service.Config.RulesJson, Service.Config.komiHalfPoints, true);
                _kcpOpponent.Start();
                return Result(true, "已启动KCP长局对手");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.kcp-opponent", "独立Fantasy Scene/KCP连接本机UDP20000并自动停一手，仅开发态", Empty, _ =>
            {
                _kcpOpponent?.Dispose();
                _kcpOpponent = new PvpKcpOpponentCheck(Service.Config.RulesJson, Service.Config.komiHalfPoints);
                _kcpOpponent.Start();
                return Result(true, "已启动独立KCP对手");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.kcp-opponent-state", "读取独立KCP对手协议、序号和指纹", Empty,
                _ => _kcpOpponent == null ? (object)new Dictionary<string, object> { ["running"] = false } : _kcpOpponent.State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.drop-socket", "开发态释放当前真实Fantasy Session和Socket，保留棋谱供正式连接入口恢复", Empty,
                _ => Result(FantasyGoTransport.Active != null && FantasyGoTransport.Active.DropSocketForCheck(), "真实Socket断开请求"), owner: Owner);
            RevRuntimeAutomation.Register("pvp.enter", "从大厅经正式GameFlow进入九路围棋；需等待过场完成再操作", Empty, _ =>
            {
                bool accepted = Flow.EnterPvp();
                return Result(accepted, accepted ? string.Empty : Flow.LastError);
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.local", "正式入口开始本地双人九路围棋，不访问单机成长或存档", Empty, _ =>
            {
                if (!Ready(out string reason))
                {
                    return Result(false, reason);
                }
                Service.StartLocal();
                return Result(Service.Core != null, Service.Status);
            }, owner: Owner);

            const string actionSchema = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"Play\",\"Pass\",\"Resign\",\"ToggleDead\",\"ConfirmScore\",\"Resume\",\"Timeout\",\"Disconnect\"]},\"x\":{\"type\":\"integer\",\"minimum\":-1,\"maximum\":8},\"y\":{\"type\":\"integer\",\"minimum\":-1,\"maximum\":8}},\"required\":[\"kind\",\"x\",\"y\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.action", "提交真实围棋动作；非法手返回accepted=false且不得改变sequence/hash；非落点动作坐标填-1", actionSchema, json =>
            {
                if (!Ready(out string reason))
                {
                    return Result(false, reason);
                }
                var input = JsonUtility.FromJson<ActionArgs>(json);
                if (!Enum.TryParse(input.kind, out GoAction action))
                {
                    return Result(false, "未知围棋动作");
                }
                bool accepted = Service.Submit(action, input.x, input.y);
                return Result(accepted, accepted ? string.Empty : Service.Status);
            }, owner: Owner);

            const string connectSchema = "{\"type\":\"object\",\"properties\":{\"endpoint\":{\"type\":\"string\",\"minLength\":3,\"maxLength\":128},\"nickname\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":24}},\"required\":[\"endpoint\",\"nickname\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.connect", "使用真实Fantasy客户端连接指定开发服务器；本机测试填本机IP:端口，等待connected后匹配", connectSchema, json =>
            {
                if (!Ready(out string reason))
                {
                    return Result(false, reason);
                }
                var input = JsonUtility.FromJson<ConnectArgs>(json);
                Service.StartServer(input.endpoint, input.nickname);
                return Result(true, "已发起连接，请查询connected/status确认结果");
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.match", "与玩家按钮一致：开始匹配，已排队时取消匹配", Empty, _ =>
            {
                if (!Ready(out string reason) || Service.Mode != PvpConnectionMode.Server || !Service.Connected)
                {
                    return Result(false, string.IsNullOrEmpty(reason) ? "服务器尚未连接" : reason);
                }
                Service.Match();
                return Result(true, "已提交匹配请求，请查询queued/roomId确认结果");
            }, owner: Owner);

            const string nicknameSchema = "{\"type\":\"object\",\"properties\":{\"nickname\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":24}},\"required\":[\"nickname\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.lan", "正式进入局域网发现页；关闭原连接后搜索同网设备", nicknameSchema, json =>
            {
                if (!Ready(out string reason))
                {
                    return Result(false, reason);
                }
                Service.StartLan(JsonUtility.FromJson<NicknameArgs>(json).nickname);
                bool accepted = Service.Lan != null && Service.Lan.IsDiscovering;
                return Result(accepted, accepted ? string.Empty : Service.Status);
            }, owner: Owner);
            const string peerSchema = "{\"type\":\"object\",\"properties\":{\"peerid\":{\"type\":\"string\",\"minLength\":32,\"maxLength\":32}},\"required\":[\"peerid\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.invite", "邀请发现列表中的真实设备，不能直接指定陌生IP", peerSchema, json =>
            {
                if (!Ready(out string reason) || Service.Lan == null)
                {
                    return Result(false, string.IsNullOrEmpty(reason) ? "尚未打开局域网" : reason);
                }
                string peerId = JsonUtility.FromJson<PeerArgs>(json).peerid;
                bool accepted = Service.Invite(peerId);
                return Result(accepted, accepted ? string.Empty : Service.Status);
            }, owner: Owner);
            const string acceptSchema = "{\"type\":\"object\",\"properties\":{\"accept\":{\"type\":\"boolean\"}},\"required\":[\"accept\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.accept", "通过玩家真实入口接受或拒绝当前局域网邀请", acceptSchema, json =>
            {
                if (!Ready(out string reason) || Service.Lan?.IncomingInvitation == null)
                {
                    return Result(false, string.IsNullOrEmpty(reason) ? "没有待处理邀请" : reason);
                }
                bool accepted = Service.ReplyInvitation(JsonUtility.FromJson<AcceptArgs>(json).accept);
                return Result(accepted, accepted ? string.Empty : Service.Status);
            }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.cancel-invite", "取消本端未完成邀请，连接建立后不会误关闭棋局", Empty, _ =>
            {
                if (!Ready(out string reason) || Service.Lan == null)
                {
                    return Result(false, string.IsNullOrEmpty(reason) ? "尚未打开局域网" : reason);
                }
                Service.Lan.CancelInvitation();
                return Result(true, Service.Lan.Status);
            }, owner: Owner);

            RevRuntimeAutomation.Register("pvp.lan-check-begin", "创建独立双端真实UDP/UTP夹具，验证发现、拒绝重试、握手、有序分片与清理；不修改玩家棋局", Empty, _ => BeginLanCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-check-state", "只读真实局域网传输夹具的完成、断言与清理结果", Empty, _ => LanCheckState(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-agent-long", "真实UTP双端Agent落满68手自动结算，验证后半局仍推进，不访问付费API", Empty, _ => BeginLanGameCheck(true, true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.agent-long-fixture", "为本机KCP测试安装68手合法候选策略，仅替换HTTP", Empty,
                _ => { PvpAgentChecks.InstallLongGame(Service); return Result(true, "已安装长局策略"); }, owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-agent-begin", "真实UTP双端提子后交给Agent自动停一手与数子，HTTP固定响应，不消费Key", Empty, _ => BeginLanGameCheck(true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-game-begin", "两份隔离PvpService真实局域网对弈：发现邀请、版本握手、提子、双停与自动计分，20秒护栏", Empty, _ => BeginLanGameCheck(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-analysis-begin", "真实 UTP 两端验证换手刷新胜率和合法建议，使用固定 HTTP 回复", Empty, _ => BeginLanGameCheck(analysis: true), owner: Owner);
            RevRuntimeAutomation.Register("pvp.lan-game-state", "只读真实局域网业务对局的双端序号、指纹、提子、终局与清理结果", Empty, _ => LanGameCheckState(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("pvp.test-cleanup", "幂等停止测试夹具，并沿正式入口离开当前PvP房间；保留当前场景供后续返回", Empty, _ => Cleanup(), owner: Owner);
            RevRuntimeAutomation.Register("pvp.return", "通过GameFlow返回大厅；成功仅代表接受，必须等待screen=Home且transitioning=false", Empty, _ =>
            {
                if (Flow.Screen == GameScreen.Home && !Flow.IsTransitioning)
                {
                    return Result(true, "已经在大厅");
                }
                bool accepted = Flow.ReturnHome();
                return Result(accepted, accepted ? string.Empty : Flow.LastError);
            }, owner: Owner);
        }

        /// <summary>Bootstrap销毁时对称注销；先清理夹具，再撤掉能力，避免异常退出后静态检查继续占端口。</summary>
        public static void Unregister()
        {
            _kcpOpponent?.Dispose();
            _kcpOpponent = null;
            StopLanCheck();
            StopLanGameCheck();
            _lanCheck = null;
            _checkError = string.Empty;
            _checkCancelled = false;
            _lanGameCheck = null;
            _gameCheckError = string.Empty;
            _gameCheckCancelled = false;
            RevRuntimeAutomation.UnregisterAllOf(Owner);
        }

        private static bool Ready(out string reason)
        {
            if (Flow.Screen != GameScreen.Pvp || Flow.IsTransitioning || Service.Config == null)
            {
                reason = "请先进入九路围棋并等待过场与配置加载完成";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static object Result(bool accepted, string reason)
        {
            return new Dictionary<string, object>
            {
                ["accepted"] = accepted,
                ["reason"] = reason ?? string.Empty,
                ["screen"] = Flow.Screen.ToString(),
                ["transitioning"] = Flow.IsTransitioning,
                ["agentControl"] = Service.AgentControlsHumanSeat,
                ["agentStatus"] = Service.AgentStatus,
                ["sequence"] = Service.Core?.Sequence ?? 0,
                ["stateHash"] = Service.Core?.StateHash.ToString() ?? string.Empty,
                ["status"] = Service.Status
            };
        }

        /// <summary>纯查询只复制当前已提交事实；不触发加载、连接、时钟推进、重放或规则计算。</summary>
        public static object State()
        {
            PvpService service = Service;
            GoCore core = service.Core;
            var peers = new List<object>();
            if (service.Lan != null)
            {
                foreach (LanPeerInfo peer in service.Lan.Peers)
                {
                    peers.Add(new Dictionary<string, object>
                    {
                        ["id"] = peer.Id, ["name"] = peer.Name, ["address"] = peer.Address,
                        ["controlPort"] = peer.ControlPort, ["busy"] = peer.Busy
                    });
                }
            }
            int[] board = new int[GoCore.PointCount];
            int black = 0, white = 0;
            if (core != null)
            {
                for (int point = 0; point < GoCore.PointCount; point++)
                {
                    board[point] = (int)core.GetStone(point);
                    if (board[point] == (int)GoSide.Black)
                    {
                        black++;
                    }
                    else if (board[point] == (int)GoSide.White)
                    {
                        white++;
                    }
                }
            }
            bool tableLoaded = RevDataTableManager.IsLoaded<PvpRuleTable>();
            var territory = new GoBoardAnalysis();
            core?.Analyze(territory);
            int blackTerritory = 0, whiteTerritory = 0;
            for (int i = 0; i < 81; i++)
            {
                if ((int)territory.Territory[i] == 1)
                {
                    blackTerritory++;
                }
                else if ((int)territory.Territory[i] == 2)
                {
                    whiteTerritory++;
                }
            }
            int tableRows = 0;
            if (tableLoaded)
            {
                foreach (var row in RevDataTableManager.Get<PvpRuleTable>().All)
                {
                    tableRows++;
                }
            }
            bool abMode = true;
#if UNITY_EDITOR
            abMode = RevResBootstrap.UseABInEditor;
#endif
            return new Dictionary<string, object>
            {
                ["screen"] = Flow.Screen.ToString(), ["transitioning"] = Flow.IsTransitioning, ["flowError"] = Flow.LastError,
                ["focused"] = UnityEngine.Application.isFocused, ["background"] = UnityEngine.Application.runInBackground, ["agentStatus"] = service.AgentStatus, ["agentControl"] = service.AgentControlsHumanSeat,
                ["mode"] = service.Mode.ToString(), ["phase"] = core?.Phase.ToString() ?? "None", ["turn"] = core?.Turn.ToString() ?? "None",
                ["sequence"] = core?.Sequence ?? 0, ["moveNumber"] = core?.MoveNumber ?? 0, ["stateHash"] = core?.StateHash.ToString() ?? string.Empty,
                ["positionHash"] = core?.PositionHash.ToString() ?? string.Empty, ["connected"] = service.Connected, ["queued"] = service.Queued,
                ["status"] = service.Status, ["lastNetworkError"] = service.LastNetworkError, ["transport"] = "Fantasy.KCP", ["mySide"] = service.MySide.ToString(), ["roomId"] = service.RoomId,
                ["blackName"] = service.BlackName, ["whiteName"] = service.WhiteName, ["blackStones"] = black, ["whiteStones"] = white,
                ["blackCaptures"] = core?.BlackCaptures ?? 0, ["whiteCaptures"] = core?.WhiteCaptures ?? 0,
                ["blackConfirmed"] = core != null && core.HasConfirmed(GoSide.Black), ["whiteConfirmed"] = core != null && core.HasConfirmed(GoSide.White),
                ["winner"] = core?.Winner.ToString() ?? "None", ["endReason"] = core?.EndReason.ToString() ?? "None",
                ["board"] = board, ["historyCount"] = service.Moves.Count, ["revision"] = service.Revision,
                ["computer"] = service.IsComputerGame, ["menu"] = RevUI.Get<PvpPanel>()?.Menu ?? "",
                ["blackTerritory"] = blackTerritory, ["whiteTerritory"] = whiteTerritory,
                ["battleVisible"] = RevUI.Get<PvpPanel>()?.BattleVisible ?? false,
                ["boardLayout"] = RevUI.Get<PvpPanel>()?.BoardLayoutSnapshot(),
                ["hintsVisible"] = service.HintsVisible, ["unlimitedHints"] = true, ["selectedPoint"] = service.SelectedPoint,
                ["preview"] = service.PreviewText, ["blackClock"] = service.ClockText(GoSide.Black), ["whiteClock"] = service.ClockText(GoSide.White),
                ["configLoaded"] = service.Config != null, ["tableLoaded"] = tableLoaded, ["tableRows"] = tableRows,
                ["rulesVersion"] = PvpService.ProtocolVersion, ["rulesJson"] = service.Config?.RulesJson ?? string.Empty,
                ["peers"] = peers, ["peerCount"] = peers.Count, ["lanConnected"] = service.Lan?.IsConnected ?? false,
                ["lanDiscovering"] = service.Lan?.IsDiscovering ?? false, ["lanStatus"] = service.Lan?.Status ?? string.Empty,
                ["incomingPeerId"] = service.Lan?.IncomingInvitation?.Id ?? string.Empty,
                ["incomingPeerName"] = service.Lan?.IncomingInvitation?.Name ?? string.Empty,
                ["pvpDomain"] = RevScope.IsActive(GameDomain.Pvp), ["logicCount"] = RevLogic.Count,
                ["monoCount"] = RevMono.Count, ["abMode"] = abMode
            };
        }

        private static object BeginLanCheck()
        {
            if (_checkRunning)
            {
                return Result(false, "局域网传输夹具正在运行，请先等待完成或清理");
            }
            if (!Ready(out string reason))
            {
                return Result(false, reason);
            }
            StopLanCheck();
            _checkCancelled = false;
            _checkError = string.Empty;
            PvpConfig config = Service.Config;
            var options = new LanPeerOptions(config.discoveryPort, config.announceSeconds, config.peerExpirySeconds,
                config.invitationTimeoutSeconds, config.connectTimeoutSeconds);
            _lanCheck = new LanTransportChecks(options);
            if (!_lanCheck.Start(out reason))
            {
                return Result(false, reason);
            }
            // 显式独立 owner，测试完成或被finally取消都只清理自己的更新，不会停止真实PvP服务。
            _checkRunning = RevMono.AddUpdate(TickLanCheck, owner: CheckOwner);
            if (!_checkRunning)
            {
                _checkError = "无法注册局域网传输夹具的逐帧驱动";
                _lanCheck.Dispose();
                return Result(false, _checkError);
            }
            return Result(true, "夹具已启动，请轮询pvp.lan-check-state直到complete=true");
        }

        private static void TickLanCheck()
        {
            try
            {
                _lanCheck.Tick();
                if (_lanCheck.Complete)
                {
                    StopLanCheck();
                }
            }
            catch (Exception error)
            {
                // 将失败保存在状态出口；自动化据此失败，不制造无穷错误日志或假装通过。
                _checkError = error.ToString();
                StopLanCheck();
            }
        }

        private static object LanCheckState()
        {
            bool complete = (_lanCheck?.Complete ?? false) || _checkCancelled || !string.IsNullOrEmpty(_checkError);
            return new Dictionary<string, object>
            {
                ["started"] = _lanCheck != null, ["running"] = _checkRunning, ["complete"] = complete,
                ["passed"] = (_lanCheck?.Passed ?? false) && !_checkCancelled && string.IsNullOrEmpty(_checkError),
                ["cancelled"] = _checkCancelled, ["assertions"] = _lanCheck?.Assertions ?? 0,
                ["detail"] = string.IsNullOrEmpty(_checkError) ? _lanCheck?.Detail ?? "尚未运行" : _checkError,
                ["cleaned"] = !_checkRunning, ["monoCount"] = RevMono.Count
            };
        }

        private static void StopLanCheck()
        {
            RevMono.RemoveAllOf(CheckOwner);
            _checkRunning = false;
            _lanCheck?.Dispose();
        }

        private static object Cleanup()
        {
            _kcpOpponent?.Dispose();
            _kcpOpponent = null;
            if (_checkRunning)
            {
                _checkCancelled = true;
            }
            StopLanCheck();
            if (_gameCheckRunning)
            {
                _gameCheckCancelled = true;
            }
            StopLanGameCheck();
            PvpAgentChecks.Restore(Service);
            if (Flow.Screen == GameScreen.Pvp && !Flow.IsTransitioning)
            {
                Service.LeaveRoom();
            }
            return new Dictionary<string, object>
            {
                ["cleaned"] = true, ["lanCheck"] = LanCheckState(), ["lanGameCheck"] = LanGameCheckState(), ["state"] = State()
            };
        }

        // 开启真实 UTP 夹具。画面用例把黑方交给当前场景，其余用例使用两份隔离服务。
        private static object BeginLanGameCheck(bool agents = false, bool longGame = false, bool duel = false, bool duelHashAudit = false, bool duelHashMismatch = false, bool duelTestEntry = false, bool analysis = false, bool visibleDuel = false)
        {
            if (_gameCheckRunning)
            {
                return Result(false, "局域网业务夹具正在运行，请先等待完成或清理");
            }
            if (!Ready(out string reason))
            {
                return Result(false, reason);
            }
            if (visibleDuel && (Service.Core != null || Service.Lan != null || Service.Connected))
            {
                return Result(false, "请从未开局的围棋入口运行预测画面验收。");
            }
            StopLanGameCheck();
            _gameCheckCancelled = false;
            _gameCheckError = string.Empty;
            _lanGameCheck = new PvpLanGameChecks(Service.Config, agents, longGame, duel, duelHashAudit, duelHashMismatch,
                duelTestEntry, analysis, visibleDuel ? Service : null);
            if (!_lanGameCheck.Start(out reason))
            {
                return Result(false, reason);
            }
            _gameCheckRunning = RevMono.AddUpdate(TickLanGameCheck, owner: GameCheckOwner);
            if (!_gameCheckRunning)
            {
                _gameCheckError = "无法注册局域网业务夹具的逐帧驱动";
                _lanGameCheck.Dispose();
                return Result(false, _gameCheckError);
            }
            return Result(true, "真实业务对局已启动，请轮询pvp.lan-game-state");
        }

        private static void TickLanGameCheck()
        {
            try
            {
                _lanGameCheck.Tick();
                if (_lanGameCheck.Complete)
                {
                    StopLanGameCheck();
                }
            }
            catch (Exception error)
            {
                _gameCheckError = error.ToString();
                StopLanGameCheck();
            }
        }

        private static object LanGameCheckState()
        {
            return new Dictionary<string, object>
            {
                ["started"] = _lanGameCheck != null, ["running"] = _gameCheckRunning,
                ["complete"] = (_lanGameCheck?.Complete ?? false) || _gameCheckCancelled || !string.IsNullOrEmpty(_gameCheckError),
                ["passed"] = (_lanGameCheck?.Passed ?? false) && !_gameCheckCancelled && string.IsNullOrEmpty(_gameCheckError),
                ["cancelled"] = _gameCheckCancelled, ["assertions"] = _lanGameCheck?.Assertions ?? 0,
                ["detail"] = string.IsNullOrEmpty(_gameCheckError) ? _lanGameCheck?.Detail ?? "尚未运行" : _gameCheckError,
                ["cleaned"] = !_gameCheckRunning && (_lanGameCheck?.Cleaned ?? true),
                ["hostSequence"] = _lanGameCheck?.HostSequence ?? 0, ["guestSequence"] = _lanGameCheck?.GuestSequence ?? 0,
                ["hostHash"] = _lanGameCheck?.HostHash ?? string.Empty, ["guestHash"] = _lanGameCheck?.GuestHash ?? string.Empty,
                ["blackCaptures"] = _lanGameCheck?.BlackCaptures ?? 0, ["winner"] = _lanGameCheck?.Winner ?? string.Empty,
                ["duelHash"] = _lanGameCheck?.DuelHashState(),
                ["monoCount"] = RevMono.Count
            };
        }

        private static void StopLanGameCheck()
        {
            RevMono.RemoveAllOf(GameCheckOwner);
            _gameCheckRunning = false;
            _lanGameCheck?.Dispose();
        }

        [Serializable]
        private sealed class ActionArgs
        {
            public string kind;
            public int x;
            public int y;
        }
        [Serializable]
        private sealed class ConnectArgs
        {
            public string endpoint;
            public string nickname;
        }
        [Serializable]
        private sealed class NicknameArgs
        {
            public string nickname;
        }
        [Serializable]
        private sealed class PeerArgs
        {
            public string peerid;
        }
        [Serializable]
        private sealed class AcceptArgs
        {
            public bool accept;
        }
    }
}
#endif

using System;
using RevGame.Flow;
using RevGame.Pvp.Networking;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>
    /// 九路围棋的简洁界面：未开局只显示两种联机入口，开局后只留双方信息、棋盘和必要动作。
    /// 装配、落屏、交互转发各守一个职责；规则、配对、时钟和棋谱都由 PvpService 持有。
    /// 面板只拥有棋盘的对象池租约和刷新计时器；退出后迟到的加载回调必须归还，不能污染下一次进入。
    /// </summary>
    [RevUIPanel("UI/Panel/Pvp", RevUILayer.Normal)]
    public sealed class PvpPanel : RevUIPanel
    {
        [RevBind("top/title")] private Text _title;
        [RevBind("top/black")] private Text _black;
        [RevBind("top/white")] private Text _white;
        [RevBind("top/turn")] private Text _turn;
        [RevBind("status")] private Text _status;
        [RevBind("preview")] private Text _preview;
        [RevBind("controls/pass")] private Button _pass;
        [RevBind("controls/resign")] private Button _resign;
        [RevBind("controls/hint")] private Button _hint;
        [RevBind("controls/hint/label")] private Text _hintLabel;
        [RevBind("controls/toggleHints")] private Button _toggleHints;
        [RevBind("controls/toggleHints/label")] private Text _toggleLabel;
        [RevBind("controls/reconnect")] private Button _reconnect;
        [RevBind("lobby")] private GameObject _lobby;
        [RevBind("lobby/nickname")] private InputField _nickname;
        [RevBind("lobby/endpoint")] private InputField _endpoint;
        [RevBind("lobby/mode")] private Button _mode;
        [RevBind("lobby/mode/label")] private Text _modeLabel;
        [RevBind("lobby/connect")] private Button _connect;
        [RevBind("lobby/match")] private Button _match;
        [RevBind("lobby/cancel")] private Button _cancel;
        [RevBind("lobby/help")] private Text _help;
        [RevBind("invite")] private GameObject _invite;
        [RevBind("invite/text")] private Text _invitationText;
        [RevBind("scoring")] private GameObject _scoring;
        [RevBind("scoring/text")] private Text _scoringText;
        [RevBind("scoring/confirmScore")] private Button _confirmScore;
        [RevBind("scoring/resume")] private Button _resume;
        [RevBind("result")] private GameObject _result;
        [RevBind("result/text")] private Text _resultText;
        [RevBind("result/replayPrevious")] private Button _previous;
        [RevBind("result/replayNext")] private Button _next;
        [RevBind("result/replayExit")] private Button _replayExit;
        [RevBind("confirm")] private GameObject _confirm;
        [RevBind("confirm/text")] private Text _confirmText;

        // 六行是预制体可见布局槽位，不是网络发现上限；点击保存的设备 Id，避免列表刷新后邀请错人。
        private readonly Button[] _peers = new Button[6];
        private readonly Text[] _peerLabels = new Text[6];
        private readonly string[] _peerIds = new string[6];
        private PvpBoardView _board;
        private Action _confirmedAction;
        private bool _opening;
        private int _leaseVersion;
        private string _feedback = string.Empty;

        private PvpService Service
        {
            get
            {
                return RevServices.GetRequired<PvpService>();
            }
        }

        /// <summary>棋盘输入查询此值；弹窗未关闭时不允许透过 UI 误落子。</summary>
        public bool IsModalOpen
        {
            get
            {
                return (_confirm != null && _confirm.activeSelf) || (_invite != null && _invite.activeSelf) || (_lobby != null && _lobby.activeSelf);
            }
        }

        protected override void OnBindView()
        {
            for (int i = 0; i < _peers.Length; i++)
            {
                Transform peer = transform.Find("lobby/peers/peer" + i);
                _peers[i] = peer.GetComponent<Button>();
                _peerLabels[i] = peer.Find("label").GetComponent<Text>();
            }
        }

        protected override void OnOpen()
        {
            _opening = true;
            _leaseVersion++;
            _feedback = string.Empty;
            _confirmedAction = null;
            Show(_confirm, false);
            if (string.IsNullOrWhiteSpace(_nickname.text))
            {
                _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            }
            if (!string.IsNullOrEmpty(Service.Endpoint))
            {
                _endpoint.text = Service.Endpoint;
            }
            // 文字每秒五次足够读秒显示；网络收包由独立的 PvP Logic 轮询，不能依赖面板是否显示。
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }

        protected override void PlayOpenTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeIn, .2f, onDone, owner: this);
        }

        protected override void PlayCloseTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeOut, .15f, onDone, owner: this);
        }

        /// <summary>GameFlow 的过场屏障等待真实 AB 棋盘就绪；加载失败抛回流程，不假装已经可操作。</summary>
        public async RevTask PrepareAsync()
        {
            int version = _leaseVersion;
            bool returned = false;
            GamePoolRequests.GetAsync<PvpBoardView>(board =>
            {
                if (!_opening || version != _leaseVersion)
                {
                    if (board != null)
                    {
                        RevPool.Return(board);
                    }
                }
                else
                {
                    _board = board;
                }
                returned = true;
            });
            float deadline = Time.realtimeSinceStartup + 30;
            while (_opening && version == _leaseVersion && (!returned || (_board != null && !_board.Ready)))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException("九路围棋棋盘加载超时，请返回后重试。");
                }
                await RevTask.Yield();
            }
            if (!_opening || version != _leaseVersion || _board == null || !_board.Ready)
            {
                throw new InvalidOperationException("九路围棋棋盘未就绪或本次进入已取消。");
            }
            RefreshView();
        }

        protected override void OnRefreshView()
        {
            PvpService service = Service;
            GoCore core = service.Core;
            bool hasGame = core != null;
            transform.Find("top").gameObject.SetActive(hasGame);
            bool playing = hasGame && core.Phase == GoPhase.Playing;
            bool scoring = hasGame && core.Phase == GoPhase.Scoring;
            bool finished = hasGame && core.Phase == GoPhase.Finished;
            bool canAct = service.CanAct;
            bool myTurn = playing && (service.Mode == PvpConnectionMode.Local || core.Turn == service.MySide);
            string connection = service.Mode == PvpConnectionMode.Server ? "服务器匹配" : service.Mode == PvpConnectionMode.Lan ? "局域网直连" : "本地双人";
            Set(_title, hasGame ? "九路围棋 · " + connection : "九路围棋");
            Set(_black, hasGame ? "● " + service.BlackName + "\n" + service.ClockText(GoSide.Black) + "   提子 " + core.BlackCaptures : "黑先白后");
            Set(_white, hasGame ? "○ " + service.WhiteName + "\n" + service.ClockText(GoSide.White) + "   提子 " + core.WhiteCaptures : "同样规则 · 公平落子");
            Set(_turn, hasGame ? service.ResultText().Split('\n')[0] : "选择一种对战方式");
            Set(_status, string.IsNullOrEmpty(_feedback) ? service.Status : _feedback);
            Set(_preview, playing ? service.PreviewText : scoring ? "点选整块死子；双方确认同一方案后结算" : string.Empty);
            Show(_lobby, !hasGame);
            Show(_scoring, scoring);
            Show(_result, finished);
            Show(_pass.gameObject, playing);
            Show(_resign.gameObject, hasGame && !finished);
            Show(_hint.gameObject, playing);
            Show(_toggleHints.gameObject, hasGame);
            Show(_reconnect.gameObject, hasGame && service.Mode == PvpConnectionMode.Server && !service.Connected);
            _pass.interactable = canAct && myTurn;
            _resign.interactable = canAct;
            _hint.interactable = canAct && myTurn && service.HintsRemaining > 0;
            Set(_hintLabel, "落子建议 " + service.HintsRemaining);
            Set(_toggleLabel, service.HintsVisible ? "隐藏辅助" : "显示辅助");
            Set(_modeLabel, service.Config.rookie ? "新手场 · 点两次落子" : "进阶场 · 点击即落子");
            _mode.interactable = !service.Queued && !service.Connected;
            _connect.interactable = !service.Queued;
            Show(_match.gameObject, service.Connected && service.Mode == PvpConnectionMode.Server && !service.Queued);
            Show(_cancel.gameObject, service.Queued);
            RefreshPeers(service);
            Set(_scoringText, service.ResultText());
            _confirmScore.interactable = canAct && scoring && (service.Mode == PvpConnectionMode.Local || !core.HasConfirmed(service.MySide));
            _resume.interactable = canAct && scoring;
            Set(_resultText, service.ResultText());
            _previous.interactable = finished && service.Moves.Count > 0 && service.ReplayStep != 0;
            _next.interactable = finished && service.ReplayStep >= 0 && service.ReplayStep < service.Moves.Count;
            _replayExit.interactable = service.ReplayStep >= 0;
        }

        private void RefreshPeers(PvpService service)
        {
            LanPeerTransport lan = service.Lan;
            int count = lan == null ? 0 : lan.Peers.Count;
            for (int i = 0; i < _peers.Length; i++)
            {
                bool present = i < count;
                Show(_peers[i].gameObject, present);
                _peerIds[i] = present ? lan.Peers[i].Id : string.Empty;
                if (present)
                {
                    LanPeerInfo peer = lan.Peers[i];
                    Set(_peerLabels[i], peer.Name + " · " + peer.Address + (peer.Busy ? "（忙碌）" : "  邀请"));
                    _peers[i].interactable = !peer.Busy;
                }
            }
            Set(_help, lan == null ? "服务器匹配需要服务器在线；局域网无需云服务器，双方连接同一网络并打开搜索。" :
                lan.Status + "\n已发现 " + count + " 台设备；若未发现，请确认同一网络与防火墙允许。" + (count > _peers.Length ? " 当前显示前 " + _peers.Length + " 台。" : string.Empty));
            LanPeerInfo invitation = lan == null ? null : lan.IncomingInvitation;
            Show(_invite, invitation != null && service.Core == null);
            if (invitation != null)
            {
                Set(_invitationText, invitation.Name + " 邀请你进行九路围棋\n" + invitation.Address);
            }
        }

        /// <summary>按钮只有意图转发；失败文案留在本屏，不抛到大厅，不改变单机流程。</summary>
        private void Run(Action action)
        {
            _feedback = string.Empty;
            try
            {
                action();
            }
            catch (Exception error)
            {
                _feedback = "操作未完成：" + error.Message;
                RevLog.Warn(_feedback, "Pvp");
            }
            RefreshView();
        }

        private void Ask(string text, Action action)
        {
            _confirmedAction = action;
            Set(_confirmText, text);
            Show(_confirm, true);
        }

        [RevButtonClick("connect")]
        private void Connect()
        {
            Run(() => Service.StartServer(_endpoint.text, _nickname.text));
        }
        [RevButtonClick("reconnect")]
        private void Reconnect()
        {
            Run(() => Service.StartServer(Service.Endpoint, Service.Nickname));
        }

        [RevButtonClick("match")]
        private void Match()
        {
            Run(() => Service.Match());
        }

        [RevButtonClick("cancel")]
        private void CancelMatch()
        {
            if (Service.Queued)
            {
                Run(() => Service.Match());
            }
        }

        [RevButtonClick("lan")]
        private void SearchLan()
        {
            Run(() => Service.StartLan(_nickname.text));
        }

        [RevButtonClick("local")]
        private void Local()
        {
            Run(() => Service.StartLocal());
        }

        [RevButtonClick("mode")]
        private void ChangeMode()
        {
            Run(() => Service.SetRookie(!Service.Config.rookie));
        }

        [RevButtonClick("accept")]
        private void AcceptInvitation()
        {
            Run(() => Service.ReplyInvitation(true));
        }

        [RevButtonClick("decline")]
        private void DeclineInvitation()
        {
            Run(() => Service.ReplyInvitation(false));
        }

        private void Invite(int index)
        {
            string peerId = _peerIds[index];
            if (!string.IsNullOrEmpty(peerId))
            {
                Run(() => Service.Invite(peerId));
            }
        }

        [RevButtonClick("peer0")]
        private void Peer0()
        {
            Invite(0);
        }
        [RevButtonClick("peer1")]
        private void Peer1()
        {
            Invite(1);
        }
        [RevButtonClick("peer2")]
        private void Peer2()
        {
            Invite(2);
        }
        [RevButtonClick("peer3")]
        private void Peer3()
        {
            Invite(3);
        }
        [RevButtonClick("peer4")]
        private void Peer4()
        {
            Invite(4);
        }
        [RevButtonClick("peer5")]
        private void Peer5()
        {
            Invite(5);
        }

        [RevButtonClick("pass")]
        private void Pass()
        {
            Run(() => Service.Submit(GoAction.Pass));
        }

        [RevButtonClick("resign")]
        private void Resign()
        {
            Ask("确认认输并结束这局围棋？", () => Service.Submit(GoAction.Resign));
        }

        [RevButtonClick("hint")]
        private void Hint()
        {
            Run(() => Service.RequestHint());
        }

        [RevButtonClick("toggleHints")]
        private void ToggleHints()
        {
            Run(() => Service.ToggleHints());
        }

        [RevButtonClick("confirmScore")]
        private void ConfirmScore()
        {
            Run(() => Service.Submit(GoAction.ConfirmScore));
        }

        [RevButtonClick("resume")]
        private void Resume()
        {
            Run(() => Service.Submit(GoAction.Resume));
        }

        [RevButtonClick("replayPrevious")]
        private void Previous()
        {
            Run(() => Service.Replay(Service.ReplayStep < 0 ? Service.Moves.Count - 1 : Service.ReplayStep - 1));
        }

        [RevButtonClick("replayNext")]
        private void Next()
        {
            Run(() => Service.Replay(Service.ReplayStep + 1));
        }

        [RevButtonClick("replayExit")]
        private void ExitReplay()
        {
            Run(() => Service.ExitReplay());
        }

        [RevButtonClick("newGame")]
        private void NewGame()
        {
            Run(() => Service.LeaveRoom());
        }

        [RevButtonClick("back")]
        private void Back()
        {
            if (Service.Core != null && Service.Core.Phase != GoPhase.Finished)
            {
                Ask("确认离开当前对局并返回大厅？\n对手将收到离开提示。", ReturnHome);
            }
            else
            {
                Run(ReturnHome);
            }
        }

        private void ReturnHome()
        {
            Service.LeaveRoom();
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            if (!flow.ReturnHome())
            {
                _feedback = flow.LastError;
            }
        }

        [RevButtonClick("yes")]
        private void ConfirmYes()
        {
            Action action = _confirmedAction;
            _confirmedAction = null;
            Show(_confirm, false);
            if (action != null)
            {
                Run(action);
            }
        }

        [RevButtonClick("no")]
        private void ConfirmNo()
        {
            _confirmedAction = null;
            Show(_confirm, false);
        }

        private static void Set(Text target, string value)
        {
            if (target.text != value)
            {
                target.text = value;
            }
        }

        private static void Show(GameObject target, bool visible)
        {
            if (target.activeSelf != visible)
            {
                target.SetActive(visible);
            }
        }

        protected override void OnClose()
        {
            _opening = false;
            _leaseVersion++;
            _confirmedAction = null;
            RevTimer.CancelAllOf(this);
            RevUIAnim.StopAllOf(this);
            if (_board != null)
            {
                RevPool.Return(_board);
                _board = null;
            }
        }
    }
}

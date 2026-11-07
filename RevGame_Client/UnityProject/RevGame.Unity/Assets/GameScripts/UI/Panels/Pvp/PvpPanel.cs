using System;
using System.Collections.Generic;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pvp.Networking;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace RevGame.Pvp
{
    /// <summary>
    /// 九路围棋对局HUD。四选一入口、服务器匹配和局域网发现由独立面板承载。
    /// 装配、落屏、交互转发各守一个职责；规则、配对、时钟和棋谱都由 PvpService 持有。
    /// 面板只拥有棋盘的对象池租约和刷新计时器；退出后迟到的加载回调必须归还，不能污染下一次进入。
    /// </summary>
    [RevUIPanel("UI/Panel/Pvp", RevUILayer.Normal)]
    public sealed partial class PvpPanel : RevUIPanel
    {
        protected override void OnBindView()
        {
            // 闲置时恢复Prefab配置的默认立绘。每句台词的表情由共享目录提供。
            _idlePortrait = _portrait.sprite;
            // 沿用围棋按钮的九宫格素材和字体，战术选项只在满足资格时弹出。
            _duelButton = Instantiate(_resign, _resign.transform.parent);
            _duelButton.name = "DuelChallenge";
            _duelButton.onClick.RemoveAllListeners();
            _duelButton.onClick.AddListener(ChallengeDuelFromPanel);
            _duelButton.GetComponentInChildren<Text>().text = "跟对面爆了";
            var rect = (RectTransform)_duelButton.transform;
            // 左下空位避开中央棋盘，不盖住最后一排交叉点。
            rect.anchoredPosition += new Vector2(-665, 102);
            _duelButton.gameObject.SetActive(false);
        }

        private Button _duelButton;

        private void ChallengeDuelFromPanel()
        {
            Service.ChallengeDuel();
        }

        private void RefreshDuelChallenge()
        {
            bool visible = _battleVisible && Service.CanChallengeDuel;
            if (_duelButton == null || _duelButton.gameObject.activeSelf == visible)
            {
                return;
            }
            _duelButton.transform.DOKill();
            _duelButton.gameObject.SetActive(visible);
            if (visible)
            {
                _duelButton.transform.localScale = Vector3.one * .7f;
                _duelButton.transform.DOScale(1, .24f).SetEase(Ease.OutBack).SetUpdate(true).SetLink(_duelButton.gameObject);
            }
        }

        [RevBind("top/title")] private Text _title;
        [RevBind("top/black")] private Text _black;
        [RevBind("top/white")] private Text _white;
        [RevBind("top/turn")] private Text _turn;
        [RevBind("controls/pass")] private Button _pass;
        [RevBind("controls/resign")] private Button _resign;
        [RevBind("controls/hint")] private Button _hint;
        [RevBind("controls/hint/label")] private Text _hintLabel;
        [RevBind("controls/toggleHints")] private Button _toggleHints;
        [RevBind("controls/toggleHints/label")] private Text _toggleLabel;
        [RevBind("controls/reconnect")] private Button _reconnect;
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

        [RevBind("controls/agent/label")] private Text _agentLabel;
        [RevBind("controls/agent")] private Button _agent;

        [RevButtonClick("agentSettings")]
        private void OpenAgentSettings()
        {
            PvpAgentSettingsPanel.OpenSettings();
        }

        [RevButtonClick("agent")]
        private void ToggleAgent()
        {
            Run(() =>
            {
                Service.SetAgentControl(!Service.AgentControlsHumanSeat);
                PopToast(Service.AgentStatus);
            });
        }

        private PvpBoardView _board;
        [SerializeField] private YayaExpressionCatalog portraitCatalog;
        // 持有双眼和正式表情图集的租约，关闭面板时成对释放。
        private readonly List<GameSpriteAtlasService.ScopedAtlasLease> _matchAtlases = new List<GameSpriteAtlasService.ScopedAtlasLease>();
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
                return (_confirm != null && _confirm.activeSelf) || (_invite != null && _invite.activeSelf) || DefeatVisible || !_battleVisible || _battleTransition;
            }
        }

        protected override void OnOpen()
        {
            _opening = true;
            _battleVisible = false;
            _battleTransition = false;
            _seenStatus = "";
            _leaseVersion++;
            _feedback = string.Empty;
            _confirmedAction = null;
            Show(_confirm, false);
            ResetMatchFeedback();
            // 文字每秒五次足够读秒显示；网络收包由独立的 PvP Logic 轮询，不能依赖面板是否显示。
            RevTimer.Every(.2f, TickPresentation, domain: RevTimeDomain.Unscaled, owner: this);
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
            var atlases = RevServices.GetRequired<GameSpriteAtlasService>();
            // 面板持有两张实际用到的图集。先等像素就绪，再让GameFlow撤掉转场遮挡。
            if (portraitCatalog == null)
            {
                throw new InvalidOperationException("九路围棋缺少鸦鸦表情目录");
            }
            var eyeAtlas = atlases.AcquireScopedAtlas("BoardStatus");
            _matchAtlases.Add(eyeAtlas);
            _matchAtlases.Add(atlases.AcquireScopedAtlas("PvpDefeat"));
            foreach (string name in portraitCatalog.AtlasNames)
            {
                _matchAtlases.Add(atlases.AcquireScopedAtlas(name));
            }
            for (int i = 0; i < _matchAtlases.Count; i++)
            {
                await _matchAtlases[i].WaitReadyAsync();
                if (!_opening || version != _leaseVersion)
                {
                    throw new OperationCanceledException("九路围棋资源加载已取消");
                }
            }
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
            _board.BindEyeFrames(eyeAtlas);
            _board.gameObject.SetActive(false);
            _yayaLines = new PvpYayaLines(portraitCatalog);
            PrepareMatchSound();
            RefreshView();
        }

        protected override void OnRefreshView()
        {
            RefreshDuelChallenge();
            PvpService service = Service;
            GoCore core = service.Core;
            bool hasGame = core != null;
            transform.Find("top").gameObject.SetActive(hasGame);
            bool playing = hasGame && core.Phase == GoPhase.Playing;
            bool scoring = hasGame && core.Phase == GoPhase.Scoring;
            bool finished = hasGame && core.Phase == GoPhase.Finished;
            bool canAct = service.CanAct && !service.AgentControlsHumanSeat;
            _agent.interactable = hasGame && !finished && service.Mode != PvpConnectionMode.Local;
            Set(_agentLabel, service.AgentControlsHumanSeat ? "停止托管" : "Agent托管");
            bool myTurn = playing && ((service.Mode == PvpConnectionMode.Local && !service.IsComputerGame) || core.Turn == service.MySide);
            string connection = service.Mode == PvpConnectionMode.Server ? "服务器匹配" : service.Mode == PvpConnectionMode.Lan ? "局域网直连" : service.IsComputerGame ? "本地人机" : "本地双人";
            Set(_title, hasGame ? "九路围棋 · " + connection : "九路围棋");
            Set(_black, hasGame ? "● " + service.BlackName + "\n" + service.ClockText(GoSide.Black) + "   提子 " + core.BlackCaptures : "黑先白后");
            Set(_white, hasGame ? "○ " + service.WhiteName + "\n" + service.ClockText(GoSide.White) + "   提子 " + core.WhiteCaptures : "同样规则 · 公平落子");
            RefreshMatchFeedback(service, playing);
            Set(_turn, _counting ? "终局 · 正在清点" : hasGame ? service.ResultText().Split('\n')[0] : "选择一种对战方式");
            Show(_scoring, scoring);
            Show(_result, finished);
            Show(_pass.gameObject, playing);
            Show(_resign.gameObject, hasGame && !finished);
            Show(_hint.gameObject, playing);
            Show(_toggleHints.gameObject, hasGame);
            Show(_reconnect.gameObject, hasGame && service.Mode == PvpConnectionMode.Server && !service.Connected);
            _pass.interactable = canAct && myTurn;
            _resign.interactable = canAct;
            _hint.interactable = service.CanRequestHint && !service.HintPending;
            Set(_hintLabel, service.HintPending ? "鸦鸦思考中" : "落子建议");
            Set(_toggleLabel, service.HintsVisible ? "隐藏辅助" : "显示辅助");
            Set(_scoringText, service.ResultText());
            _confirmScore.interactable = canAct && scoring && (service.Mode == PvpConnectionMode.Local || !core.HasConfirmed(service.MySide));
            _resume.interactable = canAct && scoring && !core.PlacementLimitReached;
            _previous.interactable = finished && !_counting && service.Moves.Count > 0 && service.ReplayStep != 0;
            _next.interactable = finished && !_counting && service.ReplayStep >= 0 && service.ReplayStep < service.Moves.Count;
            _replayExit.interactable = service.ReplayStep >= 0;
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
            PopToast(_feedback);
            RefreshView();
        }

        private void Ask(string text, Action action)
        {
            _confirmedAction = action;
            Set(_confirmText, text);
            Show(_confirm, true);
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
            Run(() => { Service.LeaveRoom(); ShowDestination("Entry"); });
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
            CloseMenus();
            _opening = false;
            _leaseVersion++;
            _confirmedAction = null;
            RevTimer.CancelAllOf(this);
            RevUIAnim.StopAllOf(this);
            ResetMatchFeedback();
            ReleaseMatchSound();
            _yayaLines = null;
            if (_board != null)
            {
                _board.ReleaseEyeFrames();
                RevPool.Return(_board);
                _board = null;
            }
            foreach (var atlas in _matchAtlases)
            {
                atlas.Dispose();
            }
            _matchAtlases.Clear();
        }
    }
}

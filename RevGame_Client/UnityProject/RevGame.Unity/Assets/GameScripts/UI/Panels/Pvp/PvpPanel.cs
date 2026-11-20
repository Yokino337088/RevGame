using System;
using System.Collections.Generic;
using System.Linq;
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
        // 对局两侧各自使用实际安全宽度；顶部计时与棋盘下方的三个操作留在设计框内。
        private static readonly string[] LeftSidebarPaths =
        {
            "controls/back", "controls/reconnect", "controls/agent", "controls/agentSettings",
            "controls/DuelChallenge", "controls/DuelTest", "boardToast"
        };
        private static readonly string[] RightSidebarPaths =
        {
            "controls/toggleHints", "yayaPortrait", "yayaBubble", "scoring", "result"
        };

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        private static readonly string[] MatchLayoutPaths = LeftSidebarPaths.Concat(RightSidebarPaths)
            .Concat(new[] { "top", "winRate", "controls/pass", "controls/resign", "controls/hint" }).ToArray();

        /// <summary>只读对局的实际控制区。菜单或切场时没有可测棋盘，直接返回空。</summary>
        internal object BoardLayoutSnapshot()
        {
            return _battleVisible && _board != null ? _board.InspectControls(transform, MatchLayoutPaths) : null;
        }
#endif

        protected override void OnBindView()
        {
            BindOutcomeTitle();
            BindAnalysisHud();
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            BindDuelTestButton();
#endif
            // 动态按钮先创建，再读取原始锚点；设备变化只从这一份设计坐标重算。
            RevGame.UI.GameMobileLayout.EnableBoardFrame(transform, LeftSidebarPaths, RightSidebarPaths, "defeat/backdrop");
        }

        // 按当前面板生命周期持有的挑战按钮，位置避开棋盘。
        private Button _duelButton;

        // 点击仅转发给 PvP 服务；界面不能自行宣布挑战获准。
        private void ChallengeDuelFromPanel()
        {
            Service.ChallengeDuel();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 开发测试控件和面板同寿命，不写入正式 AB 或影响原战术挑战控件。
        private Button _duelTestButton;

        private void BindDuelTestButton()
        {
            _duelTestButton = Instantiate(_resign, _resign.transform.parent);
            _duelTestButton.name = "DuelTest";
            _duelTestButton.onClick = new Button.ButtonClickedEvent();
            _duelTestButton.onClick.AddListener(TestDuelFromPanel);
            _duelTestButton.GetComponentInChildren<Text>().text = "决斗测试";
            var rect = (RectTransform)_duelTestButton.transform;
            // 放在左侧联机重连按钮上方，避开棋盘、Agent 按钮和原有战术挑战。
            rect.anchorMin = new Vector2(.025f, .56f);
            rect.anchorMax = new Vector2(.20f, .64f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            _duelTestButton.gameObject.SetActive(false);
        }

        // 面板转发测试动作，只有权威回执才能触发换场。
        private void TestDuelFromPanel()
        {
            Service.TestDuel();
        }
#endif

        // 根据业务资格更新显示，仅在可见性变化时启动或停止 Tween。
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
        /// <summary>返回按钮在退房进行期间按房间快照禁用。</summary>
        [RevBind("controls/back")] private Button _back;
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
        [RevBind("result/newGame")] private Button _newGame;
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
        private GamePortraitScope _matchPortraits;
        /// <summary>棋局确认窗口由当前 PvP 域持有，串行处理不同来源的确认。</summary>
        private RevPopupQueue _confirmationQueue;
        /// <summary>当前确认窗口的唯一请求，过期按钮不能完成下一项。</summary>
        private RevPopupRequest _visibleConfirmation;
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

        /// <summary>初始化这一轮 PvP 视图以及随域结束的确认队列。</summary>
        protected override void OnOpen()
        {
            _opening = true;
            _battleVisible = false;
            _battleTransition = false;
            _seenStatus = "";
            _leaseVersion++;
            _feedback = string.Empty;
            _confirmationQueue = new RevPopupQueue();
            _visibleConfirmation = null;
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
            // 面板持有本局实际用到的图集。先等像素就绪，再让 GameFlow 撤掉转场遮挡。
            if (portraitCatalog == null)
            {
                throw new InvalidOperationException("九路围棋缺少鸦鸦表情目录");
            }
            var eyeAtlas = atlases.AcquireScopedAtlas("BoardStatus");
            _matchAtlases.Add(eyeAtlas);
            var agentAtlas = atlases.AcquireScopedAtlas("PvpAgent");
            _matchAtlases.Add(agentAtlas);
            _matchAtlases.Add(atlases.AcquireScopedAtlas("PvpDefeat"));
            for (int i = 0; i < _matchAtlases.Count; i++)
            {
                await _matchAtlases[i].WaitReadyAsync();
                if (!_opening || version != _leaseVersion)
                {
                    throw new OperationCanceledException("九路围棋资源加载已取消");
                }
            }
            var portraits = new GamePortraitScope();
            _matchPortraits = portraits;
            // PvP 只预载台词表和默认站姿用到的表情；策划新增表情仍由同一个目录校验编号。
            bool legacy = false;
            foreach (int id in RevDataTableManager.Get<PvpYayaLineTable>().All.Select(row => row.Expression).Append(26).Distinct())
            {
                var entry = portraitCatalog.Find(id);
                if (entry.Reference.IsSet)
                {
                    await portraits.WarmAsync(entry.Reference);
                }
                else
                {
                    legacy = true;
                }
                if (!_opening || version != _leaseVersion)
                {
                    throw new OperationCanceledException("九路围棋立绘加载已取消");
                }
            }
            if (legacy)
            {
                foreach (string name in portraitCatalog.AtlasNames)
                {
                    await portraits.WarmAtlasAsync(name);
                }
            }
            if (!_opening || version != _leaseVersion)
            {
                throw new OperationCanceledException("九路围棋立绘加载已取消");
            }
            _idlePortrait = GetMatchPortrait(26);
            _portrait.sprite = _idlePortrait;
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
            BindAnalysisSkin(agentAtlas);
            _board.gameObject.SetActive(false);
            _yayaLines = new PvpYayaLines(portraitCatalog);
            PrepareMatchSound();
            RefreshView();
        }

        private Sprite GetMatchPortrait(int id)
        {
            var entry = portraitCatalog.Find(id);
            return entry.Reference.IsSet ? _matchPortraits.Get(entry.Reference) : entry.Sprite;
        }

        /// <summary>关闭时先解除立绘显示，再归还本次对局持有的图集租约。</summary>
        private void ReleaseMatchPortraits()
        {
            if (_matchPortraits == null)
            {
                return;
            }
            _portrait.sprite = null;
            _idlePortrait = null;
            _matchPortraits.Dispose();
            _matchPortraits = null;
        }

        /// <summary>应用房间快照的显示与按钮门禁，动画和复盘继续使用各自的表现状态。</summary>
        protected override void OnRefreshView()
        {
            RefreshDuelChallenge();
            PvpService service = Service;
            PvpService.PvpRoomSnapshot room = service.RoomSnapshot;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            _presentedRoom = room;
            Show(_duelTestButton.gameObject, !IsModalOpen && service.CanTestDuel);
#endif
            bool hasGame = room.HasGame;
            transform.Find("top").gameObject.SetActive(hasGame);
            bool playing = room.Phase == GoPhase.Playing;
            bool scoring = room.Phase == GoPhase.Scoring;
            bool finished = room.Phase == GoPhase.Finished;
            _agent.interactable = room.Allows(PvpRoomAction.Agent);
            Set(_agentLabel, room.AgentLabel);
            Set(_title, room.Title);
            Set(_black, room.BlackText);
            Set(_white, room.WhiteText);
            RefreshMatchFeedback(service, playing);
            RefreshAnalysisHud(service, _battleVisible && playing);
            Set(_turn, room.MissingCriticalState ? room.TurnText : _counting ? "终局 · 正在清点" : _settling ? "终局" : room.TurnText);
            Show(_scoring, _battleVisible && scoring);
            Show(_result, _battleVisible && finished && (_counting || !_settling));
            Show(_pass.gameObject, playing);
            Show(_resign.gameObject, hasGame && !finished);
            Show(_hint.gameObject, playing);
            Show(_toggleHints.gameObject, hasGame);
            Show(_reconnect.gameObject, room.ShowReconnect);
            _reconnect.interactable = room.Allows(PvpRoomAction.Reconnect);
            _back.interactable = room.Allows(PvpRoomAction.Leave);
            _pass.interactable = room.Allows(PvpRoomAction.Pass);
            _resign.interactable = room.Allows(PvpRoomAction.Resign);
            _hint.interactable = room.Allows(PvpRoomAction.Hint);
            Set(_hintLabel, room.HintLabel);
            Set(_toggleLabel, service.HintsVisible ? "隐藏辅助" : "显示辅助");
            Set(_scoringText, room.ResultText);
            _confirmScore.interactable = room.Allows(PvpRoomAction.ConfirmScore);
            _resume.interactable = room.Allows(PvpRoomAction.Resume);
            Show(_previous.gameObject, !_settling);
            Show(_next.gameObject, !_settling);
            Show(_replayExit.gameObject, !_settling);
            Show(_newGame.gameObject, !_settling);
            _previous.interactable = finished && !_settling && service.Moves.Count > 0 && service.ReplayStep != 0;
            _next.interactable = finished && !_settling && service.ReplayStep >= 0 && service.ReplayStep < service.Moves.Count;
            _replayExit.interactable = !_settling && service.ReplayStep >= 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>最后应用到 HUD 的只读房间快照，供 Play 验收用来对齐显示版本。</summary>
        private PvpService.PvpRoomSnapshot _presentedRoom;

        /// <summary>与同源模型对齐房间与版本，再读取实际控件文本和禁用状态。</summary>
        internal object RoomDisplayEvidence(PvpService.PvpRoomSnapshot current)
        {
            return new Dictionary<string, object>
            {
                ["ready"] = BattleReady && _presentedRoom != null,
                ["applied"] = BattleReady && _presentedRoom?.RoomId == current.RoomId && _presentedRoom.Revision == current.Revision,
                ["roomId"] = _presentedRoom?.RoomId ?? string.Empty,
                ["revision"] = _presentedRoom?.Revision ?? 0,
                ["title"] = _title.text,
                ["black"] = _black.text,
                ["white"] = _white.text,
                ["blackTitle"] = _black.text.Split('\n')[0],
                ["whiteTitle"] = _white.text.Split('\n')[0],
                ["turn"] = _turn.text,
                ["pass"] = BattleReady && _pass.gameObject.activeInHierarchy && _pass.IsInteractable(),
                ["resign"] = BattleReady && _resign.gameObject.activeInHierarchy && _resign.IsInteractable(),
                ["reconnect"] = BattleReady && _reconnect.gameObject.activeInHierarchy && _reconnect.IsInteractable(),
                ["leave"] = BattleReady && _back.gameObject.activeInHierarchy && _back.IsInteractable()
            };
        }
#endif

        /// <summary>使用当前房间的端点与昵称重连，交给服务恢复已确认棋谱。</summary>
        [RevButtonClick("reconnect")]
        private void Reconnect()
        {
            PvpService service = Service;
            if (service.RoomSnapshot.Allows(PvpRoomAction.Reconnect))
            {
                Run(() => service.StartServer(service.Endpoint, service.Nickname));
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
            PopToast(_feedback);
            RefreshView();
        }

        /// <summary>对局确认排队，仅玩家确认且面板仍属同一次打开时执行业务。</summary>
        private void Ask(string key, string text, Action action, int priority = 0)
        {
            EnqueueConfirmation(key, text, action, priority, "Battle", 10).Forget();
        }

        /// <summary>显示和隐藏沿用现有确认控件。返回调度结果供运行期和验收检查。</summary>
        private async RevTask<RevPopupStatus> EnqueueConfirmation(string key, string text, Action action,
            int priority, string phase, double waitSeconds)
        {
            int version = _leaseVersion;
            RevPopupRequest request = _confirmationQueue.Enqueue(key, phase, priority, waitSeconds, shown =>
            {
                _visibleConfirmation = shown;
                Set(_confirmText, text);
                Show(_confirm, true);
            }, hidden =>
            {
                if (_visibleConfirmation == hidden)
                {
                    _visibleConfirmation = null;
                    Show(_confirm, false);
                }
            });
            RevPopupStatus status = await request.WaitAsync();
            if (status == RevPopupStatus.Confirmed && action != null && _opening && _leaseVersion == version)
            {
                Run(action);
            }
            return status;
        }

        [RevButtonClick("pass")]
        private void Pass()
        {
            Run(() => Service.Submit(GoAction.Pass));
        }

        /// <summary>提交认输确认，玩家确定后重新走业务提交。</summary>
        [RevButtonClick("resign")]
        private void Resign()
        {
            Ask("resign", "确认认输并结束这局围棋？", () => Service.Submit(GoAction.Resign));
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

        /// <summary>对局期间排队确认离场；无进行中的棋局则直接返回。</summary>
        [RevButtonClick("back")]
        private void Back()
        {
            if (Service.Core != null && Service.Core.Phase != GoPhase.Finished)
            {
                Ask("leave", "确认离开当前对局并返回大厅？\n对手将收到离开提示。", ReturnHome, priority: 1);
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

        /// <summary>完成当前票据；队列先隐藏确认窗口，再通知业务。</summary>
        [RevButtonClick("yes")]
        private void ConfirmYes()
        {
            _confirmationQueue?.TryComplete(_visibleConfirmation, true);
        }

        /// <summary>为当前票据提交拒绝，允许队列处理下一项。</summary>
        [RevButtonClick("no")]
        private void ConfirmNo()
        {
            _confirmationQueue?.TryComplete(_visibleConfirmation, false);
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

        /// <summary>关闭已显示界面时先撤销确认与挂载，再归还棋盘和图集。</summary>
        protected override void OnClose()
        {
            CloseMenus();
            RevUI.Close<PvpNoticePanel>();
            _opening = false;
            _leaseVersion++;
            _confirmationQueue?.Dispose();
            _confirmationQueue = null;
            RevTimer.CancelAllOf(this);
            RevUIAnim.StopAllOf(this);
            ResetMatchFeedback();
            ReleaseMatchPortraits();
            ReleaseAnalysisSkin();
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

        /// <summary>直接销毁界面时也撤销确认；等待方根据版本停止执行业务。</summary>
        protected override void OnRelease()
        {
            _opening = false;
            _leaseVersion++;
            _confirmationQueue?.Dispose();
            _confirmationQueue = null;
        }
    }
}

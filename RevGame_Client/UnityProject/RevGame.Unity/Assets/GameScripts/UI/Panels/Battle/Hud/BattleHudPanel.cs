// ============================================================
// BattleHudPanel.cs
// HUD 只读服务。每次打开重新订阅，关闭取消计时和序列，防止回池后丢监听或重复刷新。
// ============================================================
using System.Collections.Generic;
using DG.Tweening;
using Unity.Profiling;
using RevGame.Data;
using RevGame.Core;
using RevGame.View;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 战斗 HUD（策划案 §03：局内只有"落子"一个操作，其余全部自动 —— 所以 HUD 只负责**读**）。
    ///
    /// 每秒观察十次。右栏战斗期在全队增益和在场战况之间切换，主将血条始终可见，事件用独立弹幕。
    /// </summary>
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed partial class BattleHudPanel : RevUIPanel
    {
        [RevButtonClick("btnSettings")]
        private void OnSettings()
        {
            RevServices.GetRequired<RevGame.Flow.GameFlow>().OpenAudioSettings();
        }
        [RevBind]
        private Text _roundText;
        [RevBind]
        private Text _countText;
        [RevBind]
        private Button _btnHome;
        [RevBind]
        private Text _bossText;
        [RevBind]
        private Image _bossBar;
        [RevBind]
        private Image _bossFrame;
        [RevBind]
        private Text _eventText;
        [RevBind]
        private Image _eventIcon;
        [SerializeField]
        private Sprite[] eventIcons;
        // 提示零件单独池化。独立同级容器避开面板停用子树时的换父节点限制。
        private RectTransform _noticeRoot;
        private readonly HashSet<BattleEventNoticeView> _notices = new HashSet<BattleEventNoticeView>();
        private int _noticeGeneration;
        private int _regularNoticeGeneration;
        private readonly Queue<PreparationBuffDef> _preparationQueue = new Queue<PreparationBuffDef>();
        private BattleEventNoticeView _preparationNotice;
        // 敌方落子与玩家增益分通道，避免同一模拟步触发增益时把落子提示立即收走。
        private BattleEventNoticeView _enemyNotice;
        private int _enemyNoticeGeneration;
        private bool _preparationLoading;
        private Tween _bossTween;
        // 血条复用绑定期创建的委托，0～1 的填充比例只影响显示。Tween 归池前先清引用。
        private DG.Tweening.Core.DOGetter<float> _readBossBar;
        private DG.Tweening.Core.DOSetter<float> _writeBossBar;
        private TweenCallback _clearBossBar;
        private float _bossTarget = -1f;
        public int PendingPreparationNoticeCount => _preparationQueue.Count;
        private bool _noticeClosed;
        // 已发布 AB 和编辑器配方还带着这些节点。面板绑定时统一停用，无需替换素材或路径。
        private static readonly string[] HiddenInfoNodes = { "phaseIcon", "phaseLabel", "progressBar", "progressFill", "logText", "helperScoreLabel" };
        public int ActiveNoticeCount
        {
            get
            {
                return _notices.Count;
            }
        }
        // 性能标记只包HUD刷新段，单位由Profiler记录；不能把它当成整场CPU或GPU耗时。
        private static readonly ProfilerMarker HudRefreshMarker = new ProfilerMarker("RevGame.Hud.Refresh");
        // “上次落屏记录是否有效”。首次打开强制false，保证即使数值碰巧与上一局相同也完整初始化。
        private bool _hudValid;
        // 只缓存已经显示的整数百分比。真实增益由战斗服务拥有，刷新不重复格式化相同文字。
        private int _hudChapter, _hudRound, _hudAttack, _hudSpeed, _hudDefense;
        // 上次显示的Boss回合；同一章节/回合值不变时，标签后缀也可能变化，故要独立比较。
        private bool _hudBossRound;
        /// <summary>战况页和增益页的轮播间隔，单位为战斗界面使用的秒。</summary>
        private const float InfoPageSeconds = 5f;
        /// <summary>当前轮播期应展示战况页。新阶段回到增益页。</summary>
        private bool _enemyInfoRequested;
        /// <summary>上次实际显示的页码，用于识别返回同数值的另一页。</summary>
        private bool _hudShowsEnemy;
        /// <summary>上一次显示的我方在场数。</summary>
        private int _hudFriendlyCount;
        /// <summary>上一次显示的敌方在场数。</summary>
        private int _hudEnemyCount;
        /// <summary>上一次显示的敌方战术档位，范围 1 至 4。</summary>
        private int _hudEnemyTactics;
        // 主将换人或实际生命值变化时才更新数字，不能只比较血条比例。
        private int _hudKingId, _hudKingHp, _hudKingMax;
        private IBattleService Battle
        {
            get
            {
                return RevServices.GetRequired<IBattleService>();
            }
        }
        /// <summary>绑定顶栏、信息牌坐标和深墨色，创建本面板血条复用的只读及写入委托。</summary>
        protected override void OnBindView()
        {
            // 血条和文字挂在信息牌下，跟父节点一起移动。
            GameMobileLayout.EnableBoardFrame(transform, leftNodes: null, rightNodes: null,
                topNodes: new[] { "roundText", "scoreFrame", "infoPlate", "btnHome", "btnSettings" }, bottomNodes: null);
            _readBossBar = () => _bossBar.fillAmount;
            _writeBossBar = value => _bossBar.fillAmount = value;
            _clearBossBar = ClearBossTween;
            // 旧制作配方仍可能给这个字段赋值。优先停用它实际绑定的控件，再处理早期 AB 的同名节点。
            if (helperScoreLabel != null)
            {
                helperScoreLabel.gameObject.SetActive(false);
            }
            foreach (string name in HiddenInfoNodes)
            {
                Transform node = transform.Find(name);
                if (node != null)
                {
                    node.gameObject.SetActive(false);
                }
            }

            // 使用新信息牌作为坐标原点，文字留在平静的纸面，主将血条固定在底部。
            var plate = (RectTransform)transform.Find("infoPlate");
            AnchorInPlate(_countText.rectTransform, plate, .13f, .36f, .87f, .86f);
            AnchorInPlate(_bossFrame.rectTransform, plate, .13f, .12f, .87f, .20f);
            AnchorInPlate(_bossBar.rectTransform, plate, .13f, .12f, .87f, .20f);
            AnchorInPlate(_bossText.rectTransform, plate, .13f, .21f, .87f, .35f);
            _countText.alignment = TextAnchor.MiddleCenter;
            _countText.color = new Color32(0x14, 0x29, 0x30, 255);
            _bossText.color = _countText.color;
            _countText.resizeTextForBestFit = true;
            _countText.resizeTextMinSize = 16;
            _countText.resizeTextMaxSize = 32;
            RectTransform round = _roundText.rectTransform;
            round.anchorMin = new Vector2(.5f, round.anchorMin.y);
            round.anchorMax = new Vector2(.5f, round.anchorMax.y);
            round.anchoredPosition = new Vector2(0f, round.anchoredPosition.y);
        }

        /// <summary>将原有控件装入信息牌的归一化范围，保留预制体素材及射线设置。</summary>
        private static void AnchorInPlate(RectTransform child, RectTransform plate, float left, float bottom, float right, float top)
        {
            child.SetParent(plate, false);
            child.anchorMin = new Vector2(left, bottom);
            child.anchorMax = new Vector2(right, top);
            child.offsetMin = child.offsetMax = Vector2.zero;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>测试读取实际控件状态，避免只看服务数值就把 HUD 当成已经正确显示。</summary>
        public object InfoSnapshot()
        {
            bool legacyControlsHidden = true;
            foreach (string name in HiddenInfoNodes)
            {
                Transform node = transform.Find(name);
                if (node != null && node.gameObject.activeInHierarchy)
                {
                    legacyControlsHidden = false;
                    break;
                }
            }
            bool bossVisible = _bossBar.gameObject.activeInHierarchy && _bossText.gameObject.activeInHierarchy;
            return new Dictionary<string, object>
            {
                ["bonuses"] = _hudShowsEnemy ? string.Empty : _countText.text,
                ["infoPage"] = _hudShowsEnemy ? "Enemy" : "Team",
                ["infoText"] = _countText.text,
                ["attackBonusPercent"] = _hudAttack,
                ["attackSpeedBonusPercent"] = _hudSpeed,
                ["defenseBonusPercent"] = _hudDefense,
                ["legacyControlsHidden"] = legacyControlsHidden,
                ["bossVisible"] = bossVisible,
                ["bossHpText"] = bossVisible ? _bossText.text : string.Empty,
                ["bossFill"] = bossVisible ? _bossBar.fillAmount : 0f
            };
        }
#endif
        /// <summary>面板重新打开时使显示缓存失效，并重新注册归属于当前面板的观察和轮播计时。</summary>
        protected override void OnOpen()
        {
            _hudValid = false;
            _enemyInfoRequested = false;
            OpenScore();
            ResetBarAnimations();
            _noticeClosed = false;
            _noticeGeneration++;
            var container = new GameObject("BattleEventNotices", typeof(RectTransform));
            _noticeRoot = container.GetComponent<RectTransform>();
            _noticeRoot.SetParent(transform.parent, false);
            _noticeRoot.anchorMin = Vector2.zero;
            _noticeRoot.anchorMax = Vector2.one;
            _noticeRoot.offsetMin = _noticeRoot.offsetMax = Vector2.zero;
            OpenPieceManagement();
            SetNoticeVisible(false);
            RevTimer.Every(0.1f, RefreshView, owner: this);
            RevTimer.Every(InfoPageSeconds, RotateBattleInfo, owner: this);
            RevEvent.AddEventListener(Events.PreparationBuffsGranted, OnPreparationGranted, owner: this);
            // owner: this → 面板释放时框架自动解绑，不用手写解绑清单。
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnUpgraded, owner: this);
            RevEvent.AddEventListener<int, string>(Events.EnemyDeployed, OnEnemyDeployed, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, OnBossTelegraph, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, 0, this);
            RevEvent.AddEventListener<string>(Events.BattleEventTriggered, OnBattleEvent, owner: this);
        }
        /// <summary>只做"把服务状态画到屏上"，不修改任何游戏数据。</summary>
        // 10Hz观察仍保留。先取真实状态，与上一份落屏收据比较，再格式化字符串/写Text。
        // 减少的是没有变化时的字符串分配和UI脏标记；HP、日志、积分发生变化时仍立即在下一次观察刷新。
        protected override void OnRefreshView()
        {
            using (HudRefreshMarker.Auto())
            {
                IBattleService _battle = Battle;
                RefreshScore(_battle);
                RefreshPieceManagement();

                if (_battle == null)
                {
                    return;
                }
                if (_roundText != null && (!_hudValid || _hudChapter != _battle.Chapter || _hudRound != _battle.Round || _hudBossRound != _battle.IsBossRound))
                {
                    _roundText.text = "第 " + _battle.Chapter + " / 6 章 · 第 " + _battle.Round + " 回合" + (_battle.IsBossRound ? "（擒王 · 不限时）" : "（不限时）");
                }
                RefreshBattleInfo(_battle);

                Pieces.Piece king = null;
                IReadOnlyList<Pieces.Piece> enemies = _battle.EnemyPieces;
                for (int i = 0; i < enemies.Count; i++)
                {
                    Pieces.Piece piece = enemies[i];
                    if (piece.IsKing)
                    {
                        king = piece;
                        break;
                    }
                }

                if (!_hudValid || _hudKingId != (king == null ? 0 : king.Id))
                {
                    _bossFrame.gameObject.SetActive(king != null);
                    _bossBar.gameObject.SetActive(king != null);
                    _bossText.gameObject.SetActive(king != null);
                }
                if (king != null)
                {
                    UpdateBar(_bossBar, king.HpRatio, ref _bossTween, ref _bossTarget, _battle.Phase != GamePhase.Battle, _readBossBar, _writeBossBar, _clearBossBar);
                    if (!_hudValid || _hudKingId != king.Id || _hudKingHp != king.Hp || _hudKingMax != king.MaxHp)
                    {
                        _bossText.text = $"主将生命 {king.Hp} / {king.MaxHp}";
                    }
                    // 先完成本次显示，再更新收据；如果先把缓存写成新值，变化条件会误判“不用显示”。
                    _hudKingHp = king.Hp;
                    _hudKingMax = king.MaxHp;
                }
                _hudKingId = king == null ? 0 : king.Id;
                _hudChapter = _battle.Chapter;
                _hudRound = _battle.Round;
                _hudBossRound = _battle.IsBossRound;
                _hudValid = true;
            }
        }
        /// <summary>战斗期才翻页。文字由既有的十次每秒观察统一更新，计时器本身不写控件。</summary>
        private void RotateBattleInfo()
        {
            if (Battle.Phase == GamePhase.Battle)
            {
                _enemyInfoRequested = !_enemyInfoRequested;
            }
        }

        /// <summary>比较页码及已显示的整数快照后再拼文案，避免未变化时重建字形和布局。</summary>
        private void RefreshBattleInfo(IBattleService battle)
        {
            bool showEnemy = battle.Phase == GamePhase.Battle && _enemyInfoRequested;
            bool pageChanged = !_hudValid || showEnemy != _hudShowsEnemy;
            int attack = Mathf.RoundToInt(battle.TeamAttackBonus * 100f);
            int speed = Mathf.RoundToInt(battle.TeamAttackSpeedBonus * 100f);
            int defense = Mathf.RoundToInt(battle.TeamDefenseBonus * 100f);
            if (showEnemy)
            {
                int friends = battle.PlayerPieces.Count;
                int enemies = battle.EnemyPieces.Count;
                // 战术档来自当前关卡与回合，维持同一回合时直接复用。
                int tactics = pageChanged || _hudChapter != battle.Chapter || _hudRound != battle.Round
                    ? battle.EnemyIntelligence : _hudEnemyTactics;
                if (pageChanged || friends != _hudFriendlyCount || enemies != _hudEnemyCount || tactics != _hudEnemyTactics)
                {
                    _countText.text = $"敌军在场 {enemies}\n我方在场 {friends}\n敌方战术 {tactics} / 4";
                    _hudFriendlyCount = friends;
                    _hudEnemyCount = enemies;
                    _hudEnemyTactics = tactics;
                }
            }
            else if (pageChanged || attack != _hudAttack || speed != _hudSpeed || defense != _hudDefense)
            {
                _countText.text = $"攻击力 {attack:+0;-0;+0}%\n攻速 {speed:+0;-0;+0}%\n防御力 {defense:+0;-0;+0}%";
            }
            _hudAttack = attack;
            _hudSpeed = speed;
            _hudDefense = defense;
            _hudShowsEnemy = showEnemy;
        }

        /// <summary>提交退场确认，玩家确认后才会交给 GameFlow 切回大厅。</summary>
        [RevButtonClick("btnHome")]
        private void OnHome()
        {
            RevServices.GetRequired<Flow.GameFlow>().RequestBattleExit(_btnHome);
        }

        /// <summary>关闭本轮 HUD，清除其退场提示、提示池和界面事件。</summary>
        protected override void OnClose()
        {
            RevServices.Get<Flow.GameFlow>()?.DismissBattleExitConfirmation();
            CloseScore();
            RevTimer.CancelAllOf(this);
            _noticeClosed = true;
            ClearRoundPresentation();
            RevPool.DestroyPool<BattleEventNoticeView>();

            if (_noticeRoot != null)
            {
                Destroy(_noticeRoot.gameObject);
                _noticeRoot = null;
            }
        }

        /// <summary>归还当前提示并使尚未加载完的旧请求失效；回合间保留池，避免反复创建。</summary>
        private void ClearNotices()
        {
            _noticeGeneration++;
            _regularNoticeGeneration++;
            _enemyNoticeGeneration++;
            _enemyNotice = null;
            _preparationQueue.Clear();
            _preparationNotice = null;
            _preparationLoading = false;
            _roundOutcomeNotice = null;
            foreach (BattleEventNoticeView notice in _notices)
            {
                if (notice != null)
                {
                    RevPool.Return(notice, 0);
                }
            }
            _notices.Clear();
        }

        // 结算弹幕优先于准备和增益消息，继续复用战斗域的提示池。
        private BattleEventNoticeView _roundOutcomeNotice;
        public bool IsRoundOutcomeVisible => _roundOutcomeNotice != null;

        /// <summary>从当前战斗读取败局重试目标，在自动推进之前显示并在播完后回调。</summary>
        internal void ShowRoundOutcome(GameSide winner, System.Action completed)
        {
            ClearNotices();
            int generation = _noticeGeneration;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error("回合结算弹幕加载失败", "RevGame");
                    return;
                }
                if (_noticeClosed || this == null || generation != _noticeGeneration ||
                    RevServices.Get<IBattleService>()?.Phase != GamePhase.Settle)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _roundOutcomeNotice = notice;
                _notices.Add(notice);
                notice.ShowRoundOutcome(winner == GameSide.Player, Battle.DefeatRetryRound, finished =>
                {
                    _roundOutcomeNotice = null;
                    OnNoticeFinished(finished);
                    completed();
                });
            }, _noticeRoot);
        }
        /// <summary>清除“上轮发生过什么”，服务负责重置数值，HUD 负责重置表现，两者缺一不可。</summary>
        private void ClearRoundPresentation()
        {
            ClearNotices();
            ResetBarAnimations();
            // 旧版静态提示节点也清空并隐藏，防止它与池化的新提示同时显示。
            _eventText.text = string.Empty;
            _eventIcon.sprite = null;
            SetNoticeVisible(false);
        }
        /// <summary>从配置读取弹幕文案。能量炸弹的间隔和扣血比例使用结算实际读取的参数。</summary>
        private void OnBattleEvent(string title)
        {
            if (title == "眼位成型" || title == "眼位复苏")
            {
                BattleText eyeText = GameTextConfig.Require("Event_" + title);
                string detail = eyeText.Detail.Replace("{defense}", ConfigText.Percent(Balance.EyeGuardDefenseBonus))
                    .Replace("{regen}", ConfigText.Percent(Balance.EyeHealRatio))
                    .Replace("{heal}", ConfigText.Percent(Balance.EyeReviveHealRatio));
                ShowNotice(eyeText, eyeText.Text, null, detail, central: true);
                return;
            }
            Sprite sprite = eventIcons.Length > 0 ? eventIcons[0] : null;
            for (int i = 0; i < eventIcons.Length; i++)
            {
                if (Data.BattleEventCatalog.NameOf((Data.BattleEventKind)i) == title)
                {
                    sprite = eventIcons[i];
                    break;
                }
            }
            BattleText config = GameTextConfig.Require("Event_" + title);
            string energyDetail = title == "能量巨大" ? config.Detail
                .Replace("{interval}", ConfigText.Number(Balance.BombIntervalSeconds))
                .Replace("{damage}", ConfigText.Percent(Balance.BombDamageHpRatio)) : null;
            ShowNotice(config, config.Text, sprite, energyDetail);
        }
        /// <summary>
        /// 只播报已经提交的敌方落子。先把棋种/坐标转成文字，异步加载不能保存可能阵亡的Piece引用。
        /// 新回合/关闭面板会改变代次：旧加载回调只回池、不补播，避免大厅突然出现敌方弹幕。
        /// </summary>
        private void OnEnemyDeployed(int id, string intention)
        {
            Pieces.Piece piece = Battle.Board.FindPiece(id);

            if (piece == null || piece.Side != GameSide.Enemy)
            {
                return;
            }
            BattleText config = GameTextConfig.Require("EnemyDeployment");
            string title = config.Text.Replace("{intention}", "敌方落子 · " + intention);
            string detail = config.Detail.Replace("{piece}", piece.Def.DisplayName).Replace("{position}", piece.Pos.ToString());
            ShowEnemyNotice(config, title, detail, "敌方落子提示加载失败");
        }
        /// <summary>
        /// Boss 开始蓄力时弹出“技能名 + 应对提示”。与敌方落子共用右栏同一条提示通道：
        /// Boss 技能更重要，新提示直接顶掉旧的，保证玩家看到的是“马上要发生的事”。
        /// 文字来自 Excel（BossSkillConfig 的 Name/Hint、BattleText 的 BossSkill 样式），这里不写死文案。
        /// </summary>
        private void OnBossTelegraph(BossSkillInfo info)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            BattleText config = GameTextConfig.Require("BossSkill");
            string title = config.Text.Replace("{skill}", (info.Enraged ? "狂暴 · " : "Boss · ") + def.Name);
            string detail = config.Detail.Replace("{hint}", def.Hint);
            ShowEnemyNotice(config, title, detail, "Boss 技能提示加载失败");
        }
        /// <summary>
        /// 右栏“敌方动态”提示通道：同一时间只显示一条，新提示替换旧提示。
        /// 用两层代次防止过期显示：_noticeGeneration 代表回合/面板寿命，_enemyNoticeGeneration 代表本通道的请求序号；
        /// 异步加载回来时任意一个不匹配都直接回池，不会在新回合里冒出旧提示。
        /// </summary>
        private void ShowEnemyNotice(BattleText config, string title, string detail, string loadError)
        {
            if (_enemyNotice != null)
            {
                _notices.Remove(_enemyNotice);
                RevPool.Return(_enemyNotice, 0);
                _enemyNotice = null;
            }
            int generation = _noticeGeneration;
            int request = ++_enemyNoticeGeneration;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error(loadError, "RevGame");
                    return;
                }
                if (_noticeClosed || this == null || generation != _noticeGeneration || request != _enemyNoticeGeneration)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _enemyNotice = notice;
                _notices.Add(notice);
                notice.Show(config, title, null, item =>
                {
                    _notices.Remove(item);

                    if (_enemyNotice == item)
                    {
                        _enemyNotice = null;
                    }
                }, detail, enemyDeployment: true);
            }, _noticeRoot);
        }
        private void OnUpgraded(int id)
        {
            foreach (Pieces.Piece piece in Battle.PlayerPieces)
            {
                if (piece.Id != id || !piece.IsAlive)
                {
                    continue;
                }
                BattleText config = GameTextConfig.Require("Upgrade_" + (int)piece.Def.Star);
                string title = config.Text.Replace("{piece}", piece.Def.DisplayName.Split(' ')[0]).Replace("{star}", ((int)piece.Def.Star).ToString());
                ShowNotice(config, title, eventIcons.Length > 0 ? eventIcons[0] : null);
                break;
            }
        }
        /// <summary>使用处理后的事件说明播报；异步过期时将提示归还池中。</summary>
        private void ShowNotice(BattleText config, string title, Sprite sprite, string detail = null, bool central = false)
        {
            // 同一时刻只读一条大提示。连续升级时显示最新结果，旧提示立即回池；
            // 不排一个跨回合的长队列，否则玩家已进入准备期却还在观看上轮支援。
            // 准备横幅与右栏升级提示分别管理：打牌升级不能吞掉尚未播报的准备增益。
            foreach (BattleEventNoticeView old in new List<BattleEventNoticeView>(_notices))
            {
                if (old == _preparationNotice || old == _enemyNotice)
                {
                    continue;
                }
                _notices.Remove(old);
                RevPool.Return(old, 0);
            }
            int regularGeneration = ++_regularNoticeGeneration;
            int generation = _noticeGeneration;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error("战斗事件提示预制体加载失败", "RevGame");
                    return;
                }
                // 异步加载结束时面板可能已经关闭；过期结果必须回池，不能在大厅补播。
                if (_noticeClosed || this == null || generation != _noticeGeneration || regularGeneration != _regularNoticeGeneration)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _notices.Add(notice);

                if (central)
                {
                    notice.ShowPreparation(title, detail, OnNoticeFinished);
                }
                else
                {
                    notice.Show(config, title, sprite, OnNoticeFinished, detail);
                }
            }, _noticeRoot);
        }
        /// <summary>数据已经生效后仅排播报队列，不在动画完成回调里修改战斗属性。</summary>
        private void OnPreparationGranted()
        {
            foreach (PreparationBuffDef buff in Battle.PreparationBuffs)
            {
                _preparationQueue.Enqueue(buff);
            }
            TryShowPreparationNotice();
            RefreshView();
        }
        private void TryShowPreparationNotice()
        {
            if (_noticeClosed || Battle.Phase != GamePhase.Prepare || _preparationLoading || _preparationNotice != null || _preparationQueue.Count == 0)
            {
                return;
            }
            PreparationBuffDef buff = _preparationQueue.Dequeue();
            int generation = _noticeGeneration;
            _preparationLoading = true;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                // 先判代次，再动loading。旧资源回调不能把新回合的加载状态改成false。
                if (_noticeClosed || this == null || generation != _noticeGeneration)
                {
                    if (notice != null)
                    {
                        RevPool.Return(notice, 0);
                    }
                    return;
                }
                _preparationLoading = false;

                if (notice == null)
                {
                    RevLog.Error("准备增益弹幕加载失败：" + buff.Title, "RevGame");
                    TryShowPreparationNotice();
                    return;
                }
                _preparationNotice = notice;
                _notices.Add(notice);
                notice.ShowPreparation(buff.Title, buff.Detail, OnPreparationNoticeFinished);
            }, _noticeRoot);
        }
        private void OnPreparationNoticeFinished(BattleEventNoticeView notice)
        {
            _notices.Remove(notice);
            _preparationNotice = null;
            TryShowPreparationNotice();
        }
        /// <summary>只有目标值改变才接续缓动，避免每0.1秒刷新把同一动画反复从头启动。</summary>
        // 此函数只有目标比例变化才创建Tween。read/write/onKilled来自面板已有委托，函数体不再捕获Image。
        // 0.28秒是表现缓动，不修改生命；Kill回调清当前句柄，避免对象池复用后误伤另一条动画。
        private static void UpdateBar(Image image, float value, ref Tween tween, ref float target, bool instant, DG.Tweening.Core.DOGetter<float> read, DG.Tweening.Core.DOSetter<float> write, TweenCallback onKilled)
        {
            value = Mathf.Clamp01(value);

            if (Mathf.Abs(target - value) < 0.00001f)
            {
                return;
            }
            tween?.Kill(false);
            tween = null;
            bool first = target < 0f;
            target = value;

            if (instant || first)
            {
                image.fillAmount = value;
                return;
            }
            // 【新手注意】项目开启了 Tween 复用：动画结束后，同一个 C# 对象可能立刻被借给死亡消融。
            // 因此不能只用 IsActive 判断“旧血条动画还活着”；它可能已经是别人的动画。
            // AutoKill（自然结束）和手动 Kill 都会执行 OnKill，必须在进入复用池前清空本面板的引用。
            // ref 参数不能在 lambda 中捕获，所以由各字段自己的清理方法接收这个回调。
            tween = DOTween.To(read, write, value, 0.28f)
                .SetEase(Ease.OutCubic).SetUpdate(true).OnKill(onKilled);
        }
        private void ClearBossTween()
        {
            _bossTween = null;
        }
        private void ResetBarAnimations()
        {
            _bossTween?.Kill(false);
            _bossTween = null;
            _bossTarget = -1f;
        }
        private void OnNoticeFinished(BattleEventNoticeView notice)
        {
            _notices.Remove(notice);
        }
        private void SetNoticeVisible(bool visible)
        {
            _eventText.transform.parent.gameObject.SetActive(visible);
        }
        /// <summary>阶段切换时清掉上一阶段的临时表现，并回到增益页。</summary>
        private void OnPhaseChanged(int round)
        {
            ClearRoundPresentation();
            _swallowWaiting = false;
            _enemyInfoRequested = false;

            if (Battle.Phase == GamePhase.Prepare)
            {
                NotifyBossSwallow();
            }
            ClearScoreFlights();
            RefreshView();
        }
        private void OnSettle(GameSide winner)
        {
            ClearRoundPresentation();
            ClearScoreFlights();
            RefreshView();
        }
    }
}

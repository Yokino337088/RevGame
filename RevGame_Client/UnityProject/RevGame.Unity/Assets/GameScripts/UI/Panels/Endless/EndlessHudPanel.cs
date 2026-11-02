using System;
using System.Text;
using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽战斗 HUD（新手先读这段）。
    ///
    /// 【设计原则：手机上一眼看懂、一根手指玩】
    /// 平时屏幕上只有：棋心血条、暂停、手牌、等级；其余按钮只在用得上的时候才出现：
    ///   - “开战”只在布阵和整备时出现；
    ///   - 领主血条只在领主在场时出现；
    ///   - 提示框只在需要告诉玩家下一步时出现。
    /// 同名手牌升级一枚棋子，进化由规则自动完成，移动棋子只要点棋子，都不需要按钮。
    ///
    /// 【代码分工】OnBindView 只找节点，OnRefreshView 只把数据写到屏幕，按钮方法只转发给 EndlessService。
    /// 每秒刷新 5 次文字，不是每帧，这样不会频繁重建整块 Canvas。
    ///
    /// 【动画】和六章同一套手感：面板用 RevUIAnim 淡入/淡出；弹窗、提示、新发的牌用 PopIn 回弹弹入，
    /// 血条/经验条 0.28 秒缓动（BattleHudPanel 同款），升级/受击/选牌/击杀里程碑用 DOPunch/DOShake 给反馈。
    /// 动画只由“状态变了”触发，不改任何业务数据；关闭面板时统一停止并还原。
    /// 底板/按钮的流光与手牌光泽来自预制体上的六章共享材质（ArcanePanel / CardGleam），这里不碰材质。
    /// </summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Normal)]
    public sealed partial class EndlessHudPanel : RevUIPanel
    {
        [RevBind("top/status")] private Text _status;
        [RevBind("top/heartTrack/heartFill")] private RectTransform _heartFill;
        [RevBind("boss")] private GameObject _boss;
        [RevBind("boss/bossTrack/bossFill")] private RectTransform _bossFill;
        [RevBind("boss/name")] private Text _bossName;
        [RevBind("tip")] private GameObject _tip;
        [RevBind("tip/text")] private Text _tipText;
        [RevBind("start")] private GameObject _start;
        [RevBind("start/label")] private Text _startLabel;
        [RevBind("level/label")] private Text _level;
        [RevBind("xpTrack/xpFill")] private RectTransform _xpFill;
        [RevBind("modal")] private GameObject _modal;
        [RevBind("modal/title")] private Text _modalTitle;
        [RevBind("modal/reroll")] private GameObject _reroll;
        /// <summary>弹窗右上角的圆形叉：只在暂停页出现，点了等于“继续战斗”。</summary>
        [RevBind("modal/modalClose")] private GameObject _modalClose;
        /// <summary>右上角横幅：自动获得的升级 / 自动处理的事件用它弹一句话，2.8 秒后自己收起，不打断战斗。</summary>
        [RevBind("banner")] private GameObject _banner;
        [RevBind("banner/text")] private Text _bannerText;
        /// <summary>
        /// 大横幅（领主来袭 / 领主击破后的强力增益）：右侧空白栏里一张木纹奖励卡，旋转回弹弹入、停留 4 秒再缩回。
        /// 放在棋盘右侧空栏，不遮挡任何棋子；颜色取自 Excel（EndlessBoon.ColorHex）。
        /// </summary>
        [RevBind("boon")] private GameObject _boon;
        [RevBind("boon/title")] private Text _boonTitle;
        [RevBind("boon/text")] private Text _boonText;
        /// <summary>手牌底图：平时用 UI_roleItem_imgBK，选中时换成带金光的 UI_roleItem_roleItem（由无尽资产生成器装配）。</summary>
        [SerializeField] private Sprite cardNormal;
        [SerializeField] private Sprite cardSelected;
        /// <summary>横幅已显示到第几条、显示到什么时候（真实时间）。</summary>
        private int _bannerSerial;
        private float _bannerUntil;
        private const float BannerSeconds = 2.8f;
        /// <summary>大横幅停留时间（真实秒，纯表现节奏）。</summary>
        private const float MajorSeconds = 4f;
        private float _boonUntil;
        private int _majorSerial;
        private RectTransform _boonRect;
        /// <summary>大横幅默认的金色字（Excel 没给颜色时用，纯表现常量）。</summary>
        private static readonly Color BoonGold = new Color(1f, .86f, .42f);
        /// <summary>棋心血条的图片，危险时染红。</summary>
        private Image _heartImage;
        /// <summary>手牌的三部分：按钮本身、棋子头像、名字。下标就是手牌序号。</summary>
        /// <summary>手牌槽位：4 张（与 Excel HandLimit 对应的预制体结构，改上限需同时重建 HUD）。</summary>
        private readonly GameObject[] _cards = new GameObject[4];
        private readonly Image[] _cardImages = new Image[4], _cardIcons = new Image[4];
        private readonly Text[] _cardNames = new Text[4];
        /// <summary>弹窗里的三个大选项。只有两个选项时用左右两张（0 和 2），只有一个时用中间（1）。</summary>
        private readonly GameObject[] _choices = new GameObject[3];
        private readonly Text[] _choiceTexts = new Text[3];
        private readonly StringBuilder _text = new StringBuilder(256);
        private EndlessArena _arena;
        private bool _opening;
        private int _version;
        /// <summary>上一次看到的错误，以及这条错误显示到什么时候。错误只显示 2.5 秒，不长期占屏幕。</summary>
        private string _lastError = "";
        private float _errorUntil;
        /// <summary>上一次刷新看到的状态，只用来判断“变化了没有”以触发动画。</summary>
        private bool _modalShown, _bossShown, _startShown, _tipShown;
        private int _modalSignature = int.MinValue, _shownLevel, _shownCards = -1, _shownSelected = -1, _shownKills;
        private float _shownHeart = -1;
        private string _shownTip = "";
        /// <summary>三条进度条各自的缓动；新目标到来先停旧的，不叠加。</summary>
        private Tween _heartTween, _xpTween, _bossTween;
        /// <summary>开战按钮的呼吸动画（RevUIAnim.Breathe），按钮隐藏时停止。</summary>
        private RevUIAnimHandle _startBreath;
        private RectTransform _top, _modalRect, _levelRect, _tipRect, _bossRect, _startRect;
        private static readonly Color DimCard = new Color(.72f, .70f, .68f);
        private static readonly string[] BreakthroughNames =
        {
            "多重弩阵", "灼尽再燃", "棋心回响", "临阵换锋"
        };
        private static readonly string[] AffixNames =
        {
            "双门奔袭", "碎盾潮", "潮汐冲锋", "断线精英"
        };
        public object PerformanceSnapshot()
        {
            return _arena == null ? null : _arena.Metrics();
        }
        private EndlessService Service
        {
            get
            {
                return RevServices.GetRequired<EndlessService>();
            }
        }
        /// <summary>打开：淡入（六章大厅/设置面板同款）。必须把 onDone 交给动画，框架才会结束“打开中”。</summary>
        protected override void PlayOpenTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeIn, .25f, onDone, owner: this);
        }
        /// <summary>关闭：淡出播完才真正隐藏。</summary>
        protected override void PlayCloseTransition(Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeOut, .15f, onDone, owner: this);
        }
        protected override void OnBindView()
        {
            _heartImage = _heartFill.GetComponent<Image>();
            _top = (RectTransform)transform.Find("top");
            _modalRect = (RectTransform)_modal.transform;
            _levelRect = (RectTransform)transform.Find("level");
            _tipRect = (RectTransform)_tip.transform;
            _bossRect = (RectTransform)_boss.transform;
            _startRect = (RectTransform)_start.transform;
            _boonRect = (RectTransform)_boon.transform;
            for (int i = 0; i < _cards.Length; i++)
            {
                Transform card = transform.Find("bottom/card" + i);
                _cards[i] = card.gameObject;
                _cardImages[i] = card.GetComponent<Image>();
                _cardIcons[i] = card.Find("icon").GetComponent<Image>();
                _cardNames[i] = card.Find("label").GetComponent<Text>();
            }
            BindCardDrag();
            for (int i = 0; i < _choices.Length; i++)
            {
                Transform choice = transform.Find("modal/choice" + i);
                _choices[i] = choice.gameObject;
                _choiceTexts[i] = choice.Find("label").GetComponent<Text>();
            }
        }
        protected override void OnOpen()
        {
            _opening = true;
            SetCardDragBinding(true);
            _version++;
            _lastError = "";
            _errorUntil = 0;
            ResetAnimationState();
            RevTimer.Every(0.2f, RefreshView, owner: this);
        }
        /// <summary>过场屏障等真实池对象与全部棋子可画；退出/失败时释放迟到的异步租约。</summary>
        public async RevTask PrepareAsync()
        {
            int version = _version;
            bool returned = false;
            GamePoolRequests.GetAsync<EndlessArena>(arena =>
            {
                if (!_opening || version != _version)
                {
                    if (arena != null)
                    {
                        RevPool.Return(arena);
                    }
                }
                else
                {
                    _arena = arena;
                }
                returned = true;
            }
            );
            float deadline = Time.realtimeSinceStartup + 30;
            while (_opening && (!returned || (_arena != null && !_arena.Ready && string.IsNullOrEmpty(_arena.Error))))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException("无尽棋盘资源就绪超时");
                }
                await RevTask.Yield();
            }
            if (_arena == null || !_arena.Ready)
            {
                throw new InvalidOperationException(_arena == null ? "无尽棋盘预制体加载失败" : _arena.Error);
            }
            RefreshView();
        }
        /// <summary>只在内容变化时才写文字：给 Text 赋值会让 Canvas 重建，能省则省。</summary>
        private static void Set(Text text, string value)
        {
            if (text.text != value)
            {
                text.text = value;
            }
        }
        /// <summary>只在状态变化时才切换显示，原因同上。</summary>
        private static void Show(GameObject target, bool visible)
        {
            if (target.activeSelf != visible)
            {
                target.SetActive(visible);
            }
        }
        protected override void OnRefreshView()
        {
            EndlessRun run = Service.Run;
            if (run == null)
            {
                return;
            }
            RefreshTop(run);
            RefreshBoss(run);
            RefreshHand(run);
            RefreshTip(run);
            RefreshBanner();
            RefreshModal(run);
        }
        /// <summary>顶部：棋心血条 + 时间 + 击杀。棋心掉血时顶栏抖一下，击杀每满 25 只顶栏弹一下（割草的节奏感）。</summary>
        private void RefreshTop(EndlessRun run)
        {
            float heartRatio = Mathf.Clamp01(run.heart / EndlessConfig.Value("HeartHp"));
            TweenBar(ref _heartTween, _heartFill, heartRatio, ClearHeartTween);
            // 血条图本身是金色：健康时不染色，危险时才染红，提示更醒目。
            _heartImage.color = heartRatio < 0.3f ? new Color(1, .3f, .22f) : Color.white;
            int kills = run.kills + run.invaderKills;
            Set(_status, "第" + Service.WaveNumber + "波   棋心 " + Mathf.CeilToInt(run.heart) + "   " + ((int)run.seconds / 60).ToString("00") + ":" + ((int)run.seconds % 60).ToString("00") + "   击杀 " + kills);
            if (_shownHeart >= 0 && run.heart < _shownHeart)
            {
                Shake(_top, new Vector2(16, 6));
            }
            else if (kills / 25 > _shownKills / 25)
            {
                Punch(_top, .05f);
            }
            _shownHeart = run.heart;
            _shownKills = kills;
        }
        /// <summary>领主血条：只有领主在场时才显示。</summary>
        private void RefreshBoss(EndlessRun run)
        {
            EndlessUnit boss = Service.BossUnit();
            Show(_boss, boss != null);
            if (boss != null && !_bossShown)
            {
                // 领主登场：血条从左边滑进来再抖一下，和六章 Boss 登场横幅一个节奏。
                RevUIAnim.SlideIn(_bossRect, RevUISlideDirection.Left, .3f, BossBarArrived, this);
            }
            _bossShown = boss != null;
            if (boss != null)
            {
                TweenBar(ref _bossTween, _bossFill, boss.Piece.HpRatio, ClearBossTween);
                Set(_bossName, boss.BrokenUntil > run.seconds ? "领主破阵！快打" : "第" + boss.BossChapter + "章领主");
            }
        }
        /// <summary>底部：手牌（没牌的格子直接隐藏）、等级、经验条、开战按钮。</summary>
        private void RefreshHand(EndlessRun run)
        {
            // 开战按钮只在开局布阵时出现；手牌在布阵和战斗中都能随时打出（无尽全程不停顿）。
            bool arranging = run.phase == EndlessPhase.Formation || run.phase == EndlessPhase.Preparation;
            bool playable = arranging || run.phase == EndlessPhase.Fighting;
            for (int i = 0; i < _cards.Length; i++)
            {
                bool has = i < run.hand.Count;
                Show(_cards[i], has);
                if (!has)
                {
                    continue;
                }
                if (_shownCards >= 0 && i >= _shownCards)
                {
                    // 新发到手的牌弹进来；进场后的第一次刷新不播，避免整排一起跳。
                    RevUIAnim.PopIn(_cards[i].transform, .28f, null, this);
                }
                var kind = (PieceKind)run.hand[i];
                Set(_cardNames[i], PieceCatalog.Get(kind, StarTier.One).DisplayName);
                // 被选中的牌换成金光底图，不能出牌的时候整体变暗，玩家不用读字就知道状态。
                Sprite face = run.selectedCard == i && cardSelected != null ? cardSelected : cardNormal;
                if (face != null && _cardImages[i].sprite != face)
                {
                    _cardImages[i].sprite = face;
                }
                _cardImages[i].color = playable ? Color.white : DimCard;
                Sprite icon = _arena == null ? null : _arena.PieceIcon(kind);
                if (icon != null && _cardIcons[i].sprite != icon)
                {
                    _cardIcons[i].sprite = icon;
                }
            }
            _shownCards = run.hand.Count;
            if (run.selectedCard >= 0 && run.selectedCard != _shownSelected && run.selectedCard < _cards.Length)
            {
                // 选中的牌轻弹一下，告诉玩家“拿起来了”，接着去棋盘看绿色落点。
                Punch(_cards[run.selectedCard].transform, .1f);
            }
            _shownSelected = run.selectedCard;
            Set(_level, "Lv " + run.level);
            if (_shownLevel > 0 && run.level > _shownLevel)
            {
                Punch(_levelRect, .18f);
            }
            _shownLevel = run.level;
            TweenBar(ref _xpTween, _xpFill, Mathf.Clamp01((float)run.xp / Mathf.Max(1, Service.ExperienceNeeded())), ClearXpTween);
            Show(_start, arranging);
            if (arranging && !_startShown)
            {
                RevUIAnim.PopIn(_startRect, .3f, StartBreathing, this);
            }
            else if (!arranging && _startShown)
            {
                StopBreathing();
            }
            _startShown = arranging;
            if (arranging)
            {
                Set(_startLabel, run.phase == EndlessPhase.Formation ? "开战 " + Mathf.CeilToInt(run.formationLeft) : "开战");
            }
        }
        /// <summary>提示框：永远只说“现在该做什么”这一件事。优先级：错误 → 领主招式 → 操作引导。</summary>
        private void RefreshTip(EndlessRun run)
        {
            if (Service.LastError != _lastError)
            {
                _lastError = Service.LastError;
                _errorUntil = string.IsNullOrEmpty(_lastError) ? 0 : Time.unscaledTime + 2.5f;
            }
            string tip;
            if (Time.unscaledTime < _errorUntil)
            {
                tip = _lastError;
            }
            else if (Service.BossBanner.Length > 0)
            {
                tip = Service.BossBanner;
            }
            else if (run.selectedCard >= 0)
            {
                tip = "拖到棋盘空格出牌\n同名牌升级一枚棋子";
            }
            else if (run.selectedUnit >= 0)
            {
                tip = "点亮起的空格\n把棋子移过去";
            }
            else if (run.phase == EndlessPhase.Formation || run.phase == EndlessPhase.Preparation)
            {
                tip = run.hand.Count > 0 ? "点手牌，再点棋盘\n摆好后点开战" : "点棋子可移动\n准备好就开战";
            }
            else
            {
                tip = "";
            }
            Show(_tip, tip.Length > 0);
            if (tip.Length > 0 && (!_tipShown || tip != _shownTip))
            {
                // 提示换了内容才弹一下，不会每 0.2 秒刷新都在动。
                RevUIAnim.PopIn(_tipRect, .22f, null, this);
            }
            _tipShown = tip.Length > 0;
            _shownTip = tip;
            Set(_tipText, tip);
        }
        /// <summary>
        /// 横幅：服务里每推一条（自动升级/自动事件），这里弹入显示 2.8 秒再收起。
        /// 只看序号变化，不排队：连续升级时显示最新那条，玩家不会被一串横幅刷屏。
        /// </summary>
        private void RefreshBanner()
        {
            if (Service.BannerSerial != _bannerSerial)
            {
                _bannerSerial = Service.BannerSerial;
                Set(_bannerText, Service.Banner);
                Show(_banner, true);
                RevUIAnim.PopIn(_banner.transform, .3f, null, this);
                _bannerUntil = Time.unscaledTime + BannerSeconds;
            }
            if (Service.MajorBannerSerial != _majorSerial)
            {
                _majorSerial = Service.MajorBannerSerial;
                ShowMajor(Service.MajorBanner, Service.MajorBannerColor);
            }
            else if (_banner.activeSelf && Time.unscaledTime > _bannerUntil)
            {
                Show(_banner, false);
            }
            if (_boon.activeSelf && _boonUntil > 0 && Time.unscaledTime > _boonUntil)
            {
                _boonUntil = 0;
                // 缩回去再隐藏；InBack 先微微放大再收，像卡片被收走。
                _boonRect.DOKill(false);
                _boonRect.DOScale(0f, .22f).SetEase(Ease.InBack).SetUpdate(true).SetTarget(_boonRect).OnComplete(HideMajor);
            }
        }
        /// <summary>
        /// 大横幅弹入：从 0.35 倍、倾斜 -10° 回弹放大到 1.15 倍并摆正，再落回原大小，最后弹两下；
        /// 同时顶栏抖一下、标题文字跟着拉伸，强调“变强了 / 大的来了”。文案第一个“！”前当标题，其余当正文。
        /// </summary>
        private void ShowMajor(string text, string colorHex)
        {
            int split = text.IndexOf('！');
            Set(_boonTitle, split > 0 ? text.Substring(0, split + 1) : "");
            Set(_boonText, split > 0 ? text.Substring(split + 1) : text);
            _boonText.color = !string.IsNullOrEmpty(colorHex) && ColorUtility.TryParseHtmlString(colorHex, out Color color) ? color : BoonGold;
            Show(_boon, true);
            _boonRect.DOKill(false);
            _boonTitle.transform.DOKill(false);
            _boonRect.localScale = Vector3.one * .35f;
            _boonRect.localRotation = Quaternion.Euler(0, 0, -10);
            DOTween.Sequence()
                .Append(_boonRect.DOScale(1.15f, .3f).SetEase(Ease.OutBack))
                .Join(_boonRect.DOLocalRotate(Vector3.zero, .3f).SetEase(Ease.OutCubic))
                .Append(_boonRect.DOScale(1f, .16f).SetEase(Ease.OutQuad))
                .Append(_boonRect.DOPunchScale(Vector3.one * .07f, .45f, 5, .6f))
                .SetUpdate(true).SetTarget(_boonRect);
            _boonTitle.transform.localScale = Vector3.one;
            _boonTitle.transform.DOPunchScale(new Vector3(.25f, -.1f, 0), .5f, 6, .5f).SetDelay(.3f).SetUpdate(true);
            Shake(_top, new Vector2(18, 8));
            _boonUntil = Time.unscaledTime + MajorSeconds;
        }
        private void HideMajor()
        {
            Show(_boon, false);
            _boonRect.localScale = Vector3.one;
        }
        /// <summary>弹窗：巡回二选一、暂停、结算（升级和事件已改为自动，旧存档里残留的也能正常处理）。每种都只有大按钮。</summary>
        private void RefreshModal(EndlessRun run)
        {
            bool modal = run.phase == EndlessPhase.Upgrade || run.phase == EndlessPhase.Event || run.phase == EndlessPhase.Circuit || run.phase == EndlessPhase.Paused || run.phase == EndlessPhase.Result;
            Show(_modal, modal);
            if (!modal)
            {
                _modalShown = false;
                _modalSignature = int.MinValue;
                return;
            }
            int signature;
            unchecked
            {
                signature = (int)run.phase * 7919 ^ run.choices[0] * 31 ^ run.choices[1] * 131 ^ run.choices[2] * 1031 ^ run.eventKind * 17;
            }
            if (!_modalShown)
            {
                // 弹窗出现：整块回弹弹入，选项随后逐张弹出。
                RevUIAnim.PopIn(_modalRect, .32f, null, this);
                PopChoices();
            }
            else if (signature != _modalSignature)
            {
                // 连续升级 / 换一批：弹窗不动，只让新选项重新弹一次，玩家能看出“换了”。
                PopChoices();
            }
            _modalShown = true;
            _modalSignature = signature;
            string left = null, middle = null, right = null, title;
            if (run.phase == EndlessPhase.Upgrade)
            {
                title = "升级！选一个";
                left = Service.ChoiceText(run.choices[0]);
                middle = Service.ChoiceText(run.choices[1]);
                right = Service.ChoiceText(run.choices[2]);
            }
            else if (run.phase == EndlessPhase.Event)
            {
                string[] eventNames =
                {
                    "遗迹守阵", "暴走潮", "刻印交换", "救援棋手"
                };
                string[] safe =
                {
                    "获得一张同名牌", "回复棋心 " + EndlessConfig.Count("EventHeal"), "什么都不变", "免费补一张牌"
                };
                string[] risk =
                {
                    "保护棋心 " + EndlessConfig.Count("RiskSeconds") + " 秒\n成功得碎片和升级", "敌人暂时变多\n成功得碎片和同名牌", "交换一个辅印", "救援 " + EndlessConfig.Count("RiskSeconds") + " 秒\n成功得同名牌"
                };
                title = eventNames[run.eventKind];
                left = "稳一手\n" + safe[run.eventKind];
                right = "赌一把\n" + risk[run.eventKind];
            }
            else if (run.phase == EndlessPhase.Circuit)
            {
                int next = run.circuit % BreakthroughNames.Length;
                title = "巡回完成！\n继续将获得：" + BreakthroughNames[next] + "\n敌人新增：" + AffixNames[next];
                left = "继续挑战";
                right = "结算带走";
            }
            else if (run.phase == EndlessPhase.Paused)
            {
                title = "已暂停\n" + BuildSummary(run);
                left = "继续战斗";
                middle = "保存并退出";
                right = "结束本局";
            }
            else
            {
                title = "本局结束\n战绩 " + run.score + "     击杀 " + (run.kills + run.invaderKills) + "     领主 " + run.bossKills;
                middle = "返回大厅";
            }
            Set(_modalTitle, title);
            SetChoice(0, left);
            SetChoice(1, middle);
            SetChoice(2, right);
            Show(_reroll, run.phase == EndlessPhase.Upgrade && run.rerolls > 0);
            Show(_modalClose, run.phase == EndlessPhase.Paused);
        }
        private void SetChoice(int index, string text)
        {
            Show(_choices[index], text != null);
            if (text != null)
            {
                Set(_choiceTexts[index], text);
            }
        }
        /// <summary>把已有的祝福拼成一行，放在暂停页。战斗时不占屏幕，想看的时候暂停看。</summary>
        private string BuildSummary(EndlessRun run)
        {
            _text.Clear();
            for (int i = 0; i < 6; i++)
            {
                if (run.mains[i] > 0)
                {
                    _text.Append(_text.Length > 0 ? " · " : "");
                    _text.Append(run.evolved[i] ? EndlessConfig.Route(i).Evolution : EndlessConfig.Route(i).Blessing + " Lv" + run.mains[i]);
                }
            }
            return _text.Length > 0 ? _text.ToString() : "还没有祝福";
        }
        private void Choice(int index)
        {
            EndlessPhase phase = Service.Run.phase;
            if (phase == EndlessPhase.Upgrade)
            {
                Service.Choose(index);
            }
            else if (phase == EndlessPhase.Event)
            {
                Service.SelectEvent(index == 2);
            }
            else if (phase == EndlessPhase.Circuit)
            {
                if (index == 2)
                {
                    Service.Finish();
                }
                else
                {
                    // 突破和侵袭按巡回数自动轮换，玩家只决定“继续还是带走”。
                    int next = Service.Run.circuit % BreakthroughNames.Length;
                    Service.ContinueCircuit(next, next);
                }
            }
            else if (phase == EndlessPhase.Paused)
            {
                if (index == 0)
                {
                    Service.Pause();
                }
                else if (index == 1)
                {
                    SaveExit();
                }
                else
                {
                    Service.Finish();
                }
            }
            else if (phase == EndlessPhase.Result)
            {
                RevServices.GetRequired<GameFlow>().ReturnHome();
            }
            RefreshView();
        }
        private void SaveExit()
        {
            if (Service.Save())
            {
                RevServices.GetRequired<GameFlow>().ReturnHome();
            }
        }
        private void Card(int index)
        {
            if (_dragSuppressClick)
            {
                return;
            }
            Service.SelectCard(index);
            RefreshView();
        }
        [RevButtonClick("start")] private void StartWave()
        {
            Service.StartWave();
            RefreshView();
        }
        [RevButtonClick("pause")] private void Pause()
        {
            Service.Pause();
            RefreshView();
        }
        [RevButtonClick("modal/choice0")] private void Choice0()
        {
            Choice(0);
        }
        [RevButtonClick("modal/choice1")] private void Choice1()
        {
            Choice(1);
        }
        [RevButtonClick("modal/choice2")] private void Choice2()
        {
            Choice(2);
        }
        [RevButtonClick("modal/modalClose")] private void CloseModal()
        {
            // 暂停页的叉 = 继续战斗；其它弹窗不显示这个叉（必须做出选择）。
            if (Service.Run.phase == EndlessPhase.Paused)
            {
                Service.Pause();
            }
            RefreshView();
        }
        [RevButtonClick("modal/reroll")] private void Reroll()
        {
            Service.Reroll();
            RefreshView();
        }
        [RevButtonClick("bottom/card0")] private void Card0()
        {
            Card(0);
        }
        [RevButtonClick("bottom/card1")] private void Card1()
        {
            Card(1);
        }
        [RevButtonClick("bottom/card2")] private void Card2()
        {
            Card(2);
        }
        [RevButtonClick("bottom/card3")] private void Card3()
        {
            Card(3);
        }
        /// <summary>
        /// 三张选项错开 0.07 秒依次从 0 回弹放大（BossIntroPanel 同款 OutBack）。
        /// 这里用 DOTween 而不是 RevUIAnim：RevUIAnim 会把第一次播放时的缩放记成“原始大小”，先缩到 0 再播会被记成 0。
        /// </summary>
        private void PopChoices()
        {
            for (int i = 0; i < _choices.Length; i++)
            {
                Transform choice = _choices[i].transform;
                choice.DOKill(false);
                choice.localScale = Vector3.zero;
                choice.DOScale(1f, .3f).SetDelay(.08f + i * .07f).SetEase(Ease.OutBack).SetUpdate(true);
            }
        }
        private void BossBarArrived()
        {
            Shake(_bossRect, new Vector2(14, 6));
        }
        /// <summary>开战按钮弹入后开始轻轻呼吸，提醒玩家“摆好就点这里”。</summary>
        private void StartBreathing()
        {
            if (_startShown)
            {
                _startBreath = RevUIAnim.Breathe(_startRect, .7f, .96f, 1.04f, true);
            }
        }
        private void StopBreathing()
        {
            RevUIAnim.Stop(_startBreath);
            _startBreath = RevUIAnimHandle.None;
            RevUIAnim.RestoreBase(_startRect);
        }
        /// <summary>进度条缓动：0.28 秒 OutCubic，与六章 BattleHudPanel 一致；用不受战斗时间影响的真实时间。</summary>
        private static void TweenBar(ref Tween tween, RectTransform fill, float ratio, TweenCallback onKilled)
        {
            if (Mathf.Abs(fill.anchorMax.x - ratio) < .001f)
            {
                return;
            }
            tween?.Kill(false);
            tween = DOTween.To(() => fill.anchorMax.x, value => fill.anchorMax = new Vector2(value, 1), ratio, .28f)
                .SetEase(Ease.OutCubic).SetUpdate(true).OnKill(onKilled);
        }
        // 项目开启了 Tween 复用：自然结束后同一对象可能借给别人，所以结束/被停时必须清掉本面板的引用（同 BattleHudPanel）。
        private void ClearHeartTween()
        {
            _heartTween = null;
        }
        private void ClearXpTween()
        {
            _xpTween = null;
        }
        private void ClearBossTween()
        {
            _bossTween = null;
        }
        /// <summary>弹一下：先把上一次弹跳直接收尾，再从原尺寸开始，连续触发不会越弹越大。</summary>
        private static void Punch(Transform target, float strength)
        {
            target.DOKill(true);
            target.localScale = Vector3.one;
            target.DOPunchScale(Vector3.one * strength, .28f, 6, .5f).SetUpdate(true);
        }
        /// <summary>抖一下（受击、领主登场），结束后回到原位。</summary>
        private static void Shake(RectTransform target, Vector2 strength)
        {
            target.DOKill(true);
            target.DOShakeAnchorPos(.25f, strength, 14, 80, false, true).SetUpdate(true);
        }
        /// <summary>打开/关闭时清空“上次看到的状态”，停掉动画并还原尺寸，面板复用时不会带着上一局的缩放或位置。</summary>
        private void ResetAnimationState()
        {
            _modalShown = _bossShown = _startShown = _tipShown = false;
            _bannerSerial = Service.BannerSerial;
            _bannerUntil = 0;
            _banner.SetActive(false);
            _boonUntil = 0;
            _majorSerial = Service.MajorBannerSerial;
            _boonRect.DOKill(false);
            _boonTitle.transform.DOKill(false);
            _boonRect.localScale = _boonTitle.transform.localScale = Vector3.one;
            _boonRect.localRotation = Quaternion.identity;
            _boon.SetActive(false);
            _modalSignature = int.MinValue;
            _shownLevel = _shownKills = 0;
            _shownCards = _shownSelected = -1;
            _shownHeart = -1;
            _shownTip = "";
            _heartTween?.Kill(false);
            _xpTween?.Kill(false);
            _bossTween?.Kill(false);
            RevUIAnim.Stop(_startBreath);
            _startBreath = RevUIAnimHandle.None;
            RevUIAnim.StopAllOf(this);
            RectTransform[] animated =
            {
                _top, _modalRect, _levelRect, _tipRect, _bossRect, _startRect
            };
            for (int i = 0; i < animated.Length; i++)
            {
                // DOKill(true) 让抖动直接跳到终点（回到原位），再统一还原缩放。
                animated[i].DOKill(true);
                animated[i].localScale = Vector3.one;
            }
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].transform.DOKill(true);
                _cards[i].transform.localScale = Vector3.one;
            }
            for (int i = 0; i < _choices.Length; i++)
            {
                _choices[i].transform.DOKill(true);
                _choices[i].transform.localScale = Vector3.one;
            }
        }
        protected override void OnClose()
        {
            CancelCardDrag();
            SetCardDragBinding(false);
            _opening = false;
            _version++;
            RevTimer.CancelAllOf(this);
            ResetAnimationState();
            if (_arena != null)
            {
                RevPool.Return(_arena);
                _arena = null;
            }
        }
    }
}

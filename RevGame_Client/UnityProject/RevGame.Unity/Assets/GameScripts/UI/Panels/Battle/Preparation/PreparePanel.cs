// ============================================================
// PreparePanel.cs —— 准备阶段的手牌与准备按钮。
// 装配、显示、操作分开：OnBindView 接线，OnRefreshView 读服务画屏，点击只向 Logic 提交请求。
// 服务统一扣牌，避免「界面扣了牌，但棋盘没有落子」的两份数据不一致。
// ============================================================
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class PreparePanel : RevUIPanel
    {
        [RevBind]
        private Text _hintText;
        /// <summary>普通提示的底板，教学时与普通提示一起隐藏。</summary>
        private UnityEngine.GameObject _hintPlate;
        /// <summary>复用的禁用手牌点击反馈，和准备面板同寿命。</summary>
        private Action<int> _rejectedTutorialCard;
        [RevBind]
        private Button _btnReady;
        [RevBind]
        private Button _btnSkip;
        /// <summary>缓存末轮跳过按钮的标题，按业务许可与实际加成更新。</summary>
        private Text _skipLabel;
        [RevBind]
        private Button _btnEye;
        // RevBind 按字段名寻找 btnRefresh 节点，预制体由项目 MCP 装配工具生成。
        [RevBind] private Button _btnRefresh;
        // 仅本次重画使用翻牌；普通血量/选择刷新不能反复重启动画。
        private bool _refreshAnimation;
        private CardItem[] _cards;
        /// <summary>开始战斗按钮的文字，绑定时从现有预制体取一次。</summary>
        private Text _readyLabel;
        /// <summary>刷新手牌按钮的文字，绑定时从现有预制体取一次。</summary>
        private Text _refreshLabel;
        /// <summary>眼位石按钮的文字，节点可能隐藏，绑定时一并取到。</summary>
        private Text _eyeLabel;
        /// <summary>上次提示的剩余次数、操作文案和错误。默认值要求首次刷新。</summary>
        private (int remaining, string prompt, string error) _hintStamp;
        /// <summary>上次的准备提示状态。-1 是教学门禁，0 可开战，正数为剩余秒数。</summary>
        private int _shownReadySeconds = int.MinValue;
        /// <summary>上次展示的手牌数，未绘制时是 int.MinValue。</summary>
        private int _shownHandCount = int.MinValue;
        /// <summary>上次显示的眼位石数量，未绘制时是 int.MinValue。</summary>
        private int _shownEyeStones = int.MinValue;
        private bool AnyCardFlipping
        {
            get
            {
                foreach (CardItem card in _cards)
                {
                    if (card.IsFlipping)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
        /// <summary>绑定设计框、卡槽和按钮文字，后续刷新复用同一组节点。</summary>
        protected override void OnBindView()
        {
            // 左侧课题提示靠上，右侧准备按钮靠下。手牌跟随棋盘设计框。
            GameMobileLayout.EnableBoardFrame(transform, leftNodes: null, rightNodes: null,
                topNodes: new[] { "hintPlate", "hintText" },
                bottomNodes: new[] { "btnReady", "btnRefresh", "btnEye", "btnSkip" });
            _cards = GetComponentsInChildren<CardItem>(true);
            _hintPlate = transform.Find("hintPlate")?.gameObject;
            _rejectedTutorialCard = OnRejectedTutorialCard;
            _readyLabel = _btnReady.GetComponentInChildren<Text>(true);
            _skipLabel = _btnSkip.GetComponentInChildren<Text>(true);
            _refreshLabel = _btnRefresh.GetComponentInChildren<Text>(true);
            _eyeLabel = _btnEye.GetComponentInChildren<Text>(true);
            // 六槽按钮随预制体一同装配，由方法特性接线；点击时读取当前 CardId。
        }
        /// <summary>每次打开从真实准备状态重新绘制文字，计时和事件仍跟随当前面板。</summary>
        protected override void OnOpen()
        {
            _hintStamp = default;
            _shownReadySeconds = _shownHandCount = _shownEyeStones = int.MinValue;
            ResetRoundCards();
            RevTimer.Every(0.25f, RefreshView, owner: this);
            // ★ Close 会自动摘掉 owner 事件，复用时 OnBindView 不重跑，因此每次打开重新订阅。
            RevEvent.AddEventListener(Events.HandRerolled, OnHandRerolled, owner: this);
            RevEvent.AddEventListener(Events.HandChanged, RefreshView, owner: this);
            RevEvent.AddEventListener(Events.SelectionChanged, RefreshView, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPrepare, owner: this);
        }
        private void ResetRoundCards()
        {
            _refreshAnimation = false;
            // 保留 OnBindView 中“含隐藏项”的全部六槽，不因旧回合只剩一张而缩短缓存。
            // 每次开面板都主动清旧动画并逐槽重新绘制，不能等待某个旧翻牌回调来刷新。
            foreach (CardItem card in _cards)
            {
                card.ResetForRound();
            }
        }
        private void OnPrepare(int round)
        {
            ResetRoundCards();
            RefreshView();
        }
        protected override void OnClose()
        {
            RevTimer.CancelAllOf(this);
            ResetRoundCards();
        }
        /// <summary>根据实际手牌和准备状态更新显示，相同文字输入直接沿用上次的结果。</summary>
        protected override void OnRefreshView()
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            var lesson = RevLogic.Get<TutorialLogic>();
            bool guided = lesson != null && lesson.Active;
            if (_hintText.gameObject.activeSelf == guided)
            {
                _hintText.gameObject.SetActive(!guided);
            }
            if (_hintPlate != null && _hintPlate.activeSelf == guided)
            {
                _hintPlate.SetActive(!guided);
            }
            if (!guided)
            {
                RefreshHint(battle);
            }

            // 教学浮层和真实按钮共用同一张可用手牌，点其他牌只显示本步提示。
            int tutorialCard = guided ? lesson.SelectableCardId : 0;
            for (int i = 0; i < _cards.Length; i++)
            {
                var card = i < battle.Hand.Count ? battle.Hand[i] : null;

                if (_refreshAnimation && card != null)
                {
                    _cards[i].FlipTo(card, battle.SelectedCardId, i * 0.055f, RefreshView);
                }
                else
                {
                    _cards[i].Show(card, battle.SelectedCardId);
                }
                _cards[i].SetSelectionAllowed(!guided || (card != null && card.Id == tutorialCard),
                    guided ? _rejectedTutorialCard : null);
            }
            bool canGuideReady = !guided || lesson.Step == TutorialStep.Ready;
            bool flipping = AnyCardFlipping;
            _btnReady.interactable = canGuideReady && battle.Phase == GamePhase.Prepare && battle.PreparationGranted && !flipping;
            int readySeconds = !canGuideReady ? -1 : battle.PreparationGranted ? 0
                : System.Math.Max(1, (int)System.Math.Ceiling(3f - battle.PreparationElapsed));
            if (_shownReadySeconds != readySeconds)
            {
                _readyLabel.text = readySeconds < 0 ? "完成引导后开战" : readySeconds == 0 ? "开始战斗" : "增益抵达 " + readySeconds + "秒";
                _shownReadySeconds = readySeconds;
            }

            int handCount = battle.Hand.Count;
            _btnRefresh.interactable = !guided && handCount > 0 && !flipping;
            if (_shownHandCount != handCount)
            {
                _refreshLabel.text = "刷新手牌（" + handCount + "张）";
                _shownHandCount = handCount;
            }

            bool showSkip = battle.Round == Balance.NormalRoundsPerChapter && battle.Phase == GamePhase.Prepare && !guided;
            _btnSkip.gameObject.SetActive(showSkip);
            if (showSkip)
            {
                bool allowed = battle.SkipRoundBlockReason.Length == 0;
                _btnSkip.interactable = allowed && !flipping;
                string label = allowed ? "直达 Boss · 攻击 +" + ConfigText.Percent(Balance.RoundSkipAttackBonus) : "已落子 · 无法跳过";
                if (_skipLabel.text != label)
                {
                    _skipLabel.text = label;
                }
            }
            int eyeStones = battle.EyeStones;
            _btnEye.gameObject.SetActive(eyeStones > 0);
            if (_shownEyeStones != eyeStones)
            {
                _eyeLabel.text = "眼位石 ×" + eyeStones;
                _shownEyeStones = eyeStones;
            }
        }

        /// <summary>触摸禁用的教学牌时复用选牌门禁，由教学逻辑显示当前操作提示。</summary>
        private void OnRejectedTutorialCard(int id)
        {
            RevLogic.Get<BattleLogic>()?.SelectCard(id);
        }

        /// <summary>按业务输入构造普通准备提示，眼位石模式直接显示自己的操作说明。</summary>
        private void RefreshHint(IBattleService battle)
        {
            bool eye = battle.EyeStoneSelected;
            string prompt = eye ? "点击金色亮格补眼；没有亮格时可保留眼位石"
                : battle.SelectedPieceId != 0 ? "点空格调整位置（免费）"
                : battle.SelectedCardId == 0 ? "选手牌落子，或点己方棋子换位" : "全盘空格均可落子";
            var stamp = (remaining: eye ? 0 : battle.CardsRemaining, prompt, error: eye ? "" : battle.LastError ?? "");
            if (_hintStamp == stamp)
            {
                return;
            }

            string text = eye ? prompt : "剩余落子 " + stamp.remaining + " 次  ·  " + prompt;
            if (stamp.error.Length > 0)
            {
                text += "\n" + stamp.error;
            }
            _hintText.text = text;
            _hintStamp = stamp;
        }

        private void OnHandRerolled()
        {
            _refreshAnimation = true;
            RefreshView();
            _refreshAnimation = false;
        }
        [RevButtonClick("btnRefresh")]
        private void OnRefreshClick()
        {
            if (!AnyCardFlipping)
            {
                RevLogic.Get<BattleLogic>().RefreshHand();
            }
        }

        /// <summary>六个固定槽位由框架反射接线；读取槽内当前牌编号，不捕获上一轮的手牌数据。</summary>
        [RevButtonClick("card0")]
        private void OnCard0(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        [RevButtonClick("card1")]
        private void OnCard1(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        [RevButtonClick("card2")]
        private void OnCard2(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        [RevButtonClick("card3")]
        private void OnCard3(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        [RevButtonClick("card4")]
        private void OnCard4(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        [RevButtonClick("card5")]
        private void OnCard5(string nodeName)
        {
            SelectCardSlot(nodeName);
        }
        private void SelectCardSlot(string nodeName)
        {
            foreach (CardItem card in _cards)
            {
                if (card.name == nodeName && card.CardId != 0 && !card.IsFlipping)
                {
                    card.PlayClickFeedback();
                    RevLogic.Get<BattleLogic>().SelectCard(card.CardId);
                    return;
                }
            }
        }
        [RevButtonClick("btnReady")]
        private void OnReadyClick()
        {
            if (!AnyCardFlipping)
            {
                RevLogic.Get<BattleLogic>().BeginBattle();
            }
        }

        /// <summary>提交末轮跳过；动画及业务门禁在真实点击时仍然复核。</summary>
        [RevButtonClick("btnSkip")]
        private void OnSkipClick()
        {
            if (!AnyCardFlipping)
            {
                RevLogic.Get<BattleLogic>().SkipRound();
            }
        }
        [RevButtonClick("btnEye")]
        private void OnEyeClick()
        {
            RevLogic.Get<BattleLogic>().SelectEyeStone();
        }
        protected override void OnRelease()
        {
            foreach (CardItem card in _cards)
            {
                card.Release();
            }
        }
    }
}

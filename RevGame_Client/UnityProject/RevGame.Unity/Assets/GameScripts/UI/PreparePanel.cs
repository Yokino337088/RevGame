// ============================================================
// PreparePanel.cs —— 准备阶段的手牌与准备按钮。
// 装配、显示、操作分开：OnBindView 接线，OnRefreshView 读服务画屏，点击只向 Logic 提交请求。
// 服务统一扣牌，避免「界面扣了牌，但棋盘没有落子」的两份数据不一致。
// ============================================================
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI, RevUILayer.Normal)]
    public sealed class PreparePanel : RevUIPanel
    {
        [RevBind]
        private Text _hintText;
        [RevBind]
        private Button _btnReady;
        [RevBind]
        private Button _btnSkip;
        [RevBind]
        private Button _btnEye;
        // RevBind 按字段名寻找 btnRefresh 节点，预制体由项目 MCP 装配工具生成。
        [RevBind] private Button _btnRefresh;
        // 仅本次重画使用翻牌；普通血量/选择刷新不能反复重启动画。
        private bool _refreshAnimation;
        private CardItem[] _cards;
        private bool AnyCardFlipping
        {
            get
            {
                foreach (CardItem card in _cards) { if (card.IsFlipping) { return true; } }
                return false;
            }
        }
        protected override void OnBindView()
        {
            _cards = GetComponentsInChildren<CardItem>(true);
            foreach (CardItem card in _cards)
            {
                card.BindClick(OnCardClick);
            }

            _btnReady.onClick.AddListener(OnReadyClick);
            _btnSkip.onClick.AddListener(OnSkipClick);
            _btnEye.onClick.AddListener(OnEyeClick);
            _btnRefresh.onClick.AddListener(OnRefreshClick);
        }

        protected override void OnOpen()
        {
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
            foreach (CardItem card in _cards) { card.ResetForRound(); }
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

        protected override void OnRefreshView()
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            _hintText.text = "剩余落子 " + battle.CardsRemaining + " 次  ·  " + (battle.SelectedPieceId != 0 ? "点空格调整位置（免费）" : battle.SelectedCardId == 0 ? "选手牌落子，或点己方棋子换位" : "全盘空格均可落子");
            if (!string.IsNullOrEmpty(battle.LastError))
            {
                _hintText.text += "\n" + battle.LastError;
            }

            for (int i = 0; i < _cards.Length; i++)
            {
                var card = i < battle.Hand.Count ? battle.Hand[i] : null;
                if (_refreshAnimation && card != null)
                {
                    _cards[i].FlipTo(card, battle.SelectedCardId, i * 0.055f, RefreshView);
                }
                else { _cards[i].Show(card, battle.SelectedCardId); }
            }

            _btnReady.interactable = battle.Phase == GamePhase.Prepare && battle.PreparationGranted && !AnyCardFlipping;
            _btnReady.GetComponentInChildren<Text>().text = battle.PreparationGranted ? "开始战斗" : "增益抵达 " + System.Math.Max(1, (int)System.Math.Ceiling(3f - battle.PreparationElapsed)) + "秒";
            _btnRefresh.interactable = battle.Hand.Count > 0 && !AnyCardFlipping;
            _btnRefresh.GetComponentInChildren<Text>().text = "刷新手牌（" + battle.Hand.Count + "张）";
            _btnSkip.gameObject.SetActive(battle.Round == Consts.NormalRoundsPerChapter);
            _btnSkip.interactable = !AnyCardFlipping;
            _btnEye.gameObject.SetActive(battle.EyeStones > 0);
            _btnEye.GetComponentInChildren<Text>().text = "眼位石 ×" + battle.EyeStones;
            if (battle.EyeStoneSelected)
            {
                _hintText.text = "点击金色亮格补眼；没有亮格时可保留眼位石";
            }
        }

        private void OnHandRerolled()
        {
            _refreshAnimation = true;
            RefreshView();
            _refreshAnimation = false;
        }

        private void OnRefreshClick()
        {
            if (!AnyCardFlipping) { RevLogic.Get<BattleLogic>().RefreshHand(); }
        }

        private void OnCardClick(int id)
        {
            RevLogic.Get<BattleLogic>().SelectCard(id);
        }

        private void OnReadyClick()
        {
            if (!AnyCardFlipping) { RevLogic.Get<BattleLogic>().BeginBattle(); }
        }

        private void OnSkipClick()
        {
            RevLogic.Get<BattleLogic>().SkipRound();
        }

        private void OnEyeClick()
        {
            RevLogic.Get<BattleLogic>().SelectEyeStone();
        }

        protected override void OnRelease()
        {
            _btnReady.onClick.RemoveListener(OnReadyClick);
            _btnSkip.onClick.RemoveListener(OnSkipClick);
            _btnEye.onClick.RemoveListener(OnEyeClick);
            _btnRefresh.onClick.RemoveListener(OnRefreshClick);
            foreach (CardItem card in _cards)
            {
                card.Release();
            }
        }
    }
}

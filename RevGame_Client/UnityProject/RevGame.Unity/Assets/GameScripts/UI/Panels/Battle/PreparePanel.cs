// ============================================================
// PreparePanel.cs —— 准备阶段的手牌与准备按钮。
// 装配、显示、操作分开：OnBindView 接线，OnRefreshView 读服务画屏，点击只向 Logic 提交请求。
// 服务统一扣牌，避免「界面扣了牌，但棋盘没有落子」的两份数据不一致。
// ============================================================
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
                foreach (CardItem card in _cards) {
                if (card.IsFlipping)
                {
                    return true;
                }
                }
                return false;
            }
        }
        protected override void OnBindView()
        {
            _cards = GetComponentsInChildren<CardItem>(true);
            // 六槽按钮随预制体一同装配，由方法特性接线；点击时读取当前 CardId。
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
                else
                {
                    _cards[i].Show(card, battle.SelectedCardId);
                }
            }

            _btnReady.interactable = battle.Phase == GamePhase.Prepare && battle.PreparationGranted && !AnyCardFlipping;
            _btnReady.GetComponentInChildren<Text>().text = battle.PreparationGranted ? "开始战斗" : "增益抵达 " + System.Math.Max(1, (int)System.Math.Ceiling(3f - battle.PreparationElapsed)) + "秒";
            _btnRefresh.interactable = battle.Hand.Count > 0 && !AnyCardFlipping;
            _btnRefresh.GetComponentInChildren<Text>().text = "刷新手牌（" + battle.Hand.Count + "张）";
            _btnSkip.gameObject.SetActive(battle.Round == Balance.NormalRoundsPerChapter);
            _btnSkip.interactable = !AnyCardFlipping;
            _btnEye.gameObject.SetActive(battle.EyeStones > 0);
            _btnEye.GetComponentInChildren<Text>(true).text = "眼位石 ×" + battle.EyeStones;
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

        [RevButtonClick("btnSkip")]
        private void OnSkipClick()
        {
            RevLogic.Get<BattleLogic>().SkipRound();
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

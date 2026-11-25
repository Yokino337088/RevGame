// ============================================================
// SettlePanel.cs
// 结算是一次性快照；继续成功才关面板，下一回合生成失败时保留原因供玩家查看。
// ============================================================
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 自动推进遇到关卡装配错误时保留的恢复入口。普通回合结算由战斗 HUD 播报。
    ///
    /// 这是典型的"一次性数据"界面，所以走框架推荐的数据流：
    /// 业务层 `RevUI.Open&lt;SettlePanel, SettleData&gt;(data)` → 面板 `OnRefreshView` 只负责画。
    /// </summary>
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class SettlePanel : RevUIPanel<SettlePanel.SettleData>
    {
        /// <summary>结算数据（面板只读）。
        /// 注意：**不能**命名为 Data —— 会遮蔽基类的 Data 属性，导致 "Data.Winner" 被当成类型访问。</summary>
        public sealed class SettleData
        {
            public GameSide Winner;
            public int Chapter;
            public int Round;
            public bool LastRoundOfChapter;
        }
        [RevBind]
        private Text _titleText;
        [RevBind]
        private Text _detailText;
        /// <summary>仍停在本回合时，使用原有结算按钮重试推进。</summary>
        [RevBind]
        private Button _btnContinue;
        /// <summary>复用结算按钮样式的退场入口，由本面板随预制体实例持有。</summary>
        private Button _btnHome;

        /// <summary>沿用已发布预制体，在同一安全框内同时提供重试和退场。</summary>
        protected override void OnBindView()
        {
            GameMobileLayout.SeparateViewportVeil(transform);
            GameMobileLayout.EnableSafePanel(transform, GameMobileLayout.ViewportVeil);
            _btnHome = Instantiate(_btnContinue, _btnContinue.transform.parent, false);
            _btnHome.name = "btnHome";
            ((RectTransform)_btnContinue.transform).anchoredPosition += Vector2.left * 165f;
            ((RectTransform)_btnHome.transform).anchoredPosition += Vector2.right * 165f;
            _btnContinue.GetComponentInChildren<Text>(true).text = "重试推进";
            _btnHome.GetComponentInChildren<Text>(true).text = "返回大厅";
            _btnHome.onClick = new Button.ButtonClickedEvent();
            _btnHome.onClick.AddListener(OnHomeClick);
        }

        /// <summary>显示停住的回合与最近失败原因，重试失败时仍保留两个入口。</summary>
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            bool win = Data.Winner == GameSide.Player;

            if (_titleText != null)
            {
                _titleText.text = $"第 {Data.Chapter} 章 · 第 {Data.Round} 回合{(win ? "胜利" : "战败")}";
            }
            if (_detailText != null)
            {
                string error = RevServices.GetRequired<IBattleService>().LastError;
                _detailText.text = string.IsNullOrEmpty(error) ? (win ? "暂未进入下一阶段，可以重试。" : "暂未回到准备阶段，可以重试。") : error;
            }
        }

        /// <summary>通过战斗逻辑重试。接单失败时刷新原因，玩家可以继续选择退场。</summary>
        [RevButtonClick("btnContinue")]
        private void OnContinueClick()
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();

            if (battle.Phase != GamePhase.Settle)
            {
                CloseSelf();
                return;
            }
            Flow.BattleLogic logic = RevLogic.Get<Flow.BattleLogic>();

            if (logic != null && logic.ContinueToNextRound())
            {
                CloseSelf();
                return;
            }
            RefreshView();
        }

        /// <summary>通过远征共用退场确认离场，取消时保留结算窗口和当前回合。</summary>
        private void OnHomeClick()
        {
            RevServices.GetRequired<Flow.GameFlow>().RequestBattleExit(_btnHome);
        }
    }
}

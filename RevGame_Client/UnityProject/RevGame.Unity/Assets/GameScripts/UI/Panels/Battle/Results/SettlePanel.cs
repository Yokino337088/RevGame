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
        [RevBind]
        private Button _btnContinue;
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform);
        }

        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            bool win = Data.Winner == GameSide.Player;

            if (_titleText != null)
            {
                _titleText.text = win ? "胜" : "负";
            }
            if (_detailText != null)
            {
                string error = RevServices.GetRequired<IBattleService>().LastError;
                _detailText.text = string.IsNullOrEmpty(error) ? (win ? "重试进入下一阶段" : "重试返回上一回合") : error;
            }
        }

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
            }
            else

                if (_detailText != null)
                {
                    // 下一回合出生空间不足时保留结算页并说明原因，不能关闭后留下无法操作的空界面。
                    _detailText.text = battle.LastError;
                }
        }
    }
}

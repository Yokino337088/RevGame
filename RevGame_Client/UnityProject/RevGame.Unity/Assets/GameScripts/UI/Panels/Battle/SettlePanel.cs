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
    /// 结算面板（策划案 §04：赢了进下一回合，输了重开本章；第 6 章 Boss 打完就是通关）。
    ///
    /// 这是典型的"一次性数据"界面，所以走框架推荐的数据流：
    /// 业务层 `RevUI.Open&lt;SettlePanel, SettleData&gt;(data)` → 面板 `OnRefreshView` 只负责画。
    /// </summary>
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Popup)]
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
            if (_btnContinue != null)
            {
                _btnContinue.onClick.AddListener(OnContinueClick);
            }
        }

        protected override void OnRelease()
        {
            if (_btnContinue != null)
            {
                _btnContinue.onClick.RemoveListener(OnContinueClick);
            }
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
                string tail = Data.LastRoundOfChapter ? (win ? "本章通过！" : "本章重来") : (win ? "进入下一回合" : "本章重来");
                _detailText.text = "第 " + Data.Chapter + " 章 · 第 " + Data.Round + " 回合 — " + tail;
            }
        }

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
            else if (_detailText != null)
            {
                // 下一回合出生空间不足时保留结算页并说明原因，不能关闭后留下无法操作的空界面。
                _detailText.text = battle.LastError;
            }
        }
    }
}

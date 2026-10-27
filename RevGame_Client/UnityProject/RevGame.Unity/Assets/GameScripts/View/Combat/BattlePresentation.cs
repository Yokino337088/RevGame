// ============================================================
// BattlePresentation.cs —— 战斗阶段与面板的接线处。
// 服务不依赖 UI；这里监听阶段并开关面板。异步加载后再次核对阶段，避免过期面板盖住新界面。
// ============================================================
using RevGame.Core;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed class BattlePresentation : MonoBehaviour
    {
        private bool _released;
        private void OnEnable()
        {
            _released = false;
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPrepare, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnBattle, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<BossDefeatedInfo>(Events.BossVictoryPresentationFinished, OnBossVictoryFinished, owner: this);
            RevEvent.AddEventListener(Events.BossIntroFinished, OnBossIntroFinished, owner: this);
            RevEvent.AddEventListener(Events.PhaseEnterReward, OnReward, owner: this);
            RevUI.Open<BattleHudPanel>(panel => ValidatePanel(panel, null));
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            if (battle.Phase == GamePhase.Prepare)
            {
                OnPrepare(battle.Round);
            }
            else if (battle.Phase == GamePhase.Settle)
            {
                OnSettle(battle.LastWinner.Value);
            }
            else if (battle.Phase == GamePhase.Reward)
            {
                OnReward();
            }
        }

        private void OnPrepare(int round)
        {
            RevUI.Close<SettlePanel>();
            RevUI.Close<RewardPanel>();
            RevUI.Open<PreparePanel>(panel =>
            {
                ValidatePanel(panel, GamePhase.Prepare);
                var flow = RevServices.Get<RevGame.Flow.GameFlow>();
                if (flow != null && flow.IsBossIntroPlaying) { flow.SetPrepareHidden(true); }
            });
        }

        private void OnBossIntroFinished()
        {
            if (_released) { return; }
            var flow = RevServices.Get<RevGame.Flow.GameFlow>();
            if (flow != null) { flow.SetPrepareHidden(false); }
        }

        private void OnBattle(int round)
        {
            RevUI.Close<PreparePanel>();
        }

        private void OnSettle(GameSide winner)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            RevUI.Close<PreparePanel>();
            if (winner == GameSide.Player && battle.IsBossRound && RevServices.GetRequired<RevGame.Flow.GameFlow>().IsBossVictoryPlaying) { return; }
            var data = new SettlePanel.SettleData
            {
                Winner = winner,
                Chapter = battle.Chapter,
                Round = battle.Round,
                LastRoundOfChapter = battle.IsBossRound
            };
            RevUI.Close<PreparePanel>();
            RevUI.Open<SettlePanel, SettlePanel.SettleData>(data, panel => ValidatePanel(panel, GamePhase.Settle));
        }
        private void OnBossVictoryFinished(BossDefeatedInfo info)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            if (!_released && battle.Chapter == info.Chapter && battle.Round == info.Round && battle.Phase == GamePhase.Settle)
            { OnSettle(GameSide.Player); }
        }

        private void ValidatePanel(RevUIPanel panel, GamePhase? expected)
        {
            if (panel == null)
            {
                RevLog.Error("战斗面板加载失败，请检查 GameRes/UI 的预制体", "RevGame");
                return;
            }

            if (_released || this == null)
            {
                RevUI.Close(panel);
                return;
            }

            if (expected.HasValue && RevServices.GetRequired<IBattleService>().Phase != expected.Value)
            {
                RevUI.Close(panel);
                return;
            }

            if (expected == GamePhase.Settle && RevServices.GetRequired<RevGame.Flow.GameFlow>().IsBossVictoryPlaying)
            {
                RevUI.Close(panel);
                return;
            }

            // ★ 玩家可能在加载完成前返回大厅，又进入同一种阶段。
            // 此时面板类型仍然正确，但旧回调携带的结算数据已经过期；重新从唯一服务取最新快照。
            // 不能仅靠「旧回调就关闭」处理，因为框架可能把同类型请求合并到同一个面板实例。
            IBattleService current = RevServices.GetRequired<IBattleService>();
            if (panel is SettlePanel settle)
            {
                settle.SetData(new SettlePanel.SettleData { Winner = current.LastWinner.Value, Chapter = current.Chapter, Round = current.Round, LastRoundOfChapter = current.IsBossRound });
            }
            else if (panel is RewardPanel reward)
            {
                reward.SetData(new RewardPanel.RewardData { Options = current.Rewards });
            }
        }

        private void OnReward()
        {
            RevUI.Close<SettlePanel>();
            var data = new RewardPanel.RewardData
            {
                Options = RevServices.GetRequired<IBattleService>().Rewards
            };
            RevUI.Open<RewardPanel, RewardPanel.RewardData>(data, panel => ValidatePanel(panel, GamePhase.Reward));
        }

        private void OnDisable()
        {
            _released = true;
            RevEvent.RemoveAllByOwner(this);
            RevUI.Close<PreparePanel>();
            RevUI.Close<SettlePanel>();
            RevUI.Close<BattleHudPanel>();
            RevUI.Close<RewardPanel>();
        }
    }
}

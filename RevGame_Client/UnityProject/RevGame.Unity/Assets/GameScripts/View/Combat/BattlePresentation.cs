// ============================================================
// BattlePresentation.cs —— 战斗阶段与面板的接线处。
// 服务不依赖 UI；这里监听回合切换，并在结算弹幕离场后走正常继续入口。
// ============================================================
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed class BattlePresentation : MonoBehaviour
    {
        private bool _released;
        // 当前结算所对应的章与轮。换轮、领赏或离场都会取消待推进。
        private int _settledChapter, _settledRound;
        private GameSide _winner;
        // 0 表示尚未开始播报。使用非缩放时钟，演出结束后再计时。
        private double _continueAt;

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
            else if (battle.Phase == GamePhase.Settle && battle.LastWinner.HasValue)
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
            _settledRound = 0;
            RevUI.Close<SettlePanel>();
            RevUI.Close<RewardPanel>();
            RevUI.Open<PreparePanel>(panel =>
            {
                ValidatePanel(panel, GamePhase.Prepare);
                var flow = RevServices.Get<RevGame.Flow.GameFlow>();

                if (flow != null && flow.IsBossIntroPlaying)
                {
                    flow.SetPrepareHidden(true);
                }
            });
        }
        private void OnBossIntroFinished()
        {
            if (_released)
            {
                return;
            }
            var flow = RevServices.Get<RevGame.Flow.GameFlow>();

            if (flow != null)
            {
                flow.SetPrepareHidden(false);
            }
        }

        private void OnBattle(int round)
        {
            RevUI.Close<PreparePanel>();
        }
        private void OnSettle(GameSide winner)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            RevUI.Close<PreparePanel>();

            if (winner == GameSide.Player && battle.IsBossRound && RevServices.GetRequired<GameFlow>().IsBossVictoryPlaying)
            {
                return;
            }
            _winner = winner;
            _settledChapter = battle.Chapter;
            _settledRound = battle.Round;
            _continueAt = 0;
        }

        // 结算先完成动画再进入准备期。即使弹幕资源加载失败，结果也会通过正式门面自动推进。
        private void Update()
        {
            if (_settledRound == 0)
            {
                return;
            }
            var flow = RevServices.GetRequired<GameFlow>();
            if (flow.IsGameplayBlocked || flow.Screen != GameScreen.Battle)
            {
                return;
            }
            var battle = RevServices.GetRequired<IBattleService>();
            if (battle.Phase != GamePhase.Settle || battle.Chapter != _settledChapter || battle.Round != _settledRound)
            {
                _settledRound = 0;
                return;
            }
            double now = Time.unscaledTimeAsDouble;
            if (_continueAt == 0)
            {
                var hud = RevUI.Get<BattleHudPanel>();
                _continueAt = now + BattleEventNoticeView.RoundOutcomeSeconds + (hud == null ? 0 : 2);
                hud?.ShowRoundOutcome(_winner, OnRoundNoticeFinished);
                return;
            }
            if (now < _continueAt)
            {
                return;
            }
            _settledRound = 0;
            // 比如 Boss 阵形无法落在继承下来的棋盘上，沿用面板报告原因并保留重试入口。
            if (RevLogic.Get<BattleLogic>()?.ContinueToNextRound() != true)
            {
                RevUI.Open<SettlePanel, SettlePanel.SettleData>(new SettlePanel.SettleData
                {
                    Winner = _winner, Chapter = battle.Chapter, Round = battle.Round, LastRoundOfChapter = battle.IsBossRound
                }, panel => ValidatePanel(panel, GamePhase.Settle));
            }
        }

        // 动画真正淡出后即可推进。异步资源没交付时仍有有界时间，不把玩家留在结算态。
        private void OnRoundNoticeFinished()
        {
            _continueAt = Time.unscaledTimeAsDouble;
        }
        private void OnBossVictoryFinished(BossDefeatedInfo info)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();

            if (!_released && battle.Chapter == info.Chapter && battle.Round == info.Round && battle.Phase == GamePhase.Settle)
            {
                OnSettle(GameSide.Player);
            }
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
            else

                if (panel is RewardPanel reward)
                {
                    reward.SetData(new RewardPanel.RewardData { Options = current.Rewards });
                }
        }

        private void OnReward()
        {
            _settledRound = 0;
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
            _settledRound = 0;
            RevEvent.RemoveAllByOwner(this);
            RevUI.Close<PreparePanel>();
            RevUI.Close<SettlePanel>();
            RevUI.Close<BattleHudPanel>();
            RevUI.Close<RewardPanel>();
        }
    }
}

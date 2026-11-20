using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>准备期选中己方棋后出现移除按钮；战斗期选棋继续用于观察和其它操作。</summary>
    public sealed partial class BattleHudPanel
    {
        /// <summary>由预制体绑定的准备期撤棋按钮。</summary>
        [Tooltip("准备期选中己方棋时出现，回调转发 BattleLogic。")]
        [SerializeField] private Button removePieceButton;
        [Tooltip("旧版帮手能源标签。保留给已有预制体和制作配方，由 HUD 绑定时停用。")]
        [SerializeField] private Text helperScoreLabel;
        private int _removePieceId;
        private bool _swallowWaiting;
        private int _swallowShownChapter, _swallowShownRound;

        // 每次打开绑定当前面板所有者的监听；离开本轮不会保留选中状态或积分动画。
        private void OpenPieceManagement()
        {
            RevEvent.AddEventListener(Events.SelectionChanged, RefreshPieceManagement, owner: this);
            RevEvent.AddEventListener<string>(Events.BoardRuleNotice, OnBoardRuleNotice, owner: this);
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelperScoreSpent, owner: this);
            NotifyBossSwallow();
        }
        /// <summary>节点名是RevUI自动绑定契约。处理时重新验证Id，死亡/移除后旧按钮无法删另一颗棋。</summary>
        [RevButtonClick("btnRemovePiece")]
        private void OnRemovePiece()
        {
            // 阶段可能在按下与释放之间切换，点击时再验一次，避免旧按钮在战斗期执行。
            if (Battle.Phase == GamePhase.Prepare && _removePieceId != 0)
            {
                RevLogic.Get<Flow.BattleLogic>()?.RemovePiece(_removePieceId);
            }
            RefreshPieceManagement();
        }
        /// <summary>准备期根据选中棋子和界面门禁更新按钮，战斗期直接清除可见和点击状态。</summary>
        private void RefreshPieceManagement()
        {
            if (removePieceButton == null)
            {
                return;
            }
            var battle = Battle;
            var piece = battle.Phase == GamePhase.Prepare ? battle.Board.FindPiece(battle.SelectedPieceId) : null;
            bool visible = piece != null && piece.IsAlive && piece.Side == GameSide.Player;
            if (visible)
            {
                var flow = RevServices.GetRequired<Flow.GameFlow>();
                visible = !flow.IsGameplayBlocked && !flow.IsTutorialActive;
            }

            if (removePieceButton.gameObject.activeSelf != visible)
            {
                removePieceButton.gameObject.SetActive(visible);
            }
            _removePieceId = visible ? piece.Id : 0;

            if (!visible || Camera.main == null)
            {
                return;
            }
            RectTransform parent = (RectTransform)transform;
            Vector3 world = piece.Pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset)
                + Vector3.one * ((piece.Size - 1) * Consts.CellSize * .5f);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Camera.main.WorldToScreenPoint(world), null, out Vector2 local);
            var rect = (RectTransform)removePieceButton.transform;
            // 位于棋子上方，尺寸只占一小块棋盘；窗口变化时夹在画布内，不遮右侧准备按钮。
            rect.anchoredPosition = new Vector2(Mathf.Clamp(local.x, parent.rect.xMin + 95, parent.rect.xMax - 95),
                Mathf.Clamp(local.y + 58, parent.rect.yMin + 40, parent.rect.yMax - 40));
        }
        /// <summary>规则事实复用既有木纹弹幕底板、对象池与安全区通道，闪烁只改变透明度，不拦棋盘输入。</summary>
        private void OnBoardRuleNotice(string message)
        {
            if (message.Contains("吞噬") && RevServices.GetRequired<Flow.GameFlow>().IsGameplayBlocked)
            {
                NotifyBossSwallow();
                return;
            }
            BattleText config = GameTextConfig.Require("EnemyDeployment");
            ShowEnemyNotice(config, message, "", "棋盘规则提示加载失败");
        }
        /// <summary>Boss演出锁定期间延后播报，避免提示被入场预制体遮住。回合/面板改变取消旧提示。</summary>
        private void NotifyBossSwallow()
        {
            if (!Battle.IsBossRound || !(Battle is Services.BattleService service) || service.LastBossSwallowedCount <= 0)
            {
                return;
            }
            if (_swallowWaiting || (_swallowShownChapter == Battle.Chapter && _swallowShownRound == Battle.Round))
            {
                return;
            }
            _swallowWaiting = true;
            int generation = _noticeGeneration;
            int chapter = Battle.Chapter, round = Battle.Round;
            async void WaitForIntro()
            {
                while (this != null && !_noticeClosed && generation == _noticeGeneration
                    && RevServices.GetRequired<Flow.GameFlow>().IsGameplayBlocked)
                {
                    await RevTask.Yield();
                }
                _swallowWaiting = false;

                if (this == null || _noticeClosed || generation != _noticeGeneration || Battle.Chapter != chapter || Battle.Round != round)
                {
                    return;
                }
                _swallowShownChapter = chapter;
                _swallowShownRound = round;
                OnBoardRuleNotice("不好，boss发动了技能，强制吞噬了你的棋子");
            }
            WaitForIntro();
        }
        /// <summary>先让入账飞星抵达积分面板，再从面板流向帮手。钱包扣款早已完成，粒子取消不退费或重复扣款。</summary>
        private void OnHelperScoreSpent(HelperShotInfo info)
        {
            int generation = _scoreGeneration;
            RevTimer.After(.72f, () =>
            {
                if (generation != _scoreGeneration || _noticeClosed || _noticeRoot == null || scoreFrame == null || Camera.main == null)
                {
                    return;
                }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_noticeRoot, RectTransformUtility.WorldToScreenPoint(null, scoreFrame.position), null, out Vector2 start);
                Vector3 world = Battle.Helper.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_noticeRoot, Camera.main.WorldToScreenPoint(world), null, out Vector2 end);
                int count = info.Beam ? 3 : Mathf.Min(3, info.Projectiles);
                for (int i = 0; i < count && _scoreSparks.Count + _scoreLoading < MaxScoreSparks; i++)
                {
                    _scoreLoading++;
                    int lane = _scoreLane++;
                    GamePoolRequests.GetAsync<ScoreSparkView>(spark =>
                    {
                        if (generation != _scoreGeneration || _noticeClosed)
                        {
                            if (spark != null)
                            {
                                RevPool.Return(spark, 0);
                            }
                            return;
                        }
                        _scoreLoading--;

                        if (spark == null)
                        {
                            RevLog.Warn("帮手积分粒子加载失败", "RevGame");
                            return;
                        }
                        _scoreSparks.Add(spark);
                        spark.Fly(start, end, false, lane, OnScoreSparkFinished);
                    }, _noticeRoot);
                }
            }, owner: this);
        }
    }
}

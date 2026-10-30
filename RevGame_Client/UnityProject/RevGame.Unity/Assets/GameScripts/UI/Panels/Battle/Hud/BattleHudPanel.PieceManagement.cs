using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>选中己方棋后出现局部移除按钮；只转发服务意图，UI从不自己删GameObject或退手牌。</summary>
    public sealed partial class BattleHudPanel
    {
        [Tooltip("选中己方棋时出现，回调只转发BattleLogic；由新增资源配方绑定。")]
        [SerializeField] private Button removePieceButton;
        [Tooltip("本章可用能源余额及成长档位，历史累计分数仍由原积分面板显示。")]
        [SerializeField] private Text helperScoreLabel;
        // 上次显示的能源余额；-1表示未初始化，正常余额非负，打开面板必须强制刷新。
        private long _walletShown = -1;
        // 上次显示的帮手档位/攻击/攻速百分比，余额不变时升级或增益也可能变，故需要完整比较。
        private int _walletTier = -1, _walletAttack = -1, _walletSpeed = -1;
        // 上次画布尺寸，单位为画布坐标；钱包的布局只在尺寸或安全区改变时重算。
        private Vector2 _walletLayoutSize;
        // 安全视口的上一值；手机安全区变化要重排，即使画布宽高没变也不能命中旧布局。
        private Rect _walletSafeArea;
        private int _removePieceId;
        private bool _swallowWaiting;
        private int _swallowShownChapter, _swallowShownRound;

        // 每次打开重置钱包数值和布局哨兵，再绑定当前面板所有者的监听；防止跨场景旧显示。
        private void OpenPieceManagement()
        {
            _walletShown = -1;
            _walletLayoutSize = Vector2.zero;
            RevEvent.AddEventListener(Events.SelectionChanged, RefreshPieceManagement, owner: this);
            RevEvent.AddEventListener<string>(Events.BoardRuleNotice, OnBoardRuleNotice, owner: this);
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelperScoreSpent, owner: this);
            NotifyBossSwallow();
        }
        /// <summary>节点名是RevUI自动绑定契约。处理时重新验证Id，死亡/移除后旧按钮无法删另一颗棋。</summary>
        [RevButtonClick("btnRemovePiece")]
        private void OnRemovePiece()
        {
            RevLogic.Get<Flow.BattleLogic>()?.RemovePiece(_removePieceId);
            RefreshPieceManagement();
        }
        // 重新查选中棋子身份和真实可见前置，SetActive只在变化时调用。
        // 按钮位置仍跟随当前棋子/相机，不能缓存旧坐标导致按钮点到已移走棋子的旁边。
        private void RefreshPieceManagement()
        {
            if (removePieceButton == null)
            {
                return;
            }
            var piece = Battle.Board.FindPiece(Battle.SelectedPieceId);
            bool visible = piece != null && piece.IsAlive && piece.Side == GameSide.Player
                && (Battle.Phase == GamePhase.Prepare || Battle.Phase == GamePhase.Battle);
            var flow = RevServices.GetRequired<Flow.GameFlow>();
            visible = visible && !flow.IsGameplayBlocked && !flow.IsTutorialActive;

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
        // 布局与文字分开缓存：尺寸变化只重排，余额/档位/加成变化只更新文案，避免重复布局与格式化。
        private void RefreshHelperWallet()
        {
            RefreshPieceManagement();

            if (helperScoreLabel == null)
            {
                return;
            }
            RectTransform parent = (RectTransform)transform;
            Rect safe = GameBattleLayout.SafeViewport();

            if (_walletLayoutSize != parent.rect.size || _walletSafeArea != safe)
            {
                _walletLayoutSize = parent.rect.size;
                _walletSafeArea = safe;
                var areas = GameBattleLayout.Calculate(parent.rect.size, safe);
                // 右栏信息牌下方留一行能源提示，避免底部棋盘格与存活棋子遮住数字。
                Rect slot = GameBattleLayout.InRegion(areas.Right, .68f, .92f, .08f);
                helperScoreLabel.rectTransform.sizeDelta = slot.size;
                helperScoreLabel.rectTransform.anchorMin = helperScoreLabel.rectTransform.anchorMax = new Vector2(.5f, .5f);
                helperScoreLabel.rectTransform.anchoredPosition = slot.center - parent.rect.size * .5f;
                helperScoreLabel.fontSize = 22;
            }
            long wallet = Battle.CombatScore;
            int tier = Battle.Helper.Tier;
            int attack = Mathf.RoundToInt(Battle.HelperRangedAttackBonus * 100), speed = Mathf.RoundToInt(Battle.HelperRangedSpeedBonus * 100);

            // 四个显示输入都相同才提前返回。真实服务数据没有被缓存替代，变化时照常更新。
            if (_walletShown == wallet && _walletTier == tier && _walletAttack == attack && _walletSpeed == speed)
            {
                return;
            }
            _walletShown = wallet;
            _walletTier = tier;
            _walletAttack = attack;
            _walletSpeed = speed;
            helperScoreLabel.text = "帮手能量 " + Battle.CombatScore.ToString("N0") + " · " + Battle.Helper.Tier + "档"
                + "\n远程共鸣 攻击+" + Mathf.RoundToInt(Battle.HelperRangedAttackBonus * 100) + "% · 攻速+" + Mathf.RoundToInt(Battle.HelperRangedSpeedBonus * 100) + "%";
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

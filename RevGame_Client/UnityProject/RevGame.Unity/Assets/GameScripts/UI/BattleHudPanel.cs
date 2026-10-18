// ============================================================
// BattleHudPanel.cs
// HUD 只读服务。每次打开重新订阅，关闭取消计时和序列，防止回池后丢监听或重复刷新。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.View;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 战斗 HUD（策划案 §03：局内只有"落子"一个操作，其余全部自动 —— 所以 HUD 只负责**读**）。
    ///
    /// 显示四件事：
    /// 1. 阶段图标（准备 / 战斗 / 结算）；
    /// 2. 第几章第几回合；
    /// 3. 普通回合时间进度，Boss 主将血条与决死伤害层数；
    /// 4. 双方存活数 + 一行战斗日志。
    ///
    /// 【为什么 HUD 自己读服务，而不是等别人 SetData】
    /// HUD 是"常驻观察者"：它关心的不是某一次数据快照，而是**服务的实时状态**。
    /// 让它每帧/每次事件后自己去 `IBattleService` 拉一次，比让业务层到处记得推送更不容易漏。
    /// 真正的"一次性数据"（例如结算面板的胜负）才走 SetData。
    /// </summary>
    [RevUIPanel(RevResPath.UI, RevUILayer.Normal)]
    public sealed class BattleHudPanel : RevUIPanel
    {
        [Header("阶段图标（三种阶段各一张，由预制体指定）")]
        [SerializeField]
        private Sprite phasePrepareSprite;
        [SerializeField]
        private Sprite phaseBattleSprite;
        [SerializeField]
        private Sprite phaseSettleSprite;
        [RevBind]
        private Image _phaseIcon;
        [RevBind]
        private Text _phaseLabel;
        [RevBind]
        private Text _assistText;
        [RevBind]
        private Text _roundText;
        [RevBind]
        private Image _progressFill;
        [RevBind]
        private Text _countText;
        [RevBind]
        private Text _logText;
        [RevBind]
        private Button _btnHome;
        [RevBind]
        private Text _bossText;
        [RevBind]
        private Image _bossBar;
        [RevBind]
        private Image _bossFrame;
        [RevBind]
        private Text _eventText;
        [RevBind]
        private Image _eventIcon;
        [SerializeField]
        private Sprite[] eventIcons;
        // 提示零件单独池化。独立同级容器避开面板停用子树时的换父节点限制。
        private RectTransform _noticeRoot;
        private readonly HashSet<BattleEventNoticeView> _notices = new HashSet<BattleEventNoticeView>();
        private int _noticeGeneration;
        private bool _noticeClosed;
        private readonly List<string> _logs = new List<string>();
        public int ActiveNoticeCount { get { return _notices.Count; } }
        private IBattleService Battle
        {
            get
            {
                return RevServices.GetRequired<IBattleService>();
            }
        }

        protected override void OnBindView()
        {
            _btnHome.onClick.AddListener(OnHome);
        }

        protected override void OnOpen()
        {
            _logs.Clear();
            _noticeClosed = false;
            _noticeGeneration++;
            var container = new GameObject("BattleEventNotices", typeof(RectTransform));
            _noticeRoot = container.GetComponent<RectTransform>();
            _noticeRoot.SetParent(transform.parent, false);
            _noticeRoot.anchorMin = Vector2.zero;
            _noticeRoot.anchorMax = Vector2.one;
            _noticeRoot.offsetMin = _noticeRoot.offsetMax = Vector2.zero;
            SetNoticeVisible(false);
            RevTimer.Every(0.1f, RefreshView, owner: this);
            // owner: this → 面板释放时框架自动解绑，不用手写解绑清单。
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnUpgraded, owner: this);
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPieceChanged, 0, this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, 0, this);
            RevEvent.AddEventListener<string>(Events.BattleLog, OnBattleLog, 0, this);
            RevEvent.AddEventListener<int>(Events.PieceHpChanged, OnPieceChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PieceDied, OnPieceChanged, 0, this);
            RevEvent.AddEventListener<string>(Events.BattleEventTriggered, OnBattleEvent, owner: this);
        }

        /// <summary>只做"把服务状态画到屏上"，不修改任何游戏数据。</summary>
        protected override void OnRefreshView()
        {
            IBattleService _battle = Battle;
            if (_battle == null)
            {
                return;
            }

            if (_phaseIcon != null)
            {
                _phaseIcon.sprite = PickPhaseSprite(_battle.Phase);
                // 新版美术提供通用木牌，阶段名称用文字表达，不再依赖旧版的三张阶段图片。
                _phaseLabel.text = _battle.Phase == GamePhase.Battle ? "战斗" : _battle.Phase == GamePhase.Settle ? "结算" : "准备";
            }

            if (_roundText != null)
            {
                _roundText.text = "第 " + _battle.Chapter + " / 6 章 · 第 " + _battle.Round + " 回合" + (_battle.IsBossRound ? "（擒王 · 不限时）" : "");
            }

            _assistText.text = _battle.VictoryAssistEnabled ? "通关守护 · 支援事件 " + _battle.BattleEventCount + " 次\n" + _battle.VictorySupportStatus : "";

            if (_progressFill != null)
            {
                float duration = _battle.BattleDuration;
                float ratio = duration > 0f ? Mathf.Clamp01(_battle.BattleElapsed / duration) : 0f;
                _progressFill.fillAmount = ratio;
            }

            if (_countText != null)
            {
                // 沿用两行高度：第二行放简短阵型和攻击加成，完整降级原因留给 GM，不挤出第三行遮住日志。
                string formation = _battle.EnemyFormation.Split('（')[0];
                _countText.text = "我方 " + CountAlive(_battle.PlayerPieces) + " / 敌方 " + CountAlive(_battle.EnemyPieces) + "\n" + formation + " · 我方+" + Mathf.RoundToInt(_battle.TeamAttackBonus * 100f) + "%";
            }

            if (_logText != null)
            {
                _logText.text = _logs.Count == 0 ? "" : _logs[_logs.Count - 1];
            }

            Pieces.Piece king = null;
            foreach (var piece in _battle.EnemyPieces)
            {
                if (piece.IsKing)
                {
                    king = piece;
                    break;
                }
            }

            _bossFrame.gameObject.SetActive(king != null);
            _bossBar.gameObject.SetActive(king != null);
            _bossText.gameObject.SetActive(king != null);
            if (king != null)
            {
                _bossBar.fillAmount = king.HpRatio;
                int rage = (int)(_battle.BattleElapsed / Consts.BossAccelerationSeconds) * 10;
                _bossText.text = "主将  " + king.Hp + " / " + king.MaxHp + (rage > 0 ? "  决死伤害 +" + rage + "%" : "");
            }
        }

        private Sprite PickPhaseSprite(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Prepare:
                    return phasePrepareSprite;
                case GamePhase.Battle:
                    return phaseBattleSprite;
                case GamePhase.Settle:
                    return phaseSettleSprite;
                default:
                    return phasePrepareSprite;
            }
        }

        private void OnHome()
        {
            RevServices.GetRequired<Flow.GameFlow>().ReturnHome();
        }

        protected override void OnRelease()
        {
            _btnHome.onClick.RemoveListener(OnHome);
        }

        protected override void OnClose()
        {
            RevTimer.CancelAllOf(this);
            _noticeClosed = true;
            ClearRoundPresentation();
            RevPool.DestroyPool<BattleEventNoticeView>();
            if (_noticeRoot != null)
            {
                Destroy(_noticeRoot.gameObject);
                _noticeRoot = null;
            }
        }

        /// <summary>归还当前提示并使尚未加载完的旧请求失效；回合间保留池，避免反复创建。</summary>
        private void ClearNotices()
        {
            _noticeGeneration++;
            foreach (BattleEventNoticeView notice in _notices)
            {
                if (notice != null)
                {
                    RevPool.Return(notice, 0);
                }
            }
            _notices.Clear();
        }

        /// <summary>清除“上轮发生过什么”，服务负责重置数值，HUD 负责重置表现，两者缺一不可。</summary>
        private void ClearRoundPresentation()
        {
            ClearNotices();
            _logs.Clear();
            _logText.text = string.Empty;
            // 旧版静态提示节点也清空并隐藏，防止它与池化的新提示同时显示。
            _eventText.text = string.Empty;
            _eventIcon.sprite = null;
            SetNoticeVisible(false);
        }

        private void OnBattleEvent(string title)
        {
            Sprite sprite = eventIcons.Length > 0 ? eventIcons[0] : null;
            for (int i = 0; i < eventIcons.Length; i++)
            {
                if (Data.BattleEventCatalog.NameOf((Data.BattleEventKind)i) == title)
                {
                    sprite = eventIcons[i];
                    break;
                }
            }
            BattleText config = GameTextConfig.Require("Event_" + title);
            ShowNotice(config, config.Text, sprite);
        }

        private void OnUpgraded(int id)
        {
            foreach (Pieces.Piece piece in Battle.PlayerPieces)
            {
                if (piece.Id != id || !piece.IsAlive) { continue; }
                BattleText config = GameTextConfig.Require("Upgrade_" + (int)piece.Def.Star);
                string title = config.Text.Replace("{piece}", piece.Def.DisplayName.Split(' ')[0]).Replace("{star}", ((int)piece.Def.Star).ToString());
                ShowNotice(config, title, eventIcons.Length > 0 ? eventIcons[0] : null);
                break;
            }
        }

        private void ShowNotice(BattleText config, string title, Sprite sprite)
        {
            // 同一时刻只读一条大提示。连续合成时显示最新结果，旧提示立即回池；
            // 不排一个跨回合的长队列，否则玩家已进入准备期却还在观看上轮支援。
            ClearNotices();
            int generation = _noticeGeneration;
            RevPool.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error("战斗事件提示预制体加载失败", "RevGame");
                    return;
                }
                // 异步加载结束时面板可能已经关闭；过期结果必须回池，不能在大厅补播。
                if (_noticeClosed || this == null || generation != _noticeGeneration)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _notices.Add(notice);
                notice.Show(config, title, sprite, OnNoticeFinished);
            }, _noticeRoot);
        }

        private void OnNoticeFinished(BattleEventNoticeView notice)
        {
            _notices.Remove(notice);
        }

        private void SetNoticeVisible(bool visible)
        {
            _eventText.transform.parent.gameObject.SetActive(visible);
        }

        private static int CountAlive(IReadOnlyList<Pieces.Piece> pieces)
        {
            int n = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].IsAlive)
                {
                    n++;
                }
            }

            return n;
        }

        private void OnPhaseChanged(int round)
        {
            ClearRoundPresentation();
            RefreshView();
        }

        private void OnSettle(GameSide winner)
        {
            ClearRoundPresentation();
            RefreshView();
        }

        private void OnPieceChanged(int pieceId)
        {
            RefreshView();
        }

        private void OnBattleLog(string text)
        {
            _logs.Add(text);
            // 只留最近 5 条，避免无限增长（HUD 只显示最后一条）。
            while (_logs.Count > 5)
            {
                _logs.RemoveAt(0);
            }

            RefreshView();
        }
    }
}

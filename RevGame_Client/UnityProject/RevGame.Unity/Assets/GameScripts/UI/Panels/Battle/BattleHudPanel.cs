// ============================================================
// BattleHudPanel.cs
// HUD 只读服务。每次打开重新订阅，关闭取消计时和序列，防止回池后丢监听或重复刷新。
// ============================================================
using System.Collections.Generic;
using DG.Tweening;
using RevGame.Data;
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
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed partial class BattleHudPanel : RevUIPanel
    {
        [RevButtonClick("btnSettings")]
        private void OnSettings() { RevServices.GetRequired<RevGame.Flow.GameFlow>().OpenAudioSettings(); }
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
        private int _regularNoticeGeneration;
        private readonly Queue<PreparationBuffDef> _preparationQueue = new Queue<PreparationBuffDef>();
        private BattleEventNoticeView _preparationNotice;
        // 敌方落子与玩家增益分通道，避免同一模拟步触发增益时把落子提示立即收走。
        private BattleEventNoticeView _enemyNotice;
        private int _enemyNoticeGeneration;
        private bool _preparationLoading;
        private Tween _progressTween, _bossTween;
        private float _progressTarget = -1f, _bossTarget = -1f;
        public int PendingPreparationNoticeCount => _preparationQueue.Count;
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

        }

        protected override void OnOpen()
        {
            _logs.Clear();
            OpenScore();
            ResetBarAnimations();
            _noticeClosed = false;
            _noticeGeneration++;
            var container = new GameObject("BattleEventNotices", typeof(RectTransform));
            _noticeRoot = container.GetComponent<RectTransform>();
            _noticeRoot.SetParent(transform.parent, false);
            _noticeRoot.anchorMin = Vector2.zero;
            _noticeRoot.anchorMax = Vector2.one;
            _noticeRoot.offsetMin = _noticeRoot.offsetMax = Vector2.zero;
            OpenPieceManagement();
            SetNoticeVisible(false);
            RevTimer.Every(0.1f, RefreshView, owner: this);
            RevEvent.AddEventListener(Events.PreparationBuffsGranted, OnPreparationGranted, owner: this);
            // owner: this → 面板释放时框架自动解绑，不用手写解绑清单。
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPhaseChanged, 0, this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnUpgraded, owner: this);
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPieceChanged, 0, this);
            RevEvent.AddEventListener<int, string>(Events.EnemyDeployed, OnEnemyDeployed, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillTelegraph, OnBossTelegraph, owner: this);
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
            RefreshScore(_battle);
            RefreshHelperWallet();
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
                _roundText.text = "第 " + _battle.Chapter + " / 6 章 · 第 " + _battle.Round + " 回合" + (_battle.IsBossRound ? "（擒王 · 不限时）" : "（不限时）");
            }

            _assistText.text = _battle.VictoryAssistEnabled ? "战斗增益 · 支援事件 " + _battle.BattleEventCount + " 次\n" + _battle.VictorySupportStatus : "";

            if (_progressFill != null)
            {
                float duration = _battle.BattleDuration;
                float ratio = duration > 0f ? Mathf.Clamp01(_battle.BattleElapsed / duration) : 0f;
                UpdateBar(_progressFill, ratio, ref _progressTween, ref _progressTarget, _battle.Phase != GamePhase.Battle, ClearProgressTween);
            }

            if (_countText != null)
            {
                // 沿用两行高度：第二行放简短阵型和攻击加成，完整降级原因留给 GM，不挤出第三行遮住日志。
                string formation = _battle.EnemyFormation.Split('（')[0];
                _countText.text = "我方 " + CountAlive(_battle.PlayerPieces) + " / 敌方 " + CountAlive(_battle.EnemyPieces) + "\n" + formation + " · AI " + _battle.EnemyIntelligence + "档 · 我方+" + Mathf.RoundToInt(_battle.TeamAttackBonus * 100f) + "%";
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
                UpdateBar(_bossBar, king.HpRatio, ref _bossTween, ref _bossTarget, _battle.Phase != GamePhase.Battle, ClearBossTween);
                // 决死伤害百分比 = 已满档位 × 每档加成；每档加成取自参数，旧代码这里把 10% 写死成了 *10。
                int rage = (int)((int)(_battle.BattleElapsed / Balance.BossAccelerationSeconds) * Balance.BossAccelerationBonus * 100f + 0.5f);
                _bossText.text = "主将  " + king.Hp + " / " + king.MaxHp + (king.HpRatio <= Balance.BossCaptureHpRatio ? " · 可擒王" : " · 降至" + ConfigText.Percent(Balance.BossCaptureHpRatio) + "后可擒王") + (rage > 0 ? "  决死伤害 +" + rage + "%" : "");
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

        [RevButtonClick("btnHome")]
        private void OnHome()
        {
            RevServices.GetRequired<Flow.GameFlow>().ReturnHome();
        }


        protected override void OnClose()
        {
            CloseScore();
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
            _regularNoticeGeneration++;
            _enemyNoticeGeneration++;
            _enemyNotice = null;
            _preparationQueue.Clear();
            _preparationNotice = null;
            _preparationLoading = false;
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
            ResetBarAnimations();
            _logs.Clear();
            _logText.text = string.Empty;
            // 旧版静态提示节点也清空并隐藏，防止它与池化的新提示同时显示。
            _eventText.text = string.Empty;
            _eventIcon.sprite = null;
            SetNoticeVisible(false);
        }

        private void OnBattleEvent(string title)
        {
            if (title == "眼位成型" || title == "眼位复苏")
            {
                BattleText eyeText = GameTextConfig.Require("Event_" + title);
                string detail = eyeText.Detail.Replace("{defense}", ConfigText.Percent(Balance.EyeGuardDefenseBonus))
                    .Replace("{regen}", ConfigText.Percent(Balance.EyeHealRatio))
                    .Replace("{heal}", ConfigText.Percent(Balance.EyeReviveHealRatio));
                ShowNotice(eyeText, eyeText.Text, null, detail, central: true);
                return;
            }
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

        /// <summary>
        /// 只播报已经提交的敌方落子。先把棋种/坐标转成文字，异步加载不能保存可能阵亡的Piece引用。
        /// 新回合/关闭面板会改变代次：旧加载回调只回池、不补播，避免大厅突然出现敌方弹幕。
        /// </summary>
        private void OnEnemyDeployed(int id, string intention)
        {
            Pieces.Piece piece = Battle.Board.FindPiece(id);
            if (piece == null || piece.Side != GameSide.Enemy)
            {
                return;
            }
            BattleText config = GameTextConfig.Require("EnemyDeployment");
            string title = config.Text.Replace("{intention}", "敌方落子 · " + intention);
            string detail = config.Detail.Replace("{piece}", piece.Def.DisplayName).Replace("{position}", piece.Pos.ToString());
            ShowEnemyNotice(config, title, detail, "敌方落子提示加载失败");
        }

        /// <summary>
        /// Boss 开始蓄力时弹出“技能名 + 应对提示”。与敌方落子共用右栏同一条提示通道：
        /// Boss 技能更重要，新提示直接顶掉旧的，保证玩家看到的是“马上要发生的事”。
        /// 文字来自 Excel（BossSkillConfig 的 Name/Hint、BattleText 的 BossSkill 样式），这里不写死文案。
        /// </summary>
        private void OnBossTelegraph(BossSkillInfo info)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            BattleText config = GameTextConfig.Require("BossSkill");
            string title = config.Text.Replace("{skill}", (info.Enraged ? "狂暴 · " : "Boss · ") + def.Name);
            string detail = config.Detail.Replace("{hint}", def.Hint);
            ShowEnemyNotice(config, title, detail, "Boss 技能提示加载失败");
        }

        /// <summary>
        /// 右栏“敌方动态”提示通道：同一时间只显示一条，新提示替换旧提示。
        /// 用两层代次防止过期显示：_noticeGeneration 代表回合/面板寿命，_enemyNoticeGeneration 代表本通道的请求序号；
        /// 异步加载回来时任意一个不匹配都直接回池，不会在新回合里冒出旧提示。
        /// </summary>
        private void ShowEnemyNotice(BattleText config, string title, string detail, string loadError)
        {
            if (_enemyNotice != null)
            {
                _notices.Remove(_enemyNotice);
                RevPool.Return(_enemyNotice, 0);
                _enemyNotice = null;
            }
            int generation = _noticeGeneration;
            int request = ++_enemyNoticeGeneration;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error(loadError, "RevGame"); return;
                }
                if (_noticeClosed || this == null || generation != _noticeGeneration || request != _enemyNoticeGeneration)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _enemyNotice = notice;
                _notices.Add(notice);
                notice.Show(config, title, null, item =>
                {
                    _notices.Remove(item);
                    if (_enemyNotice == item)
                    {
                        _enemyNotice = null;
                    }
                }, detail, enemyDeployment: true);
            }, _noticeRoot);
        }

        private void OnUpgraded(int id)
        {
            foreach (Pieces.Piece piece in Battle.PlayerPieces)
            {
                if (piece.Id != id || !piece.IsAlive)
                {
                    continue;
                }
                BattleText config = GameTextConfig.Require("Upgrade_" + (int)piece.Def.Star);
                string title = config.Text.Replace("{piece}", piece.Def.DisplayName.Split(' ')[0]).Replace("{star}", ((int)piece.Def.Star).ToString());
                ShowNotice(config, title, eventIcons.Length > 0 ? eventIcons[0] : null);
                break;
            }
        }

        private void ShowNotice(BattleText config, string title, Sprite sprite, string detail = null, bool central = false)
        {
            // 同一时刻只读一条大提示。连续升级时显示最新结果，旧提示立即回池；
            // 不排一个跨回合的长队列，否则玩家已进入准备期却还在观看上轮支援。
            // 准备横幅与右栏升级提示分别管理：打牌升级不能吞掉尚未播报的准备增益。
            foreach (BattleEventNoticeView old in new List<BattleEventNoticeView>(_notices))
            {
                if (old == _preparationNotice || old == _enemyNotice)
                {
                    continue;
                }
                _notices.Remove(old);
                RevPool.Return(old, 0);
            }
            int regularGeneration = ++_regularNoticeGeneration;
            int generation = _noticeGeneration;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                if (notice == null)
                {
                    RevLog.Error("战斗事件提示预制体加载失败", "RevGame");
                    return;
                }
                // 异步加载结束时面板可能已经关闭；过期结果必须回池，不能在大厅补播。
                if (_noticeClosed || this == null || generation != _noticeGeneration || regularGeneration != _regularNoticeGeneration)
                {
                    RevPool.Return(notice, 0);
                    return;
                }
                _notices.Add(notice);
                if (central) { notice.ShowPreparation(title, detail, OnNoticeFinished); }
                else { notice.Show(config, title, sprite, OnNoticeFinished); }
            }, _noticeRoot);
        }

        /// <summary>数据已经生效后仅排播报队列，不在动画完成回调里修改战斗属性。</summary>
        private void OnPreparationGranted()
        {
            foreach (PreparationBuffDef buff in Battle.PreparationBuffs) { _preparationQueue.Enqueue(buff); }
            TryShowPreparationNotice();
            RefreshView();
        }

        private void TryShowPreparationNotice()
        {
            if (_noticeClosed || Battle.Phase != GamePhase.Prepare || _preparationLoading || _preparationNotice != null || _preparationQueue.Count == 0)
            {
                return;
            }
            PreparationBuffDef buff = _preparationQueue.Dequeue();
            int generation = _noticeGeneration;
            _preparationLoading = true;
            GamePoolRequests.GetAsync<BattleEventNoticeView>(notice =>
            {
                // 先判代次，再动loading。旧资源回调不能把新回合的加载状态改成false。
                if (_noticeClosed || this == null || generation != _noticeGeneration)
                {
                    if (notice != null)
                    {
                        RevPool.Return(notice, 0);
                    }
                    return;
                }
                _preparationLoading = false;
                if (notice == null)
                {
                    RevLog.Error("准备增益弹幕加载失败：" + buff.Title, "RevGame");
                    TryShowPreparationNotice();
                    return;
                }
                _preparationNotice = notice;
                _notices.Add(notice);
                notice.ShowPreparation(buff.Title, buff.Detail, OnPreparationNoticeFinished);
            }, _noticeRoot);
        }

        private void OnPreparationNoticeFinished(BattleEventNoticeView notice)
        {
            _notices.Remove(notice);
            _preparationNotice = null;
            TryShowPreparationNotice();
        }

        /// <summary>只有目标值改变才接续缓动，避免每0.1秒刷新把同一动画反复从头启动。</summary>
        private static void UpdateBar(Image image, float value, ref Tween tween, ref float target, bool instant, TweenCallback onKilled)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Abs(target - value) < 0.00001f)
            {
                return;
            }
            tween?.Kill(false);
            tween = null;
            bool first = target < 0f;
            target = value;
            if (instant || first)
            {
                image.fillAmount = value; return;
            }
            // 【新手注意】项目开启了 Tween 复用：动画结束后，同一个 C# 对象可能立刻被借给死亡消融。
            // 因此不能只用 IsActive 判断“旧血条动画还活着”；它可能已经是别人的动画。
            // AutoKill（自然结束）和手动 Kill 都会执行 OnKill，必须在进入复用池前清空本面板的引用。
            // ref 参数不能在 lambda 中捕获，所以由各字段自己的清理方法接收这个回调。
            tween = DOTween.To(() => image.fillAmount, x => image.fillAmount = x, value, 0.28f)
                .SetEase(Ease.OutCubic).SetUpdate(true).OnKill(onKilled);
        }

        private void ClearProgressTween()
        {
            _progressTween = null;
        }

        private void ClearBossTween()
        {
            _bossTween = null;
        }

        private void ResetBarAnimations()
        {
            _progressTween?.Kill(false);
            _bossTween?.Kill(false);
            _progressTween = _bossTween = null;
            _progressTarget = _bossTarget = -1f;
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
            _swallowWaiting = false;
            if (Battle.Phase == GamePhase.Prepare) { NotifyBossSwallow(); }
            ClearScoreFlights();
            RefreshView();
        }

        private void OnSettle(GameSide winner)
        {
            ClearRoundPresentation();
            ClearScoreFlights();
            RefreshView();
        }

        private void OnPieceChanged(int pieceId)
        {
            // 伤害可以同帧触发数十次，统一交给已有10Hz时钟落屏；真实伤害数字仍逐帧显示。
            // 不在这里反复拼接全部HUD字符串、扫描主将、重建Canvas布局。
        }

        private void OnBattleLog(string text)
        {
            _logs.Add(text);
            // 只留最近 5 条，避免无限增长（HUD 只显示最后一条）。
            while (_logs.Count > 5)
            {
                _logs.RemoveAt(0);
            }

            // 最新战报由10Hz刷新显示，保留即时事件但合并重复UI工作。
        }
    }
}

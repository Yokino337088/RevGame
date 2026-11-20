using System;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using System.Linq;
#endif
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    /// <summary>
    /// GameFlow的场景交接部分。仍由同一个FSM管理大厅/战斗域，RevScene只负责真正加载Unity场景。
    /// 顺序：盖住旧画面 → 异步资源预载 → RevScene加载 → 分帧创建实例 → 等待UI与图集可画 → 淡出。
    /// 不提前开放战斗计时/输入，也不把Unity场景进度100%误当成整个游戏画面已经就绪。
    /// </summary>
    public sealed partial class GameFlow
    {
        /// <summary>正式场景名；StartScene位于Build Settings第0位，教学和战斗都由同一流程切换。</summary>
        public const string StartSceneName = "StartScene";
        /// <summary>六章战斗使用的 Unity 场景名。</summary>
        public const string BattleSceneName = "GameScene";

        /// <summary>当前流程的交接历史。由 GameFlow 写入，观察方使用句柄查询和等待。</summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        private readonly RevSceneHandoffTracker _sceneHandoffs = RevScene.CreateHandoffTracker(samplePerformance: true);
#else
        private readonly RevSceneHandoffTracker _sceneHandoffs = RevScene.CreateHandoffTracker();
#endif
        /// <summary>尚未完成退场清理的六章交接。返回大厅后冻结其资源快照并清空。</summary>
        private RevSceneHandoff _battleHandoff;

        /// <summary>最近一次接单的大厅或六章场景交接。未接单时为空，拒绝连点不会替换它。</summary>
        public RevSceneHandoff LatestSceneHandoff => _sceneHandoffs.Latest;

        /// <summary>仅在六章入场时向表现层开放首屏依赖记录，章间换背景仍使用自己的租约。</summary>
        internal RevSceneHandoffResources EnteringBattleResources => _battleHandoff != null && !_battleHandoff.IsCompleted
            ? _battleHandoff.Resources : null;

        /// <summary>按请求 ID 查询当前或最近完成的交接。不存在或已淘汰时返回 false。</summary>
        public bool TryGetSceneHandoff(Guid requestId, out RevSceneHandoff handoff)
        {
            return _sceneHandoffs.TryGet(requestId, out handoff);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>开发态查询的可选条件。空请求 ID 使用最近一次场景交接。</summary>
        [Serializable]
        private sealed class SceneHandoffQuery
        {
            /// <summary>要查询的场景交接 ID。</summary>
            public string requestId;
            /// <summary>选择 current、ready 或 exit。省略时实时采样。</summary>
            public string snapshot;
        }

        /// <summary>注册按交接 ID 查询资源和性能的只读开发态入口，随 GameFlow 注销。</summary>
        private void RegisterSceneHandoffAutomation()
        {
            const string schema = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"},"
                + "\"snapshot\":{\"type\":\"string\",\"enum\":[\"current\",\"ready\",\"exit\"]}},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.scene-handoff.resources", "按交接 ID 查询预加载来源、首屏等待、当前或历史持有者",
                schema, QuerySceneHandoffResources, readOnly: true, owner: this);
            const string performanceQuery = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"}},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.scene-handoff.performance", "按交接 ID 查询阶段耗时、帧间隔和 Unity 已分配内存峰值",
                performanceQuery, QuerySceneHandoffPerformance, readOnly: true, owner: this);
        }

        /// <summary>解析可选的交接 ID，缺省取最近一次，显式无效的 ID 不回落查询。</summary>
        private bool TryResolveSceneHandoff(string requestId, out RevSceneHandoff handoff)
        {
            handoff = LatestSceneHandoff;
            if (string.IsNullOrEmpty(requestId))
            {
                return handoff != null;
            }
            return Guid.TryParse(requestId, out Guid id) && TryGetSceneHandoff(id, out handoff);
        }

        /// <summary>返回实时或历史资源快照。已淘汰的 ID 或尚未采集的边界会标记 available=false。</summary>
        private object QuerySceneHandoffResources(string json)
        {
            var query = JsonUtility.FromJson<SceneHandoffQuery>(json);
            if (!TryResolveSceneHandoff(query.requestId, out RevSceneHandoff handoff))
            {
                return new Dictionary<string, object> { ["available"] = false, ["requestId"] = query.requestId };
            }

            RevSceneHandoffResources trace = handoff.Resources;
            IReadOnlyList<RevSceneResourceSnapshot> rows = query.snapshot switch
            {
                "ready" => trace.AtReady,
                "exit" => trace.AfterExit,
                _ => trace.CaptureCurrent()
            };
            return new Dictionary<string, object>
            {
                ["available"] = rows != null,
                ["requestId"] = handoff.RequestId.ToString(),
                ["exitRequestId"] = trace.ExitRequestId?.ToString(),
                ["snapshot"] = query.snapshot ?? "current",
                ["resources"] = rows?.Select(row => new Dictionary<string, object>
                {
                    ["root"] = row.Root,
                    ["name"] = row.Name,
                    ["introducedBy"] = row.Introducer,
                    ["blocksFirstFrame"] = row.BlocksFirstFrame,
                    ["refCount"] = row.ReferenceCount,
                    ["holders"] = row.Holders,
                    ["unattributedCount"] = row.UnattributedCount,
                    ["holdsBundleReference"] = row.HoldsBundleReference,
                    ["releasedAfterExit"] = query.snapshot == "exit"
                        ? (bool?)(row.ReferenceCount == 0 && !row.HoldsBundleReference) : null
                }).ToArray()
            };
        }

        /// <summary>读取终态时冻结的性能摘要；在途时仅返回请求、当前阶段和未就绪标志。</summary>
        private object QuerySceneHandoffPerformance(string json)
        {
            var query = JsonUtility.FromJson<SceneHandoffQuery>(json);
            if (!TryResolveSceneHandoff(query.requestId, out RevSceneHandoff handoff))
            {
                return new Dictionary<string, object> { ["available"] = false, ["requestId"] = query.requestId };
            }

            RevSceneHandoffPerformance performance = handoff.Performance;
            return new Dictionary<string, object>
            {
                ["available"] = handoff.IsCompleted,
                ["requestId"] = handoff.RequestId.ToString(),
                ["sourceScene"] = handoff.SourceScene,
                ["targetScene"] = handoff.TargetScene,
                ["status"] = handoff.Result?.Status.ToString(),
                ["stage"] = handoff.Stage.ToString(),
                ["samplesEnabled"] = performance.SamplesEnabled,
                ["totalDurationMs"] = performance.TotalDurationMs,
                ["stages"] = performance.Stages?.Select(row => new Dictionary<string, object>
                {
                    ["stage"] = row.Stage.ToString(),
                    ["durationMs"] = row.DurationMs,
                    ["frameSamples"] = row.FrameSamples,
                    ["droppedFrameSamples"] = row.DroppedFrameSamples,
                    ["frameP50Ms"] = row.FrameP50Ms,
                    ["frameP95Ms"] = row.FrameP95Ms,
                    ["frameP99Ms"] = row.FrameP99Ms,
                    ["frameMaxMs"] = row.FrameMaxMs,
                    ["memorySamples"] = row.MemorySamples,
                    ["peakAllocatedBytes"] = row.PeakAllocatedBytes
                }).ToArray()
            };
        }
#endif

        /// <summary>跨整个加载及淡出期间的互斥锁。战斗Logic据此暂停Tick，防止准备增益被遮罩吃掉。</summary>
        public bool IsTransitioning
        {
            get; private set;
        }
        /// <summary>失败遮挡仍开着时也不能推进玩法；IsTransitioning只表示异步任务是否仍在执行。</summary>
        public bool IsGameplayBlocked
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                if (_bossDevelopmentEntering)
                {
                    return true;
                }
#endif
                return IsBossIntroPlaying || IsBossVictoryPlaying || IsAudioSettingsVisible || IsStoryPlaying || IsGalPlaying || IsTransitioning || _loading != null
                    || (Screen == GameScreen.Battle && _activePresentation != null && !_activePresentation.IsChapterBackgroundReady);
            }
        }
        /// <summary>当前接管业务画面的场景表现对象；场景交接前按旧状态使用。</summary>
        private GamePresentation _activePresentation;
        /// <summary>诊断用真实阶段进度，不是DOTween插值后的视觉值；范围0~1。</summary>
        public float LoadProgress
        {
            get; private set;
        }
        /// <summary>当前阶段的人话描述，GM可查询，面板也直接显示；不携带文件系统路径。</summary>
        public string LoadStep { get; private set; } = "尚未开始";
        /// <summary>仅GameBootstrap注销服务时置true，禁止退出Play后的异步续体重新打开面板。</summary>
        private bool _disposed;
        /// <summary>与 GameFlow 同寿命的交接令牌，覆盖旧域、候选与新域。</summary>
        private readonly RevCancellationTokenSource _flowCancellation = new RevCancellationTokenSource();
        /// <summary>启动表只初始化一次；加载失败不置true，用户点返回时可以重新尝试真实资源加载。</summary>
        private bool _startupComplete;
        /// <summary>当前任务的资源账本，退出Play时主动释放，不能让晚到续体继续往编辑场景里生成对象。</summary>
        private SceneResourceWarmup _activeWarmup;
        /// <summary>本次打开的全局遮挡实例，由RevUI持有；成功关闭后清null，失败时保留返回按钮。</summary>
        private LoadingPanel _loading;
        /// <summary>仅常用菜单图集。UIBattle含共用木板，因此大厅也依赖它；备用大图不主动加载。</summary>
        private static readonly string[] MenuAtlases = { "UIMenus", "UIBattle" };
        /// <summary>战斗实际用到的图集白名单，入场先准备，离场由图集服务释放战斗专用租约。</summary>
        private static readonly string[] BattleAtlases = { "PieceFrames", "CombatFrames", "BoardStatus", "UIBattle" };
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>验收专用的一次性故障；正式包不存在此入口，不修改真实AB文件来制造失败。</summary>
        private string _developmentFault;
        /// <summary>仅在当前无交接时预约一个真实切场边界上的故障，注入一次后消耗。</summary>
        public bool ArmDevelopmentFailure(string stage)
        {
            if (IsTransitioning || (stage != "before-scene" && stage != "after-scene"))
            {
                return false;
            }
            _developmentFault = stage;
            return true;
        }
        /// <summary>到达预约边界时先消耗标记，再进正式错误处理逻辑，让回退请求正常运行。</summary>
        private void CheckDevelopmentFailure(string stage)
        {
            if (_developmentFault != stage)
            {
                return;
            }
            _developmentFault = null;
            throw new InvalidOperationException("验收主动注入过场失败：" + stage);
        }
#endif

        /// <summary>按钮不能await，因此使用唯一的async void边界；异常在SwitchSceneAsync内部收口并显示原因。</summary>
        private async void SwitchFromButtonAsync(GameScreen target)
        {
            await SwitchSceneAsync(target);
        }
        /// <summary>
        /// 按框架交接步骤连接 RevGame 过场、场景与状态机。接单拒绝时让原交接保留门禁。
        /// </summary>
        private async RevTask SwitchSceneAsync(GameScreen target)
        {
            string sceneName = target == GameScreen.Battle ? BattleSceneName : StartSceneName;
            var warmup = new SceneResourceWarmup();
            GamePresentation presentation = null;
            var plan = new RevSceneHandoffPlan
            {
                TargetScene = sceneName,
                SourceState = Screen.ToString(),
                TargetState = target.ToString(),
                Cancellation = _flowCancellation.Token,
                Cover = async handoff =>
                {
                    // 接单就重置本次诊断进度。面板需要异步创建，等待期间不能暴露上一趟的 100%。
                    string covering = target == GameScreen.Battle ? "正在展开战场…" : "正在返回营地…";
                    Report(0f, covering);
                    if (target == GameScreen.Battle)
                    {
                        _battleHandoff = handoff;
                    }
                    _activeWarmup = warmup;
                    handoff.Resources.Require(RevResPath.UI_Panel_Loading, nameof(LoadingPanel), "GameFlow.LoadingCover", blocksFirstFrame: true);
                    _loading = await RevUI.OpenAsync<LoadingPanel>();
                    ThrowIfDisposed();
                    // 过场由流程持有，目标业务域提交时旧域不能收走遮挡。
                    RevScope.Detach(_loading);
                    _loading.ResetCover();
                    Report(0.02f, covering);
                    await RevTask.Yield();
                },
                Preload = async handoff =>
                {
                    if (Screen == GameScreen.Pvp)
                    {
                        CloseDuelHud();
                        await RevServices.GetRequired<RevGame.Pvp.PvpService>().LeaveAsync();
                        ThrowIfDisposed();
                    }
                    if (!_startupComplete)
                    {
                        await LoadStartupDataAsync();
                    }
                    await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(
                        target == GameScreen.Battle ? BattleAtlases : MenuAtlases, handoff.Resources);
                    ThrowIfDisposed();
                    Report(0.18f, "正在整理行囊…");
                    await warmup.LoadAsync(target == GameScreen.Battle, handoff.Resources, value => Report(0.18f + value * 0.24f, "正在集结冒险队伍…"));
                },
                Load = async handoff =>
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                    CheckDevelopmentFailure("before-scene");
#endif
                    if (RevScene.CurrentName != handoff.TargetScene)
                    {
                        // 先归还旧战斗视图，域仍由业务提交后退出。失败期间保持过场遮挡。
                        UnityEngine.Object.FindObjectOfType<GamePresentation>()?.SuspendBattle();
                        await RevScene.LoadAsync(handoff.TargetScene, value => Report(0.42f + value * 0.23f, "正在穿越迷雾…"));
                        if (RevScene.CurrentName != handoff.TargetScene)
                        {
                            throw new InvalidOperationException("场景未完成交接：" + handoff.TargetScene + "，RevScene 状态=" + RevScene.State);
                        }
                    }
                },
                Prepare = async handoff =>
                {
                    // 等目标场景先执行 Start，再核对实际活动场景里的表现宿主。
                    await RevTask.Yield();
                    ThrowIfDisposed();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                    CheckDevelopmentFailure("after-scene");
#endif
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        presentation = root.GetComponentInChildren<GamePresentation>();
                        if (presentation != null)
                        {
                            break;
                        }
                    }
                    if (presentation == null)
                    {
                        throw new InvalidOperationException("场景缺少 GamePresentation 接线：" + handoff.TargetScene);
                    }
                    Report(0.66f, "正在点亮前路…");
                },
                Commit = async _ =>
                {
                    // 预建对象归新域。先准备池，再发布进入战斗事件，避免首屏争建实例。
                    _activePresentation = presentation;
                    Change(target, publish: target != GameScreen.Battle);
                    if (target == GameScreen.Battle)
                    {
                        await warmup.WarmBattlePoolsAsync(value => Report(0.66f + value * 0.12f, "正在集结棋子与魔法…"));
                        ThrowIfDisposed();
                        PublishScreenChanged();
                    }
                },
                WaitReady = _ => presentation.WaitUntilReadyAsync(target,
                    value => Report(0.78f + value * 0.20f, target == GameScreen.Battle ? "正在铺开棋盘与手牌…" : "正在布置营地…")),
                Cleanup = async _ =>
                {
                    warmup.Dispose();
                    // 原生回收已调度，给遮挡下的编辑器一个实际更新帧。
                    await RevTask.Yield();
                },
                Reveal = async _ =>
                {
                    Report(1f, target == GameScreen.Battle ? "战场已就绪" : "营地已就绪");
                    await _loading.FadeOutAsync();
                    ThrowIfDisposed();
                },
                Recover = failure =>
                {
                    if (failure.Status != RevSceneHandoffStatus.Cancelled)
                    {
                        // 保留遮挡，玩家按返回按钮再提交独立的回退请求，原失败 ID 保持可查。
                        LastError = "加载失败：" + failure.Error.Message;
                        RevLog.Exception(failure.Error, "场景交接失败，保留遮挡并提供返回入口", "RevGame");
                        _loading?.SetData(new LoadingPanel.ProgressData(LoadProgress, LoadStep, LastError));
                    }
                    return RevTask.Completed;
                },
                BeforePublish = (handoff, status) =>
                {
                    if (ReferenceEquals(_activeWarmup, warmup))
                    {
                        _activeWarmup = null;
                    }
                    // 淡出跨帧完成后，到结案仍保留遮挡的面板实例。解除门禁和关窗放在同一同步段。
                    IsTransitioning = false;
                    if (status == RevSceneHandoffStatus.Succeeded)
                    {
                        RevUI.Close<LoadingPanel>();
                        _loading = null;
                        LastError = string.Empty;
                        if (target == GameScreen.Home && _battleHandoff != null)
                        {
                            _battleHandoff.Resources.CaptureExit(handoff.RequestId);
                            _battleHandoff = null;
                        }
                    }
                },
                ReadActualState = () => Screen.ToString()
            };
            RevSceneHandoff accepted = RevSceneHandoffExecution.Start(_sceneHandoffs, plan);
            if (accepted != null)
            {
                await accepted.WaitAsync();
            }
            else
            {
                warmup.Dispose();
            }
        }
        /// <summary>
        /// 首次启动的表/背景也纳入同一错误出口，不能只在Bootstrap打印错误然后把玩家锁在空屏。
        /// 每次await后检查流程寿命；表由全局Bootstrap在最终退出时Unload，跨场景不会重复初始化框架。
        /// </summary>
        private async RevTask LoadStartupDataAsync()
        {
            // 章节背景只在被选中时加载；大厅启动不能预热第一章，否则独立AB仍会常驻。
            // 战斗数值/说明先于业务加载并整体校验；独立剧情文案按用户要求由下方StoryBook SO加载。
            // 下面的顺序有依赖：先 12 张业务表（GameConfig 装配）→ 再文本表（GameTextConfig 校验文案占位符）
            // → 再调参表（BattleService 读远程出手频率）→ 最后剧情。任何一步失败都走同一个过场失败出口。
            // 任何一张表缺失或填错，GameConfig.Initialize 会一次性抛出中文问题清单，走下面的过场失败出口展示，而不是玩到一半才炸。
            await LoadGameConfigAsync();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<BattleTextTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<BattleTuningTable>();
            ThrowIfDisposed();
            GameTextConfig.Validate();
            BattleTuningTable tuning = RevDataTableManager.Get<BattleTuningTable>();

            if (!tuning.FindByKey("PlayerRangedActionsPerTick", out BattleTuning rate))
            {
                throw new InvalidOperationException("BattleTuning缺少我方远程攻击频率");
            }
            ((BattleService)RevServices.GetRequired<IBattleService>()).ConfigureRangedAttackRate(rate.Value);
            await LoadStoryAsync();
            ThrowIfDisposed();
            _startupComplete = true;
        }
        /// <summary>声明冷启动配置依赖，框架处理跨帧等待、缺项和失败回收。</summary>
        private async RevTask LoadGameConfigAsync()
        {
            var plan = new RevStartupPlan();
            plan.AddTable<YayaReactionRuleTable>();
            plan.AddTable<YayaReactionLineTable>();
            plan.AddTable<PieceConfigTable>();
            plan.AddTable<StarConfigTable>();
            plan.AddTable<PrepBuffConfigTable>();
            plan.AddTable<BattleEventConfigTable>();
            plan.AddTable<EventRotationConfigTable>();
            plan.AddTable<ChapterConfigTable>();
            plan.AddTable<EnemyRosterConfigTable>();
            plan.AddTable<EnemyReinforceConfigTable>();
            plan.AddTable<RewardConfigTable>();
            plan.AddTable<RewardBoostConfigTable>();
            plan.AddTable<BossSkillConfigTable>();
            plan.AddTable<BalanceRuleTable>();
            plan.Add("鸦鸦表现配置校验", () =>
            {
                YayaReactionConfig.Validate();
                return RevTask.Completed;
            }, requires: new[] { nameof(YayaReactionRuleTable), nameof(YayaReactionLineTable) });
            plan.Add("战斗配置校验与提交", () =>
            {
                // 项目保有数值、引用和文案兼容规则。全部类型化表已就绪才交付配置快照。
                GameConfig.Initialize(new GameConfigTables
                {
                    Pieces = RevDataTableManager.Get<PieceConfigTable>(),
                    Stars = RevDataTableManager.Get<StarConfigTable>(),
                    PrepBuffs = RevDataTableManager.Get<PrepBuffConfigTable>(),
                    BattleEvents = RevDataTableManager.Get<BattleEventConfigTable>(),
                    EventRotation = RevDataTableManager.Get<EventRotationConfigTable>(),
                    Chapters = RevDataTableManager.Get<ChapterConfigTable>(),
                    EnemyRoster = RevDataTableManager.Get<EnemyRosterConfigTable>(),
                    EnemyReinforce = RevDataTableManager.Get<EnemyReinforceConfigTable>(),
                    Rewards = RevDataTableManager.Get<RewardConfigTable>(),
                    RewardBoosts = RevDataTableManager.Get<RewardBoostConfigTable>(),
                    BossSkills = RevDataTableManager.Get<BossSkillConfigTable>(),
                    Balance = RevDataTableManager.Get<BalanceRuleTable>(),
                });
                return RevTask.Completed;
            }, requires: new[]
            {
                nameof(PieceConfigTable),
                nameof(StarConfigTable),
                nameof(PrepBuffConfigTable),
                nameof(BattleEventConfigTable),
                nameof(EventRotationConfigTable),
                nameof(ChapterConfigTable),
                nameof(EnemyRosterConfigTable),
                nameof(EnemyReinforceConfigTable),
                nameof(RewardConfigTable),
                nameof(RewardBoostConfigTable),
                nameof(BossSkillConfigTable),
                nameof(BalanceRuleTable)
            });
            await plan.RunAsync(_flowCancellation.Token);
        }
        /// <summary>连续通章不重载场景；背景失败仍走既有错误面板和返回流程，而非留在冻结的棋盘上。</summary>
        public async void ShowChapterBackgroundFailure(string reason)
        {
            if (_disposed || IsTransitioning || Screen != GameScreen.Battle)
            {
                return;
            }
            LastError = "章节背景失败：" + reason;
            try
            {
                LoadingPanel panel = await RevUI.OpenAsync<LoadingPanel>();

                if (_disposed || IsTransitioning || Screen != GameScreen.Battle)
                {
                    return;
                }
                _loading = panel;
                RevScope.Detach(panel);
                panel.ResetCover();
                panel.SetData(new LoadingPanel.ProgressData(0f, "背景未就绪", LastError));
            }
            catch (Exception error) { RevLog.Exception(error, "章节背景错误面板打开失败", "RevGame"); }
        }
        /// <summary>只在过场失败后可用。回退同样通过RevScene和资源屏障，不直接删UI或重初始化框架。</summary>
        public void DismissLoadingFailure()
        {
            if (_disposed || IsTransitioning || string.IsNullOrEmpty(LastError))
            {
                return;
            }
            IsTransitioning = true;
            SwitchFromButtonAsync(GameScreen.Home);
        }
        /// <summary>进度按工作量分段，单次过场内单调增长；UI通过SetData消费快照，不反过来启动业务。</summary>
        private void Report(float value, string message)
        {
            ThrowIfDisposed();
            // 0.02是新一次过场的起点，允许从上次的1重新开始。
            float next = value <= 0.02f ? value : Mathf.Max(LoadProgress, Mathf.Clamp01(value));

            // 轮询就绪时可能多帧没有新格子；不重复分配数据/重建文字/重启同一段Tween。
            if (Mathf.Approximately(next, LoadProgress) && message == LoadStep)
            {
                return;
            }
            LoadProgress = next;
            LoadStep = message;

            if (_loading != null)
            {
                _loading.SetData(new LoadingPanel.ProgressData(LoadProgress, message));
            }
        }

        /// <summary>每次跨帧返回后拒绝继续修改已退出的流程。</summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new OperationCanceledException("游戏流程已退出");
            }
        }
    }
}

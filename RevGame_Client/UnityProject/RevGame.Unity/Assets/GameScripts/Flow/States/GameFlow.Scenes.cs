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
        private readonly RevSceneHandoffTracker _sceneHandoffs = RevScene.CreateHandoffTracker();
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
        private sealed class SceneResourceQuery
        {
            /// <summary>要查询的场景交接 ID。</summary>
            public string requestId;
            /// <summary>选择 current、ready 或 exit。省略时实时采样。</summary>
            public string snapshot;
        }

        /// <summary>注册仅查询资源引用的开发态入口，随 GameFlow 释放注销。</summary>
        private void RegisterSceneResourceAutomation()
        {
            const string schema = "{\"type\":\"object\",\"properties\":{\"requestId\":{\"type\":\"string\"},"
                + "\"snapshot\":{\"type\":\"string\",\"enum\":[\"current\",\"ready\",\"exit\"]}},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.scene-handoff.resources", "按交接 ID 查询预加载来源、首屏等待、当前或历史持有者",
                schema, QuerySceneHandoffResources, readOnly: true, owner: this);
        }

        /// <summary>返回实时或历史资源快照。已淘汰的 ID 或尚未采集的边界会标记 available=false。</summary>
        private object QuerySceneHandoffResources(string json)
        {
            var query = JsonUtility.FromJson<SceneResourceQuery>(json);
            RevSceneHandoff handoff = LatestSceneHandoff;
            if (!string.IsNullOrEmpty(query.requestId)
                && (!Guid.TryParse(query.requestId, out Guid id) || !TryGetSceneHandoff(id, out handoff)))
            {
                return new Dictionary<string, object> { ["available"] = false, ["requestId"] = query.requestId };
            }
            if (handoff == null)
            {
                return new Dictionary<string, object> { ["available"] = false };
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
        /// 执行一次完整交接。调用者须先置 IsTransitioning，接单后立即可查询句柄。
        /// 旧域在业务交割时退出。成功时在解除互斥前采样本次资源与已离场战斗的引用。
        /// </summary>
        private async RevTask SwitchSceneAsync(GameScreen target)
        {
            string sceneName = target == GameScreen.Battle ? BattleSceneName : StartSceneName;
            RevSceneHandoff handoff = _sceneHandoffs.TryBegin(sceneName, Screen.ToString(), target.ToString());

            if (handoff == null)
            {
                return; // 在途请求仍拥有互斥和收尾。
            }
            if (target == GameScreen.Battle)
            {
                _battleHandoff = handoff;
            }

            RevSceneHandoffResources resources = handoff.Resources;
            var warmup = new SceneResourceWarmup();
            _activeWarmup = warmup;
            var status = RevSceneHandoffStatus.Succeeded;
            bool cleanupSucceeded = false;
            string reasonCode = null;
            string detail = null;
            try
            {
                resources.Require(RevResPath.UI_Panel_Loading, nameof(LoadingPanel), "GameFlow.LoadingCover", blocksFirstFrame: true);
                _loading = await RevUI.OpenAsync<LoadingPanel>();
                ThrowIfDisposed();
                // 面板默认归打开时的当前域；过场属于全局流程，必须显式脱离旧域，仍由RevUI管理。
                RevScope.Detach(_loading);
                _loading.ResetCover();
                Report(0.02f, target == GameScreen.Battle ? "正在展开战场…" : "正在返回营地…");
                await RevTask.Yield(); // 让遮挡层先实际画一帧，再开始资源解码与场景工作。
                ThrowIfDisposed();
                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.PreparingResources);
                if (Screen == GameScreen.Pvp)
                {
                    CloseDuelHud();
                    await RevServices.GetRequired<RevGame.Pvp.PvpService>().LeaveAsync();
                    ThrowIfDisposed();
                    // PvP 面板会淡出，再执行 OnClose。这里先归还立绘供本次切场的 FlushUnused 回收。
                    RevUI.Get<RevGame.Pvp.PvpPanel>()?.ReleaseMatchPortraits();
                }

                if (!_startupComplete)
                {
                    await LoadStartupDataAsync();
                }
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(
                    target == GameScreen.Battle ? BattleAtlases : MenuAtlases, resources);
                ThrowIfDisposed();
                Report(0.18f, "正在整理行囊…");
                await warmup.LoadAsync(target == GameScreen.Battle, resources, p => Report(0.18f + p * 0.24f, "正在集结冒险队伍…"));
                ThrowIfDisposed();

                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.LoadingScene);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                CheckDevelopmentFailure("before-scene");
#endif

                if (RevScene.CurrentName != sceneName)
                {
                    // 离开战斗时先停用视图：它们回池之后RevScene才可以安全清池。
                    // 此时旧域仍在，加载失败可重新激活旧画面；计时已由IsTransitioning暂停。
                    GamePresentation old = UnityEngine.Object.FindObjectOfType<GamePresentation>();

                    if (old != null)
                    {
                        old.SuspendBattle();
                    }
                    await RevScene.LoadAsync(sceneName, p => Report(0.42f + p * 0.23f, "正在穿越迷雾…"));
                    ThrowIfDisposed();

                    if (RevScene.CurrentName != sceneName)
                    {
                        throw new InvalidOperationException("场景未完成交接：" + sceneName + "，RevScene状态=" + RevScene.State);
                    }
                }

                // 活动场景已就位，业务仍未交付。等新场景 Start 完成订阅后才广播 ScreenChanged。
                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.PreparingGameplay);
                await RevTask.Yield();
                ThrowIfDisposed();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                CheckDevelopmentFailure("after-scene");
#endif
                GamePresentation presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();

                if (presentation == null)
                {
                    throw new InvalidOperationException("场景缺少GamePresentation接线：" + sceneName);
                }
                Report(0.66f, "正在点亮前路…");

                if (target == GameScreen.Battle)
                {
                    await warmup.WarmBattlePoolsAsync(p => Report(0.66f + p * 0.12f, "正在集结棋子与魔法…"));
                }
                ThrowIfDisposed();
                // 交割前资源/场景已确认；现在由FSM关闭旧域、打开新域，再让表现层按新模型绘制。
                _activePresentation = presentation;
                Change(target);
                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.WaitingForGameplay);
                string finalStep = target == GameScreen.Battle ? "正在铺开棋盘与手牌…" : "正在布置营地…";
                await presentation.WaitUntilReadyAsync(target, p => Report(0.78f + p * 0.20f, finalStep));
                ThrowIfDisposed();
                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.CleaningUp);
                // 旧视图/池与战斗图集退出通知已完成，新视图持有自己的引用；预热账本也必须先放手。
                // Release只把资源放进闲置表，这里在遮挡内调用门面真正卸载零引用AB，绝不force卸共享资源。
                warmup.Dispose();

                if (target != GameScreen.Battle)
                {
                    // RevScene只清空闲实例，运行中首次创建的技能池可能仍持有prefab。
                    // 此时战斗域与视图已退出，销毁整个战斗组的池，归还池本身的长期资源租约。
                    RevPool.DestroyGroup(RevResGroup.Battle);
                }
                await RevTask.Yield();
                ThrowIfDisposed();
                RevResManager.FlushUnused();
                cleanupSucceeded = true;
                _sceneHandoffs.TryAdvance(handoff, RevSceneHandoffStage.Revealing);
                Report(1f, target == GameScreen.Battle ? "战场已就绪" : "营地已就绪");
                await _loading.FadeOutAsync();
                ThrowIfDisposed();
                RevUI.Close<LoadingPanel>();
                _loading = null;
                LastError = string.Empty;
            }
            catch (Exception error)
            {
                status = _disposed ? RevSceneHandoffStatus.Cancelled : RevSceneHandoffStatus.Failed;
                reasonCode = _disposed ? "flow-disposed" : handoff.Stage switch
                {
                    RevSceneHandoffStage.LoadingScene => "scene-load-failed",
                    RevSceneHandoffStage.PreparingGameplay => "gameplay-prepare-failed",
                    RevSceneHandoffStage.WaitingForGameplay => "gameplay-readiness-failed",
                    RevSceneHandoffStage.CleaningUp => "cleanup-failed",
                    _ => "handoff-failed"
                };
                detail = error.Message;
                if (!_disposed)
                {
                    LastError = "加载失败：" + error.Message;
                    RevLog.Exception(error, "场景交接失败，保留遮挡并提供返回入口", "RevGame");

                    // 失败时绝不以100%掩盖缺失画面；按钮执行真实回退流程，不让玩家对着残缺棋盘操作。
                    if (_loading != null)
                    {
                        _loading.SetData(new LoadingPanel.ProgressData(LoadProgress, LoadStep, LastError));
                    }
                }
            }
            finally
            {
                try
                {
                    // 早期失败也释放本次预热租约。错误遮挡和仍存活的业务域由独立的返回请求收尾。
                    warmup.Dispose();
                    cleanupSucceeded |= handoff.Stage < RevSceneHandoffStage.CleaningUp;
                }
                catch (Exception error)
                {
                    status = _disposed ? RevSceneHandoffStatus.Cancelled : RevSceneHandoffStatus.Failed;
                    reasonCode = "cleanup-failed";
                    detail = string.IsNullOrEmpty(detail) ? error.Message : detail + "；预热清理失败：" + error.Message;
                    cleanupSucceeded = false;
                    RevLog.Exception(error, "场景交接预热清理失败", "RevGame");
                }
                finally
                {
                    if (ReferenceEquals(_activeWarmup, warmup))
                    {
                        _activeWarmup = null;
                    }
                    if (status == RevSceneHandoffStatus.Succeeded)
                    {
                        resources.CaptureReady();
                        if (target == GameScreen.Home && _battleHandoff != null)
                        {
                            // 快照必须晚于场景清理和资源整理。共享图集若仍由大厅持有，会保留其实际引用。
                            _battleHandoff.Resources.CaptureExit(handoff.RequestId);
                            _battleHandoff = null;
                        }
                    }
                    // RevTask 可同步唤醒等待方，必须先解除业务互斥，再冻结并发布实际 Unity 场景与业务状态。
                    IsTransitioning = false;
                    _sceneHandoffs.TryComplete(handoff, status, Screen.ToString(), cleanupSucceeded, reasonCode, detail);
                }
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
        /// <summary>
        /// 通过框架 RevDataTableManager 逐张异步加载游戏配置表（走资源系统：AB、热更、缓存与编辑器直读一致），
        /// 全部就绪后交给 GameConfig.Initialize 统一校验装配。每次 await 后检查流程寿命。
        /// 表的卸载由 GameBootstrap 在最终退出时统一完成，这里不重复卸载。
        /// </summary>
        private async RevTask LoadGameConfigAsync()
        {
            await RevDataTableManager.LoadAsync<YayaReactionRuleTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<YayaReactionLineTable>();
            ThrowIfDisposed();
            YayaReactionConfig.Validate();
            // ==================== 配置表加载（新手必读） ====================
            // 【谁负责什么】流程层（GameFlow）只决定“什么时候加载”，GameConfig 只负责“怎么校验装配”。
            //   所以这个方法里只有 LoadAsync，没有任何数值计算或默认值——数值一律来自导表。
            // 【为什么逐张 await，不并行】表之间有依赖：增益文案、Boss 技能、奖励数值都会引用 Balance 参数表。
            //   而且每张之后都会 ThrowIfDisposed()，流程中途被销毁时立刻停手，不会把数据写进已死对象。
            // 【为什么最后才 Initialize】12 张表全部到位后一次性校验，
            //   任何一张缺失或填错都会汇总成中文清单抛给上层过场展示，而不是“先玩着，玩到一半才炸”。
            // 【顺序为什么是这样】棋子/星级 → 增益/事件 → 章节/敌人 → 奖励/Boss → 参数表（Balance）最后统一读取。
            // ==============================================================
            // 棋子基础数值（PieceConfig）：血量、攻击、走法、体型。
            await RevDataTableManager.LoadAsync<PieceConfigTable>();
            ThrowIfDisposed();
            // 星级成长倍率（StarConfig）：1★→3★ 的血量与攻击缩放。
            await RevDataTableManager.LoadAsync<StarConfigTable>();
            ThrowIfDisposed();
            // 准备期增益（PrepBuffConfig）与八种战斗事件（BattleEventConfig）、以及事件轮转顺序。
            await RevDataTableManager.LoadAsync<PrepBuffConfigTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<BattleEventConfigTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<EventRotationConfigTable>();
            ThrowIfDisposed();
            // 章节结构、敌方出场名单、援军规则：决定每章“敌人是谁、几时补兵”。
            await RevDataTableManager.LoadAsync<ChapterConfigTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<EnemyRosterConfigTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<EnemyReinforceConfigTable>();
            ThrowIfDisposed();
            // 三选一奖励池与强化项（RewardConfig / RewardBoostConfig）。
            await RevDataTableManager.LoadAsync<RewardConfigTable>();
            ThrowIfDisposed();
            await RevDataTableManager.LoadAsync<RewardBoostConfigTable>();
            ThrowIfDisposed();
            // Boss 技能表（BossSkillConfig）：18 个技能的威力、蓄力、冷却与章节解锁。
            await RevDataTableManager.LoadAsync<BossSkillConfigTable>();
            ThrowIfDisposed();
            // 全局参数表（BalanceRule）：攻速、暴击、各类倍率。放最后读，因为它被上面多张表的文案引用。
            await RevDataTableManager.LoadAsync<BalanceRuleTable>();
            ThrowIfDisposed();
            // 12 张表全部到位：交给 GameConfig 统一校验并生成一份不可变快照。
            // 这里传的是“表引用”，不是拷贝；真正的校验与数值计算都在 Initialize 内部完成。
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

// ============================================================
// GameFlow.cs —— 唯一的局外流程控制者，组合 RevLightFSM + RevScope。
// Home/选关共用大厅域，进入战斗时退出大厅；结算与奖励仍属于战斗域。
// 本类不创建 GameObject：状态先开域和启动 Logic，随后广播给表现层打开界面。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;

namespace RevGame.Flow
{
    /// <summary>唯一流程服务，由常驻GameBootstrap创建/注销。FSM管理业务域，Scenes部分编排异步场景交付。</summary>
    public sealed partial class GameFlow : IDisposable
    {
        /// <summary>互斥流程状态机：大厅、教学、战斗同时只激活一个域，不把场景回调当成第二台状态机。</summary>
        private readonly RevLightFSM<FlowState> _fsm = new RevLightFSM<FlowState>();
        /// <summary>启动时创建状态实例并复用；回合变化不会反复new状态对象。</summary>
        private readonly Dictionary<GameScreen, FlowState> _states = new Dictionary<GameScreen, FlowState>();
        /// <summary>已通过解锁检查的目标章节，从1开始；直到战斗域交割时才用于BeginCampaign。</summary>
        private int _chapter = 1;
        /// <summary>已交割的业务屏幕。加载中仍可保持旧值；查询完整结果时还需看IsTransitioning。</summary>
        public GameScreen Screen { get; private set; } = GameScreen.Exit;
        /// <summary>上次拒绝/失败的人话原因；按钮与GM均读取它，成功交割后清空。</summary>
        public string LastError { get; private set; } = string.Empty;

        public GameFlow()
        {
            //这里是直接遍历注册状态
            foreach (GameScreen screen in Enum.GetValues(typeof(GameScreen)))
            {
                _states.Add(screen, new FlowState(this, screen));
            }
        }
        /// <summary>由Bootstrap唯一调用；启动本身也经过遮挡/资源/场景/画面四道就绪门禁。</summary>
        public async RevTask StartAsync()
        {
            RevEvent.AddEventListener<bool>(Events.GameFinished, OnFinished, owner: this);
            RevEvent.AddEventListener<BossDefeatedInfo>(Events.BossDefeated, OnBossDefeated, priority: 100, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnStoryPrepare, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnBossPrepare, priority: 200, owner: this);
            IsTransitioning = true;
            await SwitchSceneAsync(GameScreen.Home);

            if (!_disposed && string.IsNullOrEmpty(LastError))
            {
                BeginIntroduction();
            }
        }

        /// <summary>大厅内部切页，不切Unity场景。必须已离开过场且处于首页，否则返回false与LastError。</summary>
        public bool OpenLevels()
        {
            if (IsGameplayBlocked || Screen != GameScreen.Home)
            {
                LastError = "请从主界面进入选关";
                return false;
            }
            Change(GameScreen.LevelSelect);
            return true;
        }
        /// <summary>提交进入指定章节的异步请求。true=已接受，完成请观察IsTransitioning和Screen。</summary>
        public bool EnterBattle(int chapter)
        {
            if (IsGameplayBlocked || Screen != GameScreen.LevelSelect)
            {
                LastError = "请先打开选关界面";
                return false;
            }
            if (chapter < 1 || chapter > RevServices.GetRequired<IBattleService>().UnlockedChapter)
            {
                LastError = "这一章尚未解锁";
                return false;
            }
            _presentedChapter = 0;
            FinalBossStoryShown = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _bossDevelopmentStartPending = false;
#endif
            _chapter = chapter;
            // true只表示请求被接受；IsTransitioning变回false且Screen=Battle才表示完整交付。
            // 按钮和GM走同一入口，连点不会创建两条加载链或重复开启战斗域。
            IsTransitioning = true;
            SwitchFromButtonAsync(GameScreen.Battle);
            return true;
        }
        /// <summary>选关返回只切页，战斗返回真实卸载GameScene。过场期间拒绝重入，避免两条异步链争抢场景。</summary>
        public bool ReturnHome()
        {
            if (IsTransitioning || Screen == GameScreen.Home || Screen == GameScreen.Exit)
            {
                LastError = "当前已经离开战斗";
                return false;
            }
            CancelStory();
            CancelBossVictory();
            CancelBossIntro();
            IsTutorialActive = false;
            _tutorialReplay = false;

            if (Screen == GameScreen.LevelSelect)
            {
                Change(GameScreen.Home);
            }
            else
            {
                IsTransitioning = true;
                SwitchFromButtonAsync(GameScreen.Home);
            }
            return true;
        }
        /// <summary>
        /// 整场战斗结束后的分流。只有首次打赢时才播 "ending"；
        /// 重打最终 Boss 时 FinalBossStoryShown 已为 true，直接回首页，不重复放结局；
        /// 失败不播剧情——中途失败没有结局可放，直接回首页。
        /// </summary>
        private void OnFinished(bool won)
        {
            if (won && FinalBossStoryShown)
            {
                ReturnHome();
            }
            else

                if (won)
                {
                    PlayStory("ending", () => ReturnHome());
                }
                else
                {
                    ReturnHome();
                }
        }
        /// <summary>所有FSM交割的唯一位置；先更新状态/域，再通知视图，因此视图读到的一定是新模型。</summary>
        private void Change(GameScreen screen)
        {
            _fsm.ChangeTo(_states[screen]);
            LastError = string.Empty;
            // ★ FSM 的 OnEnter 内不再切状态；广播放在交割后，按钮回调不能触发嵌套切换。
            RevEvent.DispatchEvent<GameScreen>(Events.ScreenChanged, screen);
        }
        /// <summary>流程状态文本，给 GM 只读查询与自动化断言用：屏幕、场景、过场、加载进度，再附框架域与 Logic 数量。</summary>
        public string Snapshot()
        {
            return "流程=" + Screen + "，场景=" + RevScene.CurrentName + "，过场=" + IsTransitioning
                + "，加载进度=" + LoadProgress.ToString("P0") + "，步骤=" + LoadStep + "\n" + RevScope.Dump() + "\nLogic数量=" + RevLogic.Count;
        }
        /// <summary>
        /// 退出前的收尾，顺序不能调换：先置 _disposed 挡住后续回调，
        /// 再取消 Boss 演出与剧情、释放剧情资源和预热句柄、摘掉本对象注册的事件，
        /// 最后才 Change(Exit) 让 FSM 关闭域——域一关服务就没了，此时还在跑的演出回调会直接打到空引用上。
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            CancelBossIntro();
            _introRunner?.Dispose();
            _introRunner = null;
            CancelBossVictory();
            _victoryRunner?.Dispose();
            _victoryRunner = null;
            CancelStory();

            if (_storyHandle != null)
            {
                RevResManager.Release("Config/Story", "StoryBook");
                _storyHandle = null;
            }
            _storyBook = null;
            _activeWarmup?.Dispose();
            _activeWarmup = null;
            RevEvent.RemoveAllByOwner(this);
            Change(GameScreen.Exit);
        }
        /// <summary>一个屏幕对应一个域句柄。只关闭自己Enter拿到的域，不能ExitAll误清别人的服务。</summary>
        private sealed class FlowState : RevLightStateBase
        {
            /// <summary>外层的 GameFlow，用来读写章节号这类全流程共享的数据。</summary>
            private readonly GameFlow _owner;
            /// <summary>这个状态代表哪个屏幕（首页/选关/战斗/退出），创建时就定死，之后不会变。</summary>
            private readonly GameScreen _screen;
            /// <summary>本状态拥有的域；退出后置null，避免重复退出与复用旧句柄。</summary>
            private RevScopeHandle _scope;
            public FlowState(GameFlow owner, GameScreen screen)
            {
                _owner = owner;
                _screen = screen;
            }
            /// <summary>
            /// 切换到这个屏幕时执行一次（状态机的"进入"回调）。按顺序做四件事：
            /// ① 先更新 Screen，这样外面任何时候问"现在在哪个屏幕"，读到的都是新值；
            /// ② Exit 只是程序退出用的假屏幕，不需要开域，直接返回；
            /// ③ 开一个"域"——域是这个屏幕的逻辑容器，里面的服务和 Logic 会随域一起销毁，
            ///    所以每个屏幕自己开一个，不要共用；
            /// ④ 如果进的是战斗屏幕，再把棋局真正建起来（见下方 if 块）。
            /// </summary>
            public override void OnEnter()
            {
                _owner.Screen = _screen;

                if (_screen == GameScreen.Exit)
                {
                    return;
                }
                // 战斗屏幕用自己的战斗域，首页和选关共用大厅域。
                string domain = _screen == GameScreen.Battle ? GameDomain.Battle : GameDomain.Lobby;
                //开启对应的域
                _scope = RevScope.Enter(domain);

                if (_scope == null || !_scope.IsOpen)
                {
                    throw new InvalidOperationException("无法进入游戏域：" + domain);
                }
                if (_screen == GameScreen.Battle)
                {
                    // 进战斗屏幕要按顺序做三步：建首回合 → 配教学开局 → 启动计时逻辑。
                    // 模型先生成首回合，再启动计时Logic；过场门禁阻止这段时间消耗准备秒数。
                    var battle = RevServices.GetRequired<IBattleService>();
                    bool started;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    // 只有开发包能走这条分支：GM 可以让这一场直接以指定章节的 Boss 战开局，正式包一律正常开章。
                    bool bossStart = _owner._bossDevelopmentStartPending;
                    _owner._bossDevelopmentStartPending = false;
                    started = bossStart ? ((BattleService)battle).BeginBossCampaignForDevelopment(_owner._chapter) : battle.BeginCampaign(_owner._chapter);

#else
                    started = battle.BeginCampaign(_owner._chapter);

#endif
                    if (!started)
                    {
                        throw new InvalidOperationException(battle.LastError);
                    }
                    // 教学局要先配好引导用的初始棋面，再启动引导逻辑，顺序反了引导会拿到普通开局。
                    if (_owner.IsTutorialActive)
                    {
                        ((BattleService)battle).ConfigureTutorialOpening();

                        if (RevLogic.Start<TutorialLogic>() == null)
                        {
                            throw new InvalidOperationException("无法启动真实棋盘引导");
                        }
                    }

                    // 最后才启动 BattleLogic：它一启动就开始按秒推进准备计时，必须放在棋局建好之后。
                    if (RevLogic.Start<BattleLogic>() == null)
                    {
                        throw new InvalidOperationException("无法启动战斗域逻辑");
                    }
                }
            }
            public override void OnExit()
            {
                // RevScope负责停Logic和关闭它持有的UI。全局加载面板已经Detach，不会被这里提前关闭。
                if (_scope != null && _scope.IsOpen)
                {
                    RevScope.Exit(_scope);
                }
                _scope = null;
            }
        }
    }
}

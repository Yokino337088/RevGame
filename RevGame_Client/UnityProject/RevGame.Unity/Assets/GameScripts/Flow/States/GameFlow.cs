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
            foreach (GameScreen screen in Enum.GetValues(typeof(GameScreen)))
            {
                _states.Add(screen, new FlowState(this, screen));
            }
        }

        /// <summary>由Bootstrap唯一调用；启动本身也经过遮挡/资源/场景/画面四道就绪门禁。</summary>
        public async RevTask StartAsync()
        {
            RevEvent.AddEventListener<bool>(Events.GameFinished, OnFinished, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnStoryPrepare, owner: this);
            IsTransitioning = true;
            await SwitchSceneAsync(GameScreen.Home);
            if (!_disposed && string.IsNullOrEmpty(LastError)) { BeginIntroduction(); }
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

        private void OnFinished(bool won)
        {
            if (won) { PlayStory("ending", () => ReturnHome()); }
            else { ReturnHome(); }
        }

        /// <summary>所有FSM交割的唯一位置；先更新状态/域，再通知视图，因此视图读到的一定是新模型。</summary>
        private void Change(GameScreen screen)
        {
            _fsm.ChangeTo(_states[screen]);
            LastError = string.Empty;
            // ★ FSM 的 OnEnter 内不再切状态；广播放在交割后，按钮回调不能触发嵌套切换。
            RevEvent.DispatchEvent<GameScreen>(Events.ScreenChanged, screen);
        }

        public string Snapshot()
        {
            return "流程=" + Screen + "，场景=" + RevScene.CurrentName + "，过场=" + IsTransitioning
                + "，加载进度=" + LoadProgress.ToString("P0") + "，步骤=" + LoadStep + "\n" + RevScope.Dump() + "\nLogic数量=" + RevLogic.Count;
        }

        public void Dispose()
        {
            _disposed = true;
            CancelStory();
            if (_storyHandle != null) { RevResManager.Release("Config/Story", "StoryBook"); _storyHandle = null; }
            _storyBook = null;
            _activeWarmup?.Dispose();
            _activeWarmup = null;
            RevEvent.RemoveAllByOwner(this);
            Change(GameScreen.Exit);
        }

        /// <summary>一个屏幕对应一个域句柄。只关闭自己Enter拿到的域，不能ExitAll误清别人的服务。</summary>
        private sealed class FlowState : RevLightStateBase
        {
            private readonly GameFlow _owner;
            private readonly GameScreen _screen;
            /// <summary>本状态拥有的域；退出后置null，避免重复退出与复用旧句柄。</summary>
            private RevScopeHandle _scope;
            public FlowState(GameFlow owner, GameScreen screen)
            {
                _owner = owner;
                _screen = screen;
            }

            public override void OnEnter()
            {
                _owner.Screen = _screen;
                if (_screen == GameScreen.Exit)
                {
                    return;
                }

                string domain = _screen == GameScreen.Battle ? GameDomain.Battle : GameDomain.Lobby;
                _scope = RevScope.Enter(domain);
                if (_scope == null || !_scope.IsOpen)
                {
                    throw new InvalidOperationException("无法进入游戏域：" + domain);
                }

                if (_screen == GameScreen.Battle)
                {
                    // 模型先生成首回合，再启动计时Logic；过场门禁阻止这段时间消耗准备秒数。
                    var battle = RevServices.GetRequired<IBattleService>();
                    bool started;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
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

                    if (_owner.IsTutorialActive)
                    {
                        ((BattleService)battle).ConfigureTutorialOpening();
                        if (RevLogic.Start<TutorialLogic>() == null) { throw new InvalidOperationException("无法启动真实棋盘引导"); }
                    }
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

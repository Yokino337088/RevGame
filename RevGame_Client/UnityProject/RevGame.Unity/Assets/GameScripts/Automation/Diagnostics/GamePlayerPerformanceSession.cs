// 运行时压力夹具复用原BattleService与GameFlow，不使用Editor反射；隔离会话才允许造局。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Automation
{
    /// <summary>隔离 Player 的远征复测入口，沿正式流程开局、记录工作量并在返回大厅时回收。</summary>
    public sealed class GamePlayerPerformanceSession : IDisposable
    {
        private string _stage = "idle", _error = "";
        private int _version, _attacks;
        private bool _disposed;
        private readonly int _originalSync = QualitySettings.vSyncCount;
        private readonly int _originalLimit = Application.targetFrameRate;
        private readonly bool _benchmark;
        /// <summary>本次普通开局要使用的真实手牌数，零表示沿用密集压力局。</summary>
        private int _normalCards;
        /// <summary>开战前玩家实际拥有的棋子数，供比较自然交火的初始工作量。</summary>
        private int _initialPlayers;
        /// <summary>开战前按本章自然布阵生成的敌人数。压力局也保留实际数。</summary>
        private int _initialEnemies;

        private readonly object _loadOwner = new object();

        /// <summary>只有普通开局明确允许安卓沿正式显示预算。密集 48/81 棋继续使用原固定口径。</summary>
        public bool UseProductionAndroidBudget => !_benchmark && _normalCards > 0 && (_stage == "loading" || _stage == "ready");

        /// <summary>登记隔离测试入口。旧压力参数保持不变，普通开局另外接收章节与手牌数。</summary>
        public GamePlayerPerformanceSession()
        {
            // 仅显式隔离基准启动解除同步等待，报告必须与默认VSync运行区分。
            _benchmark = RevServices.GetRequired<StoryProgress>().IsIsolated && Array.IndexOf(Environment.GetCommandLineArgs(), "-revBenchmark") >= 0;
            if (_benchmark)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            const string options = "{\"type\":\"object\",\"properties\":{\"chapter\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6},\"pieces\":{\"type\":\"integer\",\"enum\":[48,81]}},\"required\":[\"chapter\",\"pieces\"],\"additionalProperties\":false}";
            const string normal = "{\"type\":\"object\",\"properties\":{\"chapter\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6},\"cards\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6}},\"required\":[\"chapter\",\"cards\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.performance.prepare", "隔离会话固定48/81棋持续负载；仅接受后需等state.ready", options, json =>
            {
                Options p = JsonUtility.FromJson<Options>(json);
                return StartPreparation(p.chapter, p.pieces, 0);
            }, owner: this);
            RevRuntimeAutomation.Register("game.performance.prepare-normal", "使用真实手牌和章节敌阵按正常策略开局；安卓沿正式显示预算",
                normal, json =>
            {
                NormalOptions p = JsonUtility.FromJson<NormalOptions>(json);
                return StartPreparation(p.chapter, 0, p.cards);
            }, owner: this);
            RevRuntimeAutomation.Register("game.performance.state", "只读夹具就绪、实际棋数/攻击、首页回收", empty, _ => State(), true, owner: this);
            RevRuntimeAutomation.Register("game.performance.cleanup", "取消维持负载并经GameFlow返回首页；等待state.cleaned", empty, _ =>
            {
                Cleanup(++_version).Forget();
                return State();
            }, owner: this);
        }

        /// <summary>普通复测请求。牌数受真实手牌和本轮额度约束，不直接生成棋子。</summary>
        [Serializable]
        private sealed class NormalOptions
        {
            /// <summary>正式选关入口使用的章节编号。</summary>
            public int chapter;
            /// <summary>准备阶段通过真实选牌落子入口使用的牌数。</summary>
            public int cards;
        }

        [Serializable] private sealed class Options
        {
            public int chapter, pieces;
        }
        /// <summary>只读的隔离复测状态。普通对照同时报告初始棋数、当前阶段和攻击累计值。</summary>
        [Serializable] private sealed class Status
        {
            public string stage, error, screen;
            public bool ready, isolated, transitioning, cleaned, fullscreen, benchmark;
            public string resolution, windowMode;
            public int pieces, attacks, logic, pieceRefs;
            /// <summary>是否采用正常手牌与真实交火规则。</summary>
            public bool normal;
            /// <summary>开战前玩家的实际棋数。</summary>
            public int initialPlayers;
            /// <summary>开战前本章实际生成的敌人数。</summary>
            public int initialEnemies;
            /// <summary>模型当前游玩阶段；普通局结算后不能继续当作交火样本。</summary>
            public string phase;
        }

        /// <summary>读取实战和清理状态。棋数与攻击累计均来自生产服务。</summary>
        private Status State()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            int refs = RevResManager.GetRefCount("Atlases", "PieceFrames");
            return new Status
            {
                fullscreen = Screen.fullScreen, windowMode = Screen.fullScreenMode.ToString(), resolution = Screen.width + "x" + Screen.height, benchmark = _benchmark,
                stage = _stage, error = _error, screen = flow.Screen.ToString(), ready = _stage == "ready",
                isolated = RevServices.GetRequired<StoryProgress>().IsIsolated, transitioning = flow.IsTransitioning,
                pieces = battle.PlayerPieces.Count + battle.EnemyPieces.Count, attacks = _attacks,
                normal = _normalCards > 0, initialPlayers = _initialPlayers, initialEnemies = _initialEnemies,
                phase = battle.Phase.ToString(),
                logic = RevLogic.Count, pieceRefs = refs,
                cleaned = _stage == "cleaned" && flow.Screen == GameScreen.Home && !flow.IsTransitioning && RevLogic.Count == 0 && refs == 0 && UnityEngine.Object.FindObjectOfType<BoardView>() == null
            };
        }

        /// <summary>隔离场景接单后才设置预算意图，拒绝并发开始。返回状态仍需等待正式入场。</summary>
        private object StartPreparation(int chapter, int pieces, int cards)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || _stage == "loading" || _stage == "ready" || _stage == "cleaning")
            {
                throw new InvalidOperationException("需要隔离会话且无活动压力夹具；先cleanup");
            }

            _normalCards = cards;
            _initialPlayers = _initialEnemies = 0;
            _stage = "loading";
            _error = "";
            Prepare(chapter, pieces, cards, ++_version).Forget();
            return State();
        }

        private async RevTask Wait(Func<bool> condition, int version)
        {
            double end = Time.realtimeSinceStartupAsDouble + 60;
            while (!condition())
            {
                if (_disposed || version != _version)
                {
                    throw new OperationCanceledException();
                }
                if (Time.realtimeSinceStartupAsDouble > end)
                {
                    throw new TimeoutException("压力夹具等待正式场景交付超时");
                }
                // 与Editor夹具一致，通过正式剧情入口推进章节对白；不直接修改剧情完成标记。
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                await RevTask.Yield();
            }
            if (_disposed || version != _version)
            {
                throw new OperationCanceledException();
            }
        }

        /// <summary>走正式选关、资源交接和开战。普通局使用真实手牌，密集夹具才持续补足负载。</summary>
        private async RevTask Prepare(int chapter, int pieces, int cards, int version)
        {
            try
            {
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                await Wait(() => !flow.IsTransitioning, version);
                if (flow.Screen != GameScreen.Home)
                {
                    flow.ReturnHome();
                }
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning && !flow.IsGameplayBlocked, version);
                var b = (BattleService)RevServices.GetRequired<IBattleService>();
                b.UnlockAllChaptersForDevelopment();
                if (!flow.OpenLevels() || !flow.EnterBattle(chapter))
                {
                    throw new InvalidOperationException(flow.LastError);
                }
                await Wait(() => !flow.IsTransitioning && !flow.IsGameplayBlocked && b.Phase == GamePhase.Prepare, version);
                if (cards == 0 && !b.LoadPerformanceScenario(chapter, pieces))
                {
                    throw new InvalidOperationException(b.LastError);
                }
                await Wait(() => b.PreparationGranted && !flow.IsGameplayBlocked && UnityEngine.Object.FindObjectOfType<BoardView>()?.IsReady == true, version);
                BattleLogic logic = RevLogic.Get<BattleLogic>();
                if (cards > 0)
                {
                    if (flow.IsTutorialActive)
                    {
                        throw new InvalidOperationException("普通远征对照需要先完成新手教学");
                    }
                    PlaceNormalCards(b, logic, cards);
                }
                _initialPlayers = b.PlayerPieces.Count;
                _initialEnemies = b.EnemyPieces.Count;
                _attacks = 0;
                RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, _ => _attacks++, owner: _loadOwner);
                if (!logic.BeginBattle())
                {
                    throw new InvalidOperationException(b.LastError);
                }
                if (cards == 0)
                {
                    RevTimer.Every(1f, Sustain, domain: RevTimeDomain.Unscaled, owner: _loadOwner);
                }
                _stage = "ready";
            }
            catch (Exception e)
            {
                if (version == _version)
                {
                    _stage = "failed";
                    _error = e.ToString();
                    ReleaseLoad();
                }
            }
        }

        /// <summary>沿用前排接敌、远程留后排的已登记策略，只提交真实选牌和棋盘点击。</summary>
        private static void PlaceNormalCards(IBattleService battle, BattleLogic logic, int cards)
        {
            for (int index = 0; index < cards; index++)
            {
                if (!GameBalanceTactics.ChoosePlacement(battle, false, out int cardId, out BoardPos cell, out string reason)
                    || !logic.SelectCard(cardId) || !logic.ClickBoard(cell))
                {
                    throw new InvalidOperationException("普通远征布阵未能完成：" + reason + " " + battle.LastError);
                }
            }
        }

        private void Sustain()
        {
            ((BattleService)RevServices.GetRequired<IBattleService>()).SustainPerformanceScenario();
        }

        private void ReleaseLoad()
        {
            RevTimer.CancelAllOf(_loadOwner);
            RevEvent.RemoveAllByOwner(_loadOwner);
        }

        private async RevTask Cleanup(int version)
        {
            _stage = "cleaning";
            ReleaseLoad();
            try
            {
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                await Wait(() => !flow.IsTransitioning, version);
                if (flow.Screen != GameScreen.Home && !flow.ReturnHome())
                {
                    throw new InvalidOperationException(flow.LastError);
                }
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, version);
                _stage = "cleaned";
            }
            catch (Exception e)
            {
                if (version == _version)
                {
                    _stage = "failed";
                    _error = e.ToString();
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_benchmark)
            {
                QualitySettings.vSyncCount = _originalSync;
                Application.targetFrameRate = _originalLimit;
            }
            _version++;
            ReleaseLoad();
            RevRuntimeAutomation.UnregisterAllOf(this);
        }
    }
}
#endif

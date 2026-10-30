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
    public sealed class GamePlayerPerformanceSession : IDisposable
    {
        private string _stage = "idle", _error = "";
        private int _version, _attacks;
        private bool _disposed;
        private readonly object _loadOwner = new object();

        public GamePlayerPerformanceSession()
        {
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            const string options = "{\"type\":\"object\",\"properties\":{\"chapter\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6},\"pieces\":{\"type\":\"integer\",\"enum\":[48,81]}},\"required\":[\"chapter\",\"pieces\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.performance.prepare", "隔离会话固定48/81棋持续负载；仅接受后需等state.ready", options, json =>
            {
                if (!RevServices.GetRequired<StoryProgress>().IsIsolated || _stage == "loading" || _stage == "ready" || _stage == "cleaning")
                {
                    throw new InvalidOperationException("需要隔离会话且无活动压力夹具；先cleanup");
                }
                var p = JsonUtility.FromJson<Options>(json);
                _stage = "loading";
                _error = "";
                Prepare(p, ++_version).Forget();
                return State();
            }, owner: this);
            RevRuntimeAutomation.Register("game.performance.state", "只读夹具就绪、实际棋数/攻击、首页回收", empty, _ => State(), true, owner: this);
            RevRuntimeAutomation.Register("game.performance.cleanup", "取消维持负载并经GameFlow返回首页；等待state.cleaned", empty, _ =>
            {
                Cleanup(++_version).Forget();
                return State();
            }, owner: this);
        }

        [Serializable] private sealed class Options
        {
            public int chapter, pieces;
        }
        [Serializable] private sealed class Status
        {
            public string stage, error, screen;
            public bool ready, isolated, transitioning, cleaned;
            public int pieces, attacks, logic, pieceRefs;
        }

        private Status State()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            int refs = RevResManager.GetRefCount("Atlases", "PieceFrames");
            return new Status
            {
                stage = _stage, error = _error, screen = flow.Screen.ToString(), ready = _stage == "ready",
                isolated = RevServices.GetRequired<StoryProgress>().IsIsolated, transitioning = flow.IsTransitioning,
                pieces = battle.PlayerPieces.Count + battle.EnemyPieces.Count, attacks = _attacks,
                logic = RevLogic.Count, pieceRefs = refs,
                cleaned = _stage == "cleaned" && flow.Screen == GameScreen.Home && !flow.IsTransitioning && RevLogic.Count == 0 && refs == 0 && UnityEngine.Object.FindObjectOfType<BoardView>() == null
            };
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

        private async RevTask Prepare(Options p, int version)
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
                if (!flow.OpenLevels() || !flow.EnterBattle(p.chapter))
                {
                    throw new InvalidOperationException(flow.LastError);
                }
                await Wait(() => !flow.IsTransitioning && !flow.IsGameplayBlocked && b.Phase == GamePhase.Prepare, version);
                if (!b.LoadPerformanceScenario(p.chapter, p.pieces))
                {
                    throw new InvalidOperationException(b.LastError);
                }
                await Wait(() => b.PreparationGranted && !flow.IsGameplayBlocked && UnityEngine.Object.FindObjectOfType<BoardView>()?.IsReady == true, version);
                _attacks = 0;
                RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, _ => _attacks++, owner: _loadOwner);
                if (!RevLogic.Get<BattleLogic>().BeginBattle())
                {
                    throw new InvalidOperationException(b.LastError);
                }
                RevTimer.Every(1f, Sustain, domain: RevTimeDomain.Unscaled, owner: _loadOwner);
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
            _version++;
            ReleaseLoad();
            RevRuntimeAutomation.UnregisterAllOf(this);
        }
    }
}
#endif

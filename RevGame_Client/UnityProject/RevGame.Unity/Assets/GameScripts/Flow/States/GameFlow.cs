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
    public sealed class GameFlow : IDisposable
    {
        private readonly RevLightFSM<FlowState> _fsm = new RevLightFSM<FlowState>();
        private readonly Dictionary<GameScreen, FlowState> _states = new Dictionary<GameScreen, FlowState>();
        private int _chapter = 1;
        public GameScreen Screen { get; private set; } = GameScreen.Exit;
        public string LastError { get; private set; } = string.Empty;

        public GameFlow()
        {
            foreach (GameScreen screen in Enum.GetValues(typeof(GameScreen)))
            {
                _states.Add(screen, new FlowState(this, screen));
            }
        }

        public void Start()
        {
            Change(GameScreen.Home);
            RevEvent.AddEventListener<bool>(Events.GameFinished, OnFinished, owner: this);
        }

        public bool OpenLevels()
        {
            if (Screen != GameScreen.Home)
            {
                LastError = "请从主界面进入选关";
                return false;
            }

            Change(GameScreen.LevelSelect);
            return true;
        }

        public bool EnterBattle(int chapter)
        {
            if (Screen != GameScreen.LevelSelect)
            {
                LastError = "请先打开选关界面";
                return false;
            }

            if (chapter < 1 || chapter > RevServices.GetRequired<IBattleService>().UnlockedChapter)
            {
                LastError = "这一章尚未解锁";
                return false;
            }

            _chapter = chapter;
            Change(GameScreen.Battle);
            return true;
        }

        public bool ReturnHome()
        {
            if (Screen == GameScreen.Home || Screen == GameScreen.Exit)
            {
                LastError = "当前已经离开战斗";
                return false;
            }

            Change(GameScreen.Home);
            return true;
        }

        private void OnFinished(bool won)
        {
            ReturnHome();
        }

        private void Change(GameScreen screen)
        {
            _fsm.ChangeTo(_states[screen]);
            LastError = string.Empty;
            // ★ FSM 的 OnEnter 内不再切状态；广播放在交割后，按钮回调不能触发嵌套切换。
            RevEvent.DispatchEvent<GameScreen>(Events.ScreenChanged, screen);
        }

        public string Snapshot()
        {
            return "流程=" + Screen + "\n" + RevScope.Dump() + "\nLogic数量=" + RevLogic.Count;
        }

        public void Dispose()
        {
            RevEvent.RemoveAllByOwner(this);
            Change(GameScreen.Exit);
        }

        private sealed class FlowState : RevLightStateBase
        {
            private readonly GameFlow _owner;
            private readonly GameScreen _screen;
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
                    var battle = RevServices.GetRequired<IBattleService>();
                    if (!battle.BeginCampaign(_owner._chapter))
                    {
                        throw new InvalidOperationException(battle.LastError);
                    }

                    if (RevLogic.Start<BattleLogic>() == null)
                    {
                        throw new InvalidOperationException("无法启动战斗域逻辑");
                    }
                }
            }

            public override void OnExit()
            {
                if (_scope != null && _scope.IsOpen)
                {
                    RevScope.Exit(_scope);
                }

                _scope = null;
            }
        }
    }
}

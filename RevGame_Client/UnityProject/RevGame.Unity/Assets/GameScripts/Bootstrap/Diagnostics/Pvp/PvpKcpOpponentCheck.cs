#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>独立Scene/Session的真实KCP对手，只通过服务器操作，不共享玩家棋盘或Runtime.Session。</summary>
    internal sealed class PvpKcpOpponentCheck : IDisposable
    {
        private readonly string _rules;
        private readonly int _komi;
        private readonly bool _longGame;
        private Scene _scene;
        private Session _session;
        private bool _disposed;
        private bool _connected;
        private string _error = "";
        private int _sequence;
        private string _hash = "";
        private string _room = "";

        internal PvpKcpOpponentCheck(string rules, int komi, bool longGame = false)
        {
            _rules = rules;
            _komi = komi;
            _longGame = longGame;
        }

        internal void Start()
        {
            Run().Coroutine();
        }

        internal object State()
        {
            return new Dictionary<string, object>
            {
                ["matchesPlayer"] = _sequence == RevServices.GetRequired<PvpService>().Core?.Sequence && _hash == RevServices.GetRequired<PvpService>().Core?.StateHash.ToString(),
                ["running"] = !_disposed, ["connected"] = _connected, ["error"] = _error,
                ["sequence"] = _sequence, ["stateHash"] = _hash, ["roomId"] = _room, ["transport"] = "Fantasy.KCP"
            };
        }

        private async FTask Run()
        {
            try
            {
                // 主客户端已初始化Fantasy；独立Scene的销毁只收走测试对手，不关闭全局Runtime。
                _scene = await Scene.Create();
                if (_disposed)
                {
                    _scene.Dispose();
                    return;
                }
                _session = _scene.Connect("127.0.0.1:20000", NetworkProtocolType.KCP,
                    () => _connected = true, () => _error = "KCP连接失败", () => _connected = false, false);
                float deadline = UnityEngine.Time.realtimeSinceStartup + 300;
                while (!_connected && !_disposed && _error.Length == 0 && UnityEngine.Time.realtimeSinceStartup < deadline)
                {
                    await RevTask.Delay(100);
                }
                if (_disposed || !_connected)
                {
                    return;
                }
                using (var hello = await _session.C2G_GoHelloRequest("revgo-1", "KCP验收对手", "", _rules, "rookie"))
                {
                    Require(hello.ErrorCode == 0, hello.Reason);
                }
                using (var match = await _session.C2G_GoMatchRequest(false))
                {
                    Require(match.ErrorCode == 0, match.Reason);
                }
                while (!_disposed && _connected && UnityEngine.Time.realtimeSinceStartup < deadline)
                {
                    using var sync = await _session.C2G_GoSyncRequest();
                    Require(sync.ErrorCode == 0, sync.Reason);
                    var state = sync.State;
                    if (state != null && state.RoomId.Length > 0)
                    {
                        var core = new GoCore(_komi);
                        foreach (var move in state.Commands)
                        {
                            Require(core.StateHash.ToString() == move.PreviousHash, "重放前指纹不一致");
                            Require(core.TryApply((GoAction)move.Kind, (GoSide)move.Side, move.X, move.Y, out _), "规则重放拒绝");
                            Require(core.StateHash.ToString() == move.ResultHash, "重放后指纹不一致");
                        }
                        _sequence = core.Sequence;
                        _hash = core.StateHash.ToString();
                        _room = state.RoomId;
                        GoSide side = (GoSide)state.MySide;
                        bool pass = state.Phase == "playing" && core.Phase == GoPhase.Playing && core.Turn == side;
                        bool confirm = state.Phase == "playing" && core.Phase == GoPhase.Scoring && !core.HasConfirmed(side)
                            && (side == GoSide.Black || core.HasConfirmed(GoSide.Black));
                        if (pass || confirm)
                        {
                            string before = core.StateHash.ToString();
                            GoAction action = pass ? GoAction.Pass : GoAction.ConfirmScore;
                            int x = -1, y = -1;
                            if (pass && _longGame && core.MoveNumber < 60)
                            {
                                for (int point = 0; point < GoCore.PointCount; point++)
                                {
                                    if (core.Clone().TryPlay(side, point % 9, point / 9, out _))
                                    {
                                        action = GoAction.Play;
                                        x = point % 9;
                                        y = point / 9;
                                        break;
                                    }
                                }
                            }
                            Require(core.TryApply(action, side, x, y, out _), "对手动作拒绝");
                            var command = new GoRelayCommand { Sequence = core.Sequence, Side = (int)side, Kind = (int)action,
                                X = x, Y = y, ElapsedMs = 0, Payload = "", PreviousHash = before, ResultHash = core.StateHash.ToString() };
                            using var response = await _session.C2G_GoCommandRequest(_room, command);
                            Require(response.ErrorCode == 0, response.Reason);
                        }
                    }
                    // 只调度开发态对手轮询，不改变生产计时配置；失败/退出由Dispose令旧循环失效。
                    await RevTask.Delay(300);
                }
            }
            catch (Exception error)
            {
                if (!_disposed)
                {
                    _error = error.Message;
                    RevLog.Warn("KCP对手检查失败：" + _error, "Pvp");
                }
            }
            finally
            {
                Dispose();
            }
        }

        private static void Require(bool value, string reason)
        {
            if (!value)
            {
                throw new InvalidOperationException(reason);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _connected = false;
            _scene?.Dispose();
            _scene = null;
            _session = null;
        }
    }
}
#endif

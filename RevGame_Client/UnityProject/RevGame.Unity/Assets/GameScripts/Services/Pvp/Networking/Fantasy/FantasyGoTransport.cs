using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO.Pipelines;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using UnityEngine;

namespace RevGame.Pvp.Networking
{
    /// <summary>
    /// 把 Fantasy 的 FTask、Session 与协议隔离在网络边界内。
    /// 玩法只接收完整操作快照和错误事件，不依赖 Fantasy 全局状态。
    /// 此工程目前只有一个在线围棋连接，Runtime 的所有者就是这个适配器；
    /// 退出时释放自己的连接/Scene，不关闭 Revolution，不影响单机和局域网模式。
    /// </summary>
    public sealed class FantasyGoTransport : IDisposable
    {
        public const string ProtocolVersion = "revgo-1";
        private const float RequestTimeoutSeconds = 6f;
        private const float HeartbeatIntervalSeconds = 2f;
        internal static FantasyGoTransport Active;
        private Session _session;
        private string _nickname;
        private string _rulesJson;
        private string _matchKind;
        private string _roomId = "";
        private int _generation;
        private float _deadline;
        private float _heartbeatAt;
        private bool _disposed;

        /// <summary>快照已脱离 Fantasy 消息池，调用者可以保存并从头重放。</summary>
        public event Action<GoRelayState> StateReceived;
        public event Action<string> Error;
        /// <summary>连接仍然有效的操作拒绝；不能把“正忙/序列冲突”误报成整条连接断开。</summary>
        public event Action<string> RequestRejected;
        public bool Connected { get; private set; }
        public bool Busy { get; private set; }
        public string ResumeToken { get; private set; } = "";

        /// <summary>只在用户进入服务器模式后调用；失败不阻断游戏启动。</summary>
        public void Connect(string endpoint, string nickname, string rulesJson, string matchKind = "rookie", string resumeToken = "")
        {
            Disconnect(false);
            _disposed = false;
            if (Active != null && Active != this)
            {
                Error?.Invoke("已有一个在线围棋连接，请先退出原对局。");
                return;
            }
            if (!TryEndpoint(endpoint, out string host, out int port))
            {
                Error?.Invoke("服务器地址格式不正确，请填写 IP:端口，例如 127.0.0.1:20000。");
                return;
            }
            Active = this;
            _nickname = nickname;
            _rulesJson = rulesJson;
            _matchKind = matchKind;
            ResumeToken = resumeToken ?? "";
            Busy = true;
            _deadline = Time.realtimeSinceStartup + RequestTimeoutSeconds;
            DiagnoseSocketReceive().Coroutine();
            ConnectAsync(host, port, _generation).Coroutine();
        }

        private async FTask ConnectAsync(string host, int port, int generation)
        {
            try
            {
                // 禁用框架自带心跳，使用业务心跳同时验证身份是否过期。
                _session = await Runtime.Connect(host, port, FantasyRuntime.NetworkProtocolType.TCP, false, 5000, false, 0, 0, 0, 0,
                    () => OnSocketConnected(generation),
                    () => Fail(generation, "暂时无法连接对战服务器。你仍可游玩远征、无尽或局域网对战。"),
                    () => Fail(generation, "服务器连接已中断，可重连恢复对局；其它游戏功能不受影响。"));
                if (generation != _generation || _disposed)
                {
                    _session?.Dispose();
                }
            }
            catch (Exception exception)
            {
                Fail(generation, "对战连接失败：" + exception.Message);
            }
        }

        private void OnSocketConnected(int generation)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }
            // Runtime 已持有连接；TCP 完成回调可能早于 ConnectAsync 赋值语句。
            _session = Runtime.Session;
            HelloAsync(generation).Coroutine();
        }

        private async FTask HelloAsync(int generation)
        {
            try
            {
                using var response = await _session.C2G_GoHelloRequest(ProtocolVersion, _nickname, ResumeToken, _rulesJson, _matchKind);
                if (!IsCurrent(generation))
                {
                    return;
                }
                Busy = false;
                if (response.ErrorCode != 0)
                {
                    if (response.ErrorCode == 4)
                    {
                        // 服务进程重启或保留期结束后，旧令牌已不存在；下一次用户点击连接可建新身份。
                        ResumeToken = "";
                    }
                    Fail(generation, response.Reason);
                    return;
                }
                ResumeToken = response.Token;
                Connected = true;
                _heartbeatAt = Time.realtimeSinceStartup + HeartbeatIntervalSeconds;
                Receive(response.State);
            }
            catch (Exception exception)
            {
                Fail(generation, "对战握手失败：" + exception.Message);
            }
        }

        public void Match(bool cancel = false)
        {
            StartRequest(cancel ? 2 : 1, null);
        }

        public void Send(GoRelayCommand command)
        {
            StartRequest(3, command);
        }

        public void Sync()
        {
            StartRequest(4, null);
        }

        public void Leave()
        {
            StartRequest(5, null);
        }

        private void StartRequest(int operation, GoRelayCommand command)
        {
            if (!Connected || _session == null || _session.IsDisposed || _disposed)
            {
                Error?.Invoke("尚未连接对战服务器。");
                return;
            }
            if (Busy)
            {
                RequestRejected?.Invoke("正在等待服务器回应，请稍后操作。");
                return;
            }
            Busy = true;
            _deadline = Time.realtimeSinceStartup + RequestTimeoutSeconds;
            RequestAsync(operation, command, _generation).Coroutine();
        }

        /// <summary>每帧由 PvP 服务调用；使用不受暂停影响的时间，不占用单机战斗时钟。</summary>
        public void Poll()
        {
            if (_disposed)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (Busy && now >= _deadline)
            {
                Fail(_generation, "对战服务器回应超时。可返回大厅或重连；其它游戏功能不受影响。");
                return;
            }
            if (Connected && !Busy && now >= _heartbeatAt)
            {
                _heartbeatAt = now + HeartbeatIntervalSeconds;
                StartRequest(6, null);
            }
        }

        private async FTask RequestAsync(int operation, GoRelayCommand command, int generation)
        {
            uint code = 0;
            string reason = "";
            GoRelayState state = null;
            try
            {
                // 每个响应都在作用域结束归还；先复制快照，禁止把池消息直接交给长寿命服务。
                switch (operation)
                {
                    case 1:
                    case 2:
                        using (var response = await _session.C2G_GoMatchRequest(operation == 2))
                        {
                            code = response.ErrorCode;
                            reason = response.Reason;
                            state = response.State?.DeepCopy();
                        }
                        break;
                    case 3:
                        using (var response = await _session.C2G_GoCommandRequest(_roomId, command?.DeepCopy()))
                        {
                            code = response.ErrorCode;
                            reason = response.Reason;
                            state = response.State?.DeepCopy();
                        }
                        break;
                    case 4:
                        using (var response = await _session.C2G_GoSyncRequest())
                        {
                            code = response.ErrorCode;
                            reason = response.Reason;
                            state = response.State?.DeepCopy();
                        }
                        break;
                    case 5:
                        using (var response = await _session.C2G_GoLeaveRequest())
                        {
                            code = response.ErrorCode;
                            reason = response.Reason;
                            state = response.State?.DeepCopy();
                        }
                        break;
                    case 6:
                        using (var response = await _session.C2G_GoHeartbeatRequest())
                        {
                            code = response.ErrorCode;
                            reason = response.Reason;
                        }
                        break;
                }
                if (!IsCurrent(generation))
                {
                    return;
                }
                Busy = false;
                if (state != null)
                {
                    Receive(state);
                }
                if (code != 0)
                {
                    RequestRejected?.Invoke(string.IsNullOrEmpty(reason) ? "服务器拒绝了本次请求。" : reason);
                }
            }
            catch (Exception exception)
            {
                Fail(generation, "服务器通信中断：" + exception.Message);
            }
        }

        internal void Receive(Session session, GoRelayState state)
        {
            if (_session == session && !_disposed)
            {
                Receive(state);
            }
        }

        private void Receive(GoRelayState state)
        {
            if (state == null)
            {
                return;
            }
            _roomId = state.RoomId ?? "";
            StateReceived?.Invoke(state.DeepCopy());
        }

        private bool IsCurrent(int generation)
        {
            return generation == _generation && !_disposed;
        }

        private void Fail(int generation, string reason)
        {
            if (!IsCurrent(generation))
            {
                return;
            }
            // 先让旧回调过期，再Dispose会话；否则断开回调会重复报告同一次故障。
            Disconnect(false);
            Error?.Invoke(reason);
        }

        private void Disconnect(bool dispose)
        {
            ++_generation;
            Connected = false;
            Busy = false;
            _disposed = dispose;
            if (Active == this)
            {
                Active = null;
                Runtime.OnDestroy();
            }
            _session = null;
            _roomId = "";
        }

        public void Dispose()
        {
            Disconnect(true);
            StateReceived = null;
            Error = null;
            RequestRejected = null;
        }

        private static bool TryEndpoint(string endpoint, out string host, out int port)
        {
            host = "";
            port = 20000;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return false;
            }
            var value = endpoint.Trim();
            int separator = value.LastIndexOf(':');
            if (separator >= 0)
            {
                if (!int.TryParse(value.Substring(separator + 1), out port) || port < 1 || port > 65535)
                {
                    return false;
                }
                host = value.Substring(0, separator);
            }
            else
            {
                host = value;
            }
            return host.Length > 0 && host.Length <= 253;
        }
    }
}

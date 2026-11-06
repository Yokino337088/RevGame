using System;
using System.Text;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace RevGame.Pvp.Networking
{
    public sealed partial class LanPeerTransport
    {
        /// <summary>UTP 内部信封；票据绑定刚才接受的邀请，不能把陌生人的连接直接变成对局。</summary>
        [Serializable]
        private sealed class WirePacket
        {
            public int version;
            public string kind;
            public string id;
            public string target;
            public string ticket;
            public string body;
        }

        private bool OpenDriver(bool host, string address, int port, out string reason)
        {
            try
            {
                var settings = new NetworkSettings();
                try
                {
                    // 对局全量序列可能超过一个 UDP 包；先分片，再可靠有序，两个角色必须使用完全相同的流水线顺序。
                    settings.WithFragmentationStageParameters(payloadCapacity: MaxWireBytes);
                    settings.WithNetworkConfigParameters(disconnectTimeoutMS: (int)(_options.ConnectTimeoutSeconds * 1000));
                    _driver = NetworkDriver.Create(settings);
                }
                finally
                {
                    settings.Dispose();
                }
                _reliable = _driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
                if (_driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(0)) != 0)
                {
                    _driver.Dispose();
                    reason = "无法打开局域网对局端口，请检查网络或防火墙。";
                    LastError = reason;
                    return false;
                }
                _transportPort = _driver.GetLocalEndpoint().Port;
                if (host)
                {
                    if (_driver.Listen() != 0)
                    {
                        _driver.Dispose();
                        reason = "局域网监听启动失败。";
                        LastError = reason;
                        return false;
                    }
                }
                else
                {
                    if (!NetworkEndpoint.TryParse(address, (ushort)port, out NetworkEndpoint endpoint))
                    {
                        _driver.Dispose();
                        reason = "对方的局域网地址无效。";
                        LastError = reason;
                        return false;
                    }
                    _connection = _driver.Connect(endpoint);
                }
                reason = string.Empty;
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            {
                if (_driver.IsCreated)
                {
                    _driver.Dispose();
                }
                reason = "无法创建局域网连接：" + error.Message;
                LastError = reason;
                return false;
            }
        }

        private void PollDriver()
        {
            if (!_driver.IsCreated)
            {
                return;
            }
            _driver.ScheduleUpdate().Complete();
            if (IsHost)
            {
                for (int count = 0; count < MaxPacketsPerPoll; count++)
                {
                    NetworkConnection candidate = _driver.Accept();
                    if (!candidate.IsCreated)
                    {
                        break;
                    }
                    NetworkEndpoint remote = _driver.GetRemoteEndpoint(candidate);
                    // UTP 的 Address 实际包含端口，不能拿它与纯 IP 字符串比较；用对端源端口构造期望端点再比较。
                    bool expectedAddress = _expectedPeer != null && NetworkEndpoint.TryParse(_expectedPeer.Address, remote.Port, out NetworkEndpoint expected) && remote.Equals(expected);
                    if (!expectedAddress || _connection.IsCreated)
                    {
                        _driver.Disconnect(candidate);
                        continue;
                    }
                    _connection = candidate;
                }
            }
            if (!_connection.IsCreated)
            {
                return;
            }
            for (int count = 0; count < MaxPacketsPerPoll && _driver.IsCreated; count++)
            {
                NetworkEvent.Type kind = _driver.PopEventForConnection(_connection, out var reader);
                if (kind == NetworkEvent.Type.Empty)
                {
                    break;
                }
                if (kind == NetworkEvent.Type.Disconnect)
                {
                    ResetConnection("局域网连接已断开，对局已暂停；可返回大厅或重新邀请。", true);
                    return;
                }
                if (kind == NetworkEvent.Type.Connect && !IsHost)
                {
                    QueueAuthentication();
                }
                else if (kind == NetworkEvent.Type.Data)
                {
                    if (reader.Length <= 0 || reader.Length > MaxWireBytes)
                    {
                        ResetConnection("收到长度无效的局域网消息，连接已关闭。", true);
                        return;
                    }
                    byte[] bytes = new byte[reader.Length];
                    for (int index = 0; index < bytes.Length; index++)
                    {
                        bytes[index] = reader.ReadByte();
                    }
                    HandleWire(bytes);
                }
            }
            if (_driver.IsCreated && _connection.IsCreated)
            {
                FlushOutbox();
            }
        }

        private void QueueAuthentication()
        {
            if (_sentAuthentication)
            {
                return;
            }
            _sentAuthentication = true;
            QueueWire("auth", string.Empty);
        }

        private bool QueueWire(string kind, string payload)
        {
            var packet = new WirePacket
            {
                version = ProtocolVersion,
                kind = kind,
                id = LocalId,
                target = _expectedPeer?.Id,
                ticket = _ticket,
                body = payload
            };
            byte[] bytes = _utf8.GetBytes(JsonUtility.ToJson(packet));
            if (bytes.Length > MaxWireBytes || _outbox.Count >= MaxQueuedMessages)
            {
                return false;
            }
            _outbox.Enqueue(bytes);
            return true;
        }

        private void HandleWire(byte[] bytes)
        {
            WirePacket packet;
            try
            {
                packet = JsonUtility.FromJson<WirePacket>(_utf8.GetString(bytes));
            }
            catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException)
            {
                ResetConnection("收到损坏的局域网消息，连接已关闭。", true);
                return;
            }
            if (packet == null || _expectedPeer == null || packet.version != ProtocolVersion || packet.id != _expectedPeer.Id ||
                packet.target != LocalId || packet.ticket != _ticket)
            {
                // 陌生连接不能消耗邀请本身：房主仍监听，真正的受邀者可继续在原超时内连接。
                if (IsHost && !IsConnected)
                {
                    _driver.Disconnect(_connection);
                    _connection = default;
                    return;
                }
                ResetConnection("局域网邀请校验失败，请重新邀请。", true);
                return;
            }
            if (packet.kind == "auth" && IsHost && !IsConnected)
            {
                QueueWire("welcome", string.Empty);
                CompleteConnection();
            }
            else if (packet.kind == "welcome" && !IsHost && !IsConnected)
            {
                CompleteConnection();
            }
            else if (packet.kind == "data" && IsConnected)
            {
                int payloadBytes;
                try
                {
                    payloadBytes = string.IsNullOrEmpty(packet.body) ? 0 : _utf8.GetByteCount(packet.body);
                }
                catch (EncoderFallbackException)
                {
                    ResetConnection("对方的局域网文字编码无效，连接已关闭。", true);
                    return;
                }
                if (payloadBytes <= 0 || payloadBytes > MaxPayloadBytes)
                {
                    ResetConnection("对方发送了超长或空白的局域网消息。", true);
                    return;
                }
                PayloadReceived?.Invoke(packet.body);
            }
        }

        private void CompleteConnection()
        {
            IsConnected = true;
            IncomingInvitation = null;
            LastError = string.Empty;
            Status = "已与 " + _expectedPeer.Name + " 建立局域网直连";
            Changed?.Invoke();
            Connected?.Invoke(IsHost);
        }

        private void FlushOutbox()
        {
            // 拥塞时保留队首，不丢失、不越过。只有 UTP 明确接受后才出队，拒绝原因则通过断线出口报告。
            for (int count = 0; count < MaxPacketsPerPoll && _outbox.Count > 0; count++)
            {
                byte[] bytes = _outbox.Peek();
                int status = _driver.BeginSend(_reliable, _connection, out var writer, bytes.Length);
                if (status == -5)
                {
                    return;
                }
                if (status != 0)
                {
                    ResetConnection("局域网发送失败（" + status + "），请重新连接。", true);
                    return;
                }
                if (writer.Capacity < bytes.Length)
                {
                    _driver.AbortSend(writer);
                    ResetConnection("局域网消息超出传输容量。", true);
                    return;
                }
                for (int index = 0; index < bytes.Length; index++)
                {
                    writer.WriteByte(bytes[index]);
                }
                status = _driver.EndSend(writer);
                if (status == -5)
                {
                    return;
                }
                if (status < 0)
                {
                    ResetConnection("局域网发送失败（" + status + "），请重新连接。", true);
                    return;
                }
                _outbox.Dequeue();
            }
        }

        private void ResetConnection(string status, bool notify)
        {
            if (_driver.IsCreated)
            {
                if (_connection.IsCreated)
                {
                    _driver.Disconnect(_connection);
                    _driver.ScheduleUpdate().Complete();
                }
                _driver.Dispose();
            }
            _connection = default;
            _expectedPeer = null;
            _ticket = null;
            _transportPort = 0;
            _outbox.Clear();
            _sentAuthentication = false;
            _acceptedInvitation = false;
            IncomingInvitation = null;
            IsConnected = false;
            IsHost = false;
            Status = status;
            _nextAnnounce = 0;
            if (notify)
            {
                LastError = status;
            }
            Changed?.Invoke();
            if (notify)
            {
                Disconnected?.Invoke(status);
            }
        }
    }
}

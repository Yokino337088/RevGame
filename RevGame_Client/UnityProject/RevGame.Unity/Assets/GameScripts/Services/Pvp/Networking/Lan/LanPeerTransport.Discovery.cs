using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace RevGame.Pvp.Networking
{
    public sealed partial class LanPeerTransport
    {
        /// <summary>发现包只携带会话标识、显示名和临时端口，绝不携带完整棋谱、账号凭据或硬件标识。</summary>
        [Serializable]
        private sealed class DiscoveryPacket
        {
            public string magic;
            public int version;
            public string kind;
            public string id;
            public string target;
            public string name;
            public int controlPort;
            public int transportPort;
            public string ticket;
            public bool busy;
        }

        private void RefreshBroadcasts()
        {
            _broadcasts.Clear();
            AddBroadcast(IPAddress.Broadcast);
            // 每张启用的 IPv4 网卡都发送定向广播，不能只取“第一张网卡”，它可能是 VPN 或虚拟机适配器。
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null || IPAddress.IsLoopback(address.Address))
                    {
                        continue;
                    }
                    byte[] ip = address.Address.GetAddressBytes();
                    byte[] mask = address.IPv4Mask.GetAddressBytes();
                    for (int index = 0; index < ip.Length; index++)
                    {
                        ip[index] = (byte)(ip[index] | ~mask[index]);
                    }
                    AddBroadcast(new IPAddress(ip));
                }
            }
            // 同机两客户端可借回环广播发现彼此，正式邀请仍走各自独占控制端口。
            AddBroadcast(IPAddress.Parse("127.255.255.255"));
        }

        private void AddBroadcast(IPAddress address)
        {
            if (_broadcasts.Exists(endpoint => endpoint.Address.Equals(address)))
            {
                return;
            }
            _broadcasts.Add(new IPEndPoint(address, _options.DiscoveryPort));
        }

        private DiscoveryPacket MakePacket(string kind, string target, string ticket)
        {
            return new DiscoveryPacket
            {
                magic = Magic,
                version = ProtocolVersion,
                kind = kind,
                id = LocalId,
                target = target,
                name = _name,
                controlPort = LocalControlPort,
                transportPort = _transportPort,
                ticket = ticket,
                busy = _expectedPeer != null || IsConnected
            };
        }

        private void Broadcast()
        {
            byte[] bytes = _utf8.GetBytes(JsonUtility.ToJson(MakePacket("hello", string.Empty, string.Empty)));
            foreach (IPEndPoint endpoint in _broadcasts)
            {
                SendDatagram(bytes, endpoint);
            }
        }

        private void SendControl(string kind, LanPeerInfo peer, string ticket)
        {
            if (_control == null || peer == null || !IPAddress.TryParse(peer.Address, out IPAddress address))
            {
                return;
            }
            byte[] bytes = _utf8.GetBytes(JsonUtility.ToJson(MakePacket(kind, peer.Id, ticket)));
            SendDatagram(bytes, new IPEndPoint(address, peer.ControlPort));
        }

        private void SendDatagram(byte[] bytes, IPEndPoint endpoint)
        {
            try
            {
                _control?.SendTo(bytes, endpoint);
            }
            catch (SocketException error)
            {
                // 某张网卡禁广播不等于所有网卡失效；继续尝试其他网卡并保留可查询原因，邀请有独立超时出口。
                LastError = "局域网数据包暂未发出：" + error.SocketErrorCode;
            }
        }

        private void ReceiveDatagrams(Socket socket)
        {
            if (socket == null)
            {
                return;
            }
            for (int count = 0; count < MaxPacketsPerPoll && !_disposed; count++)
            {
                try
                {
                    if (!socket.Poll(0, SelectMode.SelectRead))
                    {
                        break;
                    }
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length = socket.ReceiveFrom(_udpBuffer, ref remote);
                    if (length <= 0 || length >= MaxDatagramBytes)
                    {
                        continue;
                    }
                    var packet = JsonUtility.FromJson<DiscoveryPacket>(_utf8.GetString(_udpBuffer, 0, length));
                    HandleDatagram(packet, (IPEndPoint)remote);
                }
                catch (SocketException error)
                {
                    if (error.SocketErrorCode != SocketError.WouldBlock && error.SocketErrorCode != SocketError.MessageSize)
                    {
                        LastError = "读取局域网数据包失败：" + error.SocketErrorCode;
                    }
                    break;
                }
                catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException)
                {
                    // 同网可能有完全无关的 UDP 程序；损坏/非 UTF8/非本协议报文只是丢弃，不能中断玩家的模式。
                }
            }
        }

        private void HandleDatagram(DiscoveryPacket packet, IPEndPoint remote)
        {
            if (packet == null || packet.magic != Magic || packet.version != ProtocolVersion || packet.id == LocalId ||
                !Guid.TryParseExact(packet.id, "N", out _) || packet.controlPort != remote.Port || remote.Address.AddressFamily != AddressFamily.InterNetwork)
            {
                return;
            }
            if (packet.kind == "hello")
            {
                ObservePeer(packet, remote);
                return;
            }
            if (packet.target != LocalId || !Guid.TryParseExact(packet.ticket, "N", out _))
            {
                return;
            }
            string remoteAddress = remote.Address.ToString();
            LanPeerInfo peer = _peers.Find(item => item.Id == packet.id && item.Address == remoteAddress && item.ControlPort == remote.Port);
            // 搜索列表可能刚收到同一设备另一张网卡的广播；已接受邀请始终以当时冻结的端点校验控制包。
            if (_expectedPeer != null && _expectedPeer.Id == packet.id && _expectedPeer.Address == remoteAddress && _expectedPeer.ControlPort == remote.Port)
            {
                peer = _expectedPeer;
            }
            if (peer == null)
            {
                return;
            }
            if (packet.kind == "invite")
            {
                HandleInvitation(packet, peer);
                return;
            }
            if (_expectedPeer == null || packet.id != _expectedPeer.Id || packet.ticket != _ticket)
            {
                return;
            }
            if (packet.kind == "accept" && IsHost && !IsConnected && !_acceptedInvitation)
            {
                _acceptedInvitation = true;
                _deadline = Now + _options.ConnectTimeoutSeconds;
                Status = "对方已接受，正在建立局域网直连";
                Changed?.Invoke();
            }
            else if (packet.kind == "decline" || packet.kind == "busy" || packet.kind == "cancel")
            {
                string reason = packet.kind == "decline" ? "对方拒绝了邀请" : packet.kind == "busy" ? "对方正在处理其他邀请或对局" : "对方已取消邀请或离开局域网";
                ResetConnection(reason, true);
            }
        }

        private void ObservePeer(DiscoveryPacket packet, IPEndPoint remote)
        {
            LanPeerInfo peer = _peers.Find(item => item.Id == packet.id);
            if (peer == null)
            {
                if (_peers.Count >= MaxPeers)
                {
                    return;
                }
                peer = new LanPeerInfo { Id = packet.id };
                _peers.Add(peer);
            }
            string name = SanitizeName(packet.name);
            string address = remote.Address.ToString();
            bool changed = peer.Name != name || peer.Address != address || peer.ControlPort != remote.Port || peer.Busy != packet.busy;
            peer.Name = name;
            peer.Address = address;
            peer.ControlPort = remote.Port;
            peer.Busy = packet.busy;
            peer.SeenAt = Now;
            if (changed)
            {
                Changed?.Invoke();
            }
        }

        private void HandleInvitation(DiscoveryPacket packet, LanPeerInfo peer)
        {
            if (packet.transportPort < 1 || packet.transportPort > 65535)
            {
                return;
            }
            if (packet.ticket == _rejectedTicket && Now < _rejectedUntil)
            {
                SendControl("decline", peer, packet.ticket);
                return;
            }
            if (_expectedPeer != null)
            {
                if (_ticket == packet.ticket && _expectedPeer.Id == peer.Id)
                {
                    if (_acceptedInvitation || IsConnected)
                    {
                        SendControl("accept", peer, packet.ticket);
                    }
                    return;
                }
                SendControl("busy", peer, packet.ticket);
                return;
            }
            // 保存邀请时的地址副本：后续另一网卡广播更新列表，不能悄悄更改已获玩家同意的目标端点。
            _expectedPeer = CopyPeer(peer);
            IncomingInvitation = _expectedPeer;
            IsHost = false;
            _ticket = packet.ticket;
            _transportPort = packet.transportPort;
            _deadline = Now + _options.InvitationTimeoutSeconds;
            Status = peer.Name + " 邀请你进行九路围棋对局";
            Changed?.Invoke();
        }

        private static LanPeerInfo CopyPeer(LanPeerInfo peer)
        {
            return new LanPeerInfo
            {
                Id = peer.Id,
                Name = peer.Name,
                Address = peer.Address,
                ControlPort = peer.ControlPort,
                Busy = peer.Busy,
                SeenAt = peer.SeenAt
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace RevGame.Pvp.Networking
{
    /// <summary>
    /// 两名同网玩家的直连通信：UDP 只发现设备与传递邀请，落子消息由 Unity 官方 Transport 可靠有序传输。
    /// 邀请者监听临时端口，受邀者连接它；这里的“房主”只是连接角色，不代替共享 GoCore 裁决围棋规则。
    /// 所有入口必须在主线程调用，由 PvP Logic 每帧 Poll；没有后台线程、Task 或第二个全局更新宿主。
    /// 退出 PvP 的所有者必须 Dispose，释放 Socket 与 UTP 原生内存，离线模式不会持有这些资源。
    /// </summary>
    public sealed partial class LanPeerTransport : IDisposable
    {
        // 这些是协议结构上限，不是策划数值：限制陌生局域网报文造成的内存、队列与解析开销。
        private const string Magic = "RevGame.Go9.LAN";
        private const int ProtocolVersion = 1;
        private const int MaxDatagramBytes = 2048;
        private const int MaxPayloadBytes = 8192;
        private const int MaxWireBytes = 16384;
        private const int MaxPeers = 64;
        private const int MaxPacketsPerPoll = 32;
        private const int MaxQueuedMessages = 32;

        private readonly LanPeerOptions _options;
        private readonly List<LanPeerInfo> _peers = new List<LanPeerInfo>();
        private readonly List<IPEndPoint> _broadcasts = new List<IPEndPoint>();
        private readonly Queue<byte[]> _outbox = new Queue<byte[]>();
        private readonly byte[] _udpBuffer = new byte[MaxDatagramBytes];
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false, true);
        private Socket _discovery;
        private Socket _control;
        private NetworkDriver _driver;
        private NetworkPipeline _reliable;
        private NetworkConnection _connection;
        private LanPeerInfo _expectedPeer;
        private string _name;
        private string _ticket;
        private string _rejectedTicket;
        private double _rejectedUntil;
        private double _nextAnnounce;
        private double _nextInvite;
        private double _deadline;
        private bool _acceptedInvitation;
        private bool _sentAuthentication;
        private bool _disposed;
        private int _transportPort;

        /// <summary>主界面只观察这些值；网络回包不直接修改任何 UI 或棋盘。</summary>
        public IReadOnlyList<LanPeerInfo> Peers => _peers;
        public LanPeerInfo IncomingInvitation { get; private set; }
        public string LocalId { get; } = Guid.NewGuid().ToString("N");
        public bool IsConnected { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsDiscovering => _control != null && !_disposed;
        public string Status { get; private set; } = "尚未搜索局域网设备";
        public string LastError { get; private set; } = string.Empty;
        public int LocalControlPort => _control == null ? 0 : ((IPEndPoint)_control.LocalEndPoint).Port;
        public int LocalTransportPort => _transportPort;

        public event Action Changed;
        public event Action<bool> Connected;
        public event Action<string> PayloadReceived;
        public event Action<string> Disconnected;

        public LanPeerTransport(LanPeerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>只在玩家进入局域网页时打开端口。失败可见地返回原因，调用方仍能返回大厅或游玩单机。</summary>
        public bool StartDiscovery(string displayName, out string reason)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            reason = "浏览器不支持局域网 UDP 直连，请使用桌面或移动客户端。";
            LastError = reason;
            return false;
#else
            if (_disposed || IsDiscovering)
            {
                reason = _disposed ? "局域网连接已释放，请重新进入该模式。" : "已经在搜索局域网设备。";
                return false;
            }
            _name = SanitizeName(displayName);
            try
            {
                // 发现端口允许同机多实例共享；邀请回复走每实例独占的临时端口，避免操作系统随机投递邀请。
                _discovery = NewSocket();
                _discovery.ExclusiveAddressUse = false;
                _discovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discovery.Bind(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort));
                _control = NewSocket();
                _control.Bind(new IPEndPoint(IPAddress.Any, 0));
                RefreshBroadcasts();
                _nextAnnounce = 0;
                LastError = string.Empty;
                Status = "正在搜索同一局域网内的玩家";
                reason = string.Empty;
                Changed?.Invoke();
                return true;
            }
            catch (Exception error) when (error is SocketException || error is NetworkInformationException || error is NotSupportedException)
            {
                CloseSockets();
                reason = "无法搜索局域网：" + error.Message;
                LastError = reason;
                Status = reason;
                Changed?.Invoke();
                return false;
            }
#endif
        }

        /// <summary>按随机设备 Id 邀请已发现玩家；不能向任意输入地址偷偷建立连接。</summary>
        public bool Invite(string peerId, out string reason)
        {
            LanPeerInfo peer = _peers.Find(item => item.Id == peerId);
            if (!IsDiscovering || _expectedPeer != null || IsConnected || peer == null || peer.Busy)
            {
                reason = "设备已离线、正在对局，或当前已有邀请。请刷新列表后再试。";
                return false;
            }
            if (!OpenDriver(true, null, 0, out reason))
            {
                return false;
            }
            IsHost = true;
            _expectedPeer = CopyPeer(peer);
            _ticket = Guid.NewGuid().ToString("N");
            _deadline = Now + _options.InvitationTimeoutSeconds;
            _nextInvite = 0;
            Status = "已邀请 " + peer.Name + "，等待对方接受";
            LastError = string.Empty;
            Changed?.Invoke();
            reason = string.Empty;
            return true;
        }

        /// <summary>必须来自玩家明确接受/拒绝按钮。接受后连接邀请者的 UTP 临时端口，双方完成票据握手才算连接成功。</summary>
        public bool ReplyInvitation(bool accept, out string reason)
        {
            if (IncomingInvitation == null || _expectedPeer == null || _acceptedInvitation || Now >= _deadline)
            {
                reason = "这条邀请已经失效。";
                return false;
            }
            if (!accept)
            {
                SendControl("decline", _expectedPeer, _ticket);
                _rejectedTicket = _ticket;
                _rejectedUntil = Now + _options.InvitationTimeoutSeconds;
                ResetConnection("已拒绝邀请", false);
                reason = string.Empty;
                return true;
            }
            if (!OpenDriver(false, _expectedPeer.Address, _transportPort, out reason))
            {
                SendControl("decline", _expectedPeer, _ticket);
                ResetConnection(reason, true);
                return false;
            }
            _acceptedInvitation = true;
            IsHost = false;
            IncomingInvitation = null;
            _deadline = Now + _options.ConnectTimeoutSeconds;
            SendControl("accept", _expectedPeer, _ticket);
            Status = "对方已受邀，正在建立局域网直连";
            Changed?.Invoke();
            reason = string.Empty;
            return true;
        }

        public void CancelInvitation()
        {
            if (_expectedPeer == null || IsConnected)
            {
                return;
            }
            SendControl("cancel", _expectedPeer, _ticket);
            ResetConnection("已取消邀请，可以重新选择玩家", false);
        }

        /// <summary>接受到本地有界队列不等于对手已落子；围棋服务仍需按自己的回合序号校验与确认消息。</summary>
        public bool Send(string payload, out string reason)
        {
            if (!IsConnected || string.IsNullOrEmpty(payload))
            {
                reason = "局域网连接尚未就绪。";
                return false;
            }
            int byteCount;
            try
            {
                byteCount = _utf8.GetByteCount(payload);
            }
            catch (EncoderFallbackException)
            {
                reason = "局域网消息包含无效文字编码。";
                return false;
            }
            if (byteCount > MaxPayloadBytes || _outbox.Count >= MaxQueuedMessages)
            {
                reason = "消息过大或发送队列已满，请等待连接恢复。";
                return false;
            }
            if (!QueueWire("data", payload))
            {
                reason = "消息编码后超过局域网协议长度限制。";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>每帧有界处理，避免同网陌生报文让游戏主线程无限循环；计时使用单调时钟，系统时钟调整不影响邀请。</summary>
        public void Poll()
        {
            if (!IsDiscovering)
            {
                return;
            }
            ReceiveDatagrams(_discovery);
            ReceiveDatagrams(_control);
            if (!IsDiscovering)
            {
                return;
            }
            double now = Now;
            if (now >= _nextAnnounce)
            {
                Broadcast();
                _nextAnnounce = now + _options.AnnounceSeconds;
                bool removed = _peers.RemoveAll(peer => now - peer.SeenAt > _options.PeerExpirySeconds) > 0;
                if (removed)
                {
                    Changed?.Invoke();
                }
            }
            if (_expectedPeer != null && !IsConnected && now >= _deadline)
            {
                SendControl("cancel", _expectedPeer, _ticket);
                ResetConnection("邀请或连接超时，请检查同一 Wi-Fi 与防火墙后重试。", true);
            }
            if (IsHost && _expectedPeer != null && !IsConnected && now >= _nextInvite)
            {
                SendControl("invite", _expectedPeer, _ticket);
                _nextInvite = now + _options.AnnounceSeconds;
            }
            PollDriver();
        }

        /// <summary>释放顺序：先让回调不可再到达，再关闭连接与驱动，最后关闭发现端口；重复调用安全。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            if (_expectedPeer != null)
            {
                SendControl("cancel", _expectedPeer, _ticket);
            }
            _disposed = true;
            Changed = null;
            Connected = null;
            PayloadReceived = null;
            Disconnected = null;
            ResetConnection("已离开局域网", false);
            CloseSockets();
            _peers.Clear();
            _broadcasts.Clear();
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private static Socket NewSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Blocking = false;
            socket.EnableBroadcast = true;
            return socket;
        }

        private static string SanitizeName(string name)
        {
            var result = new StringBuilder();
            string source = name ?? string.Empty;
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                // UTF16 表情通常占两个char；截断不能留下半个代理项，否则严格UTF8编码会在下一次广播时抛异常。
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]) && result.Length <= 22)
                    {
                        result.Append(character);
                        result.Append(source[++index]);
                    }
                    continue;
                }
                if (char.IsLowSurrogate(character))
                {
                    continue;
                }
                if (!char.IsControl(character) && character != '<' && character != '>' && result.Length < 24)
                {
                    result.Append(character);
                }
            }
            return result.Length == 0 ? "围棋玩家" : result.ToString();
        }

        private void CloseSockets()
        {
            _discovery?.Dispose();
            _control?.Dispose();
            _discovery = null;
            _control = null;
        }
    }
}

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RevGame.Pvp.Networking
{
    /// <summary>
    /// 开发态真实传输夹具：同一 Unity 进程创建两份独立 Socket/UTP 驱动，实际走操作系统 UDP 与 UTP 可靠流水线。
    /// 不修改玩家棋局，也不模拟 Send 的结果；外层自动化每帧 Tick，并在 finally 调用 Dispose。
    /// 这证明同机端点链路，不能代替另一台手机的路由器、防火墙或系统权限验收。
    /// </summary>
    public sealed class LanTransportChecks : IDisposable
    {
        private readonly LanPeerOptions _options;
        private readonly List<string> _hostReceived = new List<string>();
        private readonly List<string> _guestReceived = new List<string>();
        private LanPeerTransport _host;
        private LanPeerTransport _guest;
        private double _deadline;
        private int _step;
        private bool _guestClosed;
        private bool _hostSawDisconnect;
        private string _longMessage;

        public bool Complete { get; private set; }
        public bool Passed { get; private set; }
        public string Detail { get; private set; } = "尚未开始";
        public int Assertions { get; private set; }

        public LanTransportChecks(LanPeerOptions options)
        {
            _options = options;
        }

        public bool Start(out string reason)
        {
            if (_host != null || Complete)
            {
                reason = "传输夹具只能启动一次。";
                return false;
            }
            _host = new LanPeerTransport(_options);
            _guest = new LanPeerTransport(_options);
            _host.PayloadReceived += message => _hostReceived.Add(message);
            _guest.PayloadReceived += message => _guestReceived.Add(message);
            _host.Disconnected += message => _hostSawDisconnect = true;
            if (!_host.StartDiscovery("局域网测试黑方", out reason) || !_guest.StartDiscovery("局域网测试白方", out reason))
            {
                Fail(reason);
                return false;
            }
            _deadline = Now + _options.InvitationTimeoutSeconds + _options.ConnectTimeoutSeconds + _options.PeerExpirySeconds;
            Detail = "等待两端真实 UDP 发现";
            reason = string.Empty;
            return true;
        }

        public void Tick()
        {
            if (Complete || _host == null)
            {
                return;
            }
            _host.Poll();
            if (!_guestClosed)
            {
                _guest.Poll();
            }
            if (Now >= _deadline)
            {
                Fail("步骤 " + _step + " 超时：" + Detail + "；黑方=" + _host.Status + "；白方=" + _guest.Status);
                return;
            }
            switch (_step)
            {
                case 0:
                    if (FindPeer(_host, _guest.LocalId) == null || FindPeer(_guest, _host.LocalId) == null)
                    {
                        return;
                    }
                    Assertions += 2;
                    if (!Check(_host.Invite(_guest.LocalId, out string inviteReason), inviteReason))
                    {
                        return;
                    }
                    Next("等待收到邀请并拒绝");
                    break;
                case 1:
                    if (_guest.IncomingInvitation == null)
                    {
                        return;
                    }
                    if (!Check(_guest.ReplyInvitation(false, out string declineReason), declineReason))
                    {
                        return;
                    }
                    Next("等待拒绝送达后重新邀请");
                    break;
                case 2:
                    LanPeerInfo peer = FindPeer(_host, _guest.LocalId);
                    if (!_hostSawDisconnect || peer == null || peer.Busy)
                    {
                        return;
                    }
                    if (!Check(!_host.IsConnected && !_guest.IsConnected, "拒绝邀请后意外建立连接"))
                    {
                        return;
                    }
                    _hostSawDisconnect = false;
                    if (!Check(_host.Invite(_guest.LocalId, out string retryReason), retryReason))
                    {
                        return;
                    }
                    Next("重新邀请后等待接受");
                    break;
                case 3:
                    if (_guest.IncomingInvitation == null)
                    {
                        return;
                    }
                    if (!Check(_guest.ReplyInvitation(true, out string acceptReason), acceptReason))
                    {
                        return;
                    }
                    Next("等待 UTP 连接和随机票据握手");
                    break;
                case 4:
                    if (!_host.IsConnected || !_guest.IsConnected)
                    {
                        return;
                    }
                    if (!Check(_host.IsHost && !_guest.IsHost, "邀请者/受邀者连接角色错误"))
                    {
                        return;
                    }
                    // 中文 UTF8 大于单个 MTU，确保测试实际覆盖 UTP 分片与可靠有序流水线。
                    _longMessage = new string('棋', 1500);
                    for (int index = 0; index < 3; index++)
                    {
                        string value = index == 1 ? _longMessage : "move:" + index;
                        if (!Check(_host.Send(value, out string hostReason), hostReason) || !Check(_guest.Send(value, out string guestReason), guestReason))
                        {
                            return;
                        }
                    }
                    Next("等待双向可靠有序消息与大包分片");
                    break;
                case 5:
                    if (_hostReceived.Count < 3 || _guestReceived.Count < 3)
                    {
                        return;
                    }
                    for (int index = 0; index < 3; index++)
                    {
                        string expected = index == 1 ? _longMessage : "move:" + index;
                        if (!Check(_hostReceived[index] == expected && _guestReceived[index] == expected, "消息顺序或 UTF8 分片内容不一致"))
                        {
                            return;
                        }
                    }
                    _guest.Dispose();
                    _guestClosed = true;
                    Next("等待对方退出通知和端口释放");
                    break;
                case 6:
                    if (!_hostSawDisconnect)
                    {
                        return;
                    }
                    if (!Check(!_host.IsConnected && _host.IsDiscovering && !_guest.IsDiscovering, "退出后连接/发现生命周期错误"))
                    {
                        return;
                    }
                    _host.Dispose();
                    if (!Check(!_host.IsDiscovering, "房主发现端口未关闭"))
                    {
                        return;
                    }
                    Complete = true;
                    Passed = true;
                    Detail = "真实 UDP 双端发现、邀请拒绝/重试/接受、UTP 双向有序分片、掉线提示和释放全部通过";
                    break;
            }
        }

        private void Next(string detail)
        {
            _step++;
            Detail = detail;
        }

        private bool Check(bool condition, string reason)
        {
            if (!condition)
            {
                Fail(reason);
                return false;
            }
            Assertions++;
            return true;
        }

        private void Fail(string reason)
        {
            Complete = true;
            Passed = false;
            Detail = reason;
            Dispose();
        }

        private static LanPeerInfo FindPeer(LanPeerTransport owner, string id)
        {
            foreach (LanPeerInfo peer in owner.Peers)
            {
                if (peer.Id == id)
                {
                    return peer;
                }
            }
            return null;
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void Dispose()
        {
            _host?.Dispose();
            _guest?.Dispose();
        }
    }
}
#endif

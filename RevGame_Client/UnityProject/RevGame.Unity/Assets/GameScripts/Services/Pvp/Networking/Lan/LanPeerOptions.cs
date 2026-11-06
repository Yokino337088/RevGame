using System;

namespace RevGame.Pvp.Networking
{
    /// <summary>局域网连接参数由 PvP 配置提供；时间单位全部是现实秒，不受游戏暂停和倍速影响。</summary>
    public sealed class LanPeerOptions
    {
        public readonly int DiscoveryPort;
        public readonly double AnnounceSeconds;
        public readonly double PeerExpirySeconds;
        public readonly double InvitationTimeoutSeconds;
        public readonly double ConnectTimeoutSeconds;

        public LanPeerOptions(int discoveryPort, double announceSeconds, double peerExpirySeconds, double invitationTimeoutSeconds, double connectTimeoutSeconds)
        {
            if (discoveryPort < 1024 || discoveryPort > 65535 || announceSeconds <= 0 || peerExpirySeconds <= announceSeconds || invitationTimeoutSeconds <= 0 || connectTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(discoveryPort), "局域网端口或超时配置无效。");
            }
            DiscoveryPort = discoveryPort;
            AnnounceSeconds = announceSeconds;
            PeerExpirySeconds = peerExpirySeconds;
            InvitationTimeoutSeconds = invitationTimeoutSeconds;
            ConnectTimeoutSeconds = connectTimeoutSeconds;
        }
    }

    /// <summary>一次发现的设备；Id 是本次运行的随机标识，不使用硬件标识，也不保存到玩家存档。</summary>
    public sealed class LanPeerInfo
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string DeviceName { get; internal set; }
        public string DeviceModel { get; internal set; }
        public string Address { get; internal set; }
        public int ControlPort { get; internal set; }
        public bool Busy { get; internal set; }
        internal double SeenAt;
    }
}

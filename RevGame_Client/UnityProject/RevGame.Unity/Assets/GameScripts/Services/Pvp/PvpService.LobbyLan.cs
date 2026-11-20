using RevGame.Pvp.Networking;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>大厅局域网可见性。获得玩家授权后使用被动发现，接受邀请才将传输移交对局。</summary>
    public sealed partial class PvpService
    {
        /// <summary>授权只记录在当前设备，默认关闭。</summary>
        private const string LobbyLanPermissionKey = "RevGame.Pvp.LobbyLanAllowed.v1";
        /// <summary>被发现时使用玩家在局域网界面填写的昵称。</summary>
        private const string LobbyLanNameKey = "RevGame.Pvp.LobbyLanName.v1";
        /// <summary>大厅持有的空闲接收器。被对局接管后此处立即清空。</summary>
        private LanPeerTransport _lobbyLan;

        /// <summary>玩家是否允许在前台大厅被同网棋友发现。</summary>
        internal static bool LobbyLanAllowed => PlayerPrefs.GetInt(LobbyLanPermissionKey, 0) == 1;

        /// <summary>尚未决定的来访邀请。大厅只读取这一个有票据校验的请求。</summary>
        internal LanPeerInfo LobbyLanInvitation => _lobbyLan?.IncomingInvitation;

        /// <summary>由玩家在局域网页明确开关授权，同时保存本机昵称。</summary>
        internal static void SetLobbyLanPermission(bool allowed, string nickname)
        {
            PlayerPrefs.SetInt(LobbyLanPermissionKey, allowed ? 1 : 0);
            if (allowed)
            {
                PlayerPrefs.SetString(LobbyLanNameKey, CleanName(nickname));
            }
            PlayerPrefs.Save();
        }

        /// <summary>打开大厅的被动接收端口。失败只写诊断，玩家仍可玩其它模式。</summary>
        internal void StartLobbyLan(PvpConfig config)
        {
            if (_lobbyLan != null || !LobbyLanAllowed)
            {
                return;
            }
            var options = new LanPeerOptions(config.discoveryPort, config.announceSeconds, config.peerExpirySeconds,
                config.invitationTimeoutSeconds, config.connectTimeoutSeconds);
            var listener = new LanPeerTransport(options);
            if (listener.StartDiscovery(PlayerPrefs.GetString(LobbyLanNameKey, "棋友"), out string reason, listenOnly: true))
            {
                _lobbyLan = listener;
            }
            else
            {
                listener.Dispose();
                Revolution.RevLog.Warn("大厅局域网接收不可用：" + reason, "Pvp");
            }
        }

        /// <summary>大厅低频观察调用。只有持有接收器时才读取本地网络。</summary>
        internal void PollLobbyLan()
        {
            _lobbyLan?.Poll();
        }

        /// <summary>拒绝当前邀请，保留本机可被发现的授权和监听。</summary>
        internal void DeclineLobbyLanInvitation()
        {
            _lobbyLan?.ReplyInvitation(false, out _);
        }

        /// <summary>只在玩家明确接受后取得原接收器，避免切场释放仍需回复的邀请票据。</summary>
        internal LanPeerTransport TakeLobbyLanInvitation()
        {
            if (LobbyLanInvitation == null)
            {
                return null;
            }
            LanPeerTransport listener = _lobbyLan;
            _lobbyLan = null;
            return listener;
        }

        /// <summary>转交已有票据给本轮围棋服务，继续走原来接受、连接和规则握手。</summary>
        internal bool AdoptLobbyLanInvitation(LanPeerTransport listener)
        {
            Mode = PvpConnectionMode.Lan;
            Nickname = PlayerPrefs.GetString(LobbyLanNameKey, "棋友");
            AttachLan(listener);
            if (!listener.ReplyInvitation(true, out string reason))
            {
                Lan = null;
                listener.Dispose();
                Status = reason;
                Revision++;
                return false;
            }
            return true;
        }

        /// <summary>退出大厅或结束服务时关闭仍由大厅持有的端口。</summary>
        internal void StopLobbyLan()
        {
            LanPeerTransport listener = _lobbyLan;
            _lobbyLan = null;
            listener?.Dispose();
        }
    }
}

using System;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Pvp;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>已授权的大厅只接收邀请。确认按键才进入原局域网对战流程。</summary>
    public sealed partial class HomePanel
    {
        /// <summary>有待回复的局域网邀请时显示的小卡。</summary>
        [RevBind("lanInvite")] private GameObject _lanInvite;
        /// <summary>展示当前邀请者，邀请变化时才写入。</summary>
        [RevBind("lanInvite/title")] private Text _lanInviteTitle;
        /// <summary>已由大厅开启被动监听的对战服务。</summary>
        private PvpService _lobbyLanService;
        /// <summary>每次打开或关闭时递增，取消迟到的配置加载。</summary>
        private int _lobbyLanVersion;
        /// <summary>每秒五次短轮询。离开大厅时停止。</summary>
        private RevTimerHandle _lobbyLanTimer;
        /// <summary>上一次落屏的邀请者昵称。</summary>
        private string _lobbyLanCaller;

        /// <summary>只有前次明确授权的前台大厅才加载发现配置并监听，隔离 Play 复用专用网络夹具。</summary>
        private async RevTask OpenLobbyLanAsync()
        {
            if (!PvpService.LobbyLanAllowed || RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                return;
            }
            int version = ++_lobbyLanVersion;
            try
            {
                await RevDataTableManager.LoadAsync<PvpRuleTable>();
                if (this == null || version != _lobbyLanVersion || RevServices.GetRequired<GameFlow>().Screen != GameScreen.Home)
                {
                    return;
                }
                _lobbyLanService = RevServices.GetRequired<PvpService>();
                _lobbyLanService.StartLobbyLan(PvpConfig.Read(true));
                _lobbyLanTimer = RevTimer.Every(.2f, RefreshLobbyLanInvitation, domain: RevTimeDomain.Unscaled, owner: this);
            }
            catch (Exception error)
            {
                RevLog.Warn("大厅局域网未开启：" + error.Message, "Pvp");
            }
        }

        /// <summary>低频读取实际邀请和页面门禁。名字或可见性不变时不写 UI。</summary>
        private void RefreshLobbyLanInvitation()
        {
            _lobbyLanService?.PollLobbyLan();
            var invite = _lobbyLanService?.LobbyLanInvitation;
            bool visible = invite != null && !RevServices.GetRequired<GameFlow>().IsGameplayBlocked;
            if (_lanInvite != null && _lanInvite.activeSelf != visible)
            {
                _lanInvite.SetActive(visible);
            }
            if (visible && _lanInviteTitle != null && _lobbyLanCaller != invite.Name)
            {
                _lobbyLanCaller = invite.Name;
                _lanInviteTitle.text = invite.Name + " 邀请你下棋";
            }
        }

        /// <summary>关闭界面先使异步启动失效，再停止轮询和仍由大厅持有的端口。</summary>
        private void CloseLobbyLan()
        {
            _lobbyLanVersion++;
            _lobbyLanTimer.Stop();
            _lobbyLanService?.StopLobbyLan();
            _lobbyLanService = null;
            _lobbyLanCaller = null;
            if (_lanInvite != null)
            {
                _lanInvite.SetActive(false);
            }
        }

        /// <summary>玩家明确同意后才接管该邀请。过期时下一次刷新收起卡片。</summary>
        [RevButtonClick("lanInvite/accept")]
        private void AcceptLobbyLan()
        {
            RevServices.GetRequired<GameFlow>().EnterPvpFromLobbyInvite();
            RefreshLobbyLanInvitation();
        }

        /// <summary>回复拒绝并立即更新，接收权限保留。</summary>
        [RevButtonClick("lanInvite/decline")]
        private void DeclineLobbyLan()
        {
            _lobbyLanService?.DeclineLobbyLanInvitation();
            RefreshLobbyLanInvitation();
        }
    }
}

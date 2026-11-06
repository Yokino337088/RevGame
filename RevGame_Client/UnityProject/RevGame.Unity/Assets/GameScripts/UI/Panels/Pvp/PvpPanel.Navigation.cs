using System;
using RevGame.UI;
using RevGame.Flow;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    public sealed partial class PvpPanel
    {
        private bool _battleVisible, _battleTransition;
        private string _seenStatus = "";
        public string Menu { get; private set; } = "Entry";
        public bool BattleVisible { get { return _battleVisible; } }

        /// <summary>邀请沿用HUD已绑定的真实回调；发现页位于Popup层，临时挂到该页上，避免被不透明背景遮挡。</summary>
        public void MountInvitation(Transform parent)
        {
            _invite.transform.SetParent(parent == null ? transform : parent, false);
            _invite.transform.SetAsLastSibling();
        }

        public void ShowDestination(string destination)
        {
            Menu = destination;
            if (destination == "Computer" || destination == "Local")
            {
                if (destination == "Computer")
                {
                    Service.StartComputer();
                }
                else
                {
                    Service.StartLocal();
                }
                // 从大厅直达本地对局时，外层GameFlow已有完整星雾遮挡；不重复打开同一过场面板。
                if (RevServices.GetRequired<GameFlow>().IsTransitioning && _board != null)
                {
                    CloseMenus();
                    _board.gameObject.SetActive(true);
                    _battleVisible = true;
                    Menu = "Battle";
                }
                return;
            }
            CloseMenus();
            if (destination == "Server")
            {
                RevUI.Open<PvpServerPanel>(panel => panel.RefreshView());
            }
            else if (destination == "Lan")
            {
                RevUI.Open<PvpLanPanel>(panel => panel.RefreshView());
            }
            else
            {
                RevUI.Open<PvpEntryPanel>(panel => panel.RefreshView());
            }
        }

        private void TickPresentation()
        {
            if (!_opening)
            {
                return;
            }
            if (_board != null && Service.Core != null && !_battleVisible && !_battleTransition && !RevServices.GetRequired<GameFlow>().IsTransitioning)
            {
                BeginBattlePresentation();
            }
            if (Service.Core == null && _battleVisible)
            {
                _battleVisible = false;
                _board.gameObject.SetActive(false);
                ShowDestination("Entry");
            }
            if (Service.Queued)
            {
                PvpNoticePanel.ShowQueue((int)Service.QueueElapsedSeconds);
                _seenStatus = Service.Status;
            }
            else if (Service.Status != _seenStatus && !string.IsNullOrEmpty(Service.Status))
            {
                _seenStatus = Service.Status;
                PvpNoticePanel.ShowMessage(_seenStatus);
            }
            var invitation = Service.Lan?.IncomingInvitation;
            Show(_invite, invitation != null && Service.Core == null);
            if (invitation != null)
            {
                Set(_invitationText, invitation.Name + " 邀请你对局\n" + invitation.DeviceName + " · " + invitation.DeviceModel);
            }
            RefreshView();
        }

        // Unity计时器回调的薄适配；演出失败不吞错，退出版本检查避免旧异步关闭新面板。
        private async void BeginBattlePresentation()
        {
            _battleTransition = true;
            int version = _leaseVersion;
            LoadingPanel cover = null;
            try
            {
                // 先消费本次状态，防止同一Tick后续的普通“已连接”提示覆盖匹配成功提示。
                _seenStatus = Service.Status;
                PvpNoticePanel.ShowMessage(Service.Mode == PvpConnectionMode.Local ? "棋局准备就绪" : "匹配成功 · 即将进入对局");
                await RevTask.Delay(600);
                if (!_opening || version != _leaseVersion || Service.Core == null)
                {
                    return;
                }
                cover = await RevUI.OpenAsync<LoadingPanel>();
                cover.ResetCover();
                cover.SetData(new LoadingPanel.ProgressData(0, "正在展开棋盘…"));
                CloseMenus();
                _board.gameObject.SetActive(true);
                _battleVisible = true;
                Menu = "Battle";
                await RevTask.Yield();
                cover.SetData(new LoadingPanel.ProgressData(1, "棋局已就绪"));
                await cover.FadeOutAsync();
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "对局转场失败", "Pvp");
                PvpNoticePanel.ShowMessage("对局转场失败，请返回后重试");
            }
            finally
            {
                if (cover != null)
                {
                    RevUI.Close<LoadingPanel>();
                }
                if (version == _leaseVersion)
                {
                    _battleTransition = false;
                }
            }
        }

        private static void CloseMenus()
        {
            RevUI.Close<PvpAgentSettingsPanel>();
            RevUI.Close<PvpEntryPanel>();
            RevUI.Close<PvpServerPanel>();
            RevUI.Close<PvpLanPanel>();
        }

        [RevButtonClick("accept")]
        private void AcceptInvitation()
        {
            Run(() => Service.ReplyInvitation(true));
        }

        [RevButtonClick("decline")]
        private void DeclineInvitation()
        {
            Run(() => Service.ReplyInvitation(false));
        }
    }
}

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
        /// <summary>切菜单或开始对局都会更新版本，旧开窗票据不能重写当前菜单。</summary>
        private int _menuVersion;
        public string Menu { get; private set; } = "Entry";
        /// <summary>这一轮菜单请求的时间线，供程序判断是否已经交付。</summary>
        internal RevUIOpenTicket MenuTicket { get; private set; }
        public bool BattleVisible { get { return _battleVisible; } }
        internal bool BattleReady => _battleVisible && !_battleTransition;

        /// <summary>邀请沿用HUD已绑定的真实回调；发现页位于Popup层，临时挂到该页上，避免被不透明背景遮挡。</summary>
        public void MountInvitation(Transform parent)
        {
            _invite.transform.SetParent(parent == null ? transform : parent, false);
            _invite.transform.SetAsLastSibling();
        }

        /// <summary>只撤下仍挂在关闭页的邀请控件，避免延迟关闭移走新页已接管的控件。</summary>
        internal void UnmountInvitation(Transform previousParent)
        {
            if (_invite.transform.parent == previousParent)
            {
                MountInvitation(null);
            }
        }

        /// <summary>按钮意图转发。当前菜单在目标页实际交割后更新。</summary>
        public void ShowDestination(string destination)
        {
            ShowDestinationAsync(destination).Forget();
        }

        /// <summary>走记录式开窗并等待页面交割。用于大厅切场首屏和后续菜单切换。</summary>
        internal async RevTask<bool> ShowDestinationAsync(string destination)
        {
            int version = ++_menuVersion;
            if (destination == "Tutorial")
            {
                // 业务先装配练习，再把同一块九路围棋盘交给教学操作。
                if (Service.Tutorial == null || _board == null)
                {
                    return false;
                }
                CloseMenus();
                Menu = "Tutorial";
                _battleTransition = false;
                _battleVisible = true;
                _board.gameObject.SetActive(true);
                RefreshView();
                return true;
            }
            if (destination == "Computer" || destination == "Local")
            {
                Menu = destination;
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
                return true;
            }

            RevUIOpenTicket ticket = destination switch
            {
                "Server" => RevUI.OpenTracked<PvpServerPanel>(),
                "Lan" => RevUI.OpenTracked<PvpLanPanel>(deferAssembly: true),
                _ => RevUI.OpenTracked<PvpEntryPanel>()
            };
            MenuTicket = ticket;
            RevUIOpenStatus result = await ticket.WaitAsync();
            if (!_opening || version != _menuVersion)
            {
                return false;
            }
            if (result != RevUIOpenStatus.Succeeded)
            {
                if (result == RevUIOpenStatus.Failed)
                {
                    PvpNoticePanel.ShowMessage("页面打开失败，可继续使用当前菜单");
                }
                return false;
            }
            RevUI.Close<PvpAgentSettingsPanel>();
            Menu = destination == "Server" || destination == "Lan" ? destination : "Entry";
            PvpLanPanel lan = RevUI.Get<PvpLanPanel>();
            if (lan != null && Menu == "Lan")
            {
                MountInvitation(lan.transform);
            }
            return true;
        }

        /// <summary>把业务状态转换为可见页面和弹窗准入，再刷新当前对局视图。</summary>
        private void TickPresentation()
        {
            if (!_opening)
            {
                return;
            }
            if (Menu == "Tutorial" && Service.Tutorial != null)
            {
                // 教学没有匹配状态与房间通知，计时刷新只更新练习视图。
                RefreshView();
                return;
            }
            _confirmationQueue?.SetPhase(_battleVisible && !_battleTransition ? "Battle" : "Menu");
            if (_board != null && Service.Core != null && !_battleVisible && !_battleTransition && !RevServices.GetRequired<GameFlow>().IsTransitioning)
            {
                BeginBattlePresentation();
            }
            if (Service.Core == null && _battleVisible)
            {
                _battleVisible = false;
                // 切回大厅时资源组可能已经回收棋盘，不能再操作已失效的租约或重开菜单。
                if (_board != null)
                {
                    _board.gameObject.SetActive(false);
                }
                if (!RevServices.GetRequired<GameFlow>().IsTransitioning)
                {
                    ShowDestination("Entry");
                }
            }
            if (Service.Queued)
            {
                PvpNoticePanel.ShowQueue((int)Service.QueueElapsedSeconds);
                _seenStatus = Service.Status;
            }
            else if (Service.Status != _seenStatus && !string.IsNullOrEmpty(Service.Status))
            {
                _seenStatus = Service.Status;
                if (Service.ShouldShowStatusNotice)
                {
                    PvpNoticePanel.ShowMessage(_seenStatus);
                }
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

        /// <summary>退出菜单时同步撤销候选，已关闭页面的回包不能覆盖棋局。</summary>
        private void CloseMenus()
        {
            ++_menuVersion;
            MenuTicket = null;
            RevUI.Close<PvpAgentSettingsPanel>();
            RevUI.CloseGroup("PvpMenu");
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

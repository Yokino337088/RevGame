using System;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>大厅首先打开的四选一入口。未选择模式前不创建Socket、不加载对局场景。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed class PvpEntryPanel : RevUIPanel
    {
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "opaqueBacking");
            // 视图由RevBind装配；控件路由使用RevButtonClick，不在刷新时重复注册。
        }

        private bool _entering;
        [RevButtonClick("agentSettings")]
        private void AgentSettings()
        {
            PvpAgentSettingsPanel.OpenSettings();
        }

        [RevButtonClick("server")]
        private void Server()
        {
            Enter("Server");
        }
        [RevButtonClick("lan")]
        private void Lan()
        {
            Enter("Lan");
        }
        [RevButtonClick("computer")]
        private void Computer()
        {
            Enter("Computer");
        }
        [RevButtonClick("local")]
        private void Local()
        {
            Enter("Local");
        }

        protected override void OnOpen()
        {
            _entering = false;
        }

        private void Enter(string destination)
        {
            if (_entering)
            {
                return;
            }
            var flow = RevServices.GetRequired<GameFlow>();
            if (flow.Screen == GameScreen.Pvp)
            {
                RevUI.Get<PvpPanel>().ShowDestination(destination);
                return;
            }
            _entering = flow.EnterPvp(destination);
            if (_entering)
            {
                CloseSelf();
            }
            else
            {
                PvpNoticePanel.ShowMessage(flow.LastError);
            }
        }

        [RevButtonClick("back")]
        private void Back()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (flow.Screen == GameScreen.Pvp)
            {
                flow.ReturnHome();
            }
            CloseSelf();
        }
    }

}

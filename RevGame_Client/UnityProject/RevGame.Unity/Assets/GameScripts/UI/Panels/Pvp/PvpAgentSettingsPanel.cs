using Revolution;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>设置只装配和转发。Key使用密码输入；关闭时清空控件，持久化由服务写入本地加密存储。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose, Mask = RevUIMaskMode.ClickBlock)]
    public sealed class PvpAgentSettingsPanel : RevUIPanel
    {
        /// <summary>首次AB加载必须异步；迟到回调核对流程，离开PVP后不允许弹出设置。</summary>
        public static void OpenSettings()
        {
            var flow = RevServices.GetRequired<RevGame.Flow.GameFlow>();
            var origin = flow.Screen;
            var entry = RevUI.Get<PvpEntryPanel>();
            RevUI.Open<PvpAgentSettingsPanel>(panel =>
            {
                if (panel == null)
                {
                    RevLog.Warn("Agent设置加载失败，请返回重试", "Pvp");
                    return;
                }
                // 大厅的四选一菜单本来就先于PVP场景打开，不能把合法的Home入口当作迟到回调。
                // 同时保留原屏幕和菜单实例校验：返回/切场景后不能弹出旧设置。
                if (flow.IsTransitioning || flow.Screen != origin ||
                    (origin == RevGame.Core.GameScreen.Home && (entry == null || !RevUI.IsOpen<PvpEntryPanel>() || RevUI.Get<PvpEntryPanel>() != entry)) ||
                    (origin != RevGame.Core.GameScreen.Home && origin != RevGame.Core.GameScreen.Pvp))
                {
                    RevUI.Close<PvpAgentSettingsPanel>();
                }
            });
        }

        [RevBind("key")] private InputField _key;
        [RevBind("model")] private InputField _model;
        [RevBind("state")] private Text _state;
        protected override void OnBindView()
        {
            _key.contentType = InputField.ContentType.Password;
        }
        protected override void OnOpen()
        {
            var settings = RevServices.GetRequired<PvpService>().AgentSettings;
            _key.SetTextWithoutNotify(settings.Key);
            _model.SetTextWithoutNotify(settings.Model);
        }
        protected override void OnRefreshView()
        {
            _state.text = RevServices.GetRequired<PvpService>().AgentStatus;
        }
        [RevButtonClick("enable")]
        private void EnableCloud()
        {
            RevServices.GetRequired<PvpService>().ConfigureSavedAgent(_key.text, _model.text, true);
            RefreshView();
        }
        [RevButtonClick("disable")]
        private void DisableCloud()
        {
            RevServices.GetRequired<PvpService>().ConfigureSavedAgent("", _model.text, false);
            _key.SetTextWithoutNotify("");
            RefreshView();
        }
        [RevButtonClick("back")]
        private void Back()
        {
            CloseSelf();
        }
        protected override void OnClose()
        {
            _key.SetTextWithoutNotify("");
        }
    }
}

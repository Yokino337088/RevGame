using Revolution;
using UnityEngine.UI;
namespace RevGame.Pvp
{
    /// <summary>服务器匹配独立面板：队列秒数来自Service真实排队起点，不靠界面假倒计时。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed class PvpServerPanel : RevUIPanel
    {
        // 本次联机测试使用开发机的局域网入口，仍允许在面板中输入其他地址。
        public const string DefaultEndpoint = "192.168.10.3:20000";
        /// <summary>首次装配时让漩涡铺满屏幕，菜单底板和控件仍使用设备安全框。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            // 视图由RevBind装配；控件路由使用RevButtonClick，不在刷新时重复注册。
        }

        [RevBind("nickname")] private InputField _nickname;
        [RevBind("endpoint")] private InputField _endpoint;
        [RevBind("state")] private Text _state;
        [RevBind("duration")] private Text _duration;
        [RevBind("connect")] private Button _connect;
        [RevBind("match")] private Button _match;
        /// <summary>已连线或已入房时按快照锁定场次切换。</summary>
        [RevBind("mode")] private Button _mode;
        /// <summary>退房进行中禁用重复返回。</summary>
        [RevBind("back")] private Button _back;
        [RevBind("match/label")] private Text _matchLabel;
        [RevBind("mode/label")] private Text _modeLabel;
        private PvpService Service { get { return RevServices.GetRequired<PvpService>(); } }
        protected override void OnOpen()
        {
            _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            _endpoint.text = string.IsNullOrEmpty(Service.Endpoint) ? DefaultEndpoint : Service.Endpoint;
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }
        /// <summary>按共享房间快照设置提示、队列文字和联机入口门禁。</summary>
        protected override void OnRefreshView()
        {
            PvpService.PvpRoomSnapshot room = Service.RoomSnapshot;
            _state.text = room.ServerStatusText;
            _duration.text = room.QueueText;
            _match.interactable = room.Allows(PvpRoomAction.Match);
            _connect.interactable = room.Allows(PvpRoomAction.Connect);
            _mode.interactable = room.Allows(PvpRoomAction.ChangeMode);
            _back.interactable = room.Allows(PvpRoomAction.Leave);
            _matchLabel.text = room.MatchLabel;
            _modeLabel.text = room.ModeLabel;
        }
        [RevButtonClick("connect")]
        private void Connect()
        {
            Service.StartServer(_endpoint.text, _nickname.text);
        }
        [RevButtonClick("match")]
        private void Match()
        {
            Service.Match();
        }
        [RevButtonClick("mode")]
        private void Mode()
        {
            Service.SetRookie(!Service.Config.rookie);
        }
        [RevButtonClick("back")]
        private void Back()
        {
            Service.LeaveRoom();
            RevUI.Get<PvpPanel>().ShowDestination("Entry");
        }
        protected override void OnClose()
        {
            RevTimer.CancelAllOf(this);
        }
    }
}

using Revolution;
using UnityEngine.UI;
namespace RevGame.Pvp
{
    /// <summary>服务器匹配独立面板：队列秒数来自Service真实排队起点，不靠界面假倒计时。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed class PvpServerPanel : RevUIPanel
    {
        /// <summary>服务器地址字段的正式名称，旧资源包打开时也使用此文案。</summary>
        public const string ServerAddressLabel = "服务器地址";
        /// <summary>未连接时显示的填写说明，填写示意不会进入输入值。</summary>
        public const string ServerAddressHint = "请输入服务器域名或公网 IP 和端口号（域名:端口 或 IP:端口）";
        /// <summary>输入框上限容纳最长域名、末尾点、冒号和五位端口。</summary>
        public const int ServerAddressLimit = 260;
        /// <summary>首次装配时适配安全框，并更新旧资源包中的服务器输入控件。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            _endpointLabel.text = ServerAddressLabel;
            _endpoint.characterLimit = ServerAddressLimit;
            _endpoint.onValueChanged.AddListener(ClearAddressError);
        }

        [RevBind("nickname")] private InputField _nickname;
        /// <summary>玩家可编辑的服务器域名或 IP 和端口，没有成功记录时保持空白。</summary>
        [RevBind("endpoint")] private InputField _endpoint;
        /// <summary>已有预制体内的地址字段标签，兼容旧 AB 的节点名称。</summary>
        [RevBind("endpointLabel")] private Text _endpointLabel;
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
        /// <summary>本次输入的拒绝原因，修改地址或重新打开面板后清除。</summary>
        private string _addressError;
        private PvpService Service { get { return RevServices.GetRequired<PvpService>(); } }
        /// <summary>回填上次握手成功的地址，玩家可修改后主动连接。</summary>
        protected override void OnOpen()
        {
            _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            _addressError = null;
            _endpoint.SetTextWithoutNotify(Service.LastSuccessfulServerAddress);
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }
        /// <summary>展示当前连接状态，未连接时在原队列说明处提示地址格式。</summary>
        protected override void OnRefreshView()
        {
            PvpService.PvpRoomSnapshot room = Service.RoomSnapshot;
            _state.text = _addressError ?? room.ServerStatusText;
            _duration.text = Service.Connected && _addressError == null ? room.QueueText : ServerAddressHint;
            _match.interactable = room.Allows(PvpRoomAction.Match);
            _connect.interactable = room.Allows(PvpRoomAction.Connect);
            _mode.interactable = room.Allows(PvpRoomAction.ChangeMode);
            _back.interactable = room.Allows(PvpRoomAction.Leave);
            _matchLabel.text = room.MatchLabel;
            _modeLabel.text = room.ModeLabel;
        }
        /// <summary>只提交玩家当前填写的地址，立即展示输入拒绝或正在连接的状态。</summary>
        [RevButtonClick("connect")]
        private void Connect()
        {
            _addressError = Service.StartServer(_endpoint.text, _nickname.text) ? null : Service.Status;
            RefreshView();
        }
        /// <summary>地址有改动时撤下上一次输入对应的错误。</summary>
        private void ClearAddressError(string value)
        {
            _addressError = null;
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
        /// <summary>释放面板时移除首次绑定的地址编辑监听。</summary>
        protected override void OnRelease()
        {
            _endpoint.onValueChanged.RemoveListener(ClearAddressError);
        }
    }
}

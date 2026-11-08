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
        protected override void OnBindView()
        {
            // 视图由RevBind装配；控件路由使用RevButtonClick，不在刷新时重复注册。
        }

        [RevBind("nickname")] private InputField _nickname;
        [RevBind("endpoint")] private InputField _endpoint;
        [RevBind("state")] private Text _state;
        [RevBind("duration")] private Text _duration;
        [RevBind("connect")] private Button _connect;
        [RevBind("match")] private Button _match;
        [RevBind("match/label")] private Text _matchLabel;
        [RevBind("mode/label")] private Text _modeLabel;
        private PvpService Service { get { return RevServices.GetRequired<PvpService>(); } }
        protected override void OnOpen()
        {
            _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            _endpoint.text = string.IsNullOrEmpty(Service.Endpoint) ? DefaultEndpoint : Service.Endpoint;
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }
        protected override void OnRefreshView()
        {
            int seconds = (int)Service.QueueElapsedSeconds;
            _state.text = Service.Queued ? "正在寻找棋友…" : Service.Connected ? "连接成功 · 可以开始匹配" : Service.Mode == PvpConnectionMode.Local ? "连接服务器，与在线棋友相遇" : Service.Status;
            _duration.text = Service.Queued ? "已匹配  " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00") : "准备好后开始匹配";
            _match.interactable = Service.Connected;
            _connect.interactable = !Service.Queued;
            _matchLabel.text = Service.Queued ? "取消匹配" : "开始匹配";
            _modeLabel.text = Service.Config.rookie ? "新手场 · 二次确认落子" : "进阶场 · 直接落子";
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

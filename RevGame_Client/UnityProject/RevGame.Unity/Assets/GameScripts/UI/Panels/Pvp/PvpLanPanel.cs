using System.Collections.Generic;
using RevGame.Pvp.Networking;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>局域网专属发现页。ScrollRect承载全部发现设备，行对象复用且邀请始终使用当次冻结的Id。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed class PvpLanPanel : RevUIPanel
    {
        protected override void OnBindView()
        {
            // 视图由RevBind装配；控件路由使用RevButtonClick，不在刷新时重复注册。
        }

        [RevBind("nickname")] private InputField _nickname;
        [RevBind("state")] private Text _state;
        [RevBind("scroll/viewport/content")] private RectTransform _content;
        [RevBind("scroll/viewport/content/item")] private GameObject _template;
        private sealed class Row
        {
            internal GameObject Object;
            internal Button Button;
            internal Text Name, Detail;
            internal string Id;
        }
        private readonly List<Row> _rows = new List<Row>();
        private PvpService Service { get { return RevServices.GetRequired<PvpService>(); } }
        protected override void OnOpen()
        {
            RevUI.Get<PvpPanel>()?.MountInvitation(transform);
            _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            _template.SetActive(false);
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }
        [RevButtonClick("search")]
        private void Search()
        {
            Service.StartLan(_nickname.text);
        }
        [RevButtonClick("back")]
        private void Back()
        {
            Service.LeaveRoom();
            RevUI.Get<PvpPanel>().ShowDestination("Entry");
        }
        protected override void OnRefreshView()
        {
            LanPeerTransport lan = Service.Lan;
            int count = lan == null ? 0 : lan.Peers.Count;
            _state.text = lan == null ? "连接同一Wi-Fi后开始搜索" : count == 0 ? "正在搜索附近棋友…\n请让对方也打开搜索，确认同一网络" : "已发现 " + count + " 台设备 · 点击发送邀请\n" + lan.Status;
            while (_rows.Count < count)
            {
                GameObject item = Instantiate(_template, _content);
                item.name = "device" + _rows.Count;
                var row = new Row { Object = item, Button = item.GetComponent<Button>(),
                    Name = item.transform.Find("name").GetComponent<Text>(), Detail = item.transform.Find("detail").GetComponent<Text>() };
                row.Button.onClick.AddListener(() => Service.Invite(row.Id));
                _rows.Add(row);
            }
            // 行高为预制体结构尺寸，不是玩法数值。内容超出Viewport时由ScrollRect裁切/滚动。
            _content.sizeDelta = new Vector2(0, Mathf.Max(1, count) * 116);
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                row.Object.SetActive(i < count);
                if (i >= count)
                {
                    row.Id = "";
                    continue;
                }
                LanPeerInfo peer = lan.Peers[i];
                row.Id = peer.Id;
                row.Name.text = peer.Name + (peer.Busy ? "  ·  对局中" : "  ·  点击邀请");
                row.Detail.text = peer.DeviceName + "  /  " + peer.DeviceModel + "\n" + peer.Address;
                row.Button.interactable = !peer.Busy;
                ((RectTransform)row.Object.transform).anchoredPosition = new Vector2(0, -i * 116);
            }
        }
        protected override void OnClose()
        {
            // 邀请控件的所有者仍为HUD；关闭发现页前归还，防止缓存面板持有其它页面的子节点。
            RevUI.Get<PvpPanel>()?.MountInvitation(null);
            RevTimer.CancelAllOf(this);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        public void ScrollForCheck()
        {
            var scroll = transform.Find("scroll").GetComponent<ScrollRect>();
            scroll.OnScroll(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
            {
                scrollDelta = new Vector2(0, -40)
            });
        }
        public object ScrollStateForCheck()
        {
            var scroll = transform.Find("scroll").GetComponent<ScrollRect>();
            return new Dictionary<string, object>
            {
                ["rows"] = Service.Lan == null ? 0 : Service.Lan.Peers.Count,
                ["contentHeight"] = _content.rect.height, ["viewportHeight"] = scroll.viewport.rect.height,
                ["position"] = scroll.verticalNormalizedPosition
            };
        }
#endif
    }
}

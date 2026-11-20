using System;
using System.Collections.Generic;
using RevGame.Pvp.Networking;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>局域网发现页，用稳定设备标识驱动可见行，邀请由服务提交。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, ExclusiveGroup = "PvpMenu", Mask = RevUIMaskMode.None)]
    public sealed partial class PvpLanPanel : RevUIPanel
    {
        /// <summary>竖向行距，与预制体上下留白一并计入，单位为 UI 本地坐标。</summary>
        private const float RowHeight = 116;
        /// <summary>这一轮发现页的虚拟列表，关闭和销毁时释放。</summary>
        private RevUIVirtualList<PeerRow> _devices;
        /// <summary>只随展示事实改变而替换的设备快照。</summary>
        private PeerRow[] _snapshot = Array.Empty<PeerRow>();
        /// <summary>昵称输入框。</summary>
        [RevBind("nickname")] private InputField _nickname;
        /// <summary>搜索状态文字。</summary>
        [RevBind("state")] private Text _state;
        /// <summary>实际使用的竖向滚动容器。</summary>
        [RevBind("scroll")] private ScrollRect _scroll;
        /// <summary>保持隐藏的现有行预制原型。</summary>
        [RevBind("scroll/viewport/content/item")] private RectTransform _template;

        /// <summary>单个设备的只读显示数据，不借用可在网络轮询中变化的数据对象。</summary>
        private readonly struct PeerRow : IEquatable<PeerRow>
        {
            /// <summary>设备稳定身份，用于邀请及行复用校验。</summary>
            public readonly string Id;
            /// <summary>设备上的玩家昵称。</summary>
            public readonly string Name;
            /// <summary>局域网设备名称。</summary>
            public readonly string Device;
            /// <summary>设备型号。</summary>
            public readonly string Model;
            /// <summary>展示用地址。</summary>
            public readonly string Address;
            /// <summary>对局中的设备不能邀请。</summary>
            public readonly bool Busy;

            /// <summary>一次复制值，用于列表跨帧保持相同的显示和邀请身份。</summary>
            public PeerRow(string id, string name, string device, string model, string address, bool busy)
            {
                Id = id;
                Name = name;
                Device = device;
                Model = model;
                Address = address;
                Busy = busy;
            }

            /// <summary>同一组可见字段无变化时不重新绑定。</summary>
            public bool Equals(PeerRow other)
            {
                return Id == other.Id && Name == other.Name && Device == other.Device
                    && Model == other.Model && Address == other.Address && Busy == other.Busy;
            }
        }

        /// <summary>权威设备发现和邀请服务。</summary>
        private PvpService Service => RevServices.GetRequired<PvpService>();

        /// <summary>复用项目现有的安全区和滚动模板。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            _template.gameObject.SetActive(false);
        }

        /// <summary>准备这次打开的列表。发布后由导航接管邀请控件挂载。</summary>
        protected override void OnOpen()
        {
            _nickname.text = string.IsNullOrEmpty(Service.Nickname) ? "棋友" : Service.Nickname;
            _devices = new RevUIVirtualList<PeerRow>(_scroll, _template, RowHeight, peer => peer.Id, BindPeer);
            RevTimer.Every(.2f, RefreshView, domain: RevTimeDomain.Unscaled, owner: this);
        }

        /// <summary>等待实际视口有稳定尺寸，再为这一帧准备可见行。</summary>
        protected override async RevTask<bool> PrepareFirstScreenAsync(RevCancellationToken token)
        {
            await RevTaskScheduler.NextFrame();
            if (token.IsCancelled || _devices == null)
            {
                return false;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (_rejectNextFirstScreen)
            {
                _rejectNextFirstScreen = false;
                return false;
            }
#endif
            _devices.RefreshViewport();
            return true;
        }

        /// <summary>转发开始搜索操作，数据由服务异步更新。</summary>
        [RevButtonClick("search")]
        private void Search()
        {
            Service.StartLan(_nickname.text);
        }

        /// <summary>释放搜索会话，再提交返回菜单。</summary>
        [RevButtonClick("back")]
        private void Back()
        {
            Service.LeaveRoom();
            RevUI.Get<PvpPanel>()?.ShowDestination("Entry");
        }

        /// <summary>更新快照和提示。网络同值通知不重新分配列表，也不重复写控件。</summary>
        protected override void OnRefreshView()
        {
            if (_devices == null)
            {
                return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (_benchmarkRunning)
            {
                return;
            }
            if (_fixtureRows != null)
            {
                _devices.SetItems(_fixtureRows);
                return;
            }
#endif
            LanPeerTransport lan = Service.Lan;
            IReadOnlyList<LanPeerInfo> peers = lan?.Peers;
            int count = peers?.Count ?? 0;
            string status = lan == null ? "连接同一Wi-Fi后开始搜索" : count == 0
                ? "正在搜索附近棋友…\n请让对方也打开搜索，确认同一网络"
                : "已发现 " + count + " 台设备 · 点击发送邀请\n" + lan.Status;
            if (_state.text != status)
            {
                _state.text = status;
            }
            bool changed = count != _snapshot.Length;
            for (int i = 0; !changed && i < count; i++)
            {
                changed = !CopyPeer(peers[i]).Equals(_snapshot[i]);
            }
            if (changed)
            {
                var snapshot = new PeerRow[count];
                for (int i = 0; i < count; i++)
                {
                    snapshot[i] = CopyPeer(peers[i]);
                }
                _snapshot = snapshot;
            }
            _devices.SetItems(_snapshot);
        }

        /// <summary>冻结单行可见内容，时间戳变化不触发重绑。</summary>
        private static PeerRow CopyPeer(LanPeerInfo peer)
        {
            return new PeerRow(peer.Id, peer.Name, peer.DeviceName, peer.DeviceModel, peer.Address, peer.Busy);
        }

        /// <summary>用现有行控件落屏，点击处理仅在本代身份仍有效时提交。</summary>
        private Action BindPeer(RevUIVirtualRow row, PeerRow peer)
        {
            RectTransform root = row.Root;
            root.name = "device" + row.Index;
            Button button = root.GetComponent<Button>();
            root.Find("name").GetComponent<Text>().text = peer.Name + (peer.Busy ? "  ·  对局中" : "  ·  点击邀请");
            root.Find("detail").GetComponent<Text>().text = peer.Device + "  /  " + peer.Model + "\n" + peer.Address;
            button.interactable = !peer.Busy;
            int version = row.BindingVersion;
            UnityEngine.Events.UnityAction listener = () =>
            {
                if (row.IsCurrent(version) && !peer.Busy)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                    if (_fixtureRows != null)
                    {
                        _fixtureClicked = peer.Id;
                        return;
                    }
#endif
                    Service.Invite(peer.Id);
                }
            };
            button.onClick.AddListener(listener);
            return () =>
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(listener);
                }
            };
        }

        /// <summary>菜单关闭之后归还列表。只有仍挂在本页的邀请控件才从本页移走。</summary>
        protected override void OnClose()
        {
            RevUI.Get<PvpPanel>()?.UnmountInvitation(transform);
            ReleaseList();
        }

        /// <summary>根节点直接退出时也对称取消所有行和刷新任务。</summary>
        protected override void OnRelease()
        {
            ReleaseList();
        }

        /// <summary>清理这一轮设备行、滚动订阅和快照。</summary>
        private void ReleaseList()
        {
            RevTimer.CancelAllOf(this);
            _devices?.Dispose();
            _devices = null;
            _snapshot = Array.Empty<PeerRow>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            _fixtureRows = null;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>仅授权的回归用例拒绝下一次真实列表首屏。</summary>
        private static bool _rejectNextFirstScreen;
        /// <summary>合成的只读设备快照，不写入网络发现列表。</summary>
        private PeerRow[] _fixtureRows;
        /// <summary>走真实行按钮后观察的设备键。</summary>
        private string _fixtureClicked = string.Empty;

        /// <summary>让已经取得真实预制体的下一次候选返回首屏拒绝。</summary>
        internal static void RejectNextFirstScreenForCheck()
        {
            _rejectNextFirstScreen = true;
        }

        /// <summary>验收收尾清掉尚未消费的缺陷注入。</summary>
        internal static void ClearFirstScreenCheck()
        {
            _rejectNextFirstScreen = false;
        }

        /// <summary>复用真实滚轮入口，验证列表裁剪和绑定都随滚动更新。</summary>
        public void ScrollForCheck()
        {
            _scroll.OnScroll(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
            {
                scrollDelta = new Vector2(0, -40)
            });
        }

        /// <summary>暴露逻辑行数、实际实例和顶部身份，不读取场外硬编码的数据。</summary>
        public object ScrollStateForCheck()
        {
            int first = _devices?.FirstVisibleIndex ?? -1;
            RevUIVirtualRow row = null;
            _devices?.TryGetRow(first, out row);
            RevUIVirtualRow target = null;
            _devices?.TryGetRow(first + 1, out target);
            return new Dictionary<string, object>
            {
                ["rows"] = _devices?.Count ?? 0,
                ["active"] = _devices?.ActiveCount ?? 0,
                ["instances"] = _devices?.InstanceCount ?? 0,
                ["contentHeight"] = _scroll.content.rect.height,
                ["viewportHeight"] = _scroll.viewport.rect.height,
                ["position"] = _scroll.verticalNormalizedPosition,
                ["first"] = first,
                ["firstKey"] = row?.Key ?? string.Empty,
                ["clickQuery"] = target == null ? string.Empty : "PvpLanPanel/scroll/viewport/content/" + target.Root.name,
                ["clickKey"] = target?.Key ?? string.Empty,
                ["clicked"] = _fixtureClicked
            };
        }

        /// <summary>加载可复现的千条显示数据，实际邀请分支只记录点击键。</summary>
        internal void LoadListFixture()
        {
            _fixtureClicked = string.Empty;
            _fixtureRows = new PeerRow[1000];
            for (int i = 0; i < _fixtureRows.Length; i++)
            {
                _fixtureRows[i] = new PeerRow("peer-" + i, "棋友 " + i, "测试设备", "观察组", "loopback", false);
            }
            _devices.SetItems(_fixtureRows);
        }

        /// <summary>跳到指定视口位置，真实节点随后由绑定窗口呈现。</summary>
        internal void MoveListFixture(float normalized)
        {
            _scroll.StopMovement();
            _scroll.verticalNormalizedPosition = normalized;
            _devices.RefreshViewport();
        }

        /// <summary>删除当前顶部条目并反转排序，用于核对滚动锚点和回收后的输入身份。</summary>
        internal void ReorderListFixture()
        {
            int previous = _devices.FirstVisibleIndex;
            var next = new PeerRow[_fixtureRows.Length - 1];
            int cursor = 0;
            for (int i = _fixtureRows.Length - 1; i >= 0; i--)
            {
                if (i != previous)
                {
                    next[cursor++] = _fixtureRows[i];
                }
            }
            _fixtureRows = next;
            _devices.SetItems(_fixtureRows);
        }

        /// <summary>只供测试捕获一份真实行版本，之后验证迟到回调被识别为旧绑定。</summary>
        internal RevUIVirtualRow GetFirstFixtureRow()
        {
            _devices.TryGetRow(_devices.FirstVisibleIndex, out RevUIVirtualRow row);
            return row;
        }
#endif
    }
}

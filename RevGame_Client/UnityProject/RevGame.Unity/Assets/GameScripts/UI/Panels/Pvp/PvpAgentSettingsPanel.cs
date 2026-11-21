using RevGame.Pvp.Agent;
using Revolution;
using UnityEngine.UI;
#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#endif

namespace RevGame.Pvp
{
    /// <summary>设置只装配和转发。网页使用原生密码输入，关闭时归还浏览器焦点及控件。</summary>
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
        [RevBind("keyLabel")] private Text _keyLabel;
        [RevBind("disclosure")] private Text _disclosure;
        [RevBind("model")] private InputField _model;
        [RevBind("state")] private Text _state;
#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>测量 Key 输入框的四角，在窗口尺寸或安全框变化时复用。</summary>
        private readonly Vector3[] _keyCorners = new Vector3[4];
        /// <summary>标记当前面板拥有浏览器密码输入框。</summary>
        private bool _browserKeyOpen;
        /// <summary>面板打开前的全页键盘捕获状态，关闭时归还给 Unity。</summary>
        private bool _previousKeyboardCapture;
        /// <summary>最近一次对齐所用 Viewport。窗口尺寸变化才重新测量。</summary>
        private Vector2Int _keyViewport;
        /// <summary>Unity 安全框或布局变化后，在下一次 UI 更新时对齐一次。</summary>
        private bool _keyLayoutDirty;
#endif
        /// <summary>首次装配时让漩涡铺满屏幕，菜单底板和控件仍使用设备安全框。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            _key.contentType = InputField.ContentType.Password;
            if (PvpAgentSettingsStore.UsesBrowserSession)
            {
                _keyLabel.text = "API Key（仅当前页面）";
                _disclosure.text = "启用后人机对手、落子建议和自动胜率分析可调用 DeepSeek 并消耗 API 额度。\n刷新或关闭网页后清除 Key。";
            }
            else if (PvpAgentSettingsStore.UsesTextStorage)
            {
                _keyLabel.text = "API Key（本机明文TXT）";
                _disclosure.text = "启用后人机对手、落子建议和自动胜率分析可调用 DeepSeek 并消耗 API 额度。\nKey 保存在本机明文 TXT。";
            }
            else
            {
                _disclosure.text = "启用后人机对手、落子建议和自动胜率分析可调用 DeepSeek 并消耗 API 额度。\nKey 在本机加密保存。";
            }
        }

        /// <summary>读当前服务配置。网页 Key 放入浏览器密码控件，避免 Unity 输入框截走粘贴事件。</summary>
        protected override void OnOpen()
        {
            var settings = RevServices.GetRequired<PvpService>().AgentSettings;
#if UNITY_WEBGL && !UNITY_EDITOR
            OpenBrowserKey(settings.Key);
#else
            _key.SetTextWithoutNotify(settings.Key);
#endif
            _model.SetTextWithoutNotify(settings.Model);
        }
        protected override void OnRefreshView()
        {
            _state.text = RevServices.GetRequired<PvpService>().AgentStatus;
        }
        /// <summary>只在玩家点击授权时从当前输入取 Key，并交给同一个业务入口。</summary>
        [RevButtonClick("enable")]
        private void EnableCloud()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string key = ReadBrowserKey();
#else
            string key = _key.text;
#endif
            RevServices.GetRequired<PvpService>().ConfigureSavedAgent(key, _model.text, true);
            RefreshView();
        }
        /// <summary>撤销授权并清空控件，包括浏览器输入中尚未提交的 Key。</summary>
        [RevButtonClick("disable")]
        private void DisableCloud()
        {
            if (RevServices.GetRequired<PvpService>().ConfigureSavedAgent("", _model.text, false))
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                RevGame_SetAgentKey("");
#endif
                _key.SetTextWithoutNotify("");
            }
            RefreshView();
        }
        [RevButtonClick("back")]
        private void Back()
        {
            CloseSelf();
        }
        /// <summary>关页或退域时销毁网页控件、归还键盘焦点并清除控件里的密码。</summary>
        protected override void OnClose()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_browserKeyOpen)
            {
                _browserKeyOpen = false;
                RevGame_CloseAgentKey();
                WebGLInput.captureAllKeyboardInput = _previousKeyboardCapture;
            }
#endif
            _key.SetTextWithoutNotify("");
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>让真实 HTML 密码控件接收浏览器键盘。Unity 保留输入框原有美术背景。</summary>
        private void OpenBrowserKey(string value)
        {
            _previousKeyboardCapture = WebGLInput.captureAllKeyboardInput;
            WebGLInput.captureAllKeyboardInput = false;
            _key.enabled = false;
            _key.textComponent.enabled = false;
            if (_key.placeholder != null)
            {
                _key.placeholder.enabled = false;
            }
            RevGame_SetAgentKey(value);
            _browserKeyOpen = true;
            _keyLayoutDirty = true;
            Canvas.ForceUpdateCanvases();
            LateUpdate();
        }

        /// <summary>布局事件只标脏。真正的坐标在通过布局更新后写给浏览器。</summary>
        private void OnRectTransformDimensionsChange()
        {
            _keyLayoutDirty = true;
        }

        /// <summary>仅在窗口尺寸或 RectTransform 布局变化时对齐 DOM 控件，不逐帧读写文本。</summary>
        private void LateUpdate()
        {
            if (!_browserKeyOpen)
            {
                return;
            }
            var viewport = new Vector2Int(Screen.width, Screen.height);
            if (!_keyLayoutDirty && _keyViewport == viewport)
            {
                return;
            }
            _keyViewport = viewport;
            _keyLayoutDirty = false;
            ((RectTransform)_key.transform).GetWorldCorners(_keyCorners);
            Canvas canvas = _key.targetGraphic.canvas.rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, _keyCorners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, _keyCorners[2]);
            if (viewport.x > 0 && viewport.y > 0 && max.x > min.x && max.y > min.y)
            {
                RevGame_LayoutAgentKey(min.x / viewport.x, 1f - max.y / viewport.y,
                    (max.x - min.x) / viewport.x, (max.y - min.y) / viewport.y);
            }
        }

        /// <summary>提交时读取有界 UTF-8 字节。读取完成即清零临时缓冲，业务服务持有授权后的字符串。</summary>
        private static string ReadBrowserKey()
        {
            // 浏览器输入最多 512 个 UTF-16 代码单元，2048 字节足以容纳其 UTF-8 表示。
            var bytes = new byte[2048];
            try
            {
                int length = RevGame_CopyAgentKey(bytes, bytes.Length);
                return length > 0 && length <= bytes.Length ? Encoding.UTF8.GetString(bytes, 0, length) : "";
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        /// <summary>创建或重设当前画布的浏览器密码输入。</summary>
        [DllImport("__Internal")]
        private static extern void RevGame_SetAgentKey(string value);

        /// <summary>以画布归一化坐标对齐 HTML 输入框，顶部为浏览器坐标原点。</summary>
        [DllImport("__Internal")]
        private static extern void RevGame_LayoutAgentKey(float left, float top, float width, float height);

        /// <summary>提交时从浏览器复制密码到托管缓冲，返回有效 UTF-8 字节数。</summary>
        [DllImport("__Internal")]
        private static extern int RevGame_CopyAgentKey([Out] byte[] buffer, int capacity);

        /// <summary>清空并移除密码控件。</summary>
        [DllImport("__Internal")]
        private static extern void RevGame_CloseAgentKey();
#endif
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>合并屏幕布局请求。Unity 回调负责即时失效，少数没有回调的输入由共享计时器低频检查。</summary>
    public static class RevUILayoutScheduler
    {
        /// <summary>相机输出、设备安全区和外部变换静默变化时的检查周期，单位为真实秒。</summary>
        private const float CheckInterval = .25f;
        /// <summary>显式所有者使共享计时器贯穿面板和场景的生命周期。</summary>
        private static readonly object TimerOwner = new object();
        /// <summary>本轮先刷新原画，使内容读取最新的原画边界。</summary>
        private static readonly HashSet<Action> Artwork = new HashSet<Action>();
        /// <summary>本轮等待刷新的内容或业务布局，重复请求合并。</summary>
        private static readonly HashSet<Action> Content = new HashSet<Action>();
        /// <summary>遍历批次的复用缓冲，允许回调临时新增或撤销其它请求。</summary>
        private static readonly List<Action> DispatchBuffer = new List<Action>();
        /// <summary>复用渲染委托，队列清空后解除订阅。</summary>
        private static readonly Canvas.WillRenderCanvases RenderCallback = Flush;
        /// <summary>读取没有统一 Unity 变化回调的输入。订阅者只需请求一次刷新。</summary>
        private static Action _checkForChanges;
        /// <summary>所有启用布局共用的低频检查计时器。</summary>
        private static RevTimerHandle _timer;
        /// <summary>是否为当前队列挂上了渲染回调。</summary>
        private static bool _renderSubscribed;
        /// <summary>标记当前批次，避免 ForceUpdateCanvases 等同步入口重入。</summary>
        private static bool _flushing;
        /// <summary>同一帧共享设备安全区采样，单位为归一化主窗口坐标。</summary>
        private static Rect _screenSafe;
        /// <summary>最近实际读取设备信息的帧号。</summary>
        private static int _sampledFrame = -1;

        /// <summary>运行时每 250 毫秒发起一次共享检查，回前台也会检查。订阅要和面板启停配对。</summary>
        public static event Action CheckForChanges
        {
            add
            {
                if (value == null || !Application.isPlaying)
                {
                    return;
                }
                if (_checkForChanges == null)
                {
                    Application.focusChanged += OnFocusChanged;
                }
                _checkForChanges += value;
                if (!_timer.IsAlive)
                {
                    _timer = RevTimer.Every(CheckInterval, CheckInputs, domain: RevTimeDomain.Unscaled, owner: TimerOwner);
                }
            }
            remove
            {
                _checkForChanges -= value;
                if (_checkForChanges == null)
                {
                    _timer.Stop();
                    _timer = default;
                    Application.focusChanged -= OnFocusChanged;
                }
            }
        }

        /// <summary>请求在下一次 Canvas 排版前刷新一次。稳定时保持队列与渲染回调空闲。</summary>
        public static void RequestRefresh(Action callback)
        {
            Enqueue(Content, callback);
        }

        /// <summary>撤销未执行的请求，组件关闭或回池时调用。</summary>
        public static void CancelRefresh(Action callback)
        {
            Artwork.Remove(callback);
            Content.Remove(callback);
            if (Artwork.Count == 0 && Content.Count == 0)
            {
                UnsubscribeRender();
            }
        }

        /// <summary>返回本帧布局共用的主窗口安全区。编辑态读取现场值。</summary>
        public static Rect GetScreenSafeArea()
        {
            if (!Application.isPlaying)
            {
                return RevUIViewport.GetScreenSafeArea();
            }
            int frame = Time.frameCount;
            if (frame != _sampledFrame)
            {
                _screenSafe = RevUIViewport.GetScreenSafeArea();
                _sampledFrame = frame;
            }
            return _screenSafe;
        }

        /// <summary>把原画刷新放到内容刷新之前。</summary>
        internal static void RequestArtworkRefresh(Action callback)
        {
            Enqueue(Artwork, callback);
        }

        /// <summary>只在首个有效请求到来时订阅渲染回调。</summary>
        private static void Enqueue(HashSet<Action> queue, Action callback)
        {
            if (callback != null && Application.isPlaying && queue.Add(callback) && !_flushing)
            {
                SubscribeRender();
            }
        }

        /// <summary>队列有工作时，等待 Canvas 收集这一轮变更。</summary>
        private static void SubscribeRender()
        {
            if (!_renderSubscribed)
            {
                Canvas.preWillRenderCanvases += RenderCallback;
                _renderSubscribed = true;
            }
        }

        /// <summary>去掉渲染回调，稳定帧无需检查空列表。</summary>
        private static void UnsubscribeRender()
        {
            if (_renderSubscribed)
            {
                Canvas.preWillRenderCanvases -= RenderCallback;
                _renderSubscribed = false;
            }
        }

        /// <summary>先原画后内容。刷新的回调可请求后续工作，留待下一批执行。</summary>
        private static void Flush()
        {
            UnsubscribeRender();
            _flushing = true;
            try
            {
                Dispatch(Artwork);
                Dispatch(Content);
            }
            finally
            {
                _flushing = false;
                if (Artwork.Count > 0 || Content.Count > 0)
                {
                    SubscribeRender();
                }
            }
        }

        /// <summary>在稳定快照上遍历，通过队列成员状态识别已撤销的请求。</summary>
        private static void Dispatch(HashSet<Action> queue)
        {
            DispatchBuffer.AddRange(queue);
            foreach (Action callback in DispatchBuffer)
            {
                if (!queue.Remove(callback))
                {
                    continue;
                }
                try
                {
                    callback();
                }
                catch (Exception error)
                {
                    RevLog.Exception(error, "UI 布局刷新失败", "UI");
                }
            }
            DispatchBuffer.Clear();
        }

        /// <summary>定时唤醒各布局，让它们在刷新前核对缓存的输入。</summary>
        private static void CheckInputs()
        {
            _checkForChanges?.Invoke();
        }

        /// <summary>重新获得焦点时优先同步设备环境。</summary>
        private static void OnFocusChanged(bool focused)
        {
            if (focused)
            {
                _sampledFrame = -1;
                CheckInputs();
            }
        }

        /// <summary>关闭域重载时，同样归还上一轮渲染队列和观察者。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            UnsubscribeRender();
            Application.focusChanged -= OnFocusChanged;
            _timer.Stop();
            _timer = default;
            _checkForChanges = null;
            _sampledFrame = -1;
            _flushing = false;
            Artwork.Clear();
            Content.Clear();
            DispatchBuffer.Clear();
        }
    }
}

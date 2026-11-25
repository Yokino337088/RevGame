// ============================================================
// RevUIAdaptiveLayout.cs —— 让内容容器随窗口和设备安全区调整。
// 组件拥有内容容器的布局属性，外层面板保留动画、背景和遮罩的所有权。
// ============================================================
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>内容容器启用适配时采用的布局策略。</summary>
    public enum RevUIAdaptiveLayoutMode
    {
        /// <summary>内容延展到安全区，子控件沿用原有锚点与布局组。</summary>
        SafeArea,

        /// <summary>内容保持固定设计尺寸，等比内接并在安全区居中。</summary>
        FitDesignFrame
    }

    /// <summary>挂在全画幅面板下的独立内容容器上。框架面板根仍可播放转场，全屏背景与遮罩可放在内容容器外。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("Revolution/UI/Adaptive Layout")]
    public sealed class RevUIAdaptiveLayout : UIBehaviour
    {
        // 默认为安全区布局，业务也可以通过 Configure 在运行期切换。
        [SerializeField] private RevUIAdaptiveLayoutMode mode = RevUIAdaptiveLayoutMode.SafeArea;
        [Tooltip("等比设计框的尺寸。留零时跟随根 CanvasScaler 的参考分辨率。")]
        [SerializeField] private Vector2 designResolution;
        /// <summary>可选的同画布内容边界。可引用独立原画节点，使控件使用原画与安全区的交集。</summary>
        [Tooltip("可选内容边界。和安全区相交后决定内容可用范围。请引用独立于内容的节点。")]
        [SerializeField] private RectTransform contentBounds;

        // 由本组件接管的内容节点，停用后重新读取其预制体布局。
        private RectTransform _content;
        // 提供整张屏幕空间画幅的直接父级。
        private RectTransform _parent;
        // 当前所属的根画布，换父级或画布层级变化后刷新绑定。
        private Canvas _canvas;
        // 等比模式未指定设计尺寸时读取当前根画布的参考尺寸。
        private CanvasScaler _scaler;
        // 本次接管前的布局，用于停用和画布不可用时还原。
        private RevUIViewport.RectState _original;
        // Canvas 或父级可能已经变化，下一次布局前重新找到它们。
        private bool _needsBinding = true;
        // 当前是否已经接管 RectTransform，同时标识布局缓存是否有效。
        private bool _applied;
        // 上一次写入布局使用的父级归一化可用区域。
        private Rect _lastSafeArea;
        // 上一次根画布的像素范围，同宽高比的分辨率变化也能派发通知。
        private Rect _lastPixels;
        // 上一次父级的布局单位尺寸。
        private Vector2 _lastParentSize;
        // 上一次使用的设计尺寸，安全区模式取零。
        private Vector2 _lastDesignResolution;
        // 上一次生效的策略，保证模式切换会重新计算。
        private RevUIAdaptiveLayoutMode _lastMode;
        /// <summary>合并尺寸、原画和设备变化时复用的布局回调。</summary>
        private Action _refresh;
        /// <summary>内容布局写入中，忽略自己引发的尺寸通知。</summary>
        private bool _writing;
        /// <summary>缓存稳定输入，屏幕投影只在输入有实际变化时执行。</summary>
        private LayoutInputs _lastInputs;
        /// <summary>是否已经采集当前父级的输入。</summary>
        private bool _haveInputs;
        /// <summary>最近绑定的独立边界，边界换图时更新订阅。</summary>
        private RectTransform _observedBounds;
        /// <summary>由框架管理的原画直接通知边界变化，和内容在同一批刷新。</summary>
        private RevUIArtworkLayout _observedArtwork;

        /// <summary>内容相对父级画幅的归一化矩形。适配组件停用时为完整画幅。</summary>
        public Rect Viewport
        {
            get;
            private set;
        } = RevUIViewport.Full;

        /// <summary>布局已写入。窗口像素尺寸、可用区域或布局配置变化时派发，归一化矩形相同时也可重新取像素尺寸。</summary>
        public event Action<Rect> ViewportChanged;

        /// <summary>设置布局策略和设计尺寸，沿用当前指定的内容边界。对已启用的组件立即应用。</summary>
        public void Configure(RevUIAdaptiveLayoutMode layoutMode, Vector2 referenceResolution = default)
        {
            Configure(layoutMode, referenceResolution, contentBounds);
        }

        /// <summary>设置布局策略、设计尺寸和独立内容边界。传空边界时使用父级与设备安全区的交集。</summary>
        public void Configure(RevUIAdaptiveLayoutMode layoutMode, Vector2 referenceResolution, RectTransform bounds)
        {
            mode = layoutMode;
            designResolution = referenceResolution;
            contentBounds = bounds;
            RevUILayoutScheduler.CancelRefresh(_refresh);
            RefreshLayout();
        }

        // 内容首次开启或复用显示时立即尝试排版。
        protected override void OnEnable()
        {
            base.OnEnable();
            _refresh ??= RefreshLayout;
            RevUILayoutScheduler.CheckForChanges += RequestRefresh;
            RefreshLayout();
        }

        // 停用时归还布局，待下次显示重新取当时的预制体属性。
        protected override void OnDisable()
        {
            RevUILayoutScheduler.CheckForChanges -= RequestRefresh;
            RevUILayoutScheduler.CancelRefresh(_refresh);
            ObserveArtwork(null);
            RestoreLayout();
            _content = null;
            _parent = null;
            _canvas = null;
            _scaler = null;
            _lastInputs = default;
            _needsBinding = true;
            base.OnDisable();
        }

        // 内容换父级后让布局读新的可用尺寸。
        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            _needsBinding = true;
            RequestRefresh();
        }

        // RevUI 换画布时刷新相机、屏幕和 CanvasScaler 的引用。
        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            _needsBinding = true;
            RequestRefresh();
        }

        /// <summary>父级与容器尺寸变化时，安排一轮 Canvas 更新。</summary>
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            RequestRefresh();
        }

        /// <summary>合并引擎事件和低频检查，避免在事件内部递归重排。</summary>
        private void RequestRefresh()
        {
            if (!_writing && isActiveAndEnabled)
            {
                _refresh ??= RefreshLayout;
                RevUILayoutScheduler.RequestRefresh(_refresh);
            }
        }

        // 先绑定宿主和读输入，再一次写入内容，最后通知订阅者读取新的结果。
        private void RefreshLayout()
        {
            if (!isActiveAndEnabled || _writing)
            {
                return;
            }

            if (_content == null)
            {
                _content = (RectTransform)transform;
                _original = new RevUIViewport.RectState(_content);
                _needsBinding = true;
            }

            bool bindingChanged = _needsBinding;
            if (bindingChanged)
            {
                _parent = _content.parent as RectTransform;
                Canvas owner = _parent == null ? null : _parent.GetComponentInParent<Canvas>();
                _canvas = owner == null ? null : owner.rootCanvas;
                _scaler = _canvas == null ? null : _canvas.GetComponent<CanvasScaler>();
                _needsBinding = false;
            }

            // 屏幕安全区作用于屏幕空间内容。Canvas 脱离显示上下文时归还已接管的布局。
            if (_parent == null || _canvas == null || !_canvas.isActiveAndEnabled || _canvas.renderMode == RenderMode.WorldSpace)
            {
                ObserveArtwork(null);
                RestoreLayout();
                return;
            }

            Vector2 size = _parent.rect.size;
            if (!RevUIViewport.HasSize(size))
            {
                return;
            }

            Vector2 reference = mode == RevUIAdaptiveLayoutMode.FitDesignFrame ? ResolveDesignResolution() : Vector2.zero;
            LayoutInputs inputs = ReadInputs(reference);
            ObserveArtwork(inputs.Bounds);
            if (_haveInputs && !bindingChanged && inputs.Matches(in _lastInputs))
            {
                return;
            }
            _lastInputs = inputs;
            _haveInputs = true;
            if (!TryReadViewport(in inputs, out Rect safe))
            {
                return;
            }
            Rect pixels = inputs.Pixels;
            if (_applied && !bindingChanged && safe == _lastSafeArea && pixels == _lastPixels &&
                size == _lastParentSize && reference == _lastDesignResolution && mode == _lastMode)
            {
                return;
            }

            _lastSafeArea = safe;
            _lastPixels = pixels;
            _lastParentSize = size;
            _lastDesignResolution = reference;
            _lastMode = mode;

            _writing = true;
            try
            {
                if (mode == RevUIAdaptiveLayoutMode.FitDesignFrame && RevUIViewport.HasSize(reference))
                {
                    Viewport = RevUIViewport.FitDesignFrame(size, safe, reference);
                    RevUIViewport.ApplyDesignFrame(_content, Viewport, reference);
                }
                else
                {
                    Viewport = safe;
                    _content.anchorMin = safe.min;
                    _content.anchorMax = safe.max;
                    _content.offsetMin = _content.offsetMax = Vector2.zero;
                    _content.localScale = new Vector3(1f, 1f, _original.Scale.z);
                }
                _applied = true;
            }
            finally
            {
                _writing = false;
            }
            ViewportChanged?.Invoke(Viewport);
        }

        // 优先使用显式设计尺寸，可随 CanvasScaler 的横竖屏策略自动改用参考尺寸。
        private Vector2 ResolveDesignResolution()
        {
            if (RevUIViewport.HasSize(designResolution))
            {
                return designResolution;
            }
            return _scaler == null ? RevUISetting.ReferenceResolution : _scaler.referenceResolution;
        }

        /// <summary>读取用于检测变化的原始值，稳定时省去画布和原画的屏幕投影。</summary>
        private LayoutInputs ReadInputs(Vector2 reference)
        {
            Camera camera = _canvas.renderMode == RenderMode.ScreenSpaceCamera ? _canvas.worldCamera : null;
            Rect pixels = camera == null ? _canvas.pixelRect : camera.pixelRect;
            int display = camera == null ? _canvas.targetDisplay : camera.targetDisplay;
            bool useDeviceSafeArea = display == 0 && (camera == null || camera.targetTexture == null);
            Rect screenSafe = useDeviceSafeArea ? RevUILayoutScheduler.GetScreenSafeArea() : RevUIViewport.Full;
            Rect output = useDeviceSafeArea && camera != null ? camera.rect : RevUIViewport.Full;
            RectTransform bounds = contentBounds != null && !contentBounds.IsChildOf(_content) &&
                contentBounds.IsChildOf(_canvas.transform) ? contentBounds : null;
            return new LayoutInputs(camera, pixels, screenSafe, output, _parent, bounds, reference, mode);
        }

        /// <summary>边界由原画组件控制时，换图和尺寸改变由原画组件主动通知。</summary>
        private void ObserveArtwork(RectTransform bounds)
        {
            if (ReferenceEquals(bounds, _observedBounds))
            {
                return;
            }
            if (!ReferenceEquals(_observedArtwork, null))
            {
                _observedArtwork.BoundsChanged -= RequestRefresh;
            }
            _observedBounds = bounds;
            _observedArtwork = bounds == null ? null : bounds.GetComponent<RevUIArtworkLayout>();
            if (_observedArtwork != null)
            {
                _observedArtwork.BoundsChanged += RequestRefresh;
            }
        }

        /// <summary>仅在检测到变化时把副屏、纹理或主屏安全区与父级和原画的投影统一到像素坐标。</summary>
        private bool TryReadViewport(in LayoutInputs inputs, out Rect safe)
        {
            safe = default;
            Rect pixels = inputs.Pixels;
            if (!RevUIViewport.HasArea(pixels) ||
                !RevUIViewport.TryNormalizeSafeArea(inputs.ScreenSafe, inputs.Output, out Rect relative))
            {
                return false;
            }
            Camera camera = inputs.Camera;
            var allowed = new Rect(pixels.position + Vector2.Scale(relative.position, pixels.size),
                Vector2.Scale(relative.size, pixels.size));
            Rect parentPixels = _parent == _canvas.transform ? pixels : RevUIViewport.GetScreenBounds(_parent, camera);
            if (inputs.Bounds != null)
            {
                allowed = RevUIViewport.Intersect(allowed, RevUIViewport.GetScreenBounds(inputs.Bounds, camera));
            }
            return RevUIViewport.TryNormalizeSafeArea(allowed, parentPixels, out safe);
        }

        // 放弃接管后释放布局缓存，下一次可用时会重新应用。
        private void RestoreLayout()
        {
            if (_applied && _content != null)
            {
                _original.Restore(_content);
            }
            _applied = false;
            _haveInputs = false;
            Viewport = RevUIViewport.Full;
        }

        /// <summary>一轮布局的坐标输入。矩阵仅用于低频或事件触发的变更检测。</summary>
        private readonly struct LayoutInputs
        {
            /// <summary>屏幕相机。Overlay 画布使用空引用。</summary>
            public readonly Camera Camera;
            /// <summary>相机或画布输出的屏幕像素范围。</summary>
            public readonly Rect Pixels;
            /// <summary>归一化的主屏安全区。</summary>
            public readonly Rect ScreenSafe;
            /// <summary>屏幕相机的归一化输出区域，Overlay 使用完整范围。</summary>
            public readonly Rect Output;
            /// <summary>已校验同属根画布的独立内容边界。</summary>
            public readonly RectTransform Bounds;
            /// <summary>父级局部矩形，尺寸和锚点偏移变化均可检测。</summary>
            private readonly Rect _parentRect;
            /// <summary>原画或另一个内容框的局部范围。</summary>
            private readonly Rect _boundsRect;
            /// <summary>父级变换，补查平移或缩放等不触发尺寸回调的变化。</summary>
            private readonly Matrix4x4 _parentTransform;
            /// <summary>原画节点的世界变换。</summary>
            private readonly Matrix4x4 _boundsTransform;
            /// <summary>相机投影矩阵，识别固定尺寸内的投影参数变化。</summary>
            private readonly Matrix4x4 _projection;
            /// <summary>相机视图矩阵，屏幕空间相机变化时参与检测。</summary>
            private readonly Matrix4x4 _view;
            /// <summary>内容当前使用的设计尺寸。</summary>
            private readonly Vector2 _design;
            /// <summary>当前选择的布局策略。</summary>
            private readonly RevUIAdaptiveLayoutMode _mode;

            /// <summary>在访问屏幕投影前一次性采集各变换与属性。</summary>
            public LayoutInputs(Camera camera, Rect pixels, Rect screenSafe, Rect output, RectTransform parent,
                RectTransform bounds, Vector2 design, RevUIAdaptiveLayoutMode mode)
            {
                Camera = camera;
                Pixels = pixels;
                ScreenSafe = screenSafe;
                Output = output;
                Bounds = bounds;
                _parentRect = parent.rect;
                _parentTransform = parent.localToWorldMatrix;
                _boundsRect = bounds == null ? default : bounds.rect;
                _boundsTransform = bounds == null ? default : bounds.localToWorldMatrix;
                _projection = camera == null ? default : camera.projectionMatrix;
                _view = camera == null ? default : camera.worldToCameraMatrix;
                _design = design;
                _mode = mode;
            }

            /// <summary>逐个比较原始输入。变化为零时跳过坐标投影与布局计算。</summary>
            public bool Matches(in LayoutInputs previous)
            {
                return Camera == previous.Camera && Bounds == previous.Bounds && Pixels.Equals(previous.Pixels) &&
                    ScreenSafe.Equals(previous.ScreenSafe) && Output.Equals(previous.Output) &&
                    _parentRect.Equals(previous._parentRect) && _boundsRect.Equals(previous._boundsRect) &&
                    _parentTransform.Equals(previous._parentTransform) && _boundsTransform.Equals(previous._boundsTransform) &&
                    _projection.Equals(previous._projection) && _view.Equals(previous._view) &&
                    _design.Equals(previous._design) && _mode == previous._mode;
            }
        }
    }
}

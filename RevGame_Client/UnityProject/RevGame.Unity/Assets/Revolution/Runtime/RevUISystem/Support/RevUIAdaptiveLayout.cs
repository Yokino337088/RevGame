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
            RefreshLayout();
        }

        // 内容首次开启或复用显示时立即尝试排版。
        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshLayout();
        }

        // 停用时归还布局，待下次显示重新取当时的预制体属性。
        protected override void OnDisable()
        {
            RestoreLayout();
            _content = null;
            _needsBinding = true;
            base.OnDisable();
        }

        // 内容换父级后让布局读新的可用尺寸。
        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            _needsBinding = true;
        }

        // RevUI 换画布时刷新相机、屏幕和 CanvasScaler 的引用。
        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            _needsBinding = true;
        }

        // 窗口拖动、设备旋转和刘海变化均在这里比较。输入稳定时保留当前 RectTransform。
        private void LateUpdate()
        {
            RefreshLayout();
        }

        // 先绑定宿主和读输入，再一次写入内容，最后通知订阅者读取新的结果。
        private void RefreshLayout()
        {
            if (!isActiveAndEnabled)
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
                RestoreLayout();
                return;
            }

            Vector2 size = _parent.rect.size;
            if (!RevUIViewport.HasSize(size) || !TryReadViewport(out Rect pixels, out Rect safe))
            {
                return;
            }

            Vector2 reference = mode == RevUIAdaptiveLayoutMode.FitDesignFrame ? ResolveDesignResolution() : Vector2.zero;
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

        /// <summary>把相机输出、设备安全区和可选内容边界统一到屏幕像素，再换算到内容父级。</summary>
        private bool TryReadViewport(out Rect pixels, out Rect safe)
        {
            Camera camera = _canvas.renderMode == RenderMode.ScreenSpaceCamera ? _canvas.worldCamera : null;
            pixels = camera == null ? _canvas.pixelRect : camera.pixelRect;
            safe = default;
            if (!RevUIViewport.HasArea(pixels))
            {
                return false;
            }

            // 副屏与 RenderTexture 使用自己的输出画幅。主屏相机先把设备安全区映射到相机视口。
            Rect relative = RevUIViewport.Full;
            int display = camera == null ? _canvas.targetDisplay : camera.targetDisplay;
            if (display == 0 && (camera == null || camera.targetTexture == null) &&
                !RevUIViewport.TryNormalizeSafeArea(RevUIViewport.GetScreenSafeArea(),
                    camera == null ? RevUIViewport.Full : camera.rect, out relative))
            {
                return false;
            }
            var allowed = new Rect(pixels.position + Vector2.Scale(relative.position, pixels.size),
                Vector2.Scale(relative.size, pixels.size));
            Rect parentPixels = _parent == _canvas.transform ? pixels : RevUIViewport.GetScreenBounds(_parent, camera);
            if (contentBounds != null && !contentBounds.IsChildOf(_content) && contentBounds.IsChildOf(_canvas.transform))
            {
                // 独立边界由美术或另一层布局负责，避免内容依赖自己的结果形成循环。
                allowed = RevUIViewport.Intersect(allowed, RevUIViewport.GetScreenBounds(contentBounds, camera));
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
            Viewport = RevUIViewport.Full;
        }

    }
}

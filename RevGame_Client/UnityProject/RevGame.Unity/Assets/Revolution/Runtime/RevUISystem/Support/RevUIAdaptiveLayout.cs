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

        // 由本组件接管的内容节点，停用后重新读取其预制体布局。
        private RectTransform _content;
        // 提供整张屏幕空间画幅的直接父级。
        private RectTransform _parent;
        // 当前所属的根画布，换父级或画布层级变化后刷新绑定。
        private Canvas _canvas;
        // 等比模式未指定设计尺寸时读取当前根画布的参考尺寸。
        private CanvasScaler _scaler;
        // 本次接管前的布局，用于停用和画布不可用时还原。
        private RectState _original;
        // Canvas 或父级可能已经变化，下一次布局前重新找到它们。
        private bool _needsBinding = true;
        // 当前是否已经接管 RectTransform，同时标识布局缓存是否有效。
        private bool _applied;
        // 上一次写入布局使用的归一化安全区。
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

        /// <summary>设置布局策略和可选设计尺寸。对已启用的组件立即应用，复用面板可在装配时调用一次。</summary>
        public void Configure(RevUIAdaptiveLayoutMode layoutMode, Vector2 referenceResolution = default)
        {
            mode = layoutMode;
            designResolution = referenceResolution;
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
                _original = new RectState(_content);
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
            if (!RevUIViewport.HasSize(size))
            {
                return;
            }

            Rect safe = ReadSafeArea(_canvas);
            Rect pixels = _canvas.pixelRect;
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

        // 输入为根画布，返回该画布当前输出中的归一化安全区。
        private static Rect ReadSafeArea(Canvas canvas)
        {
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceCamera ? canvas.worldCamera : null;
            int display = camera == null ? canvas.targetDisplay : camera.targetDisplay;
            // 副屏与纹理相机沿用自己的完整画幅。主屏相机的安全区转换到相机的局部视口。
            if (display != 0 || (camera != null && camera.targetTexture != null))
            {
                return RevUIViewport.Full;
            }
            Rect safe = RevUIViewport.GetScreenSafeArea();
            return camera == null ? safe : RevUIViewport.NormalizeSafeArea(safe, camera.rect);
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

        // 只记录由本组件接管的属性，停用或池化归还时恢复当前这次启用前的布局。
        private readonly struct RectState
        {
            // 布局归还时使用接管前的锚点下界。
            private readonly Vector2 _anchorMin;
            // 布局归还时使用接管前的锚点上界。
            private readonly Vector2 _anchorMax;
            // 原父级布局单位里的锚点偏移。
            private readonly Vector2 _position;
            // 原尺寸增量，由原始锚点配合恢复可拉伸的尺寸。
            private readonly Vector2 _size;
            // 原缩放，安全区布局沿用其中的 Z 分量。
            public readonly Vector3 Scale;

            // 在第一次写入前保存一份布局快照。
            public RectState(RectTransform target)
            {
                _anchorMin = target.anchorMin;
                _anchorMax = target.anchorMax;
                _position = target.anchoredPosition;
                _size = target.sizeDelta;
                Scale = target.localScale;
            }

            // 恢复时先还原锚点，再写入当时的偏移和尺寸。
            public void Restore(RectTransform target)
            {
                target.anchorMin = _anchorMin;
                target.anchorMax = _anchorMax;
                target.anchoredPosition = _position;
                target.sizeDelta = _size;
                target.localScale = Scale;
            }
        }
    }
}

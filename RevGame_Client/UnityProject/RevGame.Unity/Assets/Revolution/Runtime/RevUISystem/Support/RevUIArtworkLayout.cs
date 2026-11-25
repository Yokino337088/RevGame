using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>把普通单图 Image 或 RawImage 放入父级画幅。父级提供留边底色，局部铺满时由父级遮罩裁边。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("Revolution/UI/Artwork Layout")]
    [DefaultExecutionOrder(-10)]
    public sealed class RevUIArtworkLayout : UIBehaviour
    {
        /// <summary>在父级画幅内显示原画，按项目需要选择铺满或完整显示。</summary>
        [SerializeField] private RevUIArtworkFit fit = RevUIArtworkFit.Cover;
        /// <summary>负责实际采样的图形组件。适配只调位置和尺寸，原画与 UV 沿用 Graphic。</summary>
        private Graphic _graphic;
        /// <summary>本轮启用时接管的图形节点。</summary>
        private RectTransform _artwork;
        /// <summary>图形停用或失去原画时要恢复的布局。</summary>
        private RevUIViewport.RectState _original;
        /// <summary>记录已适配的父级。换父级时即使尺寸一样也重新应用。</summary>
        private RectTransform _lastParent;
        /// <summary>上次适配的父级大小，单位为画布局部单位。</summary>
        private Vector2 _lastViewportSize;
        /// <summary>上次采样区域的像素尺寸，图片晚到或换图后自动刷新。</summary>
        private Vector2 _lastArtworkSize;
        /// <summary>上一次写入使用的构图方式。</summary>
        private RevUIArtworkFit _lastFit;
        /// <summary>是否已经写入布局。未接管时保留美术的原始节点设置。</summary>
        private bool _applied;
        /// <summary>复用的刷新回调。窗口尺寸和换图事件合并成一批。</summary>
        private Action _refresh;
        /// <summary>写入或恢复时忽略本身引发的顶点和尺寸通知。</summary>
        private bool _writing;
        /// <summary>最近一次检查使用的 Sprite。颜色等顶点变化沿用同一 Sprite 时跳过。</summary>
        private Sprite _seenSprite;
        /// <summary>最近一次检查使用的 RawImage 纹理。</summary>
        private Texture _seenTexture;
        /// <summary>原画采样区的宽高。UV 平移无需重排。</summary>
        private Vector2 _seenUvSize;

        /// <summary>原画显示区域变化后通知绑定到它的内容组件。</summary>
        internal event Action BoundsChanged;

        /// <summary>选择构图方式。组件已启用且图片就绪时立即更新。</summary>
        public void Configure(RevUIArtworkFit artworkFit)
        {
            fit = artworkFit;
            RevUILayoutScheduler.CancelRefresh(_refresh);
            RefreshLayout();
        }

        /// <summary>每轮启用先保留原布局，再读取当前原画。</summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshLayout();
        }

        /// <summary>隐藏、回池或停用时归还图形节点的布局。</summary>
        protected override void OnDisable()
        {
            if (_graphic != null)
            {
                _graphic.UnregisterDirtyVerticesCallback(OnVerticesDirty);
            }
            if (_graphic is RawImage)
            {
                RevUILayoutScheduler.CheckForChanges -= RequestRefresh;
            }
            RevUILayoutScheduler.CancelRefresh(_refresh);
            RestoreLayout();
            _artwork = null;
            _graphic = null;
            _lastParent = null;
            _seenSprite = null;
            _seenTexture = null;
            _seenUvSize = default;
            base.OnDisable();
        }

        /// <summary>父级和图形的尺寸变化时合并请求。</summary>
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            RequestRefresh();
        }

        /// <summary>移动到其它画幅时重新适配。</summary>
        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            RequestRefresh();
        }

        /// <summary>只对纹理、采样宽高或 Sprite 真正变化的顶点通知安排布局。</summary>
        private void OnVerticesDirty()
        {
            if (_writing)
            {
                return;
            }
            if (_graphic is Image image && (image.type == Image.Type.Simple ? image.overrideSprite : null) != _seenSprite)
            {
                RequestRefresh();
            }
            else if (_graphic is RawImage picture && (picture.texture != _seenTexture || picture.uvRect.size != _seenUvSize))
            {
                RequestRefresh();
            }
        }

        /// <summary>在内容读取原画边界之前处理图片和窗口变化。</summary>
        private void RequestRefresh()
        {
            if (!_writing && isActiveAndEnabled)
            {
                _refresh ??= RefreshLayout;
                RevUILayoutScheduler.RequestArtworkRefresh(_refresh);
            }
        }

        /// <summary>只有原画、父级或策略变化时才写入等比布局。</summary>
        private void RefreshLayout()
        {
            if (!isActiveAndEnabled || _writing)
            {
                return;
            }
            if (_artwork == null)
            {
                // 显式 Configure 或正常启用都可以先接管布局，统一捕获本轮基准。
                _artwork = (RectTransform)transform;
                _graphic = GetComponent<Graphic>();
                _original = new RevUIViewport.RectState(_artwork);
                if (_graphic != null)
                {
                    _graphic.RegisterDirtyVerticesCallback(OnVerticesDirty);
                }
                if (_graphic is RawImage)
                {
                    // RenderTexture 或动态纹理可原地变更尺寸，UGUI 不会收到换引用通知。
                    RevUILayoutScheduler.CheckForChanges += RequestRefresh;
                }
            }
            Vector2 source = ReadArtworkSize();
            if (!(_artwork.parent is RectTransform parent) || !RevUIViewport.HasSize(source))
            {
                RestoreLayout();
                return;
            }
            Vector2 viewport = parent.rect.size;
            if (_applied && parent == _lastParent && viewport == _lastViewportSize &&
                source == _lastArtworkSize && fit == _lastFit)
            {
                return;
            }
            Vector2 size = RevUIViewport.FitArtworkSize(viewport, source, fit);
            if (!RevUIViewport.HasSize(size))
            {
                return;
            }

            Vector2 normalized = new Vector2(size.x / viewport.x, size.y / viewport.y);
            var frame = new Rect((Vector2.one - normalized) * .5f, normalized);
            _writing = true;
            try
            {
                _applied = RevUIViewport.ApplyDesignFrame(_artwork, frame, size) > 0f;
            }
            finally
            {
                _writing = false;
            }
            _lastParent = parent;
            _lastViewportSize = viewport;
            _lastArtworkSize = source;
            _lastFit = fit;
            if (_applied)
            {
                BoundsChanged?.Invoke();
            }
        }

        /// <summary>读取当前有效 Sprite 或 RawImage 的采样尺寸，保留图集区域与原 UV。</summary>
        private Vector2 ReadArtworkSize()
        {
            if (_graphic is Image image)
            {
                _seenSprite = image.type == Image.Type.Simple ? image.overrideSprite : null;
                return _seenSprite == null ? Vector2.zero : _seenSprite.rect.size;
            }
            if (_graphic is RawImage picture)
            {
                _seenTexture = picture.texture;
                _seenUvSize = picture.uvRect.size;
                return _seenTexture == null ? Vector2.zero : new Vector2(_seenTexture.width * Mathf.Abs(_seenUvSize.x),
                    _seenTexture.height * Mathf.Abs(_seenUvSize.y));
            }
            return Vector2.zero;
        }

        /// <summary>归还本轮接管的尺寸与锚点，图片恢复后可重新适配。</summary>
        private void RestoreLayout()
        {
            if (!_applied || _artwork == null)
            {
                return;
            }
            _applied = false;
            _writing = true;
            try
            {
                _original.Restore(_artwork);
            }
            finally
            {
                _writing = false;
            }
            BoundsChanged?.Invoke();
        }
    }
}

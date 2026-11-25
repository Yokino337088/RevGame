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

        /// <summary>选择构图方式。组件已启用且图片就绪时立即更新。</summary>
        public void Configure(RevUIArtworkFit artworkFit)
        {
            fit = artworkFit;
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
            RestoreLayout();
            _artwork = null;
            _graphic = null;
            _lastParent = null;
            base.OnDisable();
        }

        /// <summary>在内容区收集原画边界前，更新本帧收到的图片与窗口变化。</summary>
        private void LateUpdate()
        {
            RefreshLayout();
        }

        /// <summary>只有原画、父级或策略变化时才写入等比布局。</summary>
        private void RefreshLayout()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }
            if (_artwork == null)
            {
                // 显式 Configure 或正常启用都可以先接管布局，统一捕获本轮基准。
                _artwork = (RectTransform)transform;
                _graphic = GetComponent<Graphic>();
                _original = new RevUIViewport.RectState(_artwork);
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
            _applied = RevUIViewport.ApplyDesignFrame(_artwork, frame, size) > 0f;
            _lastParent = parent;
            _lastViewportSize = viewport;
            _lastArtworkSize = source;
            _lastFit = fit;
        }

        /// <summary>读取当前有效 Sprite 或 RawImage 的采样尺寸，保留图集区域与原 UV。</summary>
        private Vector2 ReadArtworkSize()
        {
            if (_graphic is Image image && image.type == Image.Type.Simple && image.overrideSprite is Sprite sprite)
            {
                return sprite.rect.size;
            }
            if (_graphic is RawImage picture && picture.texture is Texture texture)
            {
                return new Vector2(texture.width * Mathf.Abs(picture.uvRect.width),
                    texture.height * Mathf.Abs(picture.uvRect.height));
            }
            return Vector2.zero;
        }

        /// <summary>归还本轮接管的尺寸与锚点，图片恢复后可重新适配。</summary>
        private void RestoreLayout()
        {
            if (_applied && _artwork != null)
            {
                _original.Restore(_artwork);
            }
            _applied = false;
        }
    }
}

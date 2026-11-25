using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>选角和加载页用同一个 1920×1080 设计框。Prefab Mode 与 RevUI 实例都在当前父画布中等比适配。</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public sealed class DuelPreparationLayout : MonoBehaviour
    {
        // 立绘、按钮、文字共用这一张设计框，保持尺寸、间距和字形在同一比例下缩放。
        [SerializeField] private RectTransform design;
        // 底图使用独立满屏裁切，不受安全区留边影响。
        [SerializeField] private Image backdrop;
        // 运行时由框架接管内容层，选角和加载页复用同一种布局策略。
        private RevUIAdaptiveLayout _adaptiveLayout;
        /// <summary>运行时由框架按实际素材铺满面板背景，独立于内容安全区。</summary>
        private RevUIArtworkLayout _artworkLayout;
        // 底图随面板画幅改变而裁切。
        private Vector2 _lastSize;
        /// <summary>上次已铺满的底图。AB 异步绑定后即使尺寸相同也要重新裁切。</summary>
        private Sprite _lastBackdrop;

        /// <summary>运行时使用框架接管内容与背景，Prefab 编辑态使用本地预览。</summary>
        private void OnEnable()
        {
            _lastSize = Vector2.zero;
            if (Application.IsPlaying(gameObject))
            {
                if (design != null)
                {
                    _adaptiveLayout = design.GetComponent<RevUIAdaptiveLayout>() ?? design.gameObject.AddComponent<RevUIAdaptiveLayout>();
                    _adaptiveLayout.Configure(RevUIAdaptiveLayoutMode.FitDesignFrame, GameBattleLayout.DesignResolution);
                    _adaptiveLayout.enabled = true;
                }
                if (backdrop != null)
                {
                    _artworkLayout = backdrop.GetComponent<RevUIArtworkLayout>() ?? backdrop.gameObject.AddComponent<RevUIArtworkLayout>();
                    _artworkLayout.Configure(RevUIArtworkFit.Cover);
                    _artworkLayout.enabled = true;
                }
                return;
            }
            if (backdrop != null)
            {
                backdrop.RegisterDirtyVerticesCallback(ApplyPreview);
            }
            ApplyPreview();
        }

        /// <summary>面板停用时由框架归还原内容和背景布局，供下次打开。</summary>
        private void OnDisable()
        {
            if (backdrop != null)
            {
                backdrop.UnregisterDirtyVerticesCallback(ApplyPreview);
            }
            if (_adaptiveLayout != null)
            {
                _adaptiveLayout.enabled = false;
            }
            if (_artworkLayout != null)
            {
                _artworkLayout.enabled = false;
            }
        }

        /// <summary>Prefab Stage 的画幅变化时刷新预览。</summary>
        private void OnRectTransformDimensionsChange()
        {
            ApplyPreview();
        }

        /// <summary>使用框架的几何函数预览完整画幅中的内容与原画。</summary>
        private void ApplyPreview()
        {
            if (!isActiveAndEnabled || Application.IsPlaying(gameObject) || !gameObject.scene.IsValid())
            {
                return;
            }
            var root = (RectTransform)transform;
            Vector2 size = root.rect.size;
            Sprite sprite = backdrop == null ? null : backdrop.overrideSprite;
            if (size.x <= 0f || size.y <= 0f || (_lastSize == size && _lastBackdrop == sprite))
            {
                return;
            }
            _lastSize = size;
            _lastBackdrop = sprite;

            if (design != null)
            {
                Vector2 reference = GameBattleLayout.DesignResolution;
                Rect frame = RevUIViewport.FitDesignFrame(size, RevUIViewport.Full, reference);
                RevUIViewport.ApplyDesignFrame(design, frame, reference);
            }

            if (backdrop != null)
            {
                backdrop.rectTransform.sizeDelta = sprite == null ? size
                    : RevUIViewport.FitArtworkSize(size, sprite.rect.size, RevUIArtworkFit.Cover);
            }
        }
    }
}

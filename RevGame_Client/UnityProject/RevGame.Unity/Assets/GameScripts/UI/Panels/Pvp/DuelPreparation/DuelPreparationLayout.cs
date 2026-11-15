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
        // 底图随面板画幅改变而裁切。
        private Vector2 _lastSize;

        // 内容加入安全设计框，底图继续覆盖整个面板。
        private void OnEnable()
        {
            _lastSize = Vector2.zero;
            if (design != null && Application.IsPlaying(gameObject))
            {
                _adaptiveLayout = design.GetComponent<RevUIAdaptiveLayout>() ?? design.gameObject.AddComponent<RevUIAdaptiveLayout>();
                _adaptiveLayout.Configure(RevUIAdaptiveLayoutMode.FitDesignFrame, GameBattleLayout.DesignResolution);
                _adaptiveLayout.enabled = true;
            }
            Apply();
        }

        // 面板停用时归还原布局，供下次打开面板使用。
        private void OnDisable()
        {
            if (_adaptiveLayout != null)
            {
                _adaptiveLayout.enabled = false;
            }
        }

        // Prefab Stage 与运行时的底图均随父画幅尺寸更新。
        private void OnRectTransformDimensionsChange()
        {
            Apply();
        }

        // 编辑时直接用全幅预览设计框。背景独立按原画比例覆盖全屏。
        private void Apply()
        {
            if (!isActiveAndEnabled || !gameObject.scene.IsValid())
            {
                return;
            }
            var root = (RectTransform)transform;
            Vector2 size = root.rect.size;
            if (size.x <= 0 || size.y <= 0 || _lastSize == size)
            {
                return;
            }
            _lastSize = size;

            if (design != null && !Application.IsPlaying(gameObject))
            {
                Vector2 reference = GameBattleLayout.DesignResolution;
                Rect frame = RevUIViewport.FitDesignFrame(size, RevUIViewport.Full, reference);
                RevUIViewport.ApplyDesignFrame(design, frame, reference);
            }

            if (backdrop != null && backdrop.sprite != null)
            {
                Rect art = backdrop.sprite.rect;
                float aspect = art.width / art.height;
                backdrop.rectTransform.sizeDelta = new Vector2(Mathf.Max(size.x, size.y * aspect), Mathf.Max(size.y, size.x / aspect));
            }
        }
    }
}

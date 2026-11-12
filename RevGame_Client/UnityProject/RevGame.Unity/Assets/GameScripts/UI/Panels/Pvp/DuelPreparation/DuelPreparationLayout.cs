using RevGame.UI;
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
        // 缓存尺寸和安全区，只有窗口或设备区域变化才更新位置。
        private Vector2 _lastSize;
        private Rect _lastSafe;

        // 组件绑定后立即落到当前画布，空预制体根会等下一次尺寸变化。
        private void OnEnable()
        {
            _lastSize = Vector2.zero;
            Apply();
        }

        // Prefab Stage 重算画布、运行时 RevUI 切分辨率均走此回调。
        private void OnRectTransformDimensionsChange()
        {
            Apply();
        }

        // 刘海区域变化不一定触发画布尺寸事件，设备旋转后也需重新取安全区。
        private void LateUpdate()
        {
            Apply();
        }

        // 固定逻辑尺寸，只把整张设计框缩放居中。背景单独按原画比例覆盖全屏。
        private void Apply()
        {
            if (design == null || backdrop == null || !gameObject.scene.IsValid())
            {
                return;
            }
            var root = (RectTransform)transform;
            Vector2 size = root.rect.size;
            Rect safe = Application.IsPlaying(gameObject) ? GameBattleLayout.SafeViewport() : new Rect(0, 0, 1, 1);
            if (size.x <= 0 || size.y <= 0 || (_lastSize == size && _lastSafe == safe))
            {
                return;
            }
            _lastSize = size;
            _lastSafe = safe;
            Vector2 target = GameBattleLayout.DesignResolution;
            float scale = Mathf.Min(size.x * safe.width / target.x, size.y * safe.height / target.y);
            design.localScale = new Vector3(scale, scale, 1);
            design.anchoredPosition = Vector2.Scale(safe.center - Vector2.one * .5f, size);

            Rect art = backdrop.sprite.rect;
            float aspect = art.width / art.height;
            backdrop.rectTransform.sizeDelta = new Vector2(Mathf.Max(size.x, size.y * aspect), Mathf.Max(size.y, size.x / aspect));
        }
    }
}

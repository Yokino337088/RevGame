// ============================================================
// RevUIAnimTarget.cs —— 动画"落点"：把采样结果写到控件上（Unity 侧）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【为什么要有它】
//   引擎（Core）只算"系数 0→1"，落到控件上是 Unity 的事：
//     · 透明度 → CanvasGroup.alpha（**没有就自动加一个** —— 这是"傻瓜式"的关键，
//       否则每个面板都要手动挂 CanvasGroup，一漏就是"淡入没反应"）；
//     · 缩放 → RectTransform.localScale（相对**基准缩放**，所以重复播不会越播越大）；
//     · 位移 → RectTransform.anchoredPosition（相对**基准位置** + 自身宽高的比例偏移）。
//
// 【基准值什么时候取】
//   第一次给这个控件做动画时取一次（之后复用）。这样"播到一半再起一个动画"不会把
//   基准值取在中间态上（否则连续 ScaleTo 会越缩越小 —— 这是手写动画最常见的坑）。
//
// 【为什么挂在 GameObject 上】
//   组件随对象销毁自动清理，不需要静态表 + 不用操心"面板池化复用时残留"。
// ============================================================
using UnityEngine;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>动画落点（框架内部；业务通过 <see cref="RevUIAnim"/> 使用）。</summary>
    [DisallowMultipleComponent]
    internal sealed class RevUIAnimTarget : MonoBehaviour
    {
        private RectTransform _rect;
        private CanvasGroup _group;
        private Graphic _graphic;

        private Vector2 _basePosition;
        private Vector3 _baseScale = Vector3.one;
        private float _baseAlpha = 1f;
        private bool _captured;

        private RevUIAnimHandle _handle;

        /// <summary>当前挂在它身上的动画句柄（业务查 <see cref="RevUIAnim.IsPlaying"/> 用）</summary>
        internal RevUIAnimHandle Handle
        {
            get => _handle;
            set => _handle = value;
        }

        /// <summary>取（必要时创建）某个控件上的落点。</summary>
        internal static RevUIAnimTarget Get(Component target)
        {
            if (target == null) return null;

            RevUIAnimTarget found = target.GetComponent<RevUIAnimTarget>();
            if (found == null) found = target.gameObject.AddComponent<RevUIAnimTarget>();
            found.Capture(target);
            return found;
        }

        /// <summary>记下基准值（只做一次）。</summary>
        private void Capture(Component target)
        {
            if (_rect == null) _rect = target as RectTransform;
            if (_rect == null) _rect = target.GetComponent<RectTransform>();
            if (_group == null) _group = target.GetComponent<CanvasGroup>();
            if (_graphic == null) _graphic = target.GetComponent<Graphic>();

            if (_captured) return;
            _captured = true;

            if (_rect != null)
            {
                _basePosition = _rect.anchoredPosition;
                _baseScale = _rect.localScale;
            }

            _baseAlpha = ReadAlpha();
        }

        /// <summary>取基准透明度（偏好 CanvasGroup；没有就用 Graphic 的颜色）。</summary>
        private float ReadAlpha()
        {
            if (_group != null) return _group.alpha;
            if (_graphic != null) return _graphic.color.a;
            return 1f;
        }

        // ── 采样写入 ────────────────────────────────────────

        /// <summary>写透明度（0..1）。没有 CanvasGroup 时自动补一个（否则淡入淡出没地方落）。</summary>
        internal void ApplyAlpha(float alpha)
        {
            if (alpha < 0f) alpha = 0f;
            else if (alpha > 1f) alpha = 1f;

            if (_group == null)
            {
                _group = GetComponent<CanvasGroup>();
                if (_group == null)
                {
                    _group = gameObject.AddComponent<CanvasGroup>();
                    // 补出来的 CanvasGroup 默认不挡射线，保持原样更安全（挡射线是业务的事）
                    _group.interactable = true;
                    _group.blocksRaycasts = true;
                }
            }

            _group.alpha = alpha;

            // Graphic 上还有一份 alpha 时（Image/Text 自己的颜色）保持不动：
            // 两套 alpha 相乘会让"只改一套"的预期落空，统一走 CanvasGroup。
        }

        /// <summary>写缩放（**相对基准缩放**的倍率：1 = 原大小）。</summary>
        internal void ApplyScale(float scale)
        {
            if (_rect == null) return;
            _rect.localScale = new Vector3(_baseScale.x * scale, _baseScale.y * scale, _baseScale.z);
        }

        /// <summary>写缩放（绝对值倍率，用于"悬停放大到 1.06"这种目标值动画）。</summary>
        internal void ApplyScaleAbsolute(float scale) => ApplyScale(scale);

        /// <summary>写位移：按"自身宽/高的比例"偏移（-1 = 挪一整屏高/宽）。</summary>
        internal void ApplyOffset(float x, float y)
        {
            if (_rect == null) return;

            float w = _rect.rect.width;
            float h = _rect.rect.height;
            _rect.anchoredPosition = new Vector2(_basePosition.x + x * w, _basePosition.y + y * h);
        }

        /// <summary>把控件恢复成基准态（停动画 / 关掉动画开关时用）。</summary>
        internal void RestoreBase()
        {
            if (_rect != null)
            {
                _rect.anchoredPosition = _basePosition;
                _rect.localScale = _baseScale;
            }

            if (_group != null) _group.alpha = _baseAlpha;
            else if (_graphic != null)
            {
                Color c = _graphic.color;
                c.a = _baseAlpha;
                _graphic.color = c;
            }

            _handle = RevUIAnimHandle.None;
        }

        /// <summary>立刻把某个预设的"终态"写上（<c>RevUISetting.UIAnimationsEnabled = false</c> 时用）。</summary>
        internal void ApplyEndState(in RevUIAnimSpec spec)
        {
            if (spec.UseAlpha) ApplyAlpha(spec.AlphaTo);
            if (spec.UseScale) ApplyScale(spec.ScaleTo);
            if (spec.UseOffset) ApplyOffset(spec.OffsetXTo, spec.OffsetYTo);
        }

        /// <summary>取基准缩放（ScaleTo 之类的"目标值动画"要用它换算）。</summary>
        internal float BaseScaleX => _baseScale.x;

        /// <summary>拿不到 RectTransform 时（比如 Part 根节点没挂）—— 动画会退化但不会报错。</summary>
        internal bool HasRect => _rect != null;
    }
}

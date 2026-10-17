// ============================================================
// RevUIWidgetFeedback.cs —— 控件"悬停 / 按下"反馈（Unity 侧）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【它做什么】
//   给一个可点控件（按钮 / 页签 / 图标）加上最常用的两档手感：
//     指针移入 → 放大到 HoverScale（默认 1.06）
//     指针按下 → 缩到 PressScale（默认 0.94）
//     移出 / 抬起 → 回到 1
//   这是旧框架 AddButtonAnimation 的同款能力，但**不依赖 DOTween**：
//   走的是本库的"目标值动画"（ScaleTo）—— 因为总是朝同一个目标收敛，
//   快速划过多、连点也不会出现"越缩越小"的累积误差。
//
// 【两个开关】
//   · <c>RevUISetting.UIAnimationsEnabled = false</c> → 反馈直接写终态（没有动画但手感还在）；
//   · 不需要反馈的控件别调 <c>RevUIAnim.AddHoverFeedback</c> 就行（一个控件一个组件）。
// ============================================================
using UnityEngine;
using UnityEngine.EventSystems;

namespace Revolution
{
    /// <summary>悬停 / 按下缩放反馈（框架内部；业务用 <c>RevUIAnim.AddHoverFeedback</c> 添加）。</summary>
    [DisallowMultipleComponent]
    internal sealed class RevUIWidgetFeedback : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>悬停时的缩放倍率</summary>
        internal float HoverScale = 1.06f;

        /// <summary>按下时的缩放倍率</summary>
        internal float PressScale = 0.94f;

        /// <summary>过渡时长（秒）</summary>
        internal float Duration = 0.09f;

        private bool _hover;
        private bool _pressed;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hover = true;
            Apply();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hover = false;
            _pressed = false;
            Apply();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _pressed = true;
            Apply();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
            Apply();
        }

        private void Apply()
        {
            float scale = _pressed ? PressScale : (_hover ? HoverScale : 1f);
            RevUIAnim.ScaleTo(this, scale, Duration, RevUIEase.CubicOut, null, this);
        }

        private void OnDisable()
        {
            // 被禁用/关闭时别把"放大态"留在身上（面板池化复用时尤其重要）
            _hover = false;
            _pressed = false;
            RevUIAnim.RestoreBase(this);
        }
    }
}

// ============================================================
// RevUIButtonPressRelay.cs —— 交互节点的"按住 / 松开"指针继电器
//
// 位置：Runtime\RevUISystem\Support\
//
// 【它解决什么】
//   UGUI 的 Button 只告诉你"点了"（onClick），**不告诉你"按住多久"和"什么时候松开"**。
//   而 [RevButtonLongPress] / [RevButtonLoosen] 这两个方法特性需要它们 ——
//   所以框架给"确实要用这两种事件"的交互节点挂上这个小继电器
//   （一个节点只挂一次；面板池化复用时不会重复挂）。
//
//     指针按下 → 记下时间
//     指针抬起 → 按住时长 ≥ RevUISetting.ButtonLongPressSeconds ? 派发"长按" : 跳过
//                然后总是派发"松开"
//
// 【为什么不是 Update 里轮询】
//   完全由 UGUI 的指针事件驱动（EventSystem 在正确的时机回调），
//   不需要每帧检查、也不给面板加任何 Update 开销 —— 与"事件驱动优先"的纪律一致。
//
// 【三条注意】
//   · 长按在**松开时**派发（不是按住到点就派发）：一次操作里最多只触发"长按 + 松开"，
//     不会和"点击"抢同一个瞬间；要"按住到点立刻触发"就得每帧驱动，代价大得多。
//   · 手指拖出控件再松开不会有事件（UGUI 的抬起只回调给按下时的那个对象）。
//   · 只给声明了长按 / 松开的节点挂（绑定计划里算好），其它按钮零成本。
// ============================================================
using UnityEngine;
using UnityEngine.EventSystems;

namespace Revolution
{
    /// <summary>把 UGUI 的指针按下 / 抬起转成"长按 / 松开"事件（框架内部，业务不直接用）</summary>
    [DisallowMultipleComponent]
    internal sealed class RevUIButtonPressRelay : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        private IRevUIUserEvents _receiver;
        private string _nodeName;
        private float _downTime;
        private bool _down;

        internal void Setup(IRevUIUserEvents receiver, string nodeName)
        {
            // 同一控件只归最近的面板/Part 所有；父面板扫描到 Part 子树时跳过，
            // 即使父面板先装配，也不会抢走 Part 后续绑定的接收者。
            if (!ReferenceEquals(FindNearestReceiver(), receiver)) return;
            _receiver = receiver;
            _nodeName = nodeName;
        }

        private IRevUIUserEvents FindNearestReceiver()
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                MonoBehaviour[] components = current.GetComponents<MonoBehaviour>();
                for (int i = 0; i < components.Length; i++)
                    if (components[i] is IRevUIUserEvents receiver) return receiver;
            }
            return null;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _down = true;
            // 用 unscaledTime：暂停（timeScale = 0）时"按住多久"也照常计算
            _downTime = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_down) return;
            _down = false;

            if (Time.unscaledTime - _downTime >= RevUISetting.ButtonLongPressSeconds)
                _receiver?.DispatchLongPress(_nodeName);

            _receiver?.DispatchLoosen(_nodeName);
        }

        /// <summary>节点被禁用时清掉"按住中"的残留状态（下次启用重新计时）</summary>
        private void OnDisable() => _down = false;
    }
}

// ============================================================
// RevInputListener.cs —— 事件驱动接入面（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【它解决什么】
//   "我不想在 Update 里轮询输入" —— 业务只注册一次，之后输入事件由框架**推**过来：
//
//     sealed class PlayerInput : RevInputListenerBase
//     {
//         void OnEnable()  { RevInput.AddListener(this, owner: this); }
//         void OnDisable() { RevInput.OffAllOf(this); }        // 一行清干净
//
//         public override void OnInputPressed(string action)
//         {
//             switch (action) { case "Jump": Jump(); break; case "Fire": Fire(); break; }
//         }
//         public override void OnInputAxis(string axis, float value) => _move = value;
//         public override void OnInputGesture(RevGestureEvent g) { if (g.Kind == RevGestureKind.Swipe) Dodge(g.Direction); }
//     }
//
//   投递顺序（每帧）：按下 → 抬起 → 手势 → 连发 → 轴变化。
//   本帧派发开始时会快照订阅：回调中增删订阅从下一帧生效，保证同帧其它监听者不会漏收。
//   每个回调都**逐条隔离**：一个监听者抛异常不会影响其它监听者与其它事件。
//
// 【两条铁律】
//   ① **只读事件，不查状态**：回调里拿到的就是本帧的事实；想查历史状态用轮询 API（两者可混用）。
//   ② **记得登记 owner**：销毁时 `RevInput.OffAllOf(owner)` 一行清掉全部订阅，不会残留。
// ============================================================

namespace Revolution
{
    /// <summary>
    /// 输入事件监听者（事件驱动接入面）。只关心部分事件时请继承 <see cref="RevInputListenerBase"/>，
    /// 那样只需重写用到的那几个方法。
    /// </summary>
    public interface RevInputListener
    {
        /// <summary>某动作本帧刚按下（键盘 / 鼠标 / 手柄 / 虚拟按键都算）。</summary>
        void OnInputPressed(string action);

        /// <summary>某动作本帧刚抬起。</summary>
        void OnInputReleased(string action);

        /// <summary>某动作连发触发（需要先 <c>RevInput.SetRepeat</c> 配节拍）。</summary>
        void OnInputRepeat(string action);

        /// <summary>某个轴的值发生变化（移动 / 摇杆；订阅后先收到一次当前值）。</summary>
        void OnInputAxis(string axis, float value);

        /// <summary>识别到一条手势（点击 / 双击 / 长按 / 拖动 / 滑动 / 捏合 / 旋转）。</summary>
        void OnInputGesture(RevGestureEvent gesture);
    }

    /// <summary>
    /// 监听者空实现基类：**只重写你关心的那几个方法**（接口里有五个方法，全实现一遍太啰嗦）。
    /// 这是事件驱动接入的推荐写法。
    /// </summary>
    public abstract class RevInputListenerBase : RevInputListener
    {
        /// <inheritdoc />
        public virtual void OnInputPressed(string action) { }

        /// <inheritdoc />
        public virtual void OnInputReleased(string action) { }

        /// <inheritdoc />
        public virtual void OnInputRepeat(string action) { }

        /// <inheritdoc />
        public virtual void OnInputAxis(string axis, float value) { }

        /// <inheritdoc />
        public virtual void OnInputGesture(RevGestureEvent gesture) { }
    }
}

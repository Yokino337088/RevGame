// ============================================================
// IRevUIUserEvents.cs —— "按节点名分发"的用户事件接收口（框架内部管线）
//
// 位置：Runtime\RevUISystem\Interfaces\
//
// 【它是干什么的】
//   面板和 Part 都支持"不写绑定字段、直接重写 OnClick(节点名) 就能响应点击"（这是原框架最方便的地方）。
//   绑定器需要把"某个控件被点了"转成"调用宿主的那几个回调" —— 面板和 Part 是两种类型，
//   但转发的动作完全一样，所以抽象出这个接口，绑定器只认它（继电器只用写一份）。
//
// 【为什么是 internal】
//   它是框架内部管线，不是业务 API —— 业务只需要重写 RevUIPanel / RevUIPart 上的
//   protected virtual OnClick / OnToggleChanged / … 这些方法即可，看不见这个接口的存在。
// ============================================================
namespace Revolution
{
    /// <summary>控件事件 → 宿主回调 的转发口（由 RevUIPanel / RevUIPart 显式实现）</summary>
    internal interface IRevUIUserEvents
    {
        void DispatchClick(string nodeName);

        /// <summary>控件被长按（按住超过 <c>RevUISetting.ButtonLongPressSeconds</c> 后松开；由 RevUIButtonPressRelay 报告）</summary>
        void DispatchLongPress(string nodeName);

        /// <summary>指针在控件上松开（无论按了多久；由 RevUIButtonPressRelay 报告）</summary>
        void DispatchLoosen(string nodeName);

        void DispatchToggleChanged(string nodeName, bool value);
        void DispatchSliderChanged(string nodeName, float value);
        void DispatchInputChanged(string nodeName, string value);
        void DispatchInputEndEdit(string nodeName, string value);
        void DispatchDropdownChanged(string nodeName, int index);

        /// <summary>滑动位置用两个 float 表示（x/y）—— 免得为了一次滚动回调引入 Vector2 的结构体语义</summary>
        void DispatchScrollChanged(string nodeName, float x, float y);
    }
}

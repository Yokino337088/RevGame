// ============================================================
// RevInputDefines.cs —— 输入系统的枚举 / 上限 / 错误码（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   把"输入"这件事的全部词汇固定下来：设备类型、动作相位、指针相位、手势种类、
//   屏蔽种类、失败原因。业务与内核说的是同一套词，才有"傻瓜式"可言。
//
// 【三条铁律】
//   ① **相位只有四种**：`Down`（本帧刚按下）/ `Held`（按住）/ `Up`（本帧刚抬起）/ `None`。
//      不再有第五种——"双击""长按""连发"都是**手势或动作的派生读数**，不是相位。
//   ② **失败一定有原因码**：框架不打日志、也不吞错误，失败给 `RevInputErrorReason`，
//      要观测就订阅 `RevInput.Failed` 事件（或打开 VerboseLog）。
//   ③ **时间只以参数进来**：本文件里没有任何 `Time.*`，帧率与暂停都由调用方决定。
//
// 【为什么要"纯 C#"】
//   这层能被链接进普通 .NET 工程跑断言（见模块 README 的验收一节）——
//   键位映射、死区、双击窗口、多指抢占这些最容易写错的地方，必须能脱机测。
// ============================================================

namespace Revolution
{
    /// <summary>输入来源设备的大类（用于"设备自动识别"与业务分支）。</summary>
    public enum RevInputDeviceKind : byte
    {
        /// <summary>还没识别出来（第一帧之前）。</summary>
        Unknown = 0,

        /// <summary>键盘 + 鼠标。</summary>
        KeyboardMouse = 1,

        /// <summary>触屏（手机 / 平板 / 触摸笔电）。</summary>
        Touch = 2,

        /// <summary>手柄（Xbox / PS / 通用 HID）。</summary>
        Gamepad = 3,
    }

    /// <summary>
    /// 一个<b>逻辑动作</b>的相位。业务 99% 的时间只问三件事：<see cref="Down"/> / <see cref="Held"/> / <see cref="Up"/>。
    /// </summary>
    public enum RevInputPhase : byte
    {
        None = 0,
        /// <summary>本帧刚按下（只成立一帧）。</summary>
        Down = 1,
        /// <summary>按住（含按下的那一帧）。</summary>
        Held = 2,
        /// <summary>本帧刚抬起（只成立一帧）。</summary>
        Up = 3,
    }

    /// <summary>一个指针（鼠标 / 手指）在本帧的相位。</summary>
    public enum RevPointerPhase : byte
    {
        None = 0,
        /// <summary>本帧出现（手指按下 / 鼠标键按下后的第一个可读帧）。</summary>
        Began = 1,
        /// <summary>本帧移动过。</summary>
        Moved = 2,
        /// <summary>本帧存在但没动。</summary>
        Stationary = 3,
        /// <summary>本帧离开（手指抬起 / 鼠标键抬起）。</summary>
        Ended = 4,
        /// <summary>被系统取消（来电、切后台）——**必须**当作 Ended 处理，但语义是"别信这次的轨迹"。</summary>
        Canceled = 5,
    }

    /// <summary>鼠标键（与 Unity 的 <c>Input.GetMouseButton(0..4)</c> 编号一致）。</summary>
    public enum RevMouseButton : byte
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        Back = 3,
        Forward = 4,
    }

    /// <summary>手势种类（触屏与鼠标拖动共用同一套识别）。</summary>
    public enum RevGestureKind : byte
    {
        None = 0,
        /// <summary>轻点：按下→抬起，位移小于阈值且时间够短。</summary>
        Tap = 1,
        /// <summary>双击：两次 Tap 落在同一位置附近、间隔够短。</summary>
        DoubleTap = 2,
        /// <summary>长按：按住超过阈值且几乎没移动（只触发一次）。</summary>
        LongPress = 3,
        /// <summary>拖动：按下后移动超过阈值（每帧都会产出，带本帧位移）。</summary>
        Drag = 4,
        /// <summary>滑动：抬起瞬间判定"够远 + 够快"，带方向与速度。</summary>
        Swipe = 5,
        /// <summary>捏合（双指）：两指距离变化（带本帧缩放增量）。</summary>
        Pinch = 6,
        /// <summary>旋转（双指）：两指连线角度变化（带本帧角度增量，单位：度）。</summary>
        Rotate = 7,
    }

    /// <summary>滑动方向（八向；斜向已包含，不再退化成一个轴）。</summary>
    public enum RevSwipeDirection : byte
    {
        None = 0,
        Up = 1,
        UpRight = 2,
        Right = 3,
        DownRight = 4,
        Down = 5,
        DownLeft = 6,
        Left = 7,
        UpLeft = 8,
    }

    /// <summary>屏蔽输入的种类（弹窗、过场、教程遮罩都靠它）。</summary>
    public enum RevInputBlockKind : byte
    {
        None = 0,
        /// <summary>只屏蔽<b>世界输入</b>（动作 / 手势），指针位置照样可读 —— 拖 UI 时最常用。</summary>
        World = 1,
        /// <summary>只屏蔽指定指针（例如"这根手指用来拖镜头，别当成世界点击"）。</summary>
        Pointer = 2,
        /// <summary>全部静默（动作 / 手势 / 指针都读不到；`RevInput.Flush` 仍可用）。</summary>
        All = 3,
    }

    /// <summary>
    /// 失败原因。**框架不打日志**：出问题看这里，或订阅 <c>RevInput.Failed</c>。
    /// </summary>
    public enum RevInputErrorReason : byte
    {
        None = 0,

        /// <summary>动作名不合法（空 / 空白 / 超过长度上限）。</summary>
        InvalidActionName = 1,

        /// <summary>动作数量超过上限（<see cref="RevInputLimits.MaxActions"/>）。</summary>
        TooManyActions = 2,

        /// <summary>同一个输入源被绑到了两个不同动作（改键时最容易发生）。</summary>
        BindingConflict = 3,

        /// <summary>键位名在 Unity 的 <c>KeyCode</c> 里不存在（只支持名字一致的键）。</summary>
        UnknownKey = 4,

        /// <summary>找不到指定动作（没绑过 / 名字打错）。</summary>
        ActionNotFound = 5,

        /// <summary>指针 id 越界（超过 <see cref="RevInputLimits.MaxPointers"/>）。</summary>
        PointerOutOfRange = 6,

        /// <summary>本帧指针数量超过上限，多出来的被丢弃（会明显影响上手感，见到就要调上限或查泄漏）。</summary>
        TooManyPointers = 7,

        /// <summary>绑定表文本解析失败（格式见《使用说明》的"改键与存档"一节）。</summary>
        BindingTextInvalid = 8,

        /// <summary>手动驱动（<c>ManualDriven</c>）下忘记调 <c>Tick</c>，导致输入停更。</summary>
        NotTicking = 9,
    }

    /// <summary>各种上限与默认值（改这里就能整体调参；全部是常量，编译期可见）。</summary>
    public static class RevInputLimits
    {
        /// <summary>最多多少个逻辑动作（够用且有限，避免"动作名写错"变成无限增长）。</summary>
        public const int MaxActions = 256;

        /// <summary>最多同时跟踪几个指针（手指）。手机 10 指已足够。</summary>
        public const int MaxPointers = 10;

        /// <summary>一个动作最多绑几个键位。</summary>
        public const int MaxKeysPerAction = 4;

        /// <summary>每帧最多记多少条手势事件（溢出只丢最旧的，不会卡住）。</summary>
        public const int MaxGestureEvents = 64;

        /// <summary>一帧最多记多少条输入事件（OnPressed/OnReleased 用）。</summary>
        public const int MaxPendingEvents = 32;

        /// <summary>动作名长度上限。</summary>
        public const int MaxActionNameLength = 32;

        /// <summary>按键缓冲默认窗口（秒）——"提前按下也算数"的手感补偿。</summary>
        public const float DefaultBufferSeconds = 0.15f;

        /// <summary>单击判定：按下到抬起的最长时间（秒）。</summary>
        public const float DefaultTapMaxSeconds = 0.35f;

        /// <summary>单击判定：最大允许位移（像素，屏幕坐标）。</summary>
        public const float DefaultTapMaxDistance = 24f;

        /// <summary>双击判定：两次点击的最大间隔（秒）。</summary>
        public const float DefaultDoubleTapSeconds = 0.30f;

        /// <summary>双击判定：两次点击的最大位置偏差（像素）。</summary>
        public const float DefaultDoubleTapDistance = 48f;

        /// <summary>长按判定：按住多久算长按（秒）。</summary>
        public const float DefaultLongPressSeconds = 0.50f;

        /// <summary>长按判定：允许的最大漂移（像素）。</summary>
        public const float DefaultLongPressMaxDistance = 20f;

        /// <summary>拖动判定：超过多少像素算拖动。</summary>
        public const float DefaultDragThreshold = 8f;

        /// <summary>滑动判定：抬起时至少移动多少像素。</summary>
        public const float DefaultSwipeMinDistance = 60f;

        /// <summary>滑动判定：抬起时至少多快（像素/秒）。</summary>
        public const float DefaultSwipeMinSpeed = 240f;

        /// <summary>轴死区（摇杆 / 复合键的静默区，归一化后）。</summary>
        public const float DefaultDeadzone = 0.15f;

        /// <summary>连发：按住后多久开始连发（秒）。</summary>
        public const float DefaultRepeatDelay = 0.35f;

        /// <summary>连发：连发间隔（秒）。</summary>
        public const float DefaultRepeatInterval = 0.08f;

        /// <summary>指针速度估算的滑动窗口（秒）。</summary>
        public const float VelocityWindowSeconds = 0.10f;
    }

    /// <summary>两个 float 的向量（内核里不引用 UnityEngine，所以不用 <c>Vector2</c>）。</summary>
    public readonly struct RevInputVector2
    {
        public readonly float X;
        public readonly float Y;

        public RevInputVector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>零向量。</summary>
        public static RevInputVector2 Zero => new RevInputVector2(0f, 0f);

        /// <summary>长度。</summary>
        public float Magnitude => (float)System.Math.Sqrt(X * X + Y * Y);

        /// <summary>平方长度（比较大小时省一次开方，热循环里用）。</summary>
        public float SqrMagnitude => X * X + Y * Y;

        public override string ToString() => "(" + X.ToString("F3") + ", " + Y.ToString("F3") + ")";
    }
}

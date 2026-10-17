// ============================================================
// RevInputSnapshot.cs —— 一帧输入快照 + 指针采样 + 手势事件（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   设备采集（Unity）与逻辑判定（内核）之间需要一份**边界数据**：
//   采集层每帧把设备状态写进快照，内核只读快照 —— 于是内核可以脱机跑断言，
//   写入方可以是引擎适配层，也可以是测试脚本（内核不关心是谁写的）。
//
// 【三条铁律】
//   ① **快照是复用的**：同一个实例每帧 `Reset()` 后重写，**不产生任何垃圾**（热路径零分配）。
//   ② **鼠标不占指针槽**：鼠标有自己的一组字段（位置 / 位移 / 滚轮 / 三个键掩码），
//      指针数组只装触摸手指 —— 业务不必去猜"这根是鼠标还是手指"。
//   ③ **手势是快照的派生物**：内核读快照后产出 <see cref="RevGestureEvent"/>，
//      采集层不参与识别（这样手势参数、阈值全部可在内核里单测）。
//
// 【谁写它、谁读它】
//   写：适配层（`Support\RevInputUnityDevice.cs`）或测试脚本
//   读：`Implementation\RevInputCore.cs`
// ============================================================

namespace Revolution
{
    /// <summary>
    /// 一根指针（手指）在一帧里的采样。鼠标不走这里（见 <see cref="RevInputSnapshot"/> 的鼠标字段）。
    /// </summary>
    public readonly struct RevPointerSample
    {
        /// <summary>指针 id（触屏是 fingerId；自定义设备可自编，但同一次触摸内必须稳定）。</summary>
        public readonly int Id;

        /// <summary>屏幕坐标（左下角为原点，与 Unity 屏幕坐标一致）。</summary>
        public readonly float X;
        public readonly float Y;

        /// <summary>本帧位移（相对上一帧）。</summary>
        public readonly float DeltaX;
        public readonly float DeltaY;

        /// <summary>本帧相位。</summary>
        public readonly RevPointerPhase Phase;

        public RevPointerSample(int id, float x, float y, float deltaX, float deltaY, RevPointerPhase phase)
        {
            Id = id;
            X = x;
            Y = y;
            DeltaX = deltaX;
            DeltaY = deltaY;
            Phase = phase;
        }

        /// <summary>位置（自定义结构，避免内核引用 Vector2）。</summary>
        public RevInputVector2 Position => new RevInputVector2(X, Y);

        /// <summary>本帧位移。</summary>
        public RevInputVector2 Delta => new RevInputVector2(DeltaX, DeltaY);

        public override string ToString()
            => "Pointer#" + Id + " " + Phase + " (" + X.ToString("F1") + "," + Y.ToString("F1") + ")";
    }

    /// <summary>
    /// 一条手势事件（内核识别出来后给业务读）。同一结构同时承载 7 种手势：
    /// 用 <see cref="Kind"/> 区分，其余字段按种类取用（见每个字段的说明）。
    /// </summary>
    public readonly struct RevGestureEvent
    {
        /// <summary>手势种类。</summary>
        public readonly RevGestureKind Kind;

        /// <summary>主指针 id（捏合/旋转时是两指中较小的那个 id）。</summary>
        public readonly int PointerId;

        /// <summary>滑动方向（仅 <see cref="RevGestureKind.Swipe"/> 有效）。</summary>
        public readonly RevSwipeDirection Direction;

        /// <summary>手势发生位置（结束点）。</summary>
        public readonly float X;
        public readonly float Y;

        /// <summary>手势起点。</summary>
        public readonly float StartX;
        public readonly float StartY;

        /// <summary>
        /// 增量：<c>Drag</c> = 本帧位移；<c>Pinch</c> = 本帧两指距离变化（像素）；
        /// <c>Rotate</c> = 本帧两指夹角变化（度）；其余为 0。
        /// </summary>
        public readonly float DeltaX;
        public readonly float DeltaY;

        /// <summary>
        /// 标量值：<c>Swipe</c> = 速度（像素/秒）；<c>Pinch</c> = 本帧缩放比例（1 为不变）；
        /// <c>Rotate</c> = 本帧角度增量（度）；<c>LongPress</c> = 按住时长（秒）；其余为 0。
        /// </summary>
        public readonly float Value;

        /// <summary>手势总时长（秒）。</summary>
        public readonly float Duration;

        /// <summary>产生这一帧的帧号（由调用方传入，便于和日志对齐）。</summary>
        public readonly int Frame;

        public RevGestureEvent(RevGestureKind kind, int pointerId, RevSwipeDirection direction,
            float x, float y, float startX, float startY,
            float deltaX, float deltaY, float value, float duration, int frame)
        {
            Kind = kind;
            PointerId = pointerId;
            Direction = direction;
            X = x;
            Y = y;
            StartX = startX;
            StartY = startY;
            DeltaX = deltaX;
            DeltaY = deltaY;
            Value = value;
            Duration = duration;
            Frame = frame;
        }

        public override string ToString()
            => Kind + (Direction != RevSwipeDirection.None ? "(" + Direction + ")" : string.Empty)
               + " @" + X.ToString("F0") + "," + Y.ToString("F0")
               + (Value != 0f ? " v=" + Value.ToString("F1") : string.Empty);
    }

    /// <summary>
    /// 一帧的输入快照。<b>复用同一个实例</b>：每帧 `Reset()` → 设备写入 → 内核读取。
    /// </summary>
    public sealed class RevInputSnapshot
    {
        /// <summary>鼠标指针的约定 id（业务问"这跟手指是哪根"时，鼠标永远是 -1）。</summary>
        public const int MousePointerId = -1;

        // ── 帧信息（由驱动填）────────────────────────────────
        /// <summary>本帧时间步长（受 timeScale 影响）。</summary>
        public float DeltaTime;
        /// <summary>本帧时间步长（不受 timeScale 影响）。</summary>
        public float UnscaledDeltaTime;
        /// <summary>游戏启动到现在的真实秒数。</summary>
        public double Realtime;
        /// <summary>帧号。</summary>
        public int Frame;
        /// <summary>本帧识别出的设备类型（由适配层推断：最近一次输入来自哪类设备）。</summary>
        public RevInputDeviceKind Device = RevInputDeviceKind.Unknown;

        // ── 键位（键盘 + 手柄按钮，三份掩码）─────────────────
        /// <summary>本帧刚按下的键位。</summary>
        public RevKeyMask KeyDown;
        /// <summary>本帧持续按住的键位（含刚按下的）。</summary>
        public RevKeyMask KeyHeld;
        /// <summary>本帧刚抬起的键位。</summary>
        public RevKeyMask KeyUp;

        // ── 鼠标 ────────────────────────────────────────────
        public float MouseX;
        public float MouseY;
        public float MouseDeltaX;
        public float MouseDeltaY;
        /// <summary>滚轮增量（横向 / 纵向）。</summary>
        public float ScrollX;
        public float ScrollY;
        /// <summary>鼠标键掩码：位下标 = <see cref="RevMouseButton"/>，1 = 本帧刚按下。</summary>
        public byte MouseDown;
        /// <summary>鼠标键掩码：按住。</summary>
        public byte MouseHeld;
        /// <summary>鼠标键掩码：本帧刚抬起。</summary>
        public byte MouseUp;

        // ── 指针（手指）─────────────────────────────────────
        private readonly RevPointerSample[] _pointers = new RevPointerSample[RevInputLimits.MaxPointers];
        private int _pointerCount;

        /// <summary>本帧有效指针数。</summary>
        public int PointerCount => _pointerCount;

        /// <summary>取第 <paramref name="index"/> 根指针（越界返回默认值，不抛异常）。</summary>
        public RevPointerSample Pointer(int index)
            => index >= 0 && index < _pointerCount ? _pointers[index] : default;

        /// <summary>加一根指针；超过上限直接丢弃（内核会计一次 <see cref="RevInputErrorReason.TooManyPointers"/>）。</summary>
        /// <returns>true = 已记录；false = 超上限被丢弃。</returns>
        public bool AddPointer(in RevPointerSample sample)
        {
            if (_pointerCount >= _pointers.Length) return false;
            _pointers[_pointerCount++] = sample;
            return true;
        }

        /// <summary>鼠标键读法（<paramref name="phase"/> 用 <c>Down/Held/Up</c>）。</summary>
        public bool MouseButton(RevMouseButton button, RevInputPhase phase)
        {
            byte bit = (byte)(1 << (int)button);
            switch (phase)
            {
                case RevInputPhase.Down: return (MouseDown & bit) != 0;
                case RevInputPhase.Held: return (MouseHeld & bit) != 0;
                case RevInputPhase.Up: return (MouseUp & bit) != 0;
                default: return false;
            }
        }

        /// <summary>有没有任何指针还按着（手势内核用它判断"多指是否结束"）。</summary>
        public bool AnyPointerActive
        {
            get
            {
                for (int i = 0; i < _pointerCount; i++)
                    if (_pointers[i].Phase != RevPointerPhase.Ended && _pointers[i].Phase != RevPointerPhase.Canceled)
                        return true;
                return false;
            }
        }

        /// <summary>清空（保留数组容量，零分配）。每帧开头调一次。</summary>
        public void Reset()
        {
            DeltaTime = 0f;
            UnscaledDeltaTime = 0f;
            Realtime = 0d;
            Frame = 0;
            Device = RevInputDeviceKind.Unknown;

            KeyDown.ClearAll();
            KeyHeld.ClearAll();
            KeyUp.ClearAll();

            MouseX = MouseY = 0f;
            MouseDeltaX = MouseDeltaY = 0f;
            ScrollX = ScrollY = 0f;
            MouseDown = MouseHeld = MouseUp = 0;

            _pointerCount = 0;
        }

        /// <summary>把本帧键位整体清空（失焦 / 切后台时用：让"按住"的状态不要留到下一帧）。</summary>
        public void FlushKeys()
        {
            KeyDown.ClearAll();
            KeyHeld.ClearAll();
            KeyUp.ClearAll();
            MouseDown = MouseHeld = MouseUp = 0;
        }

        public override string ToString()
            => "Snapshot frame=" + Frame + " device=" + Device + " pointers=" + _pointerCount
               + " keys(held=" + (KeyHeld.Any ? "yes" : "no") + ")";
    }
}

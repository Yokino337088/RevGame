// ============================================================
// RevGestureRecognizer.cs —— 手势识别内核（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   触屏与鼠标拖动的"手感"全在手势识别里：点多快算轻点、移动多少算拖动、
//   抬起多快算滑动、两指怎么算捏合。这些参数必须能调、能测、能复现 ——
//   所以识别放在纯 C# 内核：喂一串指针采样，断言输出的事件序列。
//
// 【七种手势（覆盖"键盘鼠标手机触屏滑动"里的触屏那一半）】
//   Tap（轻点）/ DoubleTap（双击）/ LongPress（长按）/ Drag（拖动，每帧）
//   / Swipe（滑动，抬起时八向判定 + 速度）/ Pinch（双指捏合，每帧）
//   / Rotate（双指旋转，每帧）
//
// 【三条铁律】
//   ① **零分配**：内部只有定长数组与值类型，每帧不 new（手势队列也是定长的，溢出丢最旧）。
//   ② **屏蔽期间不产出，也不"秋后算账"**：被屏蔽时指针照常跟踪，但抬手不会补发 Tap/Swipe ——
//      否则"关掉弹窗"会莫名其妙多出一次点击（这是最容易踩的手感坑）。
//   ③ **方向八向**：斜向不再退化成"轴大者胜"，方向按角度均分，速度单独给 ——
//      技能拖拽、闪避滑动都要用斜向。
// ============================================================

namespace Revolution
{
    /// <summary>手势识别内核：吃快照，出手势事件。</summary>
    public sealed class RevGestureRecognizer
    {
        private struct Track
        {
            public bool Active;
            public bool Suppressed;      // 屏蔽期间开始时被标记：抬手不产出 Tap/Swipe
            public int Id;
            public float StartX, StartY;
            public float LastX, LastY;
            public float TotalX, TotalY; // 累计位移（起点 → 当前）
            public float MaxDistance;    // 轨迹上离起点的最远距离（抖动不影响它）
            public double StartTime;
            public double LastTime;
            public bool DragStarted;
            public bool LongPressFired;
        }

        private readonly Track[] _tracks = new Track[RevInputLimits.MaxPointers];
        private readonly RevGestureEvent[] _events = new RevGestureEvent[RevInputLimits.MaxGestureEvents];
        private int _count;

        // ── 可调参数（业务从 RevInput 改；改完立刻生效）────────────
        /// <summary>轻点：按下到抬起的最长时间（秒）。</summary>
        public float TapMaxSeconds = RevInputLimits.DefaultTapMaxSeconds;
        /// <summary>轻点：允许的最大位移（像素）。</summary>
        public float TapMaxDistance = RevInputLimits.DefaultTapMaxDistance;
        /// <summary>双击：两次点击的最大间隔（秒）。</summary>
        public float DoubleTapSeconds = RevInputLimits.DefaultDoubleTapSeconds;
        /// <summary>双击：两次点击的最大位置偏差（像素）。</summary>
        public float DoubleTapDistance = RevInputLimits.DefaultDoubleTapDistance;
        /// <summary>长按：按住多久算长按（秒）。</summary>
        public float LongPressSeconds = RevInputLimits.DefaultLongPressSeconds;
        /// <summary>长按：允许的最大漂移（像素）。</summary>
        public float LongPressMaxDistance = RevInputLimits.DefaultLongPressMaxDistance;
        /// <summary>拖动：超过多少像素算拖动。</summary>
        public float DragThreshold = RevInputLimits.DefaultDragThreshold;
        /// <summary>滑动：至少移动多少像素。</summary>
        public float SwipeMinDistance = RevInputLimits.DefaultSwipeMinDistance;
        /// <summary>滑动：至少多快（像素/秒）。</summary>
        public float SwipeMinSpeed = RevInputLimits.DefaultSwipeMinSpeed;

        // 游戏刚开始时 realtime 可能正好是 0 秒，玩家这时也可能完成第一次点击。
        // 所以不能用“上次点击时间等于 0”表示“从未点击”，否则紧接着的第二次点击无法识别为双击；用独立布尔值记录是否已有上次点击。
        private double _lastTapTime;
        private bool _hasLastTap;
        private float _lastTapX, _lastTapY;

        // 双指（捏合 / 旋转）
        private bool _twoActive;
        private float _prevPinchDistance;
        private float _prevPinchAngle;
        private int _firstPointerId = int.MinValue;
        private int _secondPointerId = int.MinValue;

        /// <summary>本帧识别出的手势条数。</summary>
        public int Count => _count;

        /// <summary>取本帧第 <paramref name="index"/> 条手势（越界返回 default）。</summary>
        public RevGestureEvent At(int index) => index >= 0 && index < _count ? _events[index] : default;

        /// <summary>本帧有没有出现某种手势。</summary>
        public bool Has(RevGestureKind kind)
        {
            for (int i = 0; i < _count; i++)
                if (_events[i].Kind == kind) return true;
            return false;
        }

        /// <summary>取本帧第一条某种手势。</summary>
        public bool TryGet(RevGestureKind kind, out RevGestureEvent result)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_events[i].Kind == kind)
                {
                    result = _events[i];
                    return true;
                }
            }
            result = default;
            return false;
        }

        /// <summary>本帧双指缩放比例（1 = 不变；多段捏合累乘）。</summary>
        public float PinchScale { get; private set; } = 1f;

        /// <summary>本帧双指旋转角度增量（度）。</summary>
        public float RotateDegrees { get; private set; }

        /// <summary>清空本帧产出（每帧开头调）。</summary>
        public void BeginFrame()
        {
            _count = 0;
            PinchScale = 1f;
            RotateDegrees = 0f;
        }

        /// <summary>整表重置（失焦 / 换场景：丢掉所有进行中的手势）。</summary>
        public void ResetAll()
        {
            for (int i = 0; i < _tracks.Length; i++) _tracks[i] = default;
            _count = 0;
            _twoActive = false;
            _firstPointerId = _secondPointerId = int.MinValue;
            _lastTapTime = 0d;
            _hasLastTap = false;
            PinchScale = 1f;
            RotateDegrees = 0f;
        }

        /// <summary>
        /// 推进一帧。
        /// </summary>
        /// <param name="snapshot">本帧快照（指针在这里）。</param>
        /// <param name="realtime">真实时间（秒）—— 手势窗口按它算，不受暂停影响。</param>
        /// <param name="blocked">本帧世界输入是否被屏蔽（屏蔽则只跟踪、不产出）。</param>
        public void Update(in RevInputSnapshot snapshot, double realtime, bool blocked)
            => Update(snapshot, realtime, blocked, null);

        /// <summary>推进一帧，并可独立屏蔽单指；原始快照保持不变。</summary>
        internal void Update(in RevInputSnapshot snapshot, double realtime, bool blocked, System.Func<int, bool> pointerBlocked)
        {
            // 单指与双指事件都使用 snapshot.Frame；固定写 0 会让事件无法和产生它的输入帧对应。
            for (int i = 0; i < snapshot.PointerCount; i++)
            {
                RevPointerSample p = snapshot.Pointer(i);
                bool pointerMuted = pointerBlocked != null && pointerBlocked(p.Id);
                bool muted = blocked || pointerMuted;
                Track t = Find(p.Id);
                bool isNew = !t.Active;

                if (isNew && (p.Phase == RevPointerPhase.Began || p.Phase == RevPointerPhase.Moved
                              || p.Phase == RevPointerPhase.Stationary))
                {
                    t = NewTrack(p, realtime);
                    if (muted) t.Suppressed = true;
                }
                else if (!isNew && t.Id != p.Id)
                {
                    continue;
                }

                if (!t.Active) continue;
                if (pointerMuted) t.Suppressed = true;

                switch (p.Phase)
                {
                    case RevPointerPhase.Began:
                    case RevPointerPhase.Moved:
                    case RevPointerPhase.Stationary:
                        UpdateMoving(ref t, p, realtime, muted, snapshot.Frame);
                        break;

                    case RevPointerPhase.Ended:
                    case RevPointerPhase.Canceled:
                        FinishPointer(ref t, p, realtime, muted, p.Phase == RevPointerPhase.Canceled, snapshot.Frame);
                        break;
                }

                Store(t);
            }

            UpdateTwoFinger(snapshot, blocked, pointerBlocked, snapshot.Frame);
        }

        private Track NewTrack(in RevPointerSample p, double realtime)
        {
            Track t = default;
            t.Active = true;
            t.Id = p.Id;
            t.StartX = t.LastX = p.X;
            t.StartY = t.LastY = p.Y;
            t.StartTime = t.LastTime = realtime;
            t.MaxDistance = 0f;
            return t;
        }

        private void UpdateMoving(ref Track t, in RevPointerSample p, double realtime, bool blocked, int frame)
        {
            t.TotalX = p.X - t.StartX;
            t.TotalY = p.Y - t.StartY;
            t.MaxDistance = Farthest(t.MaxDistance, p.X - t.StartX, p.Y - t.StartY);

            if (p.Phase == RevPointerPhase.Moved)
            {
                // 拖动：超过阈值后每帧产出（含第一帧），业务可以直接拿本帧增量
                if (!t.DragStarted && t.MaxDistance >= DragThreshold) t.DragStarted = true;
                if (t.DragStarted && !blocked && !t.Suppressed)
                {
                    Emit(new RevGestureEvent(RevGestureKind.Drag, t.Id, RevSwipeDirection.None,
                        p.X, p.Y, t.StartX, t.StartY, p.DeltaX, p.DeltaY, 0f,
                        (float)(realtime - t.StartTime), frame));
                }
            }

            // 长按：只报一次（漂移不能太大，否则是拖动不是长按）
            if (!t.LongPressFired && !t.DragStarted
                && t.MaxDistance <= LongPressMaxDistance
                && (realtime - t.StartTime) >= LongPressSeconds)
            {
                t.LongPressFired = true;
                if (!blocked && !t.Suppressed)
                {
                    Emit(new RevGestureEvent(RevGestureKind.LongPress, t.Id, RevSwipeDirection.None,
                        p.X, p.Y, t.StartX, t.StartY, 0f, 0f,
                        (float)(realtime - t.StartTime), (float)(realtime - t.StartTime), frame));
                }
            }

            t.LastX = p.X;
            t.LastY = p.Y;
            t.LastTime = realtime;
        }

        private void FinishPointer(ref Track t, in RevPointerSample p, double realtime, bool blocked, bool canceled, int frame)
        {
            double duration = realtime - t.StartTime;
            // 触摸抬起时的 Ended 坐标有时比最后一次 Moved 报告的位置更靠前；它才是手指真正离开的位置。
            // 用旧 Moved 坐标算方向会出现“手指向右抬起、事件却报向上”的不一致，所以结束时用 Ended 坐标重算总距离和方向。
            t.TotalX = p.X - t.StartX;
            t.TotalY = p.Y - t.StartY;
            float distance = Farthest(t.MaxDistance, t.TotalX, t.TotalY);
            float speed = duration > 0.0001d ? (float)(distance / duration) : 0f;

            bool muted = blocked || t.Suppressed || canceled;
            if (!muted)
            {
                if (distance >= SwipeMinDistance && speed >= SwipeMinSpeed)
                {
                    Emit(new RevGestureEvent(RevGestureKind.Swipe, t.Id, DirectionOf(t.TotalX, t.TotalY),
                        p.X, p.Y, t.StartX, t.StartY, t.TotalX, t.TotalY,
                        speed, (float)duration, frame));
                }
                else if (duration <= TapMaxSeconds && distance <= TapMaxDistance && !t.LongPressFired)
                {
                    // 所有手势事件都带当前快照帧号；写常量 0 会让事件帧号失真，无法和输入帧对齐。
                    Emit(new RevGestureEvent(RevGestureKind.Tap, t.Id, RevSwipeDirection.None,
                        p.X, p.Y, t.StartX, t.StartY, 0f, 0f, (float)duration, (float)duration, frame));

                    // 双击：与上一次轻点的时间 + 距离都够近
                    if (_hasLastTap
                        && (realtime - _lastTapTime) <= DoubleTapSeconds
                        && Distance(p.X, p.Y, _lastTapX, _lastTapY) <= DoubleTapDistance)
                    {
                        Emit(new RevGestureEvent(RevGestureKind.DoubleTap, t.Id, RevSwipeDirection.None,
                            p.X, p.Y, t.StartX, t.StartY, 0f, 0f,
                            (float)(realtime - _lastTapTime), (float)duration, frame));
                        _lastTapTime = 0d;   // 双击后归零：三连击 = 双击 + 轻点（不会无限滚）
                        _hasLastTap = false;
                    }
                    else
                    {
                        _lastTapTime = realtime;
                        _hasLastTap = true;
                        _lastTapX = p.X;
                        _lastTapY = p.Y;
                    }
                }
            }

            t.Active = false;
        }

        private void UpdateTwoFinger(in RevInputSnapshot snapshot, bool blocked,
            System.Func<int, bool> pointerBlocked, int frame)
        {
            int firstId = int.MaxValue, secondId = int.MaxValue;
            float firstX = 0f, firstY = 0f, secondX = 0f, secondY = 0f;
            int found = 0;

            // 触摸设备每帧返回的手指顺序不一定相同。若直接取数组前两项，顺序交换时两指连线会反过来，计算结果可能突然跳 180 度。
            // 按固定的 pointer id 顺序挑选两根手指，保证每帧都用同一顺序计算捏合距离和旋转角度。
            for (int i = 0; i < snapshot.PointerCount; i++)
            {
                RevPointerSample p = snapshot.Pointer(i);
                if (p.Phase == RevPointerPhase.Ended || p.Phase == RevPointerPhase.Canceled) continue;
                if (pointerBlocked != null && pointerBlocked(p.Id)) continue;

                if (found == 0)
                {
                    firstId = p.Id; firstX = p.X; firstY = p.Y; found = 1;
                }
                else if (found == 1)
                {
                    secondId = p.Id; secondX = p.X; secondY = p.Y; found = 2;
                    if (secondId < firstId)
                    {
                        int id = firstId; firstId = secondId; secondId = id;
                        float x = firstX; firstX = secondX; secondX = x;
                        float y = firstY; firstY = secondY; secondY = y;
                    }
                }
                else if (p.Id < secondId)
                {
                    if (p.Id < firstId)
                    {
                        secondId = firstId; secondX = firstX; secondY = firstY;
                        firstId = p.Id; firstX = p.X; firstY = p.Y;
                    }
                    else
                    {
                        secondId = p.Id; secondX = p.X; secondY = p.Y;
                    }
                }
            }

            if (found < 2)
            {
                _twoActive = false;
                _firstPointerId = _secondPointerId = int.MinValue;
                return;
            }

            float dx = secondX - firstX;
            float dy = secondY - firstY;
            float distance = (float)System.Math.Sqrt(dx * dx + dy * dy);
            float angle = (float)(System.Math.Atan2(dy, dx) * 180d / System.Math.PI);
            bool samePair = _twoActive && firstId == _firstPointerId && secondId == _secondPointerId;

            if (samePair && distance > 0.01f && _prevPinchDistance > 0.01f && !blocked)
            {
                float scale = distance / _prevPinchDistance;
                float rotate = NormalizeAngle(angle - _prevPinchAngle);
                int mx = (int)((firstX + secondX) * 0.5f);
                int my = (int)((firstY + secondY) * 0.5f);

                if (System.Math.Abs(scale - 1f) > 0.001f)
                {
                    PinchScale *= scale;
                    Emit(new RevGestureEvent(RevGestureKind.Pinch, firstId,
                        RevSwipeDirection.None, mx, my, mx, my, 0f, 0f, scale, 0f, frame));
                }
                if (System.Math.Abs(rotate) > 0.01f)
                {
                    RotateDegrees += rotate;
                    Emit(new RevGestureEvent(RevGestureKind.Rotate, firstId,
                        RevSwipeDirection.None, mx, my, mx, my, 0f, 0f, rotate, 0f, frame));
                }
            }

            _twoActive = true;
            _firstPointerId = firstId;
            _secondPointerId = secondId;
            _prevPinchDistance = distance;
            _prevPinchAngle = angle;

            // 两指组合只使用未屏蔽指针；组合开始或换指时都抑制它们的单指抬起手势。
            for (int i = 0; i < _tracks.Length; i++)
            {
                if (!_tracks[i].Active) continue;
                if (_tracks[i].Id == firstId || _tracks[i].Id == secondId) _tracks[i].Suppressed = true;
            }
        }

        // ── 工具 ────────────────────────────────────────────

        private Track Find(int id)
        {
            for (int i = 0; i < _tracks.Length; i++)
                if (_tracks[i].Active && _tracks[i].Id == id) return _tracks[i];
            return default;
        }

        private void Store(in Track t)
        {
            for (int i = 0; i < _tracks.Length; i++)
            {
                if (_tracks[i].Active && _tracks[i].Id == t.Id)
                {
                    _tracks[i] = t;
                    return;
                }
                if (!_tracks[i].Active && t.Active)
                {
                    _tracks[i] = t;
                    return;
                }
            }
            if (!t.Active) return;
        }

        private void Emit(in RevGestureEvent e)
        {
            if (_count >= _events.Length)
            {
                // 溢出丢最旧（保留最新：业务更关心刚刚发生的事）
                for (int i = 1; i < _events.Length; i++) _events[i - 1] = _events[i];
                _events[_events.Length - 1] = e;
                return;
            }
            _events[_count++] = e;
        }

        /// <summary>八向判定：把位移换成方向（斜向均分 45°）。</summary>
        public static RevSwipeDirection DirectionOf(float dx, float dy)
        {
            if (dx == 0f && dy == 0f) return RevSwipeDirection.None;
            double angle = System.Math.Atan2(dy, dx) * 180d / System.Math.PI;   // -180..180，0 = 右
            if (angle < 0d) angle += 360d;                                     // 0..360，逆时针

            if (angle >= 337.5d || angle < 22.5d) return RevSwipeDirection.Right;
            if (angle < 67.5d) return RevSwipeDirection.UpRight;
            if (angle < 112.5d) return RevSwipeDirection.Up;
            if (angle < 157.5d) return RevSwipeDirection.UpLeft;
            if (angle < 202.5d) return RevSwipeDirection.Left;
            if (angle < 247.5d) return RevSwipeDirection.DownLeft;
            if (angle < 292.5d) return RevSwipeDirection.Down;
            return RevSwipeDirection.DownRight;
        }

        private static float Farthest(float current, float dx, float dy)
        {
            float d = (float)System.Math.Sqrt(dx * dx + dy * dy);
            return d > current ? d : current;
        }

        private static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)System.Math.Sqrt(dx * dx + dy * dy);
        }

        private static float NormalizeAngle(float degrees)
        {
            while (degrees > 180f) degrees -= 360f;
            while (degrees < -180f) degrees += 360f;
            return degrees;
        }
    }
}

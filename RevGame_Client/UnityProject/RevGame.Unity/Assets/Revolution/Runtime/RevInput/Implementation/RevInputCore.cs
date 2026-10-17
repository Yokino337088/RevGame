// ============================================================
// RevInputCore.cs —— 输入内核（纯 C#：这里没有一行 UnityEngine）
//
// 位置：Runtime\RevInput\Implementation\
//
// 【要解决的问题】
//   一帧的输入要经过：采集 → 判相位 → 算轴 → 识别手势 → 派发事件 → 供业务查询。
//   这些都在这里完成，而且**一帧只做一次**（业务查询全是读结果，不再重算）。
//
// 【三条铁律】
//   ① **纯 C#**：时间以参数进来、采集 / 日志 / 宿主一律以委托注入 ——
//      所以这个文件能被链接进普通 .NET 工程跑断言（见模块 README 的验收一节）。
//   ② **零分配**：每帧只有位运算、数组写入与 for 循环；没有 LINQ、没有装箱、没有字符串拼接。
//   ③ **失败不静默也不炸**：配置类错误记 <see cref="RevInputErrorReason"/> 并走 `Failed` 事件；
//      业务监听者抛的异常被 <see cref="RevInputLog.Guard"/> 隔离，不影响同帧其它监听者。
//
// 【和 Support 的分工】
//   这里不建 GameObject、不读 Input、不碰 EventSystem —— 那些都在 `Support\` 里，
//   通过 <see cref="EnsureDriver"/> 与 <see cref="Device"/> 两个口子接进来。
// ============================================================

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>输入内核：持有绑定表、快照、手势识别器、屏蔽栈与事件订阅。</summary>
    internal sealed class RevInputCore
    {
        /// <summary>宿主钩子：内核不知道该由谁每帧驱动，只负责喊一声（Support 层把它接到隐藏宿主上）。</summary>
        internal static Action EnsureDriver;

        // ── 组成 ────────────────────────────────────────────

        /// <summary>本帧快照（复用同一实例，零分配）。</summary>
        internal readonly RevInputSnapshot Snapshot = new RevInputSnapshot();

        /// <summary>绑定表 + 全部动作的每帧状态。</summary>
        internal readonly RevInputActionTable Actions = new RevInputActionTable();

        /// <summary>手势识别器。</summary>
        internal readonly RevGestureRecognizer Gestures = new RevGestureRecognizer();

        /// <summary>
        /// 采集口：由适配层（Support）接上引擎设备，或由测试脚本直接写入快照。
        /// 内核**不认识任何引擎类型**，只认这个委托 —— 这就是"内核纯 C#"的接缝。
        /// </summary>
        internal Action<RevInputSnapshot> Poll;
        internal Action ResetDeviceState;

        /// <summary>采集源名字（只用于自检输出）。</summary>
        internal string SourceName = "无";

        /// <summary>命名轴提供者（Support 接到 <c>Input.GetAxis</c>；纯 C# 环境下为 null = 不支持命名轴）。</summary>
        internal Func<string, float> AxisProvider;

        /// <summary>失败事件（原因码 + 说明）。</summary>
        internal Action<RevInputErrorReason, string> Failed;

        /// <summary>业务强制指定的设备类型（<see cref="RevInputDeviceKind.Unknown"/> = 自动识别）。</summary>
        internal RevInputDeviceKind ForcedDeviceKind = RevInputDeviceKind.Unknown;

        /// <summary>手动驱动中（宿主让位，业务自己调 <c>Tick</c>）。</summary>
        internal bool ManualDriven;
        private bool _isTicking;

        // 可复用派发快照：订阅在回调期间发生增删时，本轮派发仍按开始时的订阅集完成；下一次派发应用变更。
        private readonly List<ActionEntry> _pressedDispatch = new List<ActionEntry>(16);
        private readonly List<ActionEntry> _releasedDispatch = new List<ActionEntry>(16);
        private readonly List<GestureEntry> _gestureDispatch = new List<GestureEntry>(8);
        private readonly List<ListenerEntry> _listenerDispatch = new List<ListenerEntry>(8);
        private readonly List<AxisEntry> _axisDispatch = new List<AxisEntry>(8);
        private readonly List<RepeatEntry> _repeatDispatch = new List<RepeatEntry>(8);
        private readonly List<ActionStateEntry> _actionStateDispatch = new List<ActionStateEntry>(RevInputLimits.MaxActions);
        private readonly Func<int, bool> _pointerBlockPredicate;

        internal RevInputCore()
        {
            _pointerBlockPredicate = IsPointerBlocked;
        }

        // ── 本帧信息（供查询与诊断）──────────────────────────
        internal double Realtime;
        internal int Frame;
        internal float DeltaTime;

        // ── 统计（只在 <c>Dump</c> 里读）─────────────────────
        internal int TickCount;
        internal int FramesWithInput;
        internal int GestureTotal;
        internal int BlockedFrames;
        internal int FailedCount;

        // ── 屏蔽栈 ──────────────────────────────────────────
        private struct BlockEntry
        {
            public int Id;
            public RevInputBlockKind Kind;
            public int PointerId;
            public object Owner;
        }

        private readonly List<BlockEntry> _blocks = new List<BlockEntry>(8);
        private int _nextBlockId = 1;

        // ── 事件订阅（列表 + owner，退订按引用相等）──────────
        private struct ActionEntry
        {
            public string Action;
            public Action Handler;
            public object Owner;
        }

        private struct GestureEntry
        {
            public RevGestureKind Kind;
            public Action<RevGestureEvent> Handler;
            public object Owner;
        }

        private readonly List<ActionEntry> _pressed = new List<ActionEntry>(16);
        private readonly List<ActionEntry> _released = new List<ActionEntry>(16);
        private readonly List<GestureEntry> _gestureHandlers = new List<GestureEntry>(8);

        // ── 事件驱动接入：监听者 / 轴 / 连发 ──────────────────
        private struct ListenerEntry
        {
            public RevInputListener Listener;
            public object Owner;
        }

        private struct AxisEntry
        {
            public int Id;
            public string Axis;
            public Action<float> Handler;
            public object Owner;
            public bool HasValue;
            public float LastValue;
        }

        private struct ListenerAxisEntry
        {
            public RevInputListener Listener;
            public string Axis;
            public bool HasValue;
            public float LastValue;
        }

        private struct RepeatEntry
        {
            public string Action;
            public Action Handler;
            public object Owner;
        }

        private struct ActionStateEntry
        {
            public string Action;
            public RevInputActionState State;
        }

        /// <summary>轴变化判定阈值（小于它不算变化，避免摇杆抖动刷屏）。</summary>
        private const float AxisChangeEpsilon = 0.001f;

        private readonly List<ListenerEntry> _listeners = new List<ListenerEntry>(8);
        private readonly List<AxisEntry> _axisHandlers = new List<AxisEntry>(8);
        private readonly List<ListenerAxisEntry> _listenerAxisLast = new List<ListenerAxisEntry>(32);
        private readonly List<RepeatEntry> _repeatHandlers = new List<RepeatEntry>(8);
        private int _nextAxisHandlerId = 1;

        /// <summary>每个轴上次投递的值（只在变化时回调；换会话时清空）。</summary>

        // ── 每帧推进 ────────────────────────────────────────

        /// <summary>
        /// 推进一帧。**由驱动调用**（隐藏宿主或 <c>RevInput.Tick</c>）。
        /// </summary>
        internal void Tick(float deltaTime, float unscaledDeltaTime, double realtime, int frame)
        {
            // 一次 Tick 会依次写入本帧输入、更新按键状态、识别手势并派发事件；这些步骤共用同一份数据。
            // 如果采集或业务回调里又调用 Tick（重入），内层调用会把外层尚未处理完的帧数据覆盖，造成丢按键、重复事件或手势错乱。
            // 因此明确拒绝第二次进入；下面的 finally 保证即使处理过程抛异常，也会解除“正在 Tick”标记，不会让后续帧永久被挡住。
            if (_isTicking) throw new InvalidOperationException("RevInput.Tick 不能从采集或事件回调中重入。");
            _isTicking = true;
            try
            {
                TickCore(deltaTime, unscaledDeltaTime, realtime, frame);
            }
            finally
            {
                _isTicking = false;
            }
        }

        private void TickCore(float deltaTime, float unscaledDeltaTime, double realtime, int frame)
        {
            TickCount++;
            DeltaTime = deltaTime;
            Realtime = realtime;
            Frame = frame;

            // ① 采集：设备把原始输入写进快照
            Snapshot.Reset();
            Snapshot.DeltaTime = deltaTime;
            Snapshot.UnscaledDeltaTime = unscaledDeltaTime;
            Snapshot.Realtime = realtime;
            Snapshot.Frame = frame;

            Action<RevInputSnapshot> poll = Poll;
            if (poll != null)
            {
                try
                {
                    poll(Snapshot);
                }
                catch (Exception e)
                {
                    // Poll 可能在抛异常前已经写入部分字段；整帧回滚为无输入，避免半份快照驱动状态。
                    Snapshot.Reset();
                    Snapshot.DeltaTime = deltaTime;
                    Snapshot.UnscaledDeltaTime = unscaledDeltaTime;
                    Snapshot.Realtime = realtime;
                    Snapshot.Frame = frame;
                    RevInputLog.OnException?.Invoke(e, "输入采集(" + SourceName + ")");
                }
            }
            else
            {
                RevInputLog.V("[RevInput] 还没接采集口：Support 未装载时正常（纯 C# 跑断言时也如此）");
            }

            if (ForcedDeviceKind != RevInputDeviceKind.Unknown)
                Snapshot.Device = ForcedDeviceKind;

            bool allBlocked = IsBlocked(RevInputBlockKind.All);
            bool worldBlocked = allBlocked || IsBlocked(RevInputBlockKind.World);
            if (worldBlocked) BlockedFrames++;

            // ② 判相位 / 算轴：一次遍历算完所有动作
            Actions.Update(Snapshot, realtime, worldBlocked, AxisProvider);
            SnapshotDispatchSubscriptions();

            // ③ 手势识别（全部屏蔽时连识别都不做：指针位置也读不到）
            Gestures.BeginFrame();
            // Pointer 屏蔽不会从原始快照中删除手指，因为其他 API 仍可能需要查看原始采样；因此识别器也必须单独收到屏蔽规则。
            // 如果漏传，业务查询虽然看不到被屏蔽的手指，手势识别器却仍会读到它并发出 Tap/Swipe，或把它加入 Pinch/Rotate。
            Gestures.Update(Snapshot, realtime, worldBlocked || allBlocked, _pointerBlockPredicate);

            // ④ 派发动作事件
            DispatchActionEvents(_pressedDispatch, pressed: true);
            DispatchActionEvents(_releasedDispatch, pressed: false);

            // ⑤ 派发手势事件
            if (Gestures.Count > 0)
            {
                GestureTotal += Gestures.Count;
                for (int i = 0; i < Gestures.Count; i++)
                {
                    RevGestureEvent e = Gestures.At(i);
                    for (int h = 0; h < _gestureDispatch.Count; h++)
                    {
                        GestureEntry entry = _gestureDispatch[h];
                        if (entry.Kind != RevGestureKind.None && entry.Kind != e.Kind) continue;

                        Action<RevGestureEvent> handler = entry.Handler;
                        RevGestureEvent evt = e;
                        try
                        {
                            handler(evt);            // ★ 同上：热路径不用 lambda，就地 try/catch
                        }
                        catch (Exception ex)
                        {
                            RevInputLog.OnException?.Invoke(ex, "OnGesture");
                        }
                    }

                    NotifyGestureToListeners(e);          // 同一条手势也推给监听者
                }
            }

            // ⑥ 派发连发事件（配了连发的动作；不用你自己在 Update 里查 Repeat）
            DispatchRepeatEvents();

            // ⑦ 派发轴事件（只在数值变化时回调；没有订阅者时零开销）
            DispatchAxisEvents();

            // ⑧ 投给监听者（事件驱动接入面：按下 / 抬起 / 连发；没有监听者时零开销）
            DispatchToListeners();

            if (Snapshot.KeyHeld.Any || Snapshot.PointerCount > 0) FramesWithInput++;
        }

        private static void ReserveCapacity<T>(List<T> list, int required)
        {
            if (list.Capacity < required) list.Capacity = required;
        }

        private void SnapshotDispatchSubscriptions()
        {
            // 事件回调可以当场添加或移除订阅。如果一边遍历原列表一边执行回调，列表长度和位置会立刻变化，
            // 可能跳过还没收到事件的订阅者，甚至访问越界；刚添加的订阅也可能在本帧中途意外收到事件。
            // 先复制本帧开始时的订阅名单：本帧按这份名单派发，回调里的增删从下一帧开始生效。
            _pressedDispatch.Clear();
            _pressedDispatch.AddRange(_pressed);
            _releasedDispatch.Clear();
            _releasedDispatch.AddRange(_released);
            _gestureDispatch.Clear();
            _gestureDispatch.AddRange(_gestureHandlers);
            _listenerDispatch.Clear();
            _listenerDispatch.AddRange(_listeners);
            _axisDispatch.Clear();
            _axisDispatch.AddRange(_axisHandlers);
            _repeatDispatch.Clear();
            _repeatDispatch.AddRange(_repeatHandlers);
            _actionStateDispatch.Clear();
            for (int i = 0; i < Actions.Count; i++)
            {
                RevInputBinding binding = Actions.At(i);
                _actionStateDispatch.Add(new ActionStateEntry
                {
                    Action = binding.Action,
                    State = Actions.State(binding.Action),
                });
            }
        }

        private void DispatchActionEvents(List<ActionEntry> list, bool pressed)
        {
            // 本帧开始时已复制了待派发的动作名单；某个回调可能在派发途中删除另一个动作，但它仍留在这份副本里。
            // 每次调用前再查动作是否存在，已删除就跳过，避免之后继续触发旧的按下/抬起回调。
            if (list.Count == 0) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                ActionEntry entry = list[i];
                if (string.IsNullOrEmpty(entry.Action) || entry.Handler == null || Actions.Find(entry.Action) == null)
                    continue;

                RevInputActionState s = Actions.State(entry.Action);
                bool fire = pressed ? s.Down : s.Up;
                if (!fire) continue;

                Action handler = entry.Handler;
                try
                {
                    // ★ 不用 RevInputLog.Guard：它要传 lambda（每次派发分配闭包）+ 拼字符串。
                    //   派发是逐帧热路径，就地 try/catch；上下文直接用动作名（本来就是现成字符串）。
                    handler();
                }
                catch (Exception e)
                {
                    RevInputLog.OnException?.Invoke(e, entry.Action);
                }
            }
        }

        // ── 事件驱动投递（监听者 / 轴 / 连发）───────────────

        private enum NotifyKind
        {
            Pressed = 0,
            Released = 1,
            Repeat = 2,
        }

        /// <summary>把本帧的动作事件推给监听者。**不依赖有没有人订阅委托**（只注册监听者也能收到）。</summary>
        private void DispatchToListeners()
        {
            if (_listenerDispatch.Count == 0) return;
            for (int i = 0; i < _actionStateDispatch.Count; i++)
            {
                ActionStateEntry entry = _actionStateDispatch[i];
                if (Actions.Find(entry.Action) == null) continue;
                RevInputActionState state = entry.State;
                if (state.Down) NotifyActionToListeners(entry.Action, NotifyKind.Pressed);
                if (state.Up) NotifyActionToListeners(entry.Action, NotifyKind.Released);
                if (state.Repeat) NotifyActionToListeners(entry.Action, NotifyKind.Repeat);
            }
        }

        private void NotifyActionToListeners(string action, NotifyKind kind)
        {
            // 使用本帧起始时的派发快照：回调内增删订阅从下一帧起生效，不会跳过其它监听者。
            for (int i = _listenerDispatch.Count - 1; i >= 0; i--)
            {
                RevInputListener listener = _listenerDispatch[i].Listener;
                if (listener == null) continue;
                try
                {
                    switch (kind)
                    {
                        case NotifyKind.Pressed:
                            listener.OnInputPressed(action);
                            break;
                        case NotifyKind.Released:
                            listener.OnInputReleased(action);
                            break;
                        default:
                            listener.OnInputRepeat(action);
                            break;
                    }
                }
                catch (Exception e)
                {
                    RevInputLog.OnException?.Invoke(e, kind + "(" + action + ")");
                }
            }
        }

        private void NotifyGestureToListeners(in RevGestureEvent gesture)
        {
            if (_listenerDispatch.Count == 0) return;
            for (int i = _listenerDispatch.Count - 1; i >= 0; i--)
            {
                RevInputListener listener = _listenerDispatch[i].Listener;
                if (listener == null) continue;
                RevGestureEvent evt = gesture;
                try
                {
                    listener.OnInputGesture(evt);
                }
                catch (Exception e)
                {
                    RevInputLog.OnException?.Invoke(e, "OnInputGesture(" + evt.Kind + ")");
                }
            }
        }

        /// <summary>连发事件：投给 <c>OnRepeat</c> 的订阅者。</summary>
        private void DispatchRepeatEvents()
        {
            if (_repeatDispatch.Count == 0) return;
            for (int i = 0; i < _actionStateDispatch.Count; i++)
            {
                ActionStateEntry action = _actionStateDispatch[i];
                if (!action.State.Repeat) continue;

                for (int h = _repeatDispatch.Count - 1; h >= 0; h--)
                {
                    RepeatEntry entry = _repeatDispatch[h];
                    if (entry.Action != action.Action || entry.Handler == null || Actions.Find(entry.Action) == null) continue;
                    Action handler = entry.Handler;
                    try
                    {
                        handler();
                    }
                    catch (Exception e)
                    {
                        RevInputLog.OnException?.Invoke(e, "OnRepeat(" + action.Action + ")");
                    }
                }
            }
        }

        /// <summary>
        /// 轴事件：投给 <c>OnAxis</c> 订阅者与监听者。
        /// **只在数值变化时回调**（订阅后第一次会给出当前值，方便初始化移动 / UI）。
        /// </summary>
        private void DispatchAxisEvents()
        {
            if (_axisDispatch.Count == 0 && _listenerDispatch.Count == 0) return;

            for (int i = 0; i < _actionStateDispatch.Count; i++)
            {
                ActionStateEntry action = _actionStateDispatch[i];
                if (Actions.Find(action.Action) == null) continue;
                float value = action.State.Axis;

                // 每个 handler 用自己的 HasValue 基线；新订阅即使当前值为 0 也必须先收到一次初始化值。
                for (int h = _axisDispatch.Count - 1; h >= 0; h--)
                {
                    AxisEntry entry = _axisDispatch[h];
                    if (entry.Axis != action.Action || entry.Handler == null) continue;
                    if (entry.HasValue && Math.Abs(value - entry.LastValue) < AxisChangeEpsilon) continue;

                    entry.HasValue = true;
                    entry.LastValue = value;
                    Action<float> handler = entry.Handler;
                    try
                    {
                        handler(value);
                    }
                    catch (Exception e)
                    {
                        RevInputLog.OnException?.Invoke(e, "OnAxis(" + action.Action + ")");
                    }
                    StoreAxisHandlerValue(entry);
                }

                if (Actions.Find(action.Action) == null) continue;
                // Listener 也逐个记录是否收到过轴值；不可只依赖全局值变化，新监听者首值为 0 时仍要初始化。
                for (int l = _listenerDispatch.Count - 1; l >= 0; l--)
                {
                    RevInputListener listener = _listenerDispatch[l].Listener;
                    if (listener == null) continue;
                    int stateIndex = FindListenerAxis(listener, action.Action);
                    bool hasValue = stateIndex >= 0 && _listenerAxisLast[stateIndex].HasValue;
                    float lastValue = hasValue ? _listenerAxisLast[stateIndex].LastValue : 0f;
                    if (hasValue && Math.Abs(value - lastValue) < AxisChangeEpsilon) continue;

                    try
                    {
                        listener.OnInputAxis(action.Action, value);
                    }
                    catch (Exception e)
                    {
                        RevInputLog.OnException?.Invoke(e, "OnInputAxis(" + action.Action + ")");
                    }
                    StoreListenerAxisValue(listener, action.Action, value);
                }
            }
        }

        internal void ResetAxisSubscriptionValues()
        {
            _listenerAxisLast.Clear();
            for (int i = 0; i < _axisHandlers.Count; i++)
            {
                AxisEntry entry = _axisHandlers[i];
                entry.HasValue = false;
                _axisHandlers[i] = entry;
            }
        }

        private void StoreAxisHandlerValue(AxisEntry updated)
        {
            for (int i = 0; i < _axisHandlers.Count; i++)
            {
                if (_axisHandlers[i].Id != updated.Id) continue;
                _axisHandlers[i] = updated;
                return;
            }
        }

        private int FindListenerAxis(RevInputListener listener, string axis)
        {
            for (int i = 0; i < _listenerAxisLast.Count; i++)
                if (ReferenceEquals(_listenerAxisLast[i].Listener, listener) && _listenerAxisLast[i].Axis == axis) return i;
            return -1;
        }

        private void StoreListenerAxisValue(RevInputListener listener, string axis, float value)
        {
            for (int i = 0; i < _listenerAxisLast.Count; i++)
            {
                if (!ReferenceEquals(_listenerAxisLast[i].Listener, listener) || _listenerAxisLast[i].Axis != axis) continue;
                ListenerAxisEntry existing = _listenerAxisLast[i];
                existing.HasValue = true;
                existing.LastValue = value;
                _listenerAxisLast[i] = existing;
                return;
            }

            if (IsListenerRegistered(listener))
                _listenerAxisLast.Add(new ListenerAxisEntry { Listener = listener, Axis = axis, HasValue = true, LastValue = value });
        }

        private bool IsListenerRegistered(RevInputListener listener)
        {
            for (int i = 0; i < _listeners.Count; i++)
                if (ReferenceEquals(_listeners[i].Listener, listener)) return true;
            return false;
        }

        internal void AddListener(RevInputListener listener, object owner)
        {
            if (listener == null) return;
            for (int i = 0; i < _listeners.Count; i++)
                if (ReferenceEquals(_listeners[i].Listener, listener)) return;   // 重复登记忽略
            ReserveCapacity(_listenerDispatch, _listeners.Count + 1);
            _listeners.Add(new ListenerEntry { Listener = listener, Owner = owner });
        }

        internal bool RemoveListener(RevInputListener listener)
        {
            bool removed = false;
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_listeners[i].Listener, listener)) continue;
                _listeners.RemoveAt(i);
                removed = true;
            }
            if (removed)
                for (int i = _listenerAxisLast.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(_listenerAxisLast[i].Listener, listener)) _listenerAxisLast.RemoveAt(i);
            return removed;
        }

        internal int ListenerCount => _listeners.Count;

        internal bool AddAxisHandler(string axis, Action<float> handler, object owner)
        {
            if (!RevInputActionTable.IsValidName(axis) || handler == null) return false;
            ReserveCapacity(_axisDispatch, _axisHandlers.Count + 1);
            _axisHandlers.Add(new AxisEntry { Id = _nextAxisHandlerId++, Axis = axis, Handler = handler, Owner = owner });
            return true;
        }

        internal bool RemoveAxisHandler(string axis, Action<float> handler)
        {
            // handler 是 null 时，公开 API 的意思是“退订这根轴上的所有处理器”，而不是只删其中一个。
            // 倒序遍历并持续删除所有匹配项；若找到一个就提前返回，其他同轴处理器仍会继续收到输入。
            bool removed = false;
            for (int i = _axisHandlers.Count - 1; i >= 0; i--)
            {
                if (_axisHandlers[i].Axis != axis) continue;
                if (handler != null && !ReferenceEquals(_axisHandlers[i].Handler, handler)) continue;
                _axisHandlers.RemoveAt(i);
                removed = true;
            }
            return removed;
        }

        internal bool AddRepeatHandler(string action, Action handler, object owner)
        {
            if (!RevInputActionTable.IsValidName(action) || handler == null) return false;
            ReserveCapacity(_repeatDispatch, _repeatHandlers.Count + 1);
            _repeatHandlers.Add(new RepeatEntry { Action = action, Handler = handler, Owner = owner });
            return true;
        }

        internal bool RemoveRepeatHandler(string action, Action handler)
        {
            // handler 为 null 表示清空该动作全部连发订阅；必须移除所有匹配项，避免未退订处理器继续触发。
            bool removed = false;
            for (int i = _repeatHandlers.Count - 1; i >= 0; i--)
            {
                if (_repeatHandlers[i].Action != action) continue;
                if (handler != null && !ReferenceEquals(_repeatHandlers[i].Handler, handler)) continue;
                _repeatHandlers.RemoveAt(i);
                removed = true;
            }
            return removed;
        }

        // ── 复位 ────────────────────────────────────────────

        /// <summary>新会话复位（进 Play）：清掉上一局的一切（静态字段不会自己清）。</summary>
        internal void ResetForNewSession()
        {
            _isTicking = false;
            _pressedDispatch.Clear();
            _releasedDispatch.Clear();
            _gestureDispatch.Clear();
            _listenerDispatch.Clear();
            _axisDispatch.Clear();
            _repeatDispatch.Clear();
            _actionStateDispatch.Clear();
            ResetDeviceState?.Invoke();
            Actions.Clear();
            _listenerAxisLast.Clear();
            _nextAxisHandlerId = 1;
            Gestures.ResetAll();
            Snapshot.Reset();
            _blocks.Clear();
            _pressed.Clear();
            _released.Clear();
            _gestureHandlers.Clear();
            _listeners.Clear();
            _axisHandlers.Clear();
            _repeatHandlers.Clear();
            _nextBlockId = 1;

            // 关闭 Domain Reload 时，Unity 不会重建这个输入内核；如果不清事件，上一局订阅 Failed 的回调会留到下一局。
            // 这些回调常常捕获了旧 UI 或旧管理器：下一局发生键位配置错误时，会调用已经销毁的对象，还会一直占用它的内存。
            // 因此和其他输入订阅一样，在新会话开始时清空 Failed；本局业务初始化完成后可以重新订阅，不会影响正常使用。
            Failed = null;

            ManualDriven = false;
            ForcedDeviceKind = RevInputDeviceKind.Unknown;
            TickCount = FramesWithInput = GestureTotal = BlockedFrames = FailedCount = 0;
        }

        /// <summary>
        /// 局部复位（失焦 / 切后台 / 换场景）：**清按键、缓冲、连发和进行中的手势，保留绑定与屏蔽**。
        /// 这是"切后台回来按键卡住"的正解 —— 抬起事件丢了，就把状态当成"全部松手"。
        /// 复位是静默清理：不会合成 OnReleased；业务若需收尾，应监听应用生命周期的 Paused/Resumed。
        /// </summary>
        internal void ResetAll(string reason)
        {
            Snapshot.FlushKeys();
            Actions.Update(Snapshot, Realtime, false, AxisProvider);   // 让动作状态立刻归一（Held 变 false）
            Actions.ClearTransientInput();                               // 取消尚未消费的缓冲、边沿和连发节拍
            Gestures.ResetAll();
            Snapshot.Reset();
            RevInputLog.V("[RevInput] 复位：" + (reason ?? "未说明") + "（按键与手势已清空，绑定与屏蔽保留）");
        }

        // ── 屏蔽 ────────────────────────────────────────────

        internal RevInputBlock Block(RevInputBlockKind kind, int pointerId, object owner)
        {
            if (kind == RevInputBlockKind.None) return RevInputBlock.Empty;
            if (kind == RevInputBlockKind.Pointer && pointerId < 0)
            {
                Fail(RevInputErrorReason.PointerOutOfRange, "Pointer 屏蔽必须给 pointerId（鼠标用 -1 表示不适用）");
                return RevInputBlock.Empty;
            }

            var entry = new BlockEntry
            {
                Id = _nextBlockId++,
                Kind = kind,
                PointerId = pointerId,
                Owner = owner,
            };
            _blocks.Add(entry);
            RevInputLog.V("[RevInput] 屏蔽 " + kind + (kind == RevInputBlockKind.Pointer ? "#" + pointerId : string.Empty)
                          + "（当前共 " + _blocks.Count + " 条）");
            return new RevInputBlock(entry.Id);
        }

        internal bool Unblock(in RevInputBlock handle)
        {
            if (handle.IsEmpty) return false;
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Id != handle.Id) continue;
                _blocks.RemoveAt(i);
                return true;
            }
            return false;
        }

        internal int UnblockAllOf(object owner)
        {
            if (owner == null) return 0;      // 拒绝"清掉所有人"：那是 ResetAll 的活
            int removed = 0;
            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_blocks[i].Owner, owner))
                {
                    _blocks.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        internal bool IsBlocked(RevInputBlockKind kind)
        {
            for (int i = 0; i < _blocks.Count; i++)
                if (_blocks[i].Kind == kind) return true;
            return false;
        }

        internal bool IsPointerBlocked(int pointerId)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Kind == RevInputBlockKind.All) return true;
                if (_blocks[i].Kind == RevInputBlockKind.Pointer && _blocks[i].PointerId == pointerId) return true;
            }
            return false;
        }

        internal bool WorldBlocked => IsBlocked(RevInputBlockKind.All) || IsBlocked(RevInputBlockKind.World);

        internal RevInputBlockKind GetBlockKind(int id)
        {
            for (int i = 0; i < _blocks.Count; i++)
                if (_blocks[i].Id == id) return _blocks[i].Kind;
            return RevInputBlockKind.None;
        }

        internal int BlockCount => _blocks.Count;

        // ── 读取（把屏蔽语义集中在这里：业务读到的就是"该不该给"）────

        /// <summary>有效指针数（全部屏蔽时为 0；被单独屏蔽的手指不计数）。</summary>
        internal int PointerCount
        {
            get
            {
                if (IsBlocked(RevInputBlockKind.All)) return 0;
                int count = 0;
                for (int i = 0; i < Snapshot.PointerCount; i++)
                    if (!IsPointerBlocked(Snapshot.Pointer(i).Id)) count++;
                return count;
            }
        }

        /// <summary>
        /// 读第 <paramref name="index"/> 根<b>有效</b>指针（被屏蔽的不占下标，保证与 <see cref="PointerCount"/> 成对）。
        /// </summary>
        internal RevPointerSample Pointer(int index)
        {
            if (IsBlocked(RevInputBlockKind.All) || index < 0) return default;
            int seen = 0;
            for (int i = 0; i < Snapshot.PointerCount; i++)
            {
                RevPointerSample sample = Snapshot.Pointer(i);
                if (IsPointerBlocked(sample.Id)) continue;
                if (seen == index) return sample;
                seen++;
            }
            return default;
        }

        internal float MouseX => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.MouseX;
        internal float MouseY => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.MouseY;
        internal float MouseDeltaX => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.MouseDeltaX;
        internal float MouseDeltaY => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.MouseDeltaY;
        internal float ScrollX => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.ScrollX;
        internal float ScrollY => IsBlocked(RevInputBlockKind.All) ? 0f : Snapshot.ScrollY;

        internal bool MouseButton(RevMouseButton button, RevInputPhase phase)
        {
            if (IsBlocked(RevInputBlockKind.All)) return false;
            if (IsPointerBlocked(RevInputSnapshot.MousePointerId)) return false;
            return Snapshot.MouseButton(button, phase);
        }

        // ── 事件订阅 ────────────────────────────────────────

        internal bool RemoveAction(string action)
        {
            if (!Actions.Remove(action)) return false;
            // 订阅记录按动作名字保存。删除动作后如果只删绑定、不删订阅，之后重新创建同名动作时，旧的按下、抬起、连发和轴回调会突然复活。
            // 所以一并删除该动作的订阅与轴缓存；本帧已经复制出去的派发名单也会在调用前再次确认动作仍存在。
            RemoveActionSubscriptions(action);
            for (int i = _listenerAxisLast.Count - 1; i >= 0; i--)
                if (_listenerAxisLast[i].Axis == action) _listenerAxisLast.RemoveAt(i);
            return true;
        }

        private void RemoveActionSubscriptions(string action)
        {
            RemoveActionEntries(_pressed, action);
            RemoveActionEntries(_released, action);
            RemoveRepeatEntries(action);
            RemoveAxisEntries(action);
        }

        private static void RemoveActionEntries(List<ActionEntry> entries, string action)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Action == action) entries.RemoveAt(i);
        }

        private void RemoveRepeatEntries(string action)
        {
            for (int i = _repeatHandlers.Count - 1; i >= 0; i--)
                if (_repeatHandlers[i].Action == action) _repeatHandlers.RemoveAt(i);
        }

        private void RemoveAxisEntries(string action)
        {
            for (int i = _axisHandlers.Count - 1; i >= 0; i--)
                if (_axisHandlers[i].Axis == action) _axisHandlers.RemoveAt(i);
        }

        internal bool AddActionHandler(string action, Action handler, object owner, bool pressed, out RevInputErrorReason reason)
        {
            reason = RevInputErrorReason.None;
            if (!RevInputActionTable.IsValidName(action))
            {
                reason = RevInputErrorReason.InvalidActionName;
                return false;
            }
            if (handler == null) return false;

            var list = pressed ? _pressed : _released;
            ReserveCapacity(pressed ? _pressedDispatch : _releasedDispatch, list.Count + 1);
            var entry = new ActionEntry { Action = action, Handler = handler, Owner = owner };
            list.Add(entry);
            return true;
        }

        internal bool RemoveActionHandler(string action, Action handler, bool pressed)
        {
            var list = pressed ? _pressed : _released;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Action != action) continue;
                if (handler != null && !ReferenceEquals(list[i].Handler, handler)) continue;
                list.RemoveAt(i);
                return true;
            }
            return false;
        }

        internal void AddGestureHandler(RevGestureKind kind, Action<RevGestureEvent> handler, object owner)
        {
            if (handler == null) return;
            ReserveCapacity(_gestureDispatch, _gestureHandlers.Count + 1);
            _gestureHandlers.Add(new GestureEntry { Kind = kind, Handler = handler, Owner = owner });
        }

        internal int RemoveAllOf(object owner)
        {
            if (owner == null) return 0;
            int removed = 0;
            for (int i = _pressed.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_pressed[i].Owner, owner)) { _pressed.RemoveAt(i); removed++; }
            for (int i = _released.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_released[i].Owner, owner)) { _released.RemoveAt(i); removed++; }
            for (int i = _gestureHandlers.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_gestureHandlers[i].Owner, owner)) { _gestureHandlers.RemoveAt(i); removed++; }
            for (int i = _listeners.Count - 1; i >= 0; i--)                 // 事件驱动接入面
                if (ReferenceEquals(_listeners[i].Owner, owner)) { _listeners.RemoveAt(i); removed++; }
            for (int i = _listenerAxisLast.Count - 1; i >= 0; i--)
                if (!IsListenerRegistered(_listenerAxisLast[i].Listener)) _listenerAxisLast.RemoveAt(i);
            for (int i = _axisHandlers.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_axisHandlers[i].Owner, owner)) { _axisHandlers.RemoveAt(i); removed++; }
            for (int i = _repeatHandlers.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_repeatHandlers[i].Owner, owner)) { _repeatHandlers.RemoveAt(i); removed++; }
            removed += UnblockAllOf(owner);
            return removed;
        }

        internal int HandlerCount => _pressed.Count + _released.Count + _gestureHandlers.Count
                                     + _listeners.Count + _axisHandlers.Count + _repeatHandlers.Count;

        // ── 失败上报 ────────────────────────────────────────

        internal void Fail(RevInputErrorReason reason, string detail)
        {
            FailedCount++;
            Action<RevInputErrorReason, string> handler = Failed;
            if (handler != null) RevInputLog.Guard("Failed(" + reason + ")", () => handler(reason, detail));

            if (RevInputLog.IsVerbose)
                RevInputLog.V("[RevInput] 失败 " + reason + "：" + detail);
        }

        // ── 自检 ────────────────────────────────────────────

        /// <summary>一行诊断（业务打到控制台用；只在调用时分配字符串）。</summary>
        internal string Dump()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("RevInput 采集=").Append(SourceName)
              .Append(" 帧=").Append(Frame)
              .Append(" 动作=").Append(Actions.Count)
              .Append(" 屏蔽=").Append(_blocks.Count)
              .Append(" 监听=").Append(HandlerCount)
              .Append(" 指针=").Append(Snapshot.PointerCount)
              .Append('\n').Append("累计：帧=").Append(TickCount)
              .Append(" 有输入帧=").Append(FramesWithInput)
              .Append(" 手势=").Append(GestureTotal)
              .Append(" 被屏蔽帧=").Append(BlockedFrames)
              .Append(" 失败=").Append(FailedCount);
            return sb.ToString();
        }
    }
}

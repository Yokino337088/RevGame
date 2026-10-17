// ============================================================
// RevInput.cs —— 输入唯一入口（小白只需要读这一个文件）
//
// 位置：Runtime\RevInput\Facade\
//
// 【什么时候用它】
//   任何"玩家操作"的地方：按键、鼠标、触屏点击、滑动、双指缩放、手柄。
//   业务**永远不写** `Input.GetKeyDown(...)` —— 只问"动作名"，键位怎么绑由绑定表决定。
//
// 【三条铁律】
//   ① **先绑后用**：`RevInput.Bind("Jump", RevKey.Space)` 一次（或启动时 `LoadBindings` 读存档），
//      之后 `RevInput.Pressed("Jump")` 到处用。没绑过的动作**不报错**，永远返回 false。
//   ② **切后台必须复位**：隐藏宿主已自动处理失焦（这是"按键卡住"的解药）；
//      但你手动 `ResetAll` 也没坏处：换场景、结算、断线时调一次，代价接近零。
//   ③ **弹窗要屏蔽**：`using (var s = RevInput.OpenScope()) { s.Block(); … }` ——
//      Block(World) 会挡住本模块全部动作与手势（包括 ESC / 返回键；当前没有系统动作例外），按需另接 UI 输入通道。
//
// 【零配置】
//   第一次用到（例如 `RevInput.Pressed`）就自动创建一个隐藏宿主 `[RevInput]`（DontDestroyOnLoad），
//   它每帧采一次输入、失效时自动复位。**业务不用摆物体、不用挂脚本、不写 Update。**
//
// 【最短上手】
// <code>
// // 启动时绑一次（或从存档读）
// RevInput.Bind("Jump",  RevKey.Space, RevKey.JoystickButton0);
// RevInput.Bind("Fire",  RevMouseButton.Left);
// RevInput.BindAxis("MoveX", RevKey.A, RevKey.D);
// RevInput.BindAxis("MoveY", RevKey.S, RevKey.W);
//
// // 业务里随便哪里问
// if (RevInput.Pressed("Jump")) Jump();
// if (RevInput.Held("Fire"))    Shoot();
// float x = RevInput.Axis("MoveX");
//
// // 触屏：点击 / 滑动 / 捏合
// if (RevInput.HasGesture(RevGestureKind.DoubleTap)) Zoom();
// if (RevInput.Swipe(out var swipe) &amp;&amp; swipe.Direction == RevSwipeDirection.Left) Dodge();
// if (RevInput.HasGesture(RevGestureKind.Pinch)) Camera.OrthographicSize /= RevInput.PinchScale;
// </code>
//
// 【和框架里另外两个设施的分工（别选错）】
//   · UI 按钮点击：那是 **UI 系统** 的事（`RevUIPanel.OnClick(nodeName)`）——不要用本模块去点 UI；
//     本模块管的是"世界输入"（操作角色、拖地图、手势），并提供"指针在 UI 上"的判断帮你分流。
//   · 每帧逻辑：用 **公共 Mono**（`RevMono.AddUpdate`）或你自己的 MonoBehaviour —— 本模块只提供读数。
// ============================================================

using System;

namespace Revolution
{
    /// <summary>
    /// 输入系统唯一入口：绑定动作、读相位与轴、读指针与手势、屏蔽世界输入。
    /// </summary>
    public static class RevInput
    {
        // ── 内核 ────────────────────────────────────────────

        private static RevInputCore _core;

        /// <summary>输入内核（懒建；第一次用到就自动准备好宿主）。</summary>
        internal static RevInputCore Core
        {
            get
            {
                if (_core == null)
                {
                    _core = new RevInputCore();
                    RevInputCore.EnsureDriver?.Invoke();
                }
                return _core;
            }
        }

        /// <summary>内核是否已经建起来（诊断用，不会顺带建）。</summary>
        public static bool IsReady => _core != null;

        /// <summary>每个公开入口都先调它：保证宿主已就位（幂等，重复调不产生开销）。</summary>
        private static RevInputCore Ensure()
        {
            RevInputCore core = Core;
            RevInputCore.EnsureDriver?.Invoke();
            return core;
        }

        /// <summary>新会话复位：由 Support 在进 Play 时调用（业务一般不需要手动调）。</summary>
        public static void ResetForNewSession()
        {
            _core?.ResetForNewSession();
        }

        /// <summary>
        /// 局部复位（失焦 / 切后台 / 换场景 / 结算）：清按键与进行中的手势，<b>保留绑定与屏蔽</b>。
        /// 静默复位，不合成 <c>OnReleased</c>；业务若需失焦/暂停收尾，请监听应用生命周期事件。
        /// </summary>
        public static void ResetAll(string reason = null) => Core.ResetAll(reason);

        /// <summary>
        /// 手动驱动开关。置 true 后隐藏宿主**让位**（用于自动化测试，或自己掌握采集时机）。
        /// </summary>
        public static bool ManualDriven
        {
            get => Core.ManualDriven;
            set => Core.ManualDriven = value;
        }

        /// <summary>手动驱动一帧（自动置位 <see cref="ManualDriven"/>）。帧号递增由你决定。</summary>
        public static void Tick(float deltaTime)
            => Tick(deltaTime, deltaTime, Core.Realtime + deltaTime, Core.Frame + 1);

        /// <summary>手动驱动一帧（完整版：自己给时间与帧号，便于测试脚本对齐时间轴）。</summary>
        public static void Tick(float deltaTime, float unscaledDeltaTime, double realtime, int frame)
        {
            RevInputCore core = Core;
            core.ManualDriven = true;
            core.Tick(deltaTime, unscaledDeltaTime, realtime, frame);
        }

        // ── 绑定 ────────────────────────────────────────────

        /// <summary>把一个动作绑到若干键位（可多个键，任一命中即算）。重复调会累加。</summary>
        public static bool Bind(string action, params RevKey[] keys)
        {
            RevInputCore core = Ensure();
            if (keys == null)
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "Bind 键位数组不能为 null");
                return false;
            }
            // RevKey 虽然是 enum，调用方仍能把任意整数强制转换成它。设备采集只认识列在 RevKey 中的键，
            // 未知数值即使保存成功也永远收不到输入，所以在建立或修改绑定前先报错拒绝。
            for (int i = 0; i < keys.Length; i++)
            {
                if (!Enum.IsDefined(typeof(RevKey), keys[i]) || keys[i] == RevKey.None)
                {
                    core.Fail(RevInputErrorReason.UnknownKey, "Bind 收到无效键位值：" + (int)keys[i]);
                    return false;
                }
            }
            RevInputBinding binding = core.Actions.Find(action);
            bool created = binding == null;
            if (created)
            {
                binding = core.Actions.Ensure(action, out RevInputErrorReason reason);
                if (binding == null)
                {
                    core.Fail(reason, "Bind(" + action + ")");
                    return false;
                }
            }

            // 先数出这次真正新增的不同按键，再判断总数是否超限，确认整批都能加后才开始写入。
            // 若一边写一边发现超限，Bind 会失败但前几个键已经改进去，玩家得到的就是一份“只成功一半”的绑定。
            int additions = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (binding.KeyMask.Get((int)keys[i])) continue;
                bool repeatedInRequest = false;
                for (int j = 0; j < i; j++)
                    if (keys[j] == keys[i]) { repeatedInRequest = true; break; }
                if (!repeatedInRequest) additions++;
            }
            if (binding.KeyCount + additions > RevInputLimits.MaxKeysPerAction)
            {
                if (created) core.Actions.Remove(action);
                core.Fail(RevInputErrorReason.BindingTextInvalid,
                    "动作 " + action + " 的键位超过上限 " + RevInputLimits.MaxKeysPerAction);
                return false;
            }

            for (int i = 0; i < keys.Length; i++) binding.AddKey(keys[i]);
            RevInputLog.V("[RevInput] 绑定 " + binding.ToText());
            return true;
        }

        /// <summary>把一个动作绑到鼠标键。</summary>
        public static bool Bind(string action, RevMouseButton button)
        {
            RevInputCore core = Ensure();
            if (!Enum.IsDefined(typeof(RevMouseButton), button))
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "Bind 收到无效鼠标键值：" + (int)button);
                return false;
            }
            RevInputBinding binding = core.Actions.Ensure(action, out RevInputErrorReason reason);
            if (binding == null)
            {
                core.Fail(reason, "Bind(" + action + ")");
                return false;
            }
            bool ok = binding.AddMouse(button);
            RevInputLog.V("[RevInput] 绑定 " + binding.ToText());
            return ok;
        }

        /// <summary>把一个动作绑成"键位轴"（<paramref name="negative"/> 反向、<paramref name="positive"/> 正向）。</summary>
        public static bool BindAxis(string action, RevKey negative, RevKey positive)
        {
            RevInputCore core = Ensure();
            // Enum 可被强转为未定义整数；入口先拒绝无效值，并禁止正负方向相同（否则两边相抵，轴永远为 0）。
            if ((negative != RevKey.None && !Enum.IsDefined(typeof(RevKey), negative))
                || (positive != RevKey.None && !Enum.IsDefined(typeof(RevKey), positive))
                || (negative != RevKey.None && negative == positive))
            {
                core.Fail(RevInputErrorReason.UnknownKey, "BindAxis 收到无效或重复的键位值");
                return false;
            }
            RevInputBinding binding = core.Actions.Ensure(action, out RevInputErrorReason reason);
            if (binding == null)
            {
                core.Fail(reason, "BindAxis(" + action + ")");
                return false;
            }
            binding.AxisNegative = negative;
            binding.AxisPositive = positive;
            RevInputLog.V("[RevInput] 绑定 " + binding.ToText());
            return true;
        }

        /// <summary>
        /// 把一个动作绑成"命名轴"（走 <c>Input.GetAxis(名字)</c>；手柄摇杆 / 鼠标滚轮用它）。
        /// 需要在工程的 InputManager 里配好同名轴。
        /// </summary>
        public static bool BindNamedAxis(string action, string axisName, bool invert = false)
        {
            RevInputCore core = Ensure();
            if (string.IsNullOrWhiteSpace(axisName))
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "BindNamedAxis 轴名不能为空");
                return false;
            }
            RevInputBinding binding = core.Actions.Ensure(action, out RevInputErrorReason reason);
            if (binding == null)
            {
                core.Fail(reason, "BindNamedAxis(" + action + ")");
                return false;
            }
            binding.NamedAxis = axisName.Trim();
            binding.NamedAxisInvert = invert;
            RevInputLog.V("[RevInput] 绑定 " + binding.ToText());
            return true;
        }

        /// <summary>设某个动作的死区（轴用；默认 <see cref="RevInputLimits.DefaultDeadzone"/>）。</summary>
        public static void SetDeadzone(string action, float deadzone)
        {
            RevInputCore core = Ensure();
            RevInputBinding binding = core.Actions.Find(action);
            if (binding == null) return;
            // 死区表示“小于这个幅度的摇杆输入当作 0”；需要和轴值正常比较。NaN/Infinity 不是普通数，比较结果不可靠，存档后也无法正确还原。
            // 拒绝这类值；普通但超出 0..1 的数则由下面的代码限制到合法范围。
            if (float.IsNaN(deadzone) || float.IsInfinity(deadzone))
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "死区必须是有限数值");
                return;
            }
            binding.Deadzone = deadzone < 0f ? 0f : (deadzone > 1f ? 1f : deadzone);
        }

        /// <summary>设某个动作的连发节拍（<paramref name="delay"/> 秒后开始，每 <paramref name="interval"/> 秒一次）。</summary>
        public static void SetRepeat(string action, float delay, float interval)
        {
            RevInputCore core = Ensure();
            RevInputBinding binding = core.Actions.Find(action);
            if (binding == null) return;
            // delay 和 interval 会直接参与每帧倒计时；NaN/Infinity 不是有效时间，可能让连发永远不触发或计时状态无法恢复。
            // 先拒绝非有限值，再检查间隔必须为正，避免错误参数进入运行中的动作。
            if (float.IsNaN(delay) || float.IsInfinity(delay) || float.IsNaN(interval) || float.IsInfinity(interval))
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "SetRepeat 的时间必须是有限数值");
                return;
            }
            if (delay <= 0f)
            {
                binding.RepeatDelay = 0f;
                binding.RepeatInterval = 0f;
                return;
            }
            // delay 大于 0 表示要启动连发；每次重复之间必须留出正的等待时间。
            // interval 为 0 可能每帧都触发，负数会让倒计时方向反过来；这两种都不是有效连发配置，因此拒绝。
            if (interval <= 0f)
            {
                core.Fail(RevInputErrorReason.BindingTextInvalid, "连发间隔必须大于 0");
                return;
            }
            binding.RepeatDelay = delay;
            binding.RepeatInterval = interval;
        }

        /// <summary>清掉一个动作的全部输入源（改键前用）。</summary>
        public static bool Unbind(string action)
        {
            RevInputBinding binding = Ensure().Actions.Find(action);
            if (binding == null) return false;
            binding.ClearSources();
            return true;
        }

        /// <summary>解绑并删除一个动作（连事件订阅一起清）。</summary>
        public static bool RemoveAction(string action) => Ensure().RemoveAction(action);

        /// <summary>清空全部绑定（换模式 / 回登录）。</summary>
        public static void ClearBindings()
        {
            RevInputCore core = Ensure();
            core.Actions.Clear();
            core.ResetAxisSubscriptionValues();
        }

        /// <summary>已绑定的动作数量。</summary>
        public static int ActionCount => Core.Actions.Count;

        /// <summary>导出绑定表文本（存档 / 给玩家改键）。</summary>
        public static string SaveBindings() => Core.Actions.SaveText();

        /// <summary>从文本载入绑定（<b>整体替换</b>）。失败返回 false 并给出原因（见 <see cref="Failed"/>）。</summary>
        public static bool LoadBindings(string text)
        {
            RevInputCore core = Ensure();
            if (core.Actions.LoadText(text, out string error))
            {
                core.ResetAxisSubscriptionValues();
                return true;
            }
            core.Fail(RevInputErrorReason.BindingTextInvalid, error);
            return false;
        }

        /// <summary>找出"同一个键绑了两个动作"的冲突（改键后查一次）。无冲突返回 null。</summary>
        public static string FindConflicts() => Core.Actions.FindConflicts();

        // ── 动作读数（业务 99% 的时间只问这几个）─────────────

        /// <summary>本帧刚按下？</summary>
        public static bool Pressed(string action) => Core.Actions.State(action).Down;

        /// <summary>正按着（含本帧按下）？</summary>
        public static bool Held(string action) => Core.Actions.State(action).Held;

        /// <summary>本帧刚抬起？</summary>
        public static bool Released(string action) => Core.Actions.State(action).Up;

        /// <summary>
        /// 按键缓冲："最近 <paramref name="windowSeconds"/> 秒内按下过"——
        /// 手感补偿（落地前 0.15 秒按的跳跃，落地瞬间仍然生效）。默认窗口见 <see cref="RevInputLimits.DefaultBufferSeconds"/>。
        /// </summary>
        public static bool PressedBuffered(string action, float windowSeconds = RevInputLimits.DefaultBufferSeconds)
            => Core.Actions.Buffered(action, Core.Realtime, windowSeconds);

        /// <summary>连发触发（首帧按下 + 之后按节拍；只有 <see cref="SetRepeat"/> 配过的动作会为真）。</summary>
        public static bool Repeat(string action) => Core.Actions.State(action).Repeat;

        /// <summary>按住多久了（秒，受暂停影响）。</summary>
        public static float HeldSeconds(string action) => Core.Actions.State(action).HeldSeconds;

        /// <summary>按住多少帧了。</summary>
        public static int HeldFrames(string action) => Core.Actions.State(action).HeldFrames;

        /// <summary>最近一次按下的帧号（-1 = 从没按过）。</summary>
        public static int LastPressedFrame(string action)
        {
            RevInputActionState s = Core.Actions.State(action);
            return s.HasLastDown ? s.LastDownFrame : -1;
        }

        /// <summary>轴读数（-1..1，已过死区）。没绑轴的动作会退化成"按下即 1"。</summary>
        public static float Axis(string action) => Core.Actions.State(action).Axis;

        /// <summary>
        /// 两根轴的合向量：读 <c>action + "X"</c> 与 <c>action + "Y"</c>
        /// （约定：`RevInput.BindAxis("MoveX", A, D)` + `RevInput.BindAxis("MoveY", S, W)` → `RevInput.Vector("Move")`）。
        /// </summary>
        public static RevInputVector2 Vector(string action)
            => new RevInputVector2(Axis(action + "X"), Axis(action + "Y"));

        /// <summary>动作本帧是否被"世界输入屏蔽"挡掉了（诊断：区分"没按"与"被挡"）。</summary>
        public static bool ActionBlocked(string action) => Core.Actions.State(action).Blocked;

        // ── 鼠标 ────────────────────────────────────────────

        /// <summary>鼠标位置（屏幕坐标，左下角原点）。</summary>
        public static RevInputVector2 MousePosition
        {
            get
            {
                RevInputCore core = Core;
                return new RevInputVector2(core.MouseX, core.MouseY);
            }
        }

        /// <summary>鼠标本帧位移（屏幕像素）。</summary>
        public static RevInputVector2 MouseDelta
        {
            get
            {
                RevInputCore core = Core;
                return new RevInputVector2(core.MouseDeltaX, core.MouseDeltaY);
            }
        }

        /// <summary>滚轮增量（纵向 / 横向）。</summary>
        public static RevInputVector2 MouseScroll
        {
            get
            {
                RevInputCore core = Core;
                return new RevInputVector2(core.ScrollX, core.ScrollY);
            }
        }

        /// <summary>鼠标键三态（把鼠标当按钮用；也可以直接 <see cref="Bind(string, RevMouseButton)"/> 成动作）。</summary>
        public static bool MouseButton(RevMouseButton button, RevInputPhase phase = RevInputPhase.Held)
            => Core.MouseButton(button, phase);

        // ── 指针（手指）─────────────────────────────────────

        /// <summary>本帧有几根手指（鼠标不算；被屏蔽时为 0）。</summary>
        public static int PointerCount => Core.PointerCount;

        /// <summary>取第 <paramref name="index"/> 根手指的采样（越界 / 被屏蔽返回默认值）。</summary>
        public static RevPointerSample Pointer(int index) => Core.Pointer(index);

        /// <summary>第 <paramref name="index"/> 根手指的 id（用于和 <see cref="Block"/> 配合屏蔽单指）。</summary>
        public static int PointerId(int index) => Core.Pointer(index).Id;

        /// <summary>
        /// 指针是否落在 UI 上（由 Support 接到 <c>EventSystem.IsPointerOverGameObject</c>；
        /// 纯 C# 环境下恒为 false）。业务用它决定"这次点击给 UI 还是给世界"。
        /// </summary>
        public static Func<int, bool> WorldPointerOverUI;

        /// <summary>指针（<paramref name="pointerId"/>：鼠标传 -1，手指传 id）是否在 UI 上。</summary>
        public static bool IsPointerOverUI(int pointerId = RevInputSnapshot.MousePointerId)
        {
            Func<int, bool> probe = WorldPointerOverUI;
            if (probe == null) return false;
            try
            {
                // ★ 这里刻意不用 RevInputLog.Guard：它要传 lambda，会在每次查询时分配一个闭包。
                //   本方法是逐帧热路径（每根手指每帧问一次），必须零分配 —— 就地 try/catch。
                return probe(pointerId);
            }
            catch (Exception e)
            {
                RevInputLog.OnException?.Invoke(e, "WorldPointerOverUI");
                WorldPointerOverUI = null;      // 探针坏了就摘掉：不要每帧都抛
                return false;
            }
        }

        // ── 手势 ────────────────────────────────────────────

        /// <summary>本帧识别出几条手势。</summary>
        public static int GestureCount => Core.Gestures.Count;

        /// <summary>取本帧第 <paramref name="index"/> 条手势。</summary>
        public static RevGestureEvent Gesture(int index) => Core.Gestures.At(index);

        /// <summary>本帧有没有某种手势。</summary>
        public static bool HasGesture(RevGestureKind kind) => Core.Gestures.Has(kind);

        /// <summary>取本帧第一条某种手势。</summary>
        public static bool TryGetGesture(RevGestureKind kind, out RevGestureEvent gesture)
            => Core.Gestures.TryGet(kind, out gesture);

        /// <summary>本帧的滑动手势（没有则为 false）。</summary>
        public static bool Swipe(out RevGestureEvent swipe)
            => Core.Gestures.TryGet(RevGestureKind.Swipe, out swipe);

        /// <summary>本帧的轻点（没有则为 false）——位置在 <c>gesture.X / gesture.Y</c>。</summary>
        public static bool Tap(out RevGestureEvent tap)
            => Core.Gestures.TryGet(RevGestureKind.Tap, out tap);

        /// <summary>本帧双指缩放比例（1 = 不变）。</summary>
        public static float PinchScale => Core.Gestures.PinchScale;

        /// <summary>本帧双指旋转角度（度）。</summary>
        public static float RotateDegrees => Core.Gestures.RotateDegrees;

        /// <summary>轻点：最长按住时间（秒）。</summary>
        public static float TapMaxSeconds
        {
            get => Core.Gestures.TapMaxSeconds;
            set => Core.Gestures.TapMaxSeconds = value;
        }

        /// <summary>轻点：最大位移（像素）。</summary>
        public static float TapMaxDistance
        {
            get => Core.Gestures.TapMaxDistance;
            set => Core.Gestures.TapMaxDistance = value;
        }

        /// <summary>双击：两次点击的最大间隔（秒）。</summary>
        public static float DoubleTapSeconds
        {
            get => Core.Gestures.DoubleTapSeconds;
            set => Core.Gestures.DoubleTapSeconds = value;
        }

        /// <summary>长按：按住多久算长按（秒）。</summary>
        public static float LongPressSeconds
        {
            get => Core.Gestures.LongPressSeconds;
            set => Core.Gestures.LongPressSeconds = value;
        }

        /// <summary>拖动：超过多少像素算拖动。</summary>
        public static float DragThreshold
        {
            get => Core.Gestures.DragThreshold;
            set => Core.Gestures.DragThreshold = value;
        }

        /// <summary>滑动：至少移动多少像素 + 至少多快（像素/秒）。</summary>
        public static void SetSwipeThreshold(float minDistance, float minSpeed)
        {
            Core.Gestures.SwipeMinDistance = minDistance;
            Core.Gestures.SwipeMinSpeed = minSpeed;
        }

        // ── 屏蔽（弹窗 / 过场 / 教程遮罩）────────────────────

        /// <summary>
        /// 申请屏蔽。返回句柄，<c>using</c> 或 <c>Close()</c> 解除。
        /// <paramref name="kind"/> 默认 <see cref="RevInputBlockKind.World"/>（只挡世界输入，指针位置照常可读）。
        /// </summary>
        public static RevInputBlock Block(RevInputBlockKind kind = RevInputBlockKind.World,
            int pointerId = -1, object owner = null)
            => Ensure().Block(kind, pointerId, owner);

        /// <summary>解除一次屏蔽（重复调用安全）。</summary>
        public static bool Unblock(RevInputBlock handle) => Core.Unblock(handle);

        /// <summary>查某次屏蔽的种类（给 <see cref="RevInputBlock.Kind"/> 用；业务一般不需要）。</summary>
        internal static RevInputBlockKind GetBlockKind(int blockId) => Core.GetBlockKind(blockId);

        /// <summary>解除某个对象申请的全部屏蔽（一行清理，与框架其它模块的作用域写法一致）。</summary>
        public static int UnblockAllOf(object owner) => Core.UnblockAllOf(owner);

        /// <summary>当前是否有某种屏蔽。</summary>
        public static bool IsBlocked(RevInputBlockKind kind) => Core.IsBlocked(kind);

        /// <summary>世界输入是否被挡（<see cref="RevInputBlockKind.World"/> 或 <see cref="RevInputBlockKind.All"/>）。</summary>
        public static bool WorldBlocked => Core.WorldBlocked;

        /// <summary>正在生效的屏蔽条数（诊断用）。</summary>
        public static int BlockCount => Core.BlockCount;

        /// <summary>开一个作用域：出块自动解除里面申请的全部屏蔽（也自动退订当 owner 的事件）。</summary>
        public static RevInputScope OpenScope() => new RevInputScope();

        // ── 事件（可选；轮询用不顺手的场景再用）─────────────

        /// <summary>订阅"某动作按下"（记得给 owner，销毁时 <see cref="OffAllOf"/> 一行清）。</summary>
        public static bool OnPressed(string action, Action handler, object owner = null)
            => Core.AddActionHandler(action, handler, owner, pressed: true, out _);

        /// <summary>订阅"某动作抬起"。</summary>
        public static bool OnReleased(string action, Action handler, object owner = null)
            => Core.AddActionHandler(action, handler, owner, pressed: false, out _);

        /// <summary>订阅某种手势（<paramref name="kind"/> 传 <see cref="RevGestureKind.None"/> = 全部手势）。</summary>
        public static void OnGesture(Action<RevGestureEvent> handler,
            RevGestureKind kind = RevGestureKind.None, object owner = null)
            => Core.AddGestureHandler(kind, handler, owner);

        /// <summary>退订某个动作的按下/抬起回调。</summary>
        public static void OffPressed(string action, Action handler) => Core.RemoveActionHandler(action, handler, true);

        /// <summary>退订某个动作的抬起回调。</summary>
        public static void OffReleased(string action, Action handler) => Core.RemoveActionHandler(action, handler, false);

        /// <summary>退订某个对象登记的全部内容（事件 + 屏蔽）。</summary>
        public static int OffAllOf(object owner) => Core.RemoveAllOf(owner);

        /// <summary>失败事件（原因码 + 说明）。<b>框架不打日志</b>：要观测就订阅这里。</summary>
        public static Action<RevInputErrorReason, string> Failed
        {
            get => Core.Failed;
            set => Core.Failed = value;
        }

        // ── 事件驱动接入（推荐：业务不在 Update 里轮询）──────
        //
        //  两种用法，可混用：
        //   ① 监听者：实现 RevInputListener（或继承 RevInputListenerBase），AddListener 一次，
        //      之后按下 / 抬起 / 连发 / 轴变化 / 手势全部由框架推给你；
        //   ② 按事件订阅：OnPressed / OnReleased / OnRepeat / OnAxis / OnGesture。
        //  想让"按住持续做的事"也不用 Update：SetRepeat 配好节拍，再订阅 OnRepeat。

        /// <summary>
        /// 登记一个监听者：之后本模块的输入事件都会投给它。
        /// <b>记得给 owner</b> —— 销毁时 <c>RevInput.OffAllOf(owner)</c> 一行清干净。
        /// </summary>
        public static void AddListener(RevInputListener listener, object owner = null)
            => Core.AddListener(listener, owner);

        /// <summary>移除一个监听者（按引用相等；重复移除安全）。</summary>
        public static bool RemoveListener(RevInputListener listener) => Core.RemoveListener(listener);

        /// <summary>当前监听者数量（诊断用）。</summary>
        public static int ListenerCount => Core.ListenerCount;

        /// <summary>
        /// 订阅"某个轴的值变化"（移动 / 摇杆走这里）。<b>只在数值变化时回调</b>；
        /// 订阅后的第一帧会先收到一次当前值（方便初始化移动方向或 UI 摇杆）。
        /// 轴名与 <c>BindAxis</c> / <c>BindNamedAxis</c> 一致。
        /// </summary>
        public static bool OnAxis(string axis, Action<float> handler, object owner = null)
            => Core.AddAxisHandler(axis, handler, owner);

        /// <summary>退订轴变化（<paramref name="handler"/> 传 null = 退掉该轴的全部订阅）。</summary>
        public static bool OffAxis(string axis, Action<float> handler) => Core.RemoveAxisHandler(axis, handler);

        /// <summary>
        /// 订阅"某动作连发触发"（先用 <c>SetRepeat</c> 配节拍）。
        /// 用它可以彻底摆脱 <c>Update</c>：按住连发由框架按节拍推给你。
        /// </summary>
        public static bool OnRepeat(string action, Action handler, object owner = null)
            => Core.AddRepeatHandler(action, handler, owner);

        /// <summary>退订连发（<paramref name="handler"/> 传 null = 退掉该动作的全部连发订阅）。</summary>
        public static bool OffRepeat(string action, Action handler) => Core.RemoveRepeatHandler(action, handler);

        // ── 设备类型（键鼠 / 触屏 / 手柄）─────────────────────

        /// <summary>本帧识别出的设备类型（键鼠 / 触屏 / 手柄）。</summary>
        public static RevInputDeviceKind DeviceKind => Core.Snapshot.Device;

        /// <summary>强制设备类型（<see cref="RevInputDeviceKind.Unknown"/> = 交回自动识别）。</summary>
        public static RevInputDeviceKind ForceDeviceKind
        {
            get => Core.ForcedDeviceKind;
            set => Core.ForcedDeviceKind = value;
        }

        // ── 诊断 ────────────────────────────────────────────

        /// <summary>诊断日志开关（默认关；打开后输入事件会走 <c>RevLog</c> 的 "Input" 标签）。</summary>
        public static bool VerboseLog
        {
            get => RevInputLog.Verbose;
            set => RevInputLog.Verbose = value;
        }

        /// <summary>自检输出（设备 / 动作数 / 屏蔽数 / 各种累计计数）。</summary>
        public static string Dump() => Core.Dump();

        /// <summary>累计失败次数（0 = 一路正常）。</summary>
        public static int FailedCount => Core.FailedCount;
    }
}

// ============================================================
// RevInputActionTable.cs —— 绑定表 + 全部动作的每帧状态（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   每帧要回答业务几十个问题（"跳跃按下了吗""移动轴多少""按住多久了"），
//   而答案必须**又准又便宜**：一次遍历算完所有动作，之后全是 O(1) 查询。
//
// 【三条铁律】
//   ① **一帧只算一次**：`Update()` 里遍历动作、位运算判相位、累加时长；
//      业务读的是结果（<see cref="RevInputActionState"/>），不再碰绑定表。
//   ② **时间来自快照**：窗口（缓冲 / 双击 / 长按）用快照里的真实时间（`Realtime`），
//      持续时长（按住多久 / 连发间隔）用 deltaTime 累加 —— 于是"暂停时按住的计时会停"，
//      而"双击窗口"照常按真实时间走（切后台回来不会误判双击）。
//   ③ **文本可来回**：`SaveText()` ⇄ `LoadText()` 必须无损往返（改键存档靠它，
//      工程外断言专门测这一条）。
// ============================================================

using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一个动作在<b>本帧</b>的读数（业务只读它）。</summary>
    public struct RevInputActionState
    {
        /// <summary>本帧刚按下（只成立一帧）。</summary>
        public bool Down;

        /// <summary>本帧按住（含按下的那一帧）。</summary>
        public bool Held;

        /// <summary>本帧刚抬起（只成立一帧）。</summary>
        public bool Up;

        /// <summary>按住持续了多少帧。</summary>
        public int HeldFrames;

        /// <summary>按住持续了多少秒（按 deltaTime 累加，受暂停影响）。</summary>
        public float HeldSeconds;

        /// <summary>轴读数（-1..1，已过死区；命名轴直接透传并夹取）。</summary>
        public float Axis;

        /// <summary>本帧连发是否触发（首次按下也算一次；只有配了连发的动作会为真）。</summary>
        public bool Repeat;

        /// <summary>最近一次按下的时间（真实秒，用于缓冲窗口与双击判定）。</summary>
        public double LastDownTime;

        /// <summary>最近一次按下的帧号。</summary>
        public int LastDownFrame;

        /// <summary>是否发生过按下（时间戳 0 是合法值，不能拿它作为未按过的哨兵）。</summary>
        public bool HasLastDown;

        /// <summary>连发计时是否已启动。</summary>
        internal bool RepeatClockActive;

        /// <summary>本帧是否被"世界输入屏蔽"挡掉了（诊断用：能区分"没按"与"被挡"）。</summary>
        public bool Blocked;

        /// <summary>连发计时（内部用）。</summary>
        internal double NextRepeatTime;

        /// <summary>是否已绑过输入源（没绑过的动作永远返回 false，但不会报错）。</summary>
        public bool Bound;
    }

    /// <summary>绑定表 + 状态表。所有方法都不产生垃圾（查询走字典，只在绑定期写入）。</summary>
    public sealed class RevInputActionTable
    {
        private readonly Dictionary<string, RevInputBinding> _map = new Dictionary<string, RevInputBinding>(64);
        private readonly List<RevInputBinding> _list = new List<RevInputBinding>(64);
        private readonly Dictionary<string, RevInputActionState> _states = new Dictionary<string, RevInputActionState>(64);
        private readonly List<string> _tmpNames = new List<string>(64);

        /// <summary>已绑定的动作数量。</summary>
        public int Count => _list.Count;

        /// <summary>取（没有就创建）一个动作的绑定。失败（名字非法 / 超上限）返回 null，原因写在 <paramref name="reason"/>。</summary>
        public RevInputBinding Ensure(string action, out RevInputErrorReason reason)
        {
            reason = RevInputErrorReason.None;
            if (!IsValidName(action))
            {
                reason = RevInputErrorReason.InvalidActionName;
                return null;
            }
            if (_map.TryGetValue(action, out RevInputBinding exist)) return exist;
            if (_list.Count >= RevInputLimits.MaxActions)
            {
                reason = RevInputErrorReason.TooManyActions;
                return null;
            }

            var binding = new RevInputBinding(action);
            _map[action] = binding;
            _list.Add(binding);
            _states[action] = default;
            return binding;
        }

        /// <summary>查绑定（不存在返回 null，不产生垃圾）。</summary>
        public RevInputBinding Find(string action)
            => action != null && _map.TryGetValue(action, out RevInputBinding b) ? b : null;

        /// <summary>按名字查状态（不存在返回 default）。</summary>
        public RevInputActionState State(string action)
            => action != null && _states.TryGetValue(action, out RevInputActionState s) ? s : default;

        /// <summary>动作名是否合法（非空、限长、无空白）。</summary>
        public static bool IsValidName(string action)
        {
            if (string.IsNullOrEmpty(action)) return false;
            if (action.Length > RevInputLimits.MaxActionNameLength) return false;
            for (int i = 0; i < action.Length; i++)
            {
                char c = action[i];
                if (char.IsWhiteSpace(c)) return false;
            }
            return true;
        }

        /// <summary>删掉一个动作（连状态一起）。</summary>
        public bool Remove(string action)
        {
            if (action == null || !_map.TryGetValue(action, out RevInputBinding b)) return false;
            _map.Remove(action);
            _list.Remove(b);
            _states.Remove(action);
            return true;
        }

        /// <summary>清掉短按边沿、缓冲与连发计时，但保留绑定和当前轴值。</summary>
        public void ClearTransientInput()
        {
            for (int i = 0; i < _list.Count; i++)
            {
                string action = _list[i].Action;
                RevInputActionState state = _states[action];
                state.Down = state.Up = state.Held = state.Repeat = false;
                state.HeldFrames = 0;
                state.HeldSeconds = 0f;
                state.HasLastDown = false;
                state.LastDownTime = 0d;
                state.LastDownFrame = 0;
                state.RepeatClockActive = false;
                state.NextRepeatTime = 0d;
                state.Blocked = false;
                _states[action] = state;
            }
        }

        /// <summary>清空（换场景 / 换模式时用；下个会话重新加载绑定）。</summary>
        public void Clear()
        {
            _map.Clear();
            _list.Clear();
            _states.Clear();
        }

        /// <summary>把全部动作的名字列出来（诊断 / 冲突提示用；仅在需要时调用，会分配）。</summary>
        public List<string> Names()
        {
            _tmpNames.Clear();
            for (int i = 0; i < _list.Count; i++) _tmpNames.Add(_list[i].Action);
            return _tmpNames;
        }

        /// <summary>取第 <paramref name="index"/> 个绑定（遍历用，顺序 = 绑定的先后）。</summary>
        public RevInputBinding At(int index)
            => index >= 0 && index < _list.Count ? _list[index] : null;

        /// <summary>
        /// 全部绑定占用的键位<b>并集</b>。设备用它决定"这一帧要测哪些键"——
        /// 只测用到的（通常十几个），而不是无脑测一百多个键。
        /// </summary>
        public RevKeyMask BoundKeyUnion()
        {
            RevKeyMask union = default;
            for (int i = 0; i < _list.Count; i++)
            {
                union.Or(_list[i].KeyMask);
                if (_list[i].AxisNegative != RevKey.None) union.Set((int)_list[i].AxisNegative);
                if (_list[i].AxisPositive != RevKey.None) union.Set((int)_list[i].AxisPositive);
            }
            return union;
        }

        /// <summary>
        /// 每帧算一次。<paramref name="worldBlocked"/> 为真时动作相位全部按"未输入"处理，
        /// 但**仍然记录按下时间**（这样屏蔽解除后缓冲窗口依然有效，手感不丢）。
        /// </summary>
        public void Update(in RevInputSnapshot snapshot, double realtime, bool worldBlocked,
            System.Func<string, float> axisProvider)
        {
            bool shifted = false;
            for (int i = 0; i < _list.Count; i++)
            {
                RevInputBinding b = _list[i];
                RevInputActionState s = _states[b.Action];

                bool down = !worldBlocked
                            && (b.HitKeys(snapshot.KeyDown) || b.HitMouse(snapshot.MouseDown));
                bool held = !worldBlocked
                            && (b.HitKeys(snapshot.KeyHeld) || b.HitMouse(snapshot.MouseHeld));
                bool up = !worldBlocked
                          && (b.HitKeys(snapshot.KeyUp) || b.HitMouse(snapshot.MouseUp));

                // ★ 屏蔽中也要记按下时间：否则"屏蔽解除瞬间的缓冲输入"会丢（手感 bug）
                if (b.HitKeys(snapshot.KeyDown) || b.HitMouse(snapshot.MouseDown))
                {
                    s.LastDownTime = realtime;
                    s.LastDownFrame = snapshot.Frame;
                    s.HasLastDown = true;
                    shifted = true;
                }

                s.Blocked = worldBlocked;
                s.Bound = b.HasButton || b.HasAxis;
                s.Down = down;
                s.Up = up;
                s.Held = held;

                if (held)
                {
                    s.HeldFrames++;
                    s.HeldSeconds += snapshot.DeltaTime;
                }
                else
                {
                    s.HeldFrames = 0;
                    s.HeldSeconds = 0f;
                }

                // 轴：命名轴优先（手柄摇杆），否则用键位对算 ±1
                // ★ Bug 修复（2026-09-30）：屏蔽 World 期间轴必须一并归零 ——
                //   上面 down/held/up 都挡了，唯独轴漏挡：弹窗 / 过场里 MoveX 照样有值，
                //   角色在"输入被屏蔽"时照样移动。轴是动作读数的组成部分，属于世界输入。
                s.Axis = worldBlocked ? 0f : ComputeAxis(b, snapshot, axisProvider);

                // 连发按 deltaTime 累加：暂停（timeScale = 0）时不推进计时；delay <= 0 表示关闭。
                if (down)
                {
                    bool repeatEnabled = b.RepeatDelay > 0f && b.RepeatInterval > 0f;
                    s.Repeat = repeatEnabled;                       // 首帧按下算一次（仅限配了连发的动作）
                    s.RepeatClockActive = repeatEnabled;
                    s.NextRepeatTime = repeatEnabled ? b.RepeatDelay : 0d;
                }
                else if (held && s.RepeatClockActive)
                {
                    s.NextRepeatTime -= snapshot.DeltaTime;
                    if (s.NextRepeatTime <= 0d)
                    {
                        s.Repeat = true;
                        s.NextRepeatTime = b.RepeatInterval;
                    }
                    else s.Repeat = false;
                }
                else
                {
                    s.Repeat = false;
                    s.RepeatClockActive = false;
                    s.NextRepeatTime = 0d;
                }

                _states[b.Action] = s;
            }

            // 让编译器看到 shifted 被用（后续若要"屏蔽期间是否有按下"的诊断可读它）
            LastShiftFrame = shifted ? snapshot.Frame : LastShiftFrame;
        }

        /// <summary>最近一帧里"有人按下"的帧号（诊断用）。</summary>
        public int LastShiftFrame { get; private set; }

        private static float ComputeAxis(RevInputBinding b, in RevInputSnapshot snapshot,
            System.Func<string, float> axisProvider)
        {
            if (!string.IsNullOrEmpty(b.NamedAxis) && axisProvider != null)
            {
                float raw = axisProvider(b.NamedAxis);
                if (b.NamedAxisInvert) raw = -raw;
                if (raw > 1f) raw = 1f;
                else if (raw < -1f) raw = -1f;
                if (raw > -b.Deadzone && raw < b.Deadzone) return 0f;
                return raw;
            }

            float value = 0f;
            if (b.AxisPositive != RevKey.None && snapshot.KeyHeld.Get((int)b.AxisPositive)) value += 1f;
            if (b.AxisNegative != RevKey.None && snapshot.KeyHeld.Get((int)b.AxisNegative)) value -= 1f;
            if (value != 0f) return value;

            // 没有设置“正负键轴”或“命名轴”时，普通按钮仍可作为简单的 0/1 轴：按下返回 1，松开返回 0。
            // 普通按钮既可能是键盘键，也可能是鼠标键；两种都要查。若只检查键盘状态，鼠标按住时 Axis 会一直错误地返回 0。
            if (b.HitKeys(snapshot.KeyHeld) || b.HitMouse(snapshot.MouseHeld)) return 1f;
            return 0f;
        }

        /// <summary>某动作在本帧是否处于"缓冲窗口"内（按下时间在 <paramref name="windowSeconds"/> 之内）。</summary>
        public bool Buffered(string action, double realtime, float windowSeconds)
        {
            RevInputActionState s = State(action);
            if (!s.HasLastDown || windowSeconds < 0f) return false;
            double elapsed = realtime - s.LastDownTime;
            return elapsed >= 0d && elapsed <= windowSeconds;
        }

        /// <summary>
        /// 找出"同一个输入源被绑到两个动作"的冲突（改键后必须查一次）。
        /// 返回可读文本；没有冲突返回 null。
        /// </summary>
        public string FindConflicts()
        {
            if (_list.Count < 2) return null;
            string report = null;
            for (int i = 0; i < _list.Count; i++)
            {
                for (int j = i + 1; j < _list.Count; j++)
                {
                    RevInputBinding a = _list[i];
                    RevInputBinding b = _list[j];

                    if (a.KeyMask.Intersects(b.KeyMask) || (a.MouseMask & b.MouseMask) != 0)
                    {
                        for (int k = 0; k < a.KeyCount; k++)
                        {
                            RevKey key = a.Key(k);
                            if (b.KeyMask.Get((int)key))
                                report = Append(report, key + " 同时绑给了 " + a.Action + " 和 " + b.Action);
                        }
                        for (int m = 0; m <= (int)RevMouseButton.Forward; m++)
                        {
                            byte bit = (byte)(1 << m);
                            if ((a.MouseMask & bit) != 0 && (b.MouseMask & bit) != 0)
                                report = Append(report, "Mouse" + m + " 同时绑给了 " + a.Action + " 和 " + b.Action);
                        }
                    }

                    // ★ Bug 修复（2026-09-30）：轴键也是输入源，原实现漏查 ——
                    //   BindAxis("MoveX", A, D) 与 Bind("Skill", A) 这种典型改键冲突报不出来，
                    //   "改键后必须查一次冲突"的承诺对轴形同虚设。轴键与对方的键位掩码 / 轴键互查。
                    //   （两个方向都要查，不能短路：a 的轴撞 b、b 的轴撞 a 可能同时存在。）
                    if (!string.IsNullOrEmpty(a.NamedAxis) &&
                        System.String.Equals(a.NamedAxis, b.NamedAxis, System.StringComparison.OrdinalIgnoreCase))
                        report = Append(report, "命名轴 " + a.NamedAxis + " 同时绑给了 " + a.Action + " 和 " + b.Action);

                    // 命名轴冲突已在上方单独比对；这里再双向检查键位轴，防止改键后轴输入与普通键位漏报。
                    if (AxisKeyConflicts(a, b, out string axisReportA)) report = Append(report, axisReportA);
                    if (AxisKeyConflicts(b, a, out string axisReportB)) report = Append(report, axisReportB);
                }
            }
            return report;
        }

        private static string Append(string a, string b) => a == null ? b : a + "\n" + b;

        /// <summary>
        /// 查 <paramref name="check"/> 的轴键是否与 <paramref name="other"/> 的输入源冲突
        /// （★ 随 2026-09-30 的冲突检测补全新增；有冲突返回 true 并给出可读文本）。
        /// </summary>
        private static bool AxisKeyConflicts(RevInputBinding check, RevInputBinding other, out string report)
        {
            report = Append(ConflictOne(check.AxisNegative, "-"), ConflictOne(check.AxisPositive, "+"));
            return report != null;

            string ConflictOne(RevKey axisKey, string sign)
            {
                if (axisKey == RevKey.None) return null;
                if (other.KeyMask.Get((int)axisKey)
                    || other.AxisNegative == axisKey || other.AxisPositive == axisKey)
                    return axisKey + " 同时绑给了 " + check.Action + "（" + sign + "轴）和 " + other.Action;
                return null;
            }
        }

        /// <summary>导出成文本（存档 / 给玩家改键）。</summary>
        public string SaveText()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("# RevInput 绑定表（动作 = 输入源, ...）\n");
            for (int i = 0; i < _list.Count; i++)
            {
                if (!_list[i].HasButton && !_list[i].HasAxis) continue;
                sb.Append(_list[i].ToText()).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从文本载入绑定（<b>整体替换</b>，不是合并）。解析失败返回 false 并给出行号说明。
        /// 行格式：`Action = Space, Mouse0, -A, +D, axis:Mouse X`；`#` 开头是注释。
        /// </summary>
        public bool LoadText(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                // LoadText 的约定是“用这份文本整体替换现有绑定”。空文本就是一份没有任何动作的绑定表，表示用户要清空绑定，
                // 不能当成“没有提供新配置”而保留旧表，否则清空存档后旧按键仍会继续生效。
                Clear();
                return true;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var parsed = new List<RevInputBinding>(64);
            // 每个动作会同时放进“按名字查找的字典”和“按顺序遍历的列表”。若文本里同名动作出现两次，字典会覆盖成最后一条，
            // 但列表可能仍保留两条；之后查询、统计和派发会各自看到不同结果。因此发现重复名时拒绝整份导入。
            var actionNames = new HashSet<string>(System.StringComparer.Ordinal);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    error = "第 " + (i + 1) + " 行缺少 '='：" + line;
                    return false;
                }

                string action = line.Substring(0, eq).Trim();
                if (!IsValidName(action))
                {
                    error = "第 " + (i + 1) + " 行动作名不合法：" + action;
                    return false;
                }
                if (!actionNames.Add(action))
                {
                    error = "第 " + (i + 1) + " 行动作名重复：" + action;
                    return false;
                }

                string rest = line.Substring(eq + 1).Trim();
                var binding = new RevInputBinding(action);
                bool repeatSeen = false;
                bool namedAxisSeen = false;
                // 键位轴需要各一个负向键和正向键；每个方向只能出现一次，否则后写的键会遮住前一个，存档读回结果不明确。
                bool negativeAxisSeen = false;
                bool positiveAxisSeen = false;

                int dz = rest.IndexOf("deadzone=", System.StringComparison.OrdinalIgnoreCase);
                if (dz >= 0)
                {
                    string number = rest.Substring(dz + 9).Trim();
                    int space = number.IndexOf(' ');
                    if (space > 0) number = number.Substring(0, space);
                    // 死区只允许 0 到 1 之间的普通数字；它表示多小的轴输入要当作“没有推动”。
                    // NaN、Infinity 或超范围值会使后续比较失去意义，因此遇到非法值就拒绝整份导入，保留仍可用的旧绑定。
                    if (!float.TryParse(number, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float d)
                        || float.IsNaN(d) || float.IsInfinity(d) || d < 0f || d > 1f)
                    {
                        error = "第 " + (i + 1) + " 行死区不合法（应为 0..1）";
                        return false;
                    }
                    binding.Deadzone = d;
                    rest = rest.Substring(0, dz).TrimEnd(' ', ',');
                }

                string[] parts = rest.Split(',');
                for (int p = 0; p < parts.Length; p++)
                {
                    string token = parts[p].Trim();
                    if (token.Length == 0) continue;

                    if (token.StartsWith("repeat:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (repeatSeen)
                        {
                            // 一行绑定只能有一组 repeat（开始连发前等待多久、之后每隔多久触发）。写两组时程序无法知道玩家想用哪组，
                            // 如果静默取最后一组，保存文件看似保留了两组、实际却只生效一组；因此提示错误并保留旧绑定。
                            error = "第 " + (i + 1) + " 行重复声明了连发配置";
                            return false;
                        }
                        repeatSeen = true;
                        // ★ Bug 修复（2026-09-30）：连发节拍必须随存档往返 —— 原实现不导出也不解析，
                        //   SaveText ⇄ LoadText 一趟下来连发配置全部回默认值，违反本类铁律③
                        //   "SaveText ⇄ LoadText 必须无损往返（改键存档靠它）"。
                        //   格式：repeat:delay/interval（与 ToText 的导出格式对应）。
                        string pair = token.Substring(7).Trim();
                        int slash = pair.IndexOf('/');
                        // repeat:delay/interval 中的两个值都会用来倒计时，必须是普通且非负的秒数；开启连发时 interval 还必须大于 0。
                        // 如果接受 NaN、Infinity 或零间隔，连发可能永不发生或每帧都发生；因此坏存档不能写进动作状态。
                        if (slash <= 0
                            || !float.TryParse(pair.Substring(0, slash).Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float rd)
                            || !float.TryParse(pair.Substring(slash + 1).Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float ri)
                            || float.IsNaN(rd) || float.IsInfinity(rd) || rd < 0f
                            || float.IsNaN(ri) || float.IsInfinity(ri) || ri < 0f
                            || (rd > 0f && ri <= 0f))
                        {
                            error = "第 " + (i + 1) + " 行连发配置不合法（应为 repeat:delay/interval）：" + token;
                            return false;
                        }
                        binding.RepeatDelay = rd;
                        binding.RepeatInterval = ri;
                    }
                    else if (token.StartsWith("axis:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // 命名轴是 Input Manager 里配置的摇杆/滚轮轴。一行只能指定一根；写多根时后面的会覆盖前面的，
                        // 容易让存档看起来有多根轴、运行时却只用一根，所以重复声明直接报错。
                        if (namedAxisSeen)
                        {
                            error = "第 " + (i + 1) + " 行重复声明了命名轴";
                            return false;
                        }
                        namedAxisSeen = true;
                        string name = token.Substring(5).Trim();
                        bool invert = name.EndsWith("-");
                        if (invert) name = name.Substring(0, name.Length - 1).Trim();
                        if (name.Length == 0)
                        {
                            error = "第 " + (i + 1) + " 行命名轴名称不能为空";
                            return false;
                        }
                        binding.NamedAxis = name;
                        binding.NamedAxisInvert = invert;
                    }
                    else if (token.StartsWith("Mouse", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(token.Substring(5), out int mi)
                            && mi >= 0 && mi <= (int)RevMouseButton.Forward)
                            binding.AddMouse((RevMouseButton)mi);
                        else
                        {
                            error = "第 " + (i + 1) + " 行鼠标键不合法：" + token;
                            return false;
                        }
                    }
                    else if (token[0] == '-' || token[0] == '+')
                    {
                        if (!System.Enum.TryParse(token.Substring(1), out RevKey axisKey)
                            || axisKey == RevKey.None || !System.Enum.IsDefined(typeof(RevKey), axisKey))
                        {
                            error = "第 " + (i + 1) + " 行轴键位不认识：" + token;
                            return false;
                        }
                        if (token[0] == '-')
                        {
                            if (negativeAxisSeen)
                            {
                                error = "第 " + (i + 1) + " 行重复声明了负向轴键";
                                return false;
                            }
                            negativeAxisSeen = true;
                            binding.AxisNegative = axisKey;
                        }
                        else
                        {
                            if (positiveAxisSeen)
                            {
                                error = "第 " + (i + 1) + " 行重复声明了正向轴键";
                                return false;
                            }
                            positiveAxisSeen = true;
                            binding.AxisPositive = axisKey;
                        }
                    }
                    else
                    {
                        if (!System.Enum.TryParse(token, out RevKey key)
                            || key == RevKey.None || !System.Enum.IsDefined(typeof(RevKey), key))
                        {
                            error = "第 " + (i + 1) + " 行键位不认识：" + token + "（键位名要与 Unity KeyCode 一致）";
                            return false;
                        }
                        if (!binding.AddKey(key))
                        {
                            error = "第 " + (i + 1) + " 行键位超过上限（最多 " + RevInputLimits.MaxKeysPerAction + " 个）";
                            return false;
                        }
                    }
                }

                // 键位轴是一键向负、一键向正；若两边填同一个键，按下时正负输入会互相抵消，轴值永远是 0。
                // 这种绑定不会按预期移动角色，所以在导入时直接报错，而不是让玩家调试一个看似成功但无效的配置。
                if (binding.AxisNegative != RevKey.None && binding.AxisNegative == binding.AxisPositive)
                {
                    error = "第 " + (i + 1) + " 行轴的正向与负向键不能相同";
                    return false;
                }

                binding.RebuildMasks();
                parsed.Add(binding);
            }

            // ★ Bug 修复（2026-09-30）：数量上限必须在替换**之前**检查 ——
            //   原实现先 _map/_list/_states.Clear() 再在循环里查上限，超上限时 return false，
            //   但原绑定已经被清光了："半成品不落地"的承诺被自己破坏（存档超限时绑定全丢）。
            //   现在先查后换：失败时旧表完好无损。
            if (parsed.Count > RevInputLimits.MaxActions)
            {
                error = "动作数量超过上限 " + RevInputLimits.MaxActions;
                return false;
            }

            // 全部解析成功才替换（半成品不落地：这是"存档坏了不炸游戏"的防线）
            _map.Clear();
            _list.Clear();
            _states.Clear();
            for (int i = 0; i < parsed.Count; i++)
            {
                _map[parsed[i].Action] = parsed[i];
                _list.Add(parsed[i]);
                _states[parsed[i].Action] = default;
            }
            return true;
        }
    }
}

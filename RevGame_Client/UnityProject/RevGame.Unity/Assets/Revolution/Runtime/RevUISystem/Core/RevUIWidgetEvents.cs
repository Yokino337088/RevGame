// ============================================================
// RevUIWidgetEvents.cs —— 控件事件的方法特性（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Core\
//
// 【它解决什么】
//   控件事件一共三种接法，这里是最省事的一种 —— **给方法标特性**：
//
//     [RevButtonClick("btnStart")]      void OnStart()                 => StartGame();
//     [RevButtonLongPress("btnSkill")]  void OnSkillHold()             => ShowSkillTip();
//     [RevButtonLoosen("btnMove")]      void OnMoveUp()                => StopMove();
//     [RevToggleChanged("tglSound")]    void OnSound(bool on)          => SetSound(on);
//     [RevSliderChanged("sldVolume")]   void OnVolume(float v)         => SetVolume(v);
//     [RevInputChanged("inpName")]      void OnNameChanged(string txt) => Preview(txt);
//     [RevInputEndEdit("inpName")]      void OnNameDone(string txt)    => Submit(txt);
//     [RevDropdownChanged("ddlQuality")] void OnQuality(int index)     => SetQuality(index);
//     [RevScrollChanged("scrollList")]  void OnScrolled(float x, float y) => LoadMore(y);
//
//   ★ 控件名规则：写节点名即可（区分大小写，与 [RevBind] 的匹配规则一致）；
//     写多级路径时只取最后一段（"Top/btnStart" 等价于 "btnStart"）。
//   ★ 一个方法标一个特性；同一个控件可以挂多个方法（全部都会被调用）。
//   ★ 参数形状（**只支持这些**，其它在扫描时明确报错，不静默）：
//
//       点击 / 长按 / 松开 :  ()            (string nodeName)
//       Toggle 变化        :  ()            (bool value)        (string nodeName, bool value)
//       Slider 变化        :  ()            (float value)       (string nodeName, float value)
//       输入框文本变化 / 结束编辑 : ()       (string text)       (string nodeName, string text)
//       Dropdown 变化      :  ()            (int index)         (string nodeName, int index)
//       ScrollRect 滚动    :  ()            (float x, float y)  (string nodeName, float x, float y)
//
//     ⚠ 输入框的那两种，单个 string 参数是**文本**（不是节点名）—— 节点名靠特性声明，用不上；
//       想同时要两个就写 (string nodeName, string text)。
//
// 【什么时候被调用】
//   面板 / Part 按节点名派发控件事件时（点击来自 Button.onClick；长按 / 松开来自框架挂的
//   指针继电器；Toggle / Slider / InputField / Dropdown / ScrollRect 来自它们自己的回调）。
//   同一次事件里，"重写的 OnToggleChanged(节点名, 值)"与"方法特性"**两条路都会走到** ——
//   所以同一件事只在一处做。
//
// 【性能说明（诚实版）】
//   方法特性走反射调用（MethodInfo.Invoke + 参数数组），**每次触发有一次小分配** ——
//   按钮 / Toggle / Dropdown 这类低频事件完全无所谓；但 ScrollRect 的滚动、输入框的每次敲键
//   是较高频的，若在意 GC，就给这两种用**重写回调**（`OnScrollChanged` / `OnInputChanged`，直调、零分配）。
//   两条路可以并存：低频用特性图省事，高频用重写图省 GC。
//
// 【为什么每类型只扫一次】
//   反射扫方法不便宜，而面板会被反复开合：结果按类型缓存（和 RevUIBindPlan 一个套路）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Revolution
{
    /// <summary>控件事件种类（供派发与校验使用）</summary>
    public enum RevUIWidgetEventKind : byte
    {
        /// <summary>点击（按下后在同一控件内抬起）</summary>
        Click = 0,

        /// <summary>长按（按住超过阈值再松开）</summary>
        LongPress = 1,

        /// <summary>松开（指针在控件上抬起，无论按了多久）</summary>
        Loosen = 2,

        /// <summary>Toggle 选中状态变化</summary>
        ToggleChanged = 3,

        /// <summary>Slider 值变化</summary>
        SliderChanged = 4,

        /// <summary>输入框文本变化（每敲一下都会来）</summary>
        InputChanged = 5,

        /// <summary>输入框结束编辑（回车或失焦）</summary>
        InputEndEdit = 6,

        /// <summary>Dropdown 选项变化</summary>
        DropdownChanged = 7,

        /// <summary>ScrollRect 滚动（x/y 是滚动位置）</summary>
        ScrollChanged = 8,
    }

    /// <summary>
    /// 控件事件特性的共同基类：所有特性都只有一个"控件名"参数。
    /// （业务也可以用它做统一处理，例如自研的监听注册器。）
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public abstract class RevWidgetEventAttribute : Attribute
    {
        /// <summary>控件所在节点的名字（或路径，取最后一段）</summary>
        public string Name;

        protected RevWidgetEventAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>点击：<c>[RevButtonClick("btnStart")] void OnStart() => StartGame();</c></summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevButtonClickAttribute : RevWidgetEventAttribute
    {
        public RevButtonClickAttribute(string name) : base(name) { }
    }

    /// <summary>长按：按住超过 <c>RevUISetting.ButtonLongPressSeconds</c> 后松开时调用。</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevButtonLongPressAttribute : RevWidgetEventAttribute
    {
        public RevButtonLongPressAttribute(string name) : base(name) { }
    }

    /// <summary>松开：指针在控件上抬起时调用（无论按了多久）。</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevButtonLoosenAttribute : RevWidgetEventAttribute
    {
        public RevButtonLoosenAttribute(string name) : base(name) { }
    }

    /// <summary>Toggle 变化：<c>[RevToggleChanged("tglSound")] void OnSound(bool on)</c>（也支持无参 / 只带节点名）</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevToggleChangedAttribute : RevWidgetEventAttribute
    {
        public RevToggleChangedAttribute(string name) : base(name) { }
    }

    /// <summary>Slider 变化：<c>[RevSliderChanged("sldVolume")] void OnVolume(float v)</c></summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevSliderChangedAttribute : RevWidgetEventAttribute
    {
        public RevSliderChangedAttribute(string name) : base(name) { }
    }

    /// <summary>输入框文本变化：<c>[RevInputChanged("inpName")] void OnText(string text)</c>（单个 string 参数是**文本**）</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevInputChangedAttribute : RevWidgetEventAttribute
    {
        public RevInputChangedAttribute(string name) : base(name) { }
    }

    /// <summary>输入框结束编辑：<c>[RevInputEndEdit("inpName")] void OnDone(string text)</c></summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevInputEndEditAttribute : RevWidgetEventAttribute
    {
        public RevInputEndEditAttribute(string name) : base(name) { }
    }

    /// <summary>Dropdown 变化：<c>[RevDropdownChanged("ddlQuality")] void OnPick(int index)</c></summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevDropdownChangedAttribute : RevWidgetEventAttribute
    {
        public RevDropdownChangedAttribute(string name) : base(name) { }
    }

    /// <summary>ScrollRect 滚动：<c>[RevScrollChanged("scrollList")] void OnScroll(float x, float y)</c></summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class RevScrollChangedAttribute : RevWidgetEventAttribute
    {
        public RevScrollChangedAttribute(string name) : base(name) { }
    }

    /// <summary>
    /// 控件事件特性的扫描结果与派发口（按类型缓存；纯 C#，能被工程外断言直接链接编译）。
    /// </summary>
    public static class RevUIWidgetEvents
    {
        /// <summary>种类总数（校验 / 遍历用）</summary>
        public const int KindCount = 9;

        /// <summary>方法参数形状（扫描时算好，派发时照着装参数 —— 零歧义）</summary>
        private static class Slots
        {
            public const byte Invalid = 255;
            public const byte None = 0;
            public const byte Node = 1;
            public const byte Bool = 2;
            public const byte Int = 3;
            public const byte Float = 4;
            public const byte NodeBool = 5;
            public const byte NodeInt = 6;
            public const byte NodeFloat = 7;
            public const byte Text = 8;
            public const byte NodeText = 9;
            public const byte FloatFloat = 10;
            public const byte NodeFloatFloat = 11;
        }

        private struct Handler
        {
            public MethodInfo Method;
            public byte Shape;
            public string Describe;      // "BagPanel.OnBuyClicked"，出错时报出来
        }

        /// <summary>一次事件携带的载荷（各形状按需取用）</summary>
        private struct Payload
        {
            public string Node;
            public bool Bool;
            public int Int;
            public float F1;
            public float F2;
            public string Text;
        }

        private sealed class KindMap
        {
            public readonly Dictionary<string, List<Handler>> ByNode = new Dictionary<string, List<Handler>>(8);
            public readonly List<string> Nodes = new List<string>(8);
        }

        private sealed class TypeMap
        {
            public readonly KindMap[] Kinds = new KindMap[KindCount];
            public bool Any;

            public TypeMap()
            {
                for (int i = 0; i < Kinds.Length; i++) Kinds[i] = new KindMap();
            }
        }

        private static readonly Dictionary<Type, TypeMap> Cache = new Dictionary<Type, TypeMap>();
        private static readonly TypeMap Empty = new TypeMap();

        /// <summary>扫描 / 调用出问题时上报（Support 侧接到 RevUILog；框架本身不打日志）</summary>
        public static Action<Exception, string> OnException;

        /// <summary>这个类型有没有任何控件事件方法特性</summary>
        public static bool WantsWidgetEvents(Type type) => GetMap(type).Any;

        /// <summary>这个类型 + 这个种类有没有特性</summary>
        public static bool Wants(Type type, RevUIWidgetEventKind kind) => GetMap(type).Kinds[(int)kind].Nodes.Count > 0;

        /// <summary>是否声明了"长按 / 松开"（这两个需要框架给交互节点挂指针继电器）</summary>
        public static bool WantsPressEvents(Type type)
            => Wants(type, RevUIWidgetEventKind.LongPress) || Wants(type, RevUIWidgetEventKind.Loosen);

        /// <summary>某种类声明了哪些节点名（绑定器用它做"节点是否存在"的校验）</summary>
        public static string[] NodeNames(Type type, RevUIWidgetEventKind kind)
            => GetMap(type).Kinds[(int)kind].Nodes.ToArray();

        /// <summary>特性名（报错信息里用）</summary>
        public static string AttributeName(RevUIWidgetEventKind kind)
        {
            switch (kind)
            {
                case RevUIWidgetEventKind.Click: return "RevButtonClick";
                case RevUIWidgetEventKind.LongPress: return "RevButtonLongPress";
                case RevUIWidgetEventKind.Loosen: return "RevButtonLoosen";
                case RevUIWidgetEventKind.ToggleChanged: return "RevToggleChanged";
                case RevUIWidgetEventKind.SliderChanged: return "RevSliderChanged";
                case RevUIWidgetEventKind.InputChanged: return "RevInputChanged";
                case RevUIWidgetEventKind.InputEndEdit: return "RevInputEndEdit";
                case RevUIWidgetEventKind.DropdownChanged: return "RevDropdownChanged";
                default: return "RevScrollChanged";
            }
        }

        /// <summary>清缓存（域重载 / 断言用）</summary>
        public static void ClearCache() => Cache.Clear();

        /// <summary>已经扫描缓存了多少个类型（诊断用）</summary>
        public static int CachedTypeCount => Cache.Count;

        // ── 派发（每种载荷一个重载；逐条隔离，一个方法抛异常不影响其它方法）────

        /// <summary>无载荷事件：点击 / 长按 / 松开</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName });

        /// <summary>带 bool 的事件：Toggle 变化</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind, bool value)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName, Bool = value });

        /// <summary>带 int 的事件：Dropdown 变化</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind, int value)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName, Int = value });

        /// <summary>带 float 的事件：Slider 变化</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind, float value)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName, F1 = value });

        /// <summary>带文本的事件：输入框变化 / 结束编辑</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind, string text)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName, Text = text });

        /// <summary>带两个 float 的事件：ScrollRect 滚动</summary>
        public static void Invoke(object target, string nodeName, RevUIWidgetEventKind kind, float x, float y)
            => Dispatch(target, nodeName, kind, new Payload { Node = nodeName, F1 = x, F2 = y });

        private static void Dispatch(object target, string nodeName, RevUIWidgetEventKind kind, in Payload payload)
        {
            if (target == null || string.IsNullOrEmpty(nodeName)) return;

            KindMap map = GetMap(target.GetType()).Kinds[(int)kind];
            if (!map.ByNode.TryGetValue(nodeName, out List<Handler> handlers)) return;

            for (int i = 0; i < handlers.Count; i++)
            {
                Handler h = handlers[i];
                try
                {
                    h.Method.Invoke(target, BuildArgs(h.Shape, payload));
                }
                catch (Exception e)
                {
                    OnException?.Invoke(e, h.Describe + "(" + nodeName + ")");
                }
            }
        }

        private static object[] BuildArgs(byte shape, in Payload p)
        {
            switch (shape)
            {
                case Slots.None: return null;
                case Slots.Node: return new object[] { p.Node };
                case Slots.Bool: return new object[] { p.Bool };
                case Slots.Int: return new object[] { p.Int };
                case Slots.Float: return new object[] { p.F1 };
                case Slots.NodeBool: return new object[] { p.Node, p.Bool };
                case Slots.NodeInt: return new object[] { p.Node, p.Int };
                case Slots.NodeFloat: return new object[] { p.Node, p.F1 };
                case Slots.Text: return new object[] { p.Text };
                case Slots.NodeText: return new object[] { p.Node, p.Text };
                case Slots.FloatFloat: return new object[] { p.F1, p.F2 };
                default: return new object[] { p.Node, p.F1, p.F2 };
            }
        }

        // ── 扫描（每类型只做一次）────────────────────────────

        private static TypeMap GetMap(Type type)
        {
            if (type == null) return Empty;
            if (Cache.TryGetValue(type, out TypeMap map)) return map;

            map = Build(type);
            Cache[type] = map;
            return map;
        }

        private static TypeMap Build(Type type)
        {
            var map = new TypeMap();

            // 逐层取 DeclaredOnly：覆盖"基类里写特性、派生类继承"的常见写法，又不会重复算两遍
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.IsStatic || m.IsAbstract) continue;

                    for (int k = 0; k < KindCount; k++)
                        Add(map, m, (RevUIWidgetEventKind)k);
                }
            }

            return map;
        }

        private static readonly Type[] AttributeTypes = BuildAttributeTypes();

        private static Type[] BuildAttributeTypes()
        {
            var types = new Type[KindCount];
            types[(int)RevUIWidgetEventKind.Click] = typeof(RevButtonClickAttribute);
            types[(int)RevUIWidgetEventKind.LongPress] = typeof(RevButtonLongPressAttribute);
            types[(int)RevUIWidgetEventKind.Loosen] = typeof(RevButtonLoosenAttribute);
            types[(int)RevUIWidgetEventKind.ToggleChanged] = typeof(RevToggleChangedAttribute);
            types[(int)RevUIWidgetEventKind.SliderChanged] = typeof(RevSliderChangedAttribute);
            types[(int)RevUIWidgetEventKind.InputChanged] = typeof(RevInputChangedAttribute);
            types[(int)RevUIWidgetEventKind.InputEndEdit] = typeof(RevInputEndEditAttribute);
            types[(int)RevUIWidgetEventKind.DropdownChanged] = typeof(RevDropdownChangedAttribute);
            types[(int)RevUIWidgetEventKind.ScrollChanged] = typeof(RevScrollChangedAttribute);
            return types;
        }

        private static void Add(TypeMap map, MethodInfo method, RevUIWidgetEventKind kind)
        {
            object[] attrs = method.GetCustomAttributes(AttributeTypes[(int)kind], false);
            if (attrs.Length == 0) return;

            var attr = (RevWidgetEventAttribute)attrs[0];
            string raw = attr != null ? attr.Name : null;
            string node = NormalizeNode(raw);

            if (string.IsNullOrEmpty(node))
            {
                Report("特性没写控件名", method, AttributeName(kind), raw);
                return;
            }

            if (method.ReturnType != typeof(void))
            {
                Report("方法必须有 void 返回值", method, AttributeName(kind), raw);
                return;
            }

            byte slots = SlotsFor(kind, method.GetParameters());
            if (slots == Slots.Invalid)
            {
                Report("方法参数形状不支持（见 RevUIWidgetEvents 头部注释里的对照表）", method, AttributeName(kind), raw);
                return;
            }

            KindMap kindMap = map.Kinds[(int)kind];
            if (!kindMap.ByNode.TryGetValue(node, out List<Handler> list))
            {
                list = new List<Handler>(2);
                kindMap.ByNode[node] = list;
                kindMap.Nodes.Add(node);
            }

            list.Add(new Handler
            {
                Method = method,
                Shape = slots,
                Describe = method.DeclaringType != null ? method.DeclaringType.Name + "." + method.Name : method.Name,
            });
            map.Any = true;
        }

        /// <summary>按种类算出"参数形状"（不支持的返回 Invalid）</summary>
        private static byte SlotsFor(RevUIWidgetEventKind kind, ParameterInfo[] ps)
        {
            int n = ps.Length;
            if (n == 0) return Slots.None;

            Type t0 = ps[0].ParameterType;
            Type t1 = n > 1 ? ps[1].ParameterType : null;
            Type t2 = n > 2 ? ps[2].ParameterType : null;

            switch (kind)
            {
                case RevUIWidgetEventKind.Click:
                case RevUIWidgetEventKind.LongPress:
                case RevUIWidgetEventKind.Loosen:
                    return n == 1 && t0 == typeof(string) ? Slots.Node : Slots.Invalid;

                case RevUIWidgetEventKind.ToggleChanged:
                    if (n == 1 && t0 == typeof(bool)) return Slots.Bool;
                    return n == 2 && t0 == typeof(string) && t1 == typeof(bool) ? Slots.NodeBool : Slots.Invalid;

                case RevUIWidgetEventKind.SliderChanged:
                    if (n == 1 && t0 == typeof(float)) return Slots.Float;
                    return n == 2 && t0 == typeof(string) && t1 == typeof(float) ? Slots.NodeFloat : Slots.Invalid;

                case RevUIWidgetEventKind.InputChanged:
                case RevUIWidgetEventKind.InputEndEdit:
                    if (n == 1 && t0 == typeof(string)) return Slots.Text;          // 单参数 = 文本
                    return n == 2 && t0 == typeof(string) && t1 == typeof(string) ? Slots.NodeText : Slots.Invalid;

                case RevUIWidgetEventKind.DropdownChanged:
                    if (n == 1 && t0 == typeof(int)) return Slots.Int;
                    return n == 2 && t0 == typeof(string) && t1 == typeof(int) ? Slots.NodeInt : Slots.Invalid;

                default:   // ScrollChanged
                    if (n == 2 && t0 == typeof(float) && t1 == typeof(float)) return Slots.FloatFloat;
                    return n == 3 && t0 == typeof(string) && t1 == typeof(float) && t2 == typeof(float)
                        ? Slots.NodeFloatFloat : Slots.Invalid;
            }
        }

        private static void Report(string why, MethodInfo method, string attr, string raw)
        {
            string where = method.DeclaringType != null ? method.DeclaringType.Name + "." + method.Name : method.Name;
            OnException?.Invoke(new ArgumentException($"{why}：{where}（[{attr}(\"{raw ?? ""}\")]）"), "控件事件特性");
        }

        /// <summary>
        /// 控件名规范化：去空白；写多级路径时取最后一段（"Top/btnStart" → "btnStart"）。
        /// ★ 与 [RevBind] 一样**区分大小写**：规则越少越好预测，错了会明确报"找不到节点"。
        /// </summary>
        public static string NormalizeNode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string name = raw.Trim();
            int slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1) name = name.Substring(slash + 1).Trim();

            return name.Length == 0 ? null : name;
        }
    }
}

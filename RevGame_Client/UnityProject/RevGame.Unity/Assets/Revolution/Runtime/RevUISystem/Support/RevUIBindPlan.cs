// ============================================================
// RevUIBindPlan.cs —— 控件"绑定计划"（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Support\
//
// 【什么是绑定计划】
//   面板/Part 上的 [RevBind] 字段要绑到哪个节点、期望什么类型、以及"这个类到底关不关心点击/滑动…"，
//   全部是**类型元数据**，跟运行时的 GameObject 无关。所以这里一次性算好、按类型缓存，
//   运行时只做"字符串 → Transform.Find → GetComponent"这三步，不再反射。
//
// 【顺带解决了两件事】
//   ① 字段名 → 节点名的规范化规则（去前导下划线、去 m_ 前缀）变成可断言的小函数；
//   ② "这个面板重写了哪些交互回调"提前算出来 —— 没重写就不挂监听。
//      原框架（唐老师框架）是一次性把所有控件收集进字典、并且**每个按钮都挂一个闭包**，
//      面板上有几十个按钮时就是几十次委托分配；这里只在首次创建时、且只给"你确实要用"的控件挂。
//
// 【本文件不引用 UnityEngine】所以能被工程外的断言直接链接编译 ——
//   绑定规则（字段名→节点名、继承字段收集、"重写了哪些回调"）都能在工程外逐条验。
// ============================================================
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Revolution
{
    /// <summary>一条绑定：一个 [RevBind] 字段 ↔ 一个节点</summary>
    public sealed class RevUIBindEntry
    {
        /// <summary>字段名（报错信息里用它，业务一眼能对上自己写的代码）</summary>
        public readonly string FieldName;

        /// <summary>显式路径（"Top/Title" 这种多级路径）；为空 = 按节点名找</summary>
        public readonly string Path;

        /// <summary>按字段名规范化出来的节点名（Path 为空时用它）</summary>
        public readonly string NodeName;

        /// <summary>字段声明的类型（运行时要求是 Component；具体是不是，交给绑定器在 Unity 侧判断）</summary>
        public readonly Type FieldType;

        /// <summary>
        /// 字段本体。
        /// ★ 带上它，绑定器就不用"按名字再查一次字段"——直接 SetValue，少一次反射查找。
        ///   FieldInfo 是 System 类型，不影响本文件的"纯 C#"约束。
        /// </summary>
        public readonly FieldInfo Field;

        /// <summary>显示用的目标描述（显式路径优先）</summary>
        public string Target => string.IsNullOrEmpty(Path) ? NodeName : Path;

        internal RevUIBindEntry(FieldInfo field, RevBindAttribute attr)
        {
            Field = field;
            FieldName = field.Name;
            FieldType = field.FieldType;
            Path = attr != null && !string.IsNullOrEmpty(attr.Path) ? attr.Path.Trim() : null;
            NodeName = RevUIBindPlan.NormalizeNodeName(field.Name);
        }
    }

    /// <summary>某个面板/Part 类型的完整绑定计划（按类型缓存，线程约定：仅主线程/编辑器主线程）</summary>
    public sealed class RevUIBindPlan
    {
        /// <summary>被分析的类型</summary>
        public readonly Type TargetType;

        /// <summary>需要绑定的字段（含基类里声明的，派生类在前）</summary>
        public readonly RevUIBindEntry[] Fields;

        /// <summary>这个类是否重写了对应的交互回调（或标了对应的方法特性）—— 不需要就不给这类控件挂监听</summary>
        public readonly bool WantsClick;
        public readonly bool WantsToggle;
        public readonly bool WantsSlider;
        public readonly bool WantsInput;
        public readonly bool WantsInputEndEdit;
        public readonly bool WantsDropdown;
        public readonly bool WantsScroll;

        /// <summary>
        /// 方法特性里有没有"长按 / 松开"（<c>[RevButtonLongPress]</c> / <c>[RevButtonLoosen]</c>）——
        /// 有就给交互节点挂指针继电器（UGUI 的 Button 本身不报这两个事件）。
        /// 由 <see cref="RevUIWidgetEvents"/> 算出来（纯 C#，可脱机断言）。
        /// </summary>
        public readonly bool WantsButtonPress;

        /// <summary>这个类有没有任何"按节点名分发"的交互回调（一个都没重写时，绑定器连扫描子节点都省了）</summary>
        public bool WantsAnyEvent => WantsClick || WantsToggle || WantsSlider ||
                                     WantsInput || WantsInputEndEdit || WantsDropdown || WantsScroll ||
                                     WantsButtonPress;    // ★ 只有长按/松开特性时也必须进入事件装配

        private static readonly Dictionary<Type, RevUIBindPlan> Cache = new Dictionary<Type, RevUIBindPlan>();

        /// <summary>
        /// 取绑定计划（带缓存）。
        /// </summary>
        /// <param name="targetType">面板/Part 的具体类型</param>
        /// <param name="baseType">基类类型（<c>typeof(RevUIPanel)</c> / <c>typeof(RevUIPart)</c>）——
        /// 用它区分"这个回调是业务重写的"还是"基类的空实现"。
        /// ★ 之所以把基类当参数传进来，是为了让本文件不引用 UnityEngine（基类是 MonoBehaviour）。
        /// </param>
        public static RevUIBindPlan Get(Type targetType, Type baseType)
        {
            if (targetType == null) throw new ArgumentNullException(nameof(targetType));

            if (Cache.TryGetValue(targetType, out RevUIBindPlan plan)) return plan;

            plan = Build(targetType, baseType);
            Cache[targetType] = plan;
            return plan;
        }

        /// <summary>清空缓存（域重载 / 单元测试用）</summary>
        public static void ClearCache() => Cache.Clear();

        private static RevUIBindPlan Build(Type targetType, Type baseType) => new RevUIBindPlan(targetType, baseType);

        private RevUIBindPlan(Type targetType, Type baseType)
        {
            TargetType = targetType;
            Fields = CollectBindFields(targetType, baseType);

            // "重写了没"判断：取到的方法如果**不是**基类声明的，就是业务重写的；
            // 另外，**标了对应方法特性**的也要接 —— 两条路都通，这里是"要不要装监听"的判据。
            WantsClick = Overrides(targetType, baseType, "OnClick", typeof(string))
                         || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.Click);
            WantsToggle = Overrides(targetType, baseType, "OnToggleChanged", typeof(string), typeof(bool))
                          || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.ToggleChanged);
            WantsSlider = Overrides(targetType, baseType, "OnSliderChanged", typeof(string), typeof(float))
                          || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.SliderChanged);
            WantsInput = Overrides(targetType, baseType, "OnInputChanged", typeof(string), typeof(string))
                         || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.InputChanged);
            WantsInputEndEdit = Overrides(targetType, baseType, "OnInputEndEdit", typeof(string), typeof(string))
                                || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.InputEndEdit);
            WantsDropdown = Overrides(targetType, baseType, "OnDropdownChanged", typeof(string), typeof(int))
                            || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.DropdownChanged);

            // 滑动用两个 float 而不是 Vector2：既让本文件保持"纯 C#"（Vector2 是 UnityEngine 类型），
            // 业务侧写起来也更直白（x/y 就是滚动位置）。
            WantsScroll = Overrides(targetType, baseType, "OnScrollChanged", typeof(string), typeof(float), typeof(float))
                          || RevUIWidgetEvents.Wants(targetType, RevUIWidgetEventKind.ScrollChanged);

            // 长按 / 松开：UGUI 的 Button 不报这两个事件，标了特性就得给交互节点挂指针继电器
            //   ★ 重写 OnLongPress / OnLoosen 钩子同样算（以前只认特性，重写钩子的面板永远收不到长按）
            WantsButtonPress = RevUIWidgetEvents.WantsPressEvents(targetType)
                               || Overrides(targetType, baseType, "OnLongPress", typeof(string))
                               || Overrides(targetType, baseType, "OnLoosen", typeof(string));
        }

        /// <summary>
        /// 收集 [RevBind] 字段：从具体类型往基类走，遇到 baseType 停（不把基类的内部字段当成绑定目标）。
        /// ★ 用 DeclaredOnly 逐层取：否则 GetFields 会把基类字段重复算进来。
        /// </summary>
        private static RevUIBindEntry[] CollectBindFields(Type targetType, Type baseType)
        {
            var list = new List<RevUIBindEntry>();

            for (Type t = targetType; t != null && t != baseType; t = t.BaseType)
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (FieldInfo field in fields)
                {
                    // static / const 不是"这个实例上的控件引用"，天然跳过
                    if (field.IsStatic) continue;

                    object[] attrs = field.GetCustomAttributes(typeof(RevBindAttribute), false);
                    if (attrs.Length == 0) continue;

                    list.Add(new RevUIBindEntry(field, (RevBindAttribute)attrs[0]));
                }
            }

            return list.ToArray();
        }

        private static bool Overrides(Type targetType, Type baseType, string methodName, params Type[] args)
        {
            MethodInfo method = targetType.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);

            // 取不到 = 基类没有这个方法（理论上不会发生）；声明者不是基类 = 业务重写了
            return method != null && method.DeclaringType != null && method.DeclaringType != baseType;
        }

        /// <summary>
        /// 字段名 → 节点名：去掉前导下划线、去掉 Unity 老代码常见的 "m_" 前缀。
        /// "_btnClose" / "__btnClose" / "m_btnClose" / "btnClose" → 全部得到 "btnClose"。
        /// ★ 只做这两条例外，其余**原样匹配**（区分大小写）—— 规则越少越好预测，
        ///   找不到时会直接把"这个字段想找的节点名"和"根节点下真实存在的节点名"一起打出来。
        /// </summary>
        public static string NormalizeNodeName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;

            string name = fieldName;
            int i = 0;
            while (i < name.Length && name[i] == '_') i++;
            name = name.Substring(i);

            if (name.StartsWith("m_", StringComparison.Ordinal)) name = name.Substring(2);

            i = 0;
            while (i < name.Length && name[i] == '_') i++;
            name = name.Substring(i);

            return name.Length == 0 ? fieldName : name;
        }

        public override string ToString() => $"{TargetType.Name}：{Fields.Length} 个绑定字段";
    }
}

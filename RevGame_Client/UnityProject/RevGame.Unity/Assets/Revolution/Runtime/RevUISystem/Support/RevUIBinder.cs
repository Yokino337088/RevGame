// ============================================================
// RevUIBinder.cs —— 控件绑定器（强类型字段绑定 + 按节点名自动事件分发）
//
// 位置：Runtime\RevUISystem\Support\
//
// 【它做两件事，正好对应"两代框架"的优点】
//   ① 强类型字段绑定：面板上写 [RevBind] private Button _btnClose; 框架按"字段名 ↔ 节点名"自动找到并赋值。
//      好处是"能不能拿到控件"在**第一次运行就明确报错**，不会像手写 Find("Panel/Panel/Button") 那样
//      在美术改了层级之后变成一个运行时空引用（王者文档里把硬编码 Find 列为反面教材 ★☆☆☆☆）。
//   ② 按节点名自动事件分发：不想写绑定字段时，重写 OnClick(节点名) 就行 —— 这是原框架（唐老师框架）
//      最方便的地方，保留。两种方式可以混用。
//
// 【相对原框架的三处改进（都是"看得见的成本"）】
//   · 只扫"你确实要用的"控件类型：原框架一次性把 Button/Toggle/Slider/InputField/Dropdown/ScrollRect/
//     Text/Image/TMP… 全扫一遍存字典；这里由绑定计划决定（重写了 OnClick 才扫 Button）。
//   · 每个交互节点只挂一次监听：原框架是 Awake 时每个按钮挂一个闭包，且每次实例化都重挂；
//     这里是"实例创建时挂一次"，池化复用不再重挂。
//   · 找不到时给的是"能照着改"的报错：把字段名、期望类型、要找的节点名、以及**根节点下真实存在的节点名**
//     一起打出来，而不是一句"没找到控件"。
//
// 【自定义控件怎么办】（TMP_InputField、长按按钮、自研控件）
//   RevUI.RegisterAutoEvent<T>((dispatch, component) => { ...dispatch.Click... });
//   注册一次，之后任何面板里出现该类型控件都会自动接上"按节点名分发"。
// ============================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>
    /// 自定义控件的"事件转发器"：节点名已经替你填好，你只要决定"哪个事件转发成哪种分发"。
    ///
    ///     RevUI.RegisterAutoEvent&lt;LongPressButton&gt;((d, btn) =&gt;
    ///     {
    ///         btn.onShortClick.AddListener(d.Click);      // 短按 → 走 OnClick(节点名)
    ///         btn.onLongPress.AddListener(d.Click);       // 长按 → 也走 OnClick(节点名)
    ///     });
    /// </summary>
    public sealed class RevUIEventDispatch
    {
        /// <summary>控件所在节点的名字（就是 OnClick(节点名) 里那个名字）</summary>
        public readonly string NodeName;

        private readonly IRevUIUserEvents _receiver;

        internal RevUIEventDispatch(IRevUIUserEvents receiver, string nodeName)
        {
            _receiver = receiver;
            NodeName = nodeName;
        }

        public void Click() => _receiver.DispatchClick(NodeName);
        public void Toggle(bool value) => _receiver.DispatchToggleChanged(NodeName, value);
        public void Slider(float value) => _receiver.DispatchSliderChanged(NodeName, value);
        public void Input(string value) => _receiver.DispatchInputChanged(NodeName, value);
        public void InputEndEdit(string value) => _receiver.DispatchInputEndEdit(NodeName, value);
        public void Dropdown(int index) => _receiver.DispatchDropdownChanged(NodeName, index);
        /// <summary>长按（由 RevUIButtonPressRelay 报告；自研长按控件也可以直接调这个转发）</summary>
        public void LongPress() => _receiver.DispatchLongPress(NodeName);

        /// <summary>松开（由 RevUIButtonPressRelay 报告）</summary>
        public void Loosen() => _receiver.DispatchLoosen(NodeName);

        public void Scroll(float x, float y) => _receiver.DispatchScrollChanged(NodeName, x, y);
    }

    /// <summary>控件绑定器（框架内部；业务通过 RevUI.RegisterAutoEvent 扩展自定义控件）</summary>
    internal static class RevUIBinder
    {
        // ============================================================
        // 绑定入口
        // ============================================================

        /// <summary>按绑定计划装配一个面板 / Part：先绑字段，再装事件分发</summary>
        public static void Bind(MonoBehaviour target, Transform root, RevUIBindPlan plan, IRevUIPartHost partHost)
        {
            if (target == null || root == null || plan == null) return;

            for (int i = 0; i < plan.Fields.Length; i++)
                BindField(target, root, plan.Fields[i], partHost);

            // 自定义控件扩展（RegisterAutoEvent）不依赖面板是否重写了回调，注册了就必须扫描。
            if (plan.WantsAnyEvent || Extensions.Count > 0) InstallEvents(target, root, plan);
        }

        // ============================================================
        // ① 字段绑定
        // ============================================================

        private static void BindField(MonoBehaviour target, Transform root, RevUIBindEntry entry, IRevUIPartHost partHost)
        {
            // 只允许 Component / GameObject：别的类型绑不了（也是开发期该立刻发现的问题）
            if (entry.FieldType != typeof(GameObject) && !typeof(Component).IsAssignableFrom(entry.FieldType))
            {
                RevUILog.Error(
                    $"{target.GetType().Name}.{entry.FieldName} 标了 [RevBind]，但类型是 {entry.FieldType.Name} —— " +
                    $"只能绑定 Component（Button/Text/Image/…）或 GameObject。");
                return;
            }

            Transform node = FindNode(root, entry, out int sameNameCount);
            if (node == null)
            {
                RevUILog.Error(
                    $"{target.GetType().Name}.{entry.FieldName}（想要 {entry.FieldType.Name}）找不到节点：\n" +
                    $"  找的是：{(string.IsNullOrEmpty(entry.Path) ? $"名字 = \"{entry.NodeName}\"（按字段名推出来的）" : $"显式路径 = \"{entry.Path}\"")}\n" +
                    $"  根节点 {root.name} 下现有节点：{ChildNames(root)}\n" +
                    $"  → 要么把节点改成这个名字，要么写清楚路径：[RevBind(\"父节点/子节点\")]");
                return;
            }

            if (sameNameCount > 1)
                RevUILog.Warning(
                    $"{target.GetType().Name}.{entry.FieldName}：根节点下有 {sameNameCount} 个叫 \"{entry.NodeName}\" 的节点，" +
                    $"已用第一个。要用别的那个，请写显式路径 [RevBind(\"完整/路径\")]。");

            object value = FindComponent(node, entry.FieldType, out string why);
            if (value == null)
            {
                RevUILog.Error($"{target.GetType().Name}.{entry.FieldName}：{why}");
                return;
            }

            // 赋值：用绑定计划里带过来的 FieldInfo，不再按名字查一次
            try
            {
                entry.Field.SetValue(target, value);
            }
            catch (Exception e)
            {
                RevUILog.Error($"{target.GetType().Name}.{entry.FieldName} 赋值失败：{e.Message}");
                return;
            }

            // 字段是个 Part → 顺手把它初始化（它的 [RevBind] 也会被绑上）。
            // ★ host 优先用传进来的（面板），保证嵌套 Part 也认最外面那个宿主。
            if (value is RevUIPart part)
            {
                RevUILog.Info($"{target.GetType().Name}：绑定 Part {part.GetType().Name}");
                part.InternalInit(partHost ?? target as IRevUIPartHost);
            }
        }

        /// <summary>按"显式路径"或"节点名"找节点；同名多个时返回第一个并回传个数</summary>
        private static Transform FindNode(Transform root, RevUIBindEntry entry, out int sameNameCount)
        {
            sameNameCount = 0;

            // 显式路径：直接按路径找（"A/B/C" 多级），找不到就是找不到 —— 路径是你写的，不用再猜
            if (!string.IsNullOrEmpty(entry.Path))
            {
                Transform byPath = root.Find(entry.Path);
                if (byPath != null) sameNameCount = 1;
                return byPath;
            }

            // 快路：直接子节点
            Transform direct = root.Find(entry.NodeName);
            if (direct != null) { sameNameCount = 1; return direct; }

            // 慢路：整个后代里找（UI 嵌套常常很深，按钮藏三四层很常见）
            Transform found = null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name != entry.NodeName) continue;

                sameNameCount++;
                if (found == null) found = all[i];
            }

            return found;
        }

        /// <summary>在节点上找组件：节点本身没有就看子节点（唯一才认，多个就报错让你指清楚）</summary>
        private static object FindComponent(Transform node, Type type, out string why)
        {
            if (type == typeof(GameObject))
            {
                why = null;
                return node.gameObject;
            }

            Component onSelf = node.GetComponent(type);
            if (onSelf != null) { why = null; return onSelf; }

            Component[] inChildren = node.GetComponentsInChildren(type, true);
            if (inChildren.Length == 1) { why = null; return inChildren[0]; }

            why = inChildren.Length == 0
                ? $"节点 \"{node.name}\"（含子节点）上没有 {type.Name} 组件 —— 类型写错了？还是控件挂在别的节点上？"
                : $"节点 \"{node.name}\" 的子节点里有 {inChildren.Length} 个 {type.Name}，分不清要哪个 —— " +
                  $"请用 [RevBind(\"具体/路径\")] 直接指到那个节点。";
            return null;
        }

        private static string ChildNames(Transform root, int max = 14)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var sb = new StringBuilder();
            int shown = 0;

            for (int i = 0; i < all.Length && shown < max; i++)
            {
                if (all[i] == root) continue;
                sb.Append('"').Append(all[i].name).Append("\"  ");
                shown++;
            }

            if (all.Length - 1 > shown) sb.Append($"…（共 {all.Length - 1} 个）");
            return sb.Length == 0 ? "（没有任何子节点）" : sb.ToString();
        }

        // ============================================================
        // ② 按节点名自动事件分发
        // ============================================================

        private static readonly List<AutoEventExtension> Extensions = new List<AutoEventExtension>();

        private sealed class AutoEventExtension
        {
            public Type ComponentType;
            public Action<RevUIEventDispatch, Component> Bind;
        }

        /// <summary>
        /// 注册自定义控件的自动事件分发（同一个类型重复注册会覆盖）。
        /// ★ 框架只内置 UGUI 的那几种；TMP、长按按钮、自研控件都用这个口子接进来。
        /// </summary>
        public static void RegisterAutoEvent<T>(Action<RevUIEventDispatch, T> bind) where T : Component
        {
            if (bind == null) return;

            for (int i = 0; i < Extensions.Count; i++)
            {
                if (Extensions[i].ComponentType != typeof(T)) continue;

                Extensions[i].Bind = (d, c) => bind(d, (T)c);      // 覆盖旧的注册
                return;
            }

            Extensions.Add(new AutoEventExtension
            {
                ComponentType = typeof(T),
                Bind = (d, c) => bind(d, (T)c),
            });
        }

        // Unity 的父面板能遍历到自己下面所有子物体，其中也包括嵌套 Part 里的按钮。
        // 沿控件向上找到最近一个实现 IRevUIUserEvents 的面板/Part，只让它绑定这个控件：否则父面板和 Part 可能同时加监听，
        // 按钮一次点击触发两次回调，或 Part 的点击被错误地送到父面板。
        private static bool IsOwnedBy(MonoBehaviour target, Transform root, Transform node)
        {
            for (Transform current = node; current != null; current = current.parent)
            {
                MonoBehaviour[] components = current.GetComponents<MonoBehaviour>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (!(components[i] is IRevUIUserEvents)) continue;
                    return ReferenceEquals(components[i], target);
                }

                if (current == root) break;
            }

            return false;
        }

        private static void InstallEvents(MonoBehaviour target, Transform root, RevUIBindPlan plan)
        {
            if (!(target is IRevUIUserEvents receiver)) return;

            Type type = target.GetType();

            // 控件事件特性（[RevButtonClick] / [RevToggleChanged] / [RevSliderChanged] / …）：
            // 先校验它声明的节点确实存在 —— 名字打错时当场报出来，而不是"点了没反应"
            if (RevUIWidgetEvents.WantsWidgetEvents(type)) ValidateWidgetEventNodes(type, root);

            // 内置 UGUI 控件：只扫"这个类确实重写了回调（或标了特性）"的那几类
            if (plan.WantsClick)
            {
                Button[] items = root.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    items[i].onClick.AddListener(relay.OnClick);
                }
            }

            // 长按 / 松开：给交互节点挂一个小继电器（一个节点只挂一次；面板池化复用时不会重复挂）
            if (plan.WantsButtonPress)
            {
                Button[] items = root.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUIButtonPressRelay relay = items[i].GetComponent<RevUIButtonPressRelay>();
                    if (relay == null) relay = items[i].gameObject.AddComponent<RevUIButtonPressRelay>();
                    relay.Setup(receiver, items[i].name);
                }
            }

            if (plan.WantsToggle)
            {
                Toggle[] items = root.GetComponentsInChildren<Toggle>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    items[i].onValueChanged.AddListener(relay.OnToggle);
                }
            }

            if (plan.WantsSlider)
            {
                Slider[] items = root.GetComponentsInChildren<Slider>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    items[i].onValueChanged.AddListener(relay.OnSlider);
                }
            }

            if (plan.WantsInput || plan.WantsInputEndEdit)
            {
                InputField[] items = root.GetComponentsInChildren<InputField>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    if (plan.WantsInput) items[i].onValueChanged.AddListener(relay.OnInput);
                    if (plan.WantsInputEndEdit) items[i].onEndEdit.AddListener(relay.OnInputEnd);
                    // ★ 原框架在 onValueChanged 里同时回调了"输入中"和"结束编辑"，
                    //   于是每敲一个字都会触发一次"结束编辑"；这里两者分开挂，语义正确。
                }
            }

            if (plan.WantsDropdown)
            {
                Dropdown[] items = root.GetComponentsInChildren<Dropdown>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    items[i].onValueChanged.AddListener(relay.OnDropdown);
                }
            }

            if (plan.WantsScroll)
            {
                ScrollRect[] items = root.GetComponentsInChildren<ScrollRect>(true);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!IsOwnedBy(target, root, items[i].transform)) continue;
                    RevUINodeRelay relay = new RevUINodeRelay(receiver, items[i].name);
                    items[i].onValueChanged.AddListener(relay.OnScroll);
                }
            }

            // 业务注册的自定义控件：无条件扫（框架不知道你的类里重写了什么回调）
            if (Extensions.Count > 0)
            {
                for (int i = 0; i < Extensions.Count; i++)
                {
                    AutoEventExtension ext = Extensions[i];
                    Component[] items = root.GetComponentsInChildren(ext.ComponentType, true);

                    for (int j = 0; j < items.Length; j++)
                    {
                        if (!IsOwnedBy(target, root, items[j].transform)) continue;
                        ext.Bind(new RevUIEventDispatch(receiver, items[j].name), items[j]);
                    }
                }
            }
        }

        /// <summary>
        /// 每个交互节点一个"继电器"：它自己记住自己是谁，
        /// 于是**不需要为每个按钮各写一个闭包**（原框架的做法），一个实例上的监听分配次数降到最低。
        /// </summary>
        /// <summary>
        /// 校验方法特性声明的节点是否存在（**全部种类**都查：点击 / 长按 / 松开 / Toggle / Slider /
        /// 输入框 / Dropdown / 滚动）。
        /// ★ "名字打错"是这类写法最常见的坑（表现是"点了没反应"），所以这里一次把缺的节点报全，
        ///   并按 <see cref="RevUISetting.BindFailureIsError"/> 决定是错误还是告警。
        /// </summary>
        private static void ValidateWidgetEventNodes(Type type, Transform root)
        {
            for (int k = 0; k < RevUIWidgetEvents.KindCount; k++)
            {
                var kind = (RevUIWidgetEventKind)k;
                string[] nodes = RevUIWidgetEvents.NodeNames(type, kind);
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (FindDeep(root, nodes[i]) != null) continue;

                    string msg = $"{type.Name} 的 [{RevUIWidgetEvents.AttributeName(kind)}(\"{nodes[i]}\")] " +
                                 $"找不到名为 \"{nodes[i]}\" 的节点（控件名要与预制体里的节点名一致）";
                    if (RevUISetting.BindFailureIsError) RevUILog.Error(msg);
                    else RevUILog.Warning(msg);
                }
            }
        }

        /// <summary>按名字找后代节点（含未激活）—— 只在装配期调用</summary>
        private static Transform FindDeep(Transform root, string nodeName)
        {
            if (root == null || string.IsNullOrEmpty(nodeName)) return null;

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (string.Equals(all[i].name, nodeName, StringComparison.Ordinal)) return all[i];

            return null;
        }

        private sealed class RevUINodeRelay
        {
            private readonly IRevUIUserEvents _receiver;
            private readonly string _nodeName;

            public RevUINodeRelay(IRevUIUserEvents receiver, string nodeName)
            {
                _receiver = receiver;
                _nodeName = nodeName;
            }

            public void OnClick() => _receiver.DispatchClick(_nodeName);
            public void OnToggle(bool value) => _receiver.DispatchToggleChanged(_nodeName, value);
            public void OnSlider(float value) => _receiver.DispatchSliderChanged(_nodeName, value);
            public void OnInput(string value) => _receiver.DispatchInputChanged(_nodeName, value);
            public void OnInputEnd(string value) => _receiver.DispatchInputEndEdit(_nodeName, value);
            public void OnDropdown(int index) => _receiver.DispatchDropdownChanged(_nodeName, index);
            public void OnScroll(Vector2 position) => _receiver.DispatchScrollChanged(_nodeName, position.x, position.y);
        }
    }
}

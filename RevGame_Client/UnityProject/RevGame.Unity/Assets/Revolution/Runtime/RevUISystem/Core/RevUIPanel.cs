// ============================================================
// RevUIPanel.cs —— 面板基类（一个界面 = 一个脚本）
//
// 位置：Runtime\RevUISystem\Core\
//
// 【设计目标：既要"分层清楚"，又不让人为一个界面写一堆脚本】
//   王者的 UI 是 View / Logic / System / BusinessLogic 四层 + TS 热更桥接：
//   分层很干净，但一个界面要写好几个文件、还要跨语言通信。本框架的取舍是——
//
//     ★ 分层靠【钩子职责】约束，而不是靠【拆类】：
//       所有东西都在这一个面板类里，但每个钩子只许干一件事 ——
//
//          OnBindView()     只做"表现装配"：拿控件、挂交互（不许写业务规则、不许发请求）
//          OnRefreshView()  只做"数据落屏"：把数据画到界面上（不许改数据、不许发请求）
//          OnClick / OnOpen / OnClose / OnDataChanged…  业务规则：改数据、请求数据、刷新
//
//       于是"表现 / 数据 / 逻辑"在同一份代码里物理分开，却没有多出来的类与文件。
//       这正是王者《06-架构抽离与复用》给的结论：**用结构约束行为，而不是靠注释约定**。
//
// 【一个面板的完整寿命】
//     创建  Instantiate → InternalSetup（绑定控件 → OnBindView → OnInit）
//     打开  InternalOpen（转场 → OnOpen → OnRefreshView → 打开所有 Part）
//     复用  从实例池取出 → OnReuse（清掉上一次的残留：滚动位置、选中态、输入框…）→ 再走"打开"
//     关闭  InternalClose（关 Part → 转场 → OnClose → 摘掉本人注册的全部事件 → 回池 / 销毁）
//     销毁  OnRelease → Destroy
//
// 【★ 一行防泄漏】关闭时框架自动执行 RevEvent.RemoveAllByOwner(this)。
//   只要你在 OnBindView 里用 `owner: this` 注册过事件，就不必再手写"解绑清单"——
//   这正是王者靠纪律在维持、而这里靠机制保证的那件事。
//
// 【怎么声明我自己的预制体在哪】写在类上，业务侧调用时一行路径都不用填：
//     [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]     // 资源名默认 = 类名 BagPanel
//     public sealed class BagPanel : RevUIPanel<BagData> { ... }
//
// 【线程约定】只在主线程使用（Unity 的 UI 与 GameObject 都只允许主线程访问）。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>面板基类：所有界面都继承它（无数据的面板直接继承这个，有数据的继承 <see cref="RevUIPanel{TData}"/>）</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public abstract class RevUIPanel : MonoBehaviour, IRevUIPartHost, IRevUIUserEvents
    {
        // ============================================================
        // 元数据（框架填好，业务只读）
        // ============================================================

        /// <summary>面板元数据（资源路径 / 层级 / 缓存方式…，由 [RevUIPanel] 解析而来）</summary>
        public RevUIPanelMeta Meta { get; private set; }

        /// <summary>面板身份键，如 "UI/Panel/BagPanel"（管理器的查找与池化都用它）</summary>
        public string PanelKey => Meta == null ? null : Meta.Key;

        /// <summary>预制体所在的资源根目录段（如 "UI/Panel"）</summary>
        public string PrefabRoot => Meta == null ? null : Meta.Root;

        /// <summary>预制体资源名（默认 = 类名）</summary>
        public string PrefabName => Meta == null ? null : Meta.Name;

        /// <summary>所在层级</summary>
        public RevUILayer Layer => Meta == null ? RevUILayer.Normal : Meta.Layer;

        /// <summary>当前状态</summary>
        public RevUIPanelState State { get; private set; } = RevUIPanelState.None;

        /// <summary>是否已打开（可交互）</summary>
        public bool IsOpened => State == RevUIPanelState.Opened;

        // 宿主关闭/复用时失效，异步 Part 创建不能跨越一次打开生命周期。
        internal int LifetimeVersion { get; private set; }
        private bool _released;

        /// <summary>是否正被上层的遮罩盖住（被盖住时通常要暂停动画/定时器/音效）</summary>
        public bool IsCovered { get; private set; }

        /// <summary>面板根节点的 RectTransform（等价于 (RectTransform)transform，这里缓存下来省一次转换）</summary>
        public RectTransform ViewRect { get; private set; }

        /// <summary>
        /// 实际所在的画布（单 Canvas 架构下恒为 Common；三 Canvas 架构下由声明 + 层级决定，见 <see cref="RevUILayerUtil.ResolveCanvasType"/>）。
        /// </summary>
        public RevUICanvasType CanvasType { get; internal set; } = RevUICanvasType.Common;

        /// <summary>业务数据（非泛型视图；泛型面板请用 <c>RevUIPanel&lt;TData&gt;.Data</c>）</summary>
        public object DataObject { get; private set; }

        // ============================================================
        // 业务要重写的钩子
        // ============================================================

        /// <summary>实例首次装配完成时调用一次（做和"这一次打开"无关的初始化：注册全局事件、建索引…）</summary>
        protected virtual void OnInit() { }

        /// <summary>
        /// ★ 表现装配：在这里拿控件（[RevBind] 字段此时已经有值）并挂交互。
        /// 【纪律】只做 UI 相关的事 —— 写业务规则、发请求、改数据都属于别的钩子。
        /// 只在实例创建时调用一次（池化复用时不会再调，见 <see cref="OnReuse"/>）。
        /// </summary>
        protected abstract void OnBindView();

        /// <summary>每次打开时调用（包含"从池里复用后再打开"），适合放"这次要展示什么"的准备动作</summary>
        protected virtual void OnOpen() { }

        /// <summary>
        /// 从实例池里被取出、即将再次打开时调用。
        /// ★ 这是"复用"模式下唯一的数据安全网：把上一次的残留清干净
        ///   （滚动位置、页签选中、输入框内容、倒计时…），否则会出"上次的数据还在"这种最难查的 bug。
        /// </summary>
        protected virtual void OnReuse() { }

        /// <summary>
        /// ★ 数据落屏：把数据画到界面上（无数据的面板可以不重写；带数据的面板必须实现）。
        /// 【纪律】不许在这里改数据、发请求 —— 那些属于 <c>OnClick</c> / <c>OnOpen</c> 那一侧的逻辑。
        /// </summary>
        protected virtual void OnRefreshView() { }

        /// <summary>被上层遮罩盖住 / 不再被盖住时调用（暂停特效、音效、每帧逻辑）</summary>
        protected virtual void OnCovered(bool covered) { }

        /// <summary>每次关闭时调用（请在这里停掉自己起的协程与计时器；事件监听框架会自动摘）</summary>
        protected virtual void OnClose() { }

        /// <summary>真正销毁前调用（解绑外部引用、归还申请到的资源）</summary>
        protected virtual void OnRelease() { }

        /// <summary>数据变化时调用（oldData 首次为 null）；适合做"只更新变化的那几个控件"的增量刷新</summary>
        protected virtual void OnDataChanged(object oldData, object newData) { }

        // ============================================================
        // 转场动画（可插拔：框架不依赖任何缓动库）
        // ============================================================

        /// <summary>
        /// 打开转场。默认实现 = 无动画，立刻完成。
        /// ★ 想接 DOTween / 自研动画：重写它，动画结束后**务必调用 onDone**（否则面板会一直停在 Opening）；
        ///   不想写转场的面板什么都不用做。
        /// </summary>
        protected virtual void PlayOpenTransition(Action onDone) => onDone?.Invoke();

        /// <summary>关闭转场（同上：结束时必须调 onDone）</summary>
        protected virtual void PlayCloseTransition(Action onDone) => onDone?.Invoke();

        // ============================================================
        // 数据
        // ============================================================

        /// <summary>
        /// 设置业务数据（非泛型入口）→ 触发 <see cref="OnDataChanged"/>，若面板已可见则立刻重画。
        /// ★ 数据建议用 class（引用类型）：泛型面板的强类型入口 <c>RevUIPanel&lt;TData&gt;.SetData</c> 不会装箱，
        ///   但管理器按类型反射创建时走的是这条 object 通道。
        /// </summary>
        public void SetData(object data)
        {
            object oldData = DataObject;
            DataObject = data;

            // ★ Bug 修复（2026-09-30）：必须给泛型面板一个同步强类型 Data 的出口。
            //   原来管理器（OpenInternal / InstantiatePanel）持有的是基类声明，
            //   调 SetData(data) 走的一定是这条 object 通道 —— 而泛型类的 SetData(TData)
            //   是方法**隐藏**不是重写，永远轮不到执行 → RevUIPanel<TData>.Data 保持 null
            //   （复用路径更糟：InternalReuse 先把 Data 清成 default，之后也没人写回）。
            //   结果：OnRefreshView 契约是"只画 Data"，实际画出来的是 null / 上一次的旧数据。
            //   现在统一从这条通道经 OnDataSet 发下去（泛型面板 override 它同步 Data）。
            OnDataSet(data);

            RevUILog.Guard($"{GetType().Name}.OnDataChanged", () => OnDataChanged(oldData, data));

            // 还没显示出来就先不画：等 OnOpen 那次统一画，省一次无意义的刷新
            if (State == RevUIPanelState.Opened || State == RevUIPanelState.Opening) RefreshView();
        }

        /// <summary>
        /// 数据被设置（管理器的 object 通道与强类型入口最终都汇到这里）。
        /// ★ 泛型面板 <see cref="RevUIPanel{TData}"/> override 它把 object 同步成强类型 <c>Data</c>。
        /// </summary>
        protected virtual void OnDataSet(object data) { }

        /// <summary>让界面按当前数据重画一次</summary>
        public void RefreshView() => RevUILog.Guard($"{GetType().Name}.OnRefreshView", OnRefreshView);

        // ============================================================
        // 交互回调（按节点名分发；原框架最方便的地方，保留）
        // ============================================================
        // 用法：不想为按钮写绑定字段时，直接重写 OnClick，用节点名区分；
        //      两种方式可以混用（同一个按钮只走一条路，不会重复触发）。

        /// <summary>某个按钮被点击（nodeName = 节点名）</summary>
        protected virtual void OnClick(string nodeName) { }

        /// <summary>某个控件被长按（按住超过 <c>RevUISetting.ButtonLongPressSeconds</c> 后松开）</summary>
        protected virtual void OnLongPress(string nodeName) { }

        /// <summary>指针在某个控件上松开（无论按了多久；"松手就停"的操作走这里）</summary>
        protected virtual void OnLoosen(string nodeName) { }

        // ============================================================
        // 显示 / 隐藏动画（一行加动效；不重写 = 没有动画，行为与没有动画库时完全一致）
        // ============================================================

        /// <summary>
        /// 显示动画：**打开时自动播，播完才算"打开完成"**（打开回调在动画之后）。
        /// 一行加动效：
        /// <code>protected override RevUIAnimPreset ShowAnimation => RevUIAnimPreset.PopIn;</code>
        /// 想自己控制就在 <c>OnOpen</c> 里直接调 <c>RevUIAnim.FadeIn(this)</c>（此时请让本属性保持 None）。
        /// </summary>
        protected virtual RevUIAnimPreset ShowAnimation => RevUIAnimPreset.None;

        /// <summary>
        /// 隐藏动画：**关闭时自动播，播完才真正关闭 / 回池**（所以不会被"一关就隐藏"吞掉）。
        /// <code>protected override RevUIAnimPreset HideAnimation => RevUIAnimPreset.PopOut;</code>
        /// </summary>
        protected virtual RevUIAnimPreset HideAnimation => RevUIAnimPreset.None;

        /// <summary>某个 Toggle 的选中状态变化</summary>
        protected virtual void OnToggleChanged(string nodeName, bool value) { }

        /// <summary>某个 Slider 的值变化</summary>
        protected virtual void OnSliderChanged(string nodeName, float value) { }

        /// <summary>某个输入框的文本变化（每敲一下都会来）</summary>
        protected virtual void OnInputChanged(string nodeName, string value) { }

        /// <summary>某个输入框结束编辑（回车或失焦）</summary>
        protected virtual void OnInputEndEdit(string nodeName, string value) { }

        /// <summary>某个下拉框选项变化</summary>
        protected virtual void OnDropdownChanged(string nodeName, int index) { }

        /// <summary>某个 ScrollRect 滚动（x/y 就是 normalizedPosition / 数值位置）</summary>
        protected virtual void OnScrollChanged(string nodeName, float x, float y) { }

        // ============================================================
        // Part 协作（宿主一侧）
        // ============================================================

        /// <summary>挂点：Part 默认挂在这里（就是面板根节点）</summary>
        public Transform PartRoot => transform;

        /// <summary>宿主是否已打开（供 Part 判断"要不要立刻刷新"）</summary>
        public bool IsHostOpened => IsOpened;

        /// <summary>请求关闭宿主（Part 的"返回"按钮走这里，具体怎么处理由面板决定）</summary>
        public void RequestClose() => CloseSelf();

        /// <summary>Part 有变化、需要宿主协调时由 Part 调用（Part 之间不直接耦合，全经这里中转）</summary>
        public void NotifyPartChanged(RevUIPart part)
            => RevUILog.Guard($"{GetType().Name}.OnPartChanged", () => OnPartChanged(part));

        /// <summary>宿主的处理：比如"页签变了 → 换一页内容"</summary>
        protected virtual void OnPartChanged(RevUIPart part) { }

        /// <summary>找自己身上的某个 Part（含未激活节点）</summary>
        public T FindPart<T>() where T : RevUIPart => GetComponentInChildren<T>(true);

        /// <summary>
        /// 刷新身上所有 Part。
        /// ★ 需要显式调用：框架不会"每次刷新面板就顺带刷新所有 Part"——
        ///   那会让一个页签的数据变化带出整屏重绘（王者文档里点过这条性能坑）。
        /// </summary>
        public void RefreshParts()
        {
            RevUIPart[] parts = GetComponentsInChildren<RevUIPart>(true);
            for (int i = 0; i < parts.Length; i++) parts[i].InternalRefresh();
        }

        /// <summary>关闭自己（业务里"点了关闭按钮"就写这一行）</summary>
        public void CloseSelf() => RevUI.Close(this);

        // ============================================================
        // 框架内部（业务不要调用）
        // ============================================================

        /// <summary>装配：绑定控件 → OnBindView → OnInit。由管理器在实例创建后立即调用。</summary>
        internal void InternalSetup(RevUIPanelMeta meta)
        {
            Meta = meta;
            ViewRect = transform as RectTransform;
            State = RevUIPanelState.None;

            RevUIBindPlan plan = RevUIBindPlan.Get(GetType(), typeof(RevUIPanel));
            RevUILog.Guard($"{GetType().Name}.绑定控件", () => RevUIBinder.Bind(this, transform, plan, this));
            if (_released) return;

            // ★ 到这里，[RevBind] 字段已经有值了 —— 所以 OnBindView 里可以放心用
            RevUILog.Guard($"{GetType().Name}.OnBindView", OnBindView);
            // OnBindView 是业务回调，可能在里面调用 ShutdownAll。清理后这个面板已经标记为释放；
            // 若此处继续调用 OnInit，业务会初始化一个已经退出管理流程、即将销毁的界面，因此检查后立即结束装配。
            if (_released) return;
            RevUILog.Guard($"{GetType().Name}.OnInit", OnInit);
        }

        /// <summary>打开：转场结束 → OnOpen → 重画 → 打开所有 Part → 显示动画 → 回调</summary>
        internal void InternalOpen(Action onOpened, Action<Exception> onFailed)
        {
            if (State == RevUIPanelState.Opened)
            {
                onOpened?.Invoke();                      // 幂等：已经开着就直接回
                return;
            }

            State = RevUIPanelState.Opening;
            int version = ++LifetimeVersion;
            gameObject.SetActive(true);

            RunTransition(PlayOpenTransition, () =>
            {
                // 打开转场（例如淡入或滑入）需要经过若干帧；播放期间业务仍可能关闭面板，甚至把它放回对象池供下次复用。
                // 如果旧转场结束后不检查状态，它会把面板重新标成 Opened、再次调用 OnOpen，并通知业务“打开成功”，
                // 即使这个面板此时已经关闭或正被另一个界面复用。只有确认这是同一次打开、面板还处于 Opening，才继续完成流程。
                if (this == null || version != LifetimeVersion || State != RevUIPanelState.Opening) return;

                State = RevUIPanelState.Opened;

                RevUILog.Guard($"{GetType().Name}.OnOpen", OnOpen);
                if (this == null || version != LifetimeVersion || !IsOpened) return;
                RefreshView();
                if (this == null || version != LifetimeVersion || !IsOpened) return;
                OpenParts();
                if (this == null || version != LifetimeVersion || !IsOpened) return;

                // ★ 显示动画：播完才算"打开完成"（重写 ShowAnimation 一行就给面板加动效）
                //   —— 动画期间同样可能被关闭，回调同样要守状态
                PlayPanelAnimation(ShowAnimation, () =>
                {
                    if (this != null && version == LifetimeVersion && IsOpened) onOpened?.Invoke();
                });
            }, onFailed, "打开转场");
        }

        /// <summary>关闭：关 Part → 转场 → OnClose → 隐藏动画 → 摘事件 → 回调（回池还是销毁由管理器决定）</summary>
        internal void InternalClose(Action onClosed)
        {
            if (State == RevUIPanelState.Closed)
            {
                onClosed?.Invoke();
                return;
            }

            State = RevUIPanelState.Closing;
            ++LifetimeVersion;
            RevUIPart.CancelCreates(this);
            CloseParts();

            RunTransition(PlayCloseTransition, () =>
            {
                // 关闭转场也要经过若干帧；在动画结束前，业务可能又要求打开这个面板。
                // 若旧的关闭回调继续执行，就会把刚重新打开的面板当成仍在关闭：调用 OnClose、移除新订阅的事件，
                // 再将它放回对象池，造成界面消失或新一轮打开状态被破坏。只有面板仍是 Closing 才能完成旧关闭流程。
                if (State != RevUIPanelState.Closing) return;

                RevUILog.Guard($"{GetType().Name}.OnClose", OnClose);

                // ★ 一行防泄漏：本人（含老代码里手写的解绑清单）注册的事件全摘掉
                RevEvent.RemoveAllByOwner(this);

                // ★ 隐藏动画：播完才真正关闭 / 回池 —— 否则"一关就隐藏"会让动画看不到
                PlayPanelAnimation(HideAnimation, () =>
                {
                    // 隐藏动画期间状态若又变了（防御：正常流程不会有路径重开本实例），
                    // 不再置 Closed / 回池，避免把一个正被使用的实例还进池里
                    if (State != RevUIPanelState.Closing) return;

                    RevUIAnim.StopAllOf(this);           // 兜底：万一同帧还挂着别的动画（如按钮反馈）
                    State = RevUIPanelState.Closed;
                    IsCovered = false;

                    onClosed?.Invoke();
                });
            }, error =>
            {
                // 关闭动画启动失败时直接完成清理，不能永久留在 Closing。
                if (this == null || State != RevUIPanelState.Closing) { return; }
                State = RevUIPanelState.Closed;
                IsCovered = false;
                RevEvent.RemoveAllByOwner(this);
                RevUIAnim.StopAllOf(this);
                onClosed?.Invoke();
            }, "关闭转场");
        }

        private void RunTransition(Action<Action> transition, Action done, Action<Exception> failed, string context)
        {
            bool signaled = false;
            Action<Exception> fail = error =>
            {
                RevLog.Exception(error, $"{GetType().Name}.{context}", "UI");
                failed?.Invoke(error);
            };
            try
            {
                transition(() =>
                {
                    if (signaled) { return; }
                    signaled = true;
                    // onDone 可能跨帧调用；它内部的显示/隐藏动画启动也必须有失败出口。
                    try { done(); }
                    catch (Exception e) { fail(e); }
                });
            }
            catch (Exception e)
            {
                if (signaled) { RevLog.Exception(e, $"{context}重复完成后抛异常", "UI"); return; }
                signaled = true;
                fail(e);
            }
        }

        /// <summary>
        /// 播放显示 / 隐藏动画：没配预设（None）就直接回调 —— 保证"没有动画时行为与之前完全一致"。
        /// 所有动画都以 <c>this</c> 为 owner 登记，关闭时一行全清。
        /// </summary>
        private void PlayPanelAnimation(RevUIAnimPreset preset, Action onDone)
        {
            if (preset == RevUIAnimPreset.None)
            {
                onDone?.Invoke();
                return;
            }

            RevUIAnim.Play(this, preset, 0f, onDone, owner: this);
        }

        /// <summary>从池里取出、即将再次打开：给业务一次"把上次的残留清掉"的机会</summary>
        internal void InternalReuse()
        {
            State = RevUIPanelState.Closed;
            IsCovered = false;

            // ★ 先替业务断掉"上一次的数据"这条路：
            //   "复用的实例还挂着上一次的数据"是复用模式下最难查的一类 bug，
            //   框架直接把它清掉（业务不用记得清），业务只需要在 OnReuse 里清**界面上的残留**。
            InternalClearData();
            // HideAnimation 会把透明度、缩放或位置留在隐藏终态；若没有 ShowAnimation，复用时不会自动复原。
            // 先停旧动画并恢复基准值，避免池化面板再次打开时不可见、缩小或仍处于屏幕外。
            RevUIAnim.StopAllOf(this);
            RevUIAnim.RestoreAllBasesIn(this);

            RevUILog.Guard($"{GetType().Name}.OnReuse", OnReuse);

            // 上次没摘干净的事件（比如业务用别的 owner 注册的）在这里再兜一次，
            // 避免"复用的实例还在收上一次的事件"
            RevEvent.RemoveAllByOwner(this);
        }

        /// <summary>清空业务数据（复用时调用；泛型面板会连强类型 Data 一起清掉）</summary>
        internal virtual void InternalClearData() => DataObject = null;

        /// <summary>销毁前：给业务释放外部资源的机会</summary>
        internal void InternalRelease()
        {
            // 正常 Close 会等待关闭动画结束后再清理；但 ShutdownAll、场景销毁等路径可能直接销毁面板，不会经过这段正常收尾。
            // 此时旧的动画回调或异步 Part 创建仍可能稍后执行，访问已经释放的面板、重新创建子界面或改写状态。
            // 先标记释放、增加生命周期版本并取消 Part 创建，让迟到回调知道自己已经过期；再停止面板和 Part 的动画并恢复控件，防止残留回调继续运行。
            if (_released) return;
            _released = true;
            State = RevUIPanelState.Closed;
            ++LifetimeVersion;
            RevUIPart.CancelCreates(this);

            RevUIPart[] parts = GetComponentsInChildren<RevUIPart>(true);
            for (int i = 0; i < parts.Length; i++) RevUIAnim.StopAllOf(parts[i]);
            RevUIAnim.StopAllOf(this);
            RevUIAnim.RestoreAllBasesIn(this);
            RevUILog.Guard($"{GetType().Name}.OnRelease", OnRelease);
            RevEvent.RemoveAllByOwner(this);
        }

        /// <summary>被上层遮罩盖住 / 恢复（状态没变就不回调，业务不必自己防抖）</summary>
        internal void InternalSetCovered(bool covered)
        {
            if (IsCovered == covered) return;

            IsCovered = covered;
            RevUILog.Guard($"{GetType().Name}.OnCovered", () => OnCovered(covered));
        }

        private void OpenParts()
        {
            // 一次拿全部后代（含未激活）：Part 不递归通知自己下面的 Part，这里一趟走完
            RevUIPart[] parts = GetComponentsInChildren<RevUIPart>(true);
            for (int i = 0; i < parts.Length; i++) parts[i].InternalOpen();
        }

        private void CloseParts()
        {
            RevUIPart[] parts = GetComponentsInChildren<RevUIPart>(true);
            for (int i = 0; i < parts.Length; i++) parts[i].InternalClose();
        }

        // ============================================================
        // IRevUIUserEvents（绑定器把控件事件转到这里；显式实现，不污染业务 API）
        // ============================================================

        void IRevUIUserEvents.DispatchClick(string nodeName) => RevUILog.Guard($"OnClick({nodeName})", () =>
        {
            OnClick(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.Click);        // 方法特性那条路
        });

        void IRevUIUserEvents.DispatchLongPress(string nodeName) => RevUILog.Guard($"OnLongPress({nodeName})", () =>
        {
            OnLongPress(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.LongPress);
        });

        void IRevUIUserEvents.DispatchLoosen(string nodeName) => RevUILog.Guard($"OnLoosen({nodeName})", () =>
        {
            OnLoosen(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.Loosen);
        });

        void IRevUIUserEvents.DispatchToggleChanged(string nodeName, bool value) => RevUILog.Guard($"OnToggleChanged({nodeName})", () =>
        {
            OnToggleChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.ToggleChanged, value);
        });

        void IRevUIUserEvents.DispatchSliderChanged(string nodeName, float value) => RevUILog.Guard($"OnSliderChanged({nodeName})", () =>
        {
            OnSliderChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.SliderChanged, value);
        });

        void IRevUIUserEvents.DispatchInputChanged(string nodeName, string value) => RevUILog.Guard($"OnInputChanged({nodeName})", () =>
        {
            OnInputChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.InputChanged, value);
        });

        void IRevUIUserEvents.DispatchInputEndEdit(string nodeName, string value) => RevUILog.Guard($"OnInputEndEdit({nodeName})", () =>
        {
            OnInputEndEdit(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.InputEndEdit, value);
        });

        void IRevUIUserEvents.DispatchDropdownChanged(string nodeName, int index) => RevUILog.Guard($"OnDropdownChanged({nodeName})", () =>
        {
            OnDropdownChanged(nodeName, index);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.DropdownChanged, index);
        });

        void IRevUIUserEvents.DispatchScrollChanged(string nodeName, float x, float y) => RevUILog.Guard($"OnScrollChanged({nodeName})", () =>
        {
            OnScrollChanged(nodeName, x, y);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.ScrollChanged, x, y);
        });

        // ============================================================
        // Unity 生命周期
        // ============================================================

        /// <summary>
        /// 外部 Destroy 会绕过管理器的正常关闭/回池流程；通知管理器移除打开表、互斥组与池索引并归还资源租约，
        /// 避免再次打开命中已销毁实例或泄漏引用，同时清理动画与事件。
        /// </summary>
        protected virtual void OnDestroy()
        {
            RevUIManager.Instance.NotifyPanelDestroyed(this);
            RevUIAnim.StopAllOf(this);
            RevEvent.RemoveAllByOwner(this);
        }

        public override string ToString() => Meta == null ? GetType().Name : $"{GetType().Name}[{Meta.Key}]";
    }
}

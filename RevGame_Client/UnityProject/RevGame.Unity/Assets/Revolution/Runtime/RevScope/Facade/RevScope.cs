// ============================================================
// RevScope.cs —— 作用域（域）门面：给"一局 / 一屏 / 一段玩法"划一条统一的回收边界
//
// 位置：Runtime\RevScope\Facade\
//
// 【它解决什么】框架里每个模块都有自己的清理入口：
//     RevEvent.RemoveAllByOwner / RevTimer.CancelAllOf / RevMono.RemoveAllOf /
//     RevPool.DestroyGroup / RevResBootstrap.Shutdown / RevServices.UnregisterAllOf
//   零件都很好，但"什么时候调、按什么顺序调、谁负责记得调"全靠人。典型事故：
//     · 打完一局回大厅，战斗里起的计时器还在跑（因为起的时候没传 owner，它不属于任何对象，只属于"那一局"）；
//     · 切场景时资源先卸、对象池还端着引用 → 白模 / 丢材质 / 偶发空引用（顺序错了）。
//   本类给框架补上"这一局"这个概念：进入时开一个域，结束时一行 Exit，**框架按钉死的顺序**把账收干净。
//
// 【★ 最重要的三句话】
//   ① 域是"一段时间"的名字：Enter 开始，Exit 结束。
//   ② 域开着的时候，你**没写 owner / group** 的注册（计时器 / 事件 / 每帧回调 / 资源分组）自动算这个域的；
//      域一关，它们一起被清掉。—— 所以"忘了传 owner"也不会泄漏。
//   ③ 你**显式写了** owner / group 的，域永远不插手（显式永远优先，"写清楚"永远安全）。
//
// 【最短用法】
//     // ——长生命周期的域（战斗 / 大厅）：由"游戏流程"开关，不是由某段代码开关——
//     RevScope.Enter(RevDomain.Battle, RevResGroup.Battle);   // 进战斗（第二个参数 = 关域时要整组回收的资源分组，可省略）
//     ...                                                     // 这一局里任何脚本里注册的东西，没写 owner 的都算战斗域的
//     RevScope.Exit(RevDomain.Battle);                        // 一局结束：9 个步骤按固定顺序全部回收
//
//     // ——短小的、一次写完的局部段（教程一步 / 一次加载 / 一个弹窗流程）：用 using——
//     using (RevScope.Enter("TutorialStep1")) { ...这段里的东西，出块全清... }
//
// 【为什么战斗不能用 using】
//   一场战斗横跨几十个脚本、几百帧：技能脚本取子弹、网络回包改数据、界面刷血条……
//   它们不是在同一段同步代码里创建的，不可能都写进同一个 { } 里。
//   所以长域由"游戏流程"（状态机 / 场景加载完成的回调）调 Enter / Exit；using 只留给局部段。
//
// 【★ 关闭的 9 个步骤（顺序钉死，规则：谁依赖谁，谁后关）】
//   0 关域门 → 1 业务单元 RevLogic → 2 界面 RevUI → 3 每帧回调 RevMono → 4 计时器 RevTimer →
//   5 事件监听 RevEvent → 6 对象池 RevPool → 7 资源 RevRes → 8 服务 RevServices → 9 兜底扫尾
//   每一步独立 try-catch：某一步抛异常只记录、不中断后面的步骤；全部结果汇总进 LastCloseReport。
//   其中最关键的是 6 在 7 之前：池手里端着 prefab 的资源引用，必须先还实例、再卸资源。
//
// 【必须显式写 owner 的四种情况（判据：这个东西的生命周期 == 这一局吗？不是，就显式写）】
//   · 全局常驻对象（网络 / 音效 / 关系链 / 配置）—— 它比域活得久，被域回收是意外
//   · 跨域订阅（大厅脚本订阅战斗事件）          —— 域关了不该把你的监听也摘走
//   · 生命周期长于域的公共对象
//   · 后台线程 / 编辑器工具里注册               —— 那里没有"当前域"
//   服务（RevServices）永远不会被自动归域：想让它随域走，显式写 owner: RevScope.Current。
//
// 【暂停不是回收】SetTickEnabled(域, false) 只停"归本域兜底的每帧回调"和 RevLogic.OnTick，
//   对象、状态、监听都还在，恢复后继续。★ 计时器不受影响（它是"时间"不是"帧回调"）。
//
// 【和别的模块的分工】
//   RevServices 管"能力"（供别人取用，活得久）；RevScope 管"过程"（一段时间内存在的业务，活完要干净结束）。
//   RevScene 管"怎么切场景"；RevScope 管"这段时间的账"。两者不互相替代。
//   RevScene.AutoClearPool 默认也会在切场景时清池，和域的第 6 步重叠 —— 重复清理是安全的（对空池返回 0）。
//
// 【线程】只在主线程使用（和 Unity 一致）。
// 【进 Play 自动复位】关闭 Domain Reload 时，上一局的域会在新一局开始前被安静地清掉（见 Support\RevScopeUnityHooks）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>作用域门面：进入 / 退出一个域，查询当前域，暂停整个域的每帧逻辑，诊断。</summary>
    public static class RevScope
    {
        internal static readonly RevScopeCore Core = CreateCore();

        private static RevScopeCore CreateCore()
        {
            RevScopeCore core = new RevScopeCore();
            core.InstallPureSteps();
            return core;
        }

        // ============================================================
        // 各模块门面读的"兜底值"（★ 没有域时一律是 null / Unknown，行为与没有作用域系统时逐字一致）
        // ============================================================

        /// <summary>★ 没写 owner 的注册该归谁：有域 = 那个域；没有域 = null。计时器 / 事件 / 每帧回调的门面都读它。</summary>
        internal static RevScopeHandle DefaultNode => Core.DefaultNode;

        /// <summary>★ 没写资源分组时的兜底分组：有域 = 域配置的回收分组；没有域 = Unknown。资源系统读它。</summary>
        internal static RevResGroup DefaultGroup => Core.DefaultGroup;

        // ============================================================
        // 进入 / 退出
        // ============================================================

        /// <summary>
        /// 进入一个域。返回的句柄可以放进 <c>using</c>（出块自动关），也可以不管它、以后用 <see cref="Exit(string)"/> 关。
        /// <code>RevScope.Enter(RevDomain.Battle, RevResGroup.Battle);</code>
        /// </summary>
        /// <param name="domain">域名（建议用 <see cref="RevDomain"/> 常量或自己集中定义的常量）</param>
        /// <param name="reclaim">关闭这个域时要整组回收的资源分组（对象池 + 资源）。
        /// 省略 = 不按组回收。★ 别设成全程共用的组（UI / Config / Sound），它是整组清。</param>
        /// <returns>域句柄；失败（空域名 / 重复进入）时返回 <see cref="RevScopeHandle.Empty"/>（安全空操作，并有明确报错）</returns>
        public static RevScopeHandle Enter(string domain, RevResGroup reclaim = RevResGroup.Unknown)
            => Core.Enter(domain, reclaim, takeOverOwner: true, reenterIsError: true, note: null);

        /// <summary>进入一个域（带可选项：回收分组 / 是否兜底归属 / 重复进入的处理 / 备注）。</summary>
        public static RevScopeHandle Enter(string domain, RevScopeOptions options)
        {
            if (options == null) { options = new RevScopeOptions(); }

            return Core.Enter(domain, options.Reclaim, options.TakeOverOwner, options.ReenterIsError, options.DebugNote);
        }

        /// <summary>
        /// 退出一个域：按 9 个步骤回收，返回回收的总数。
        /// <para>★ 域只能自顶向下关：关一个不在栈顶的域，会先把压在它上面的域按顺序关掉（并打警告）。</para>
        /// <para>★ 重复 Exit 同一个域返回 0，不报错、不重复回收。</para>
        /// </summary>
        public static int Exit(string domain) => Core.Exit(domain);

        /// <summary>退出一个域（用句柄）。</summary>
        public static int Exit(RevScopeHandle handle) => Core.Exit(handle);

        /// <summary>关闭全部域（回登录 / 退出游戏用），自顶向下。返回回收的总数。</summary>
        public static int ExitAll() => Core.ExitAll();

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>当前域：栈里最上面那个"开着"的域。没有域为 null。</summary>
        public static RevScopeHandle Current => Core.Top;

        /// <summary>当前域名（没有域为 null）。</summary>
        public static string CurrentName => Core.Top != null ? Core.Top.Name : null;

        /// <summary>这个域现在开着吗（用来防"域结束后晚到的异步回调"）。</summary>
        public static bool IsActive(string domain)
        {
            RevScopeHandle node = Core.Find(domain);
            return node != null && node.State == RevScopeState.Open;
        }

        /// <summary>按域名取句柄（没有返回 null）。</summary>
        public static RevScopeHandle Get(string domain) => Core.Find(domain);

        /// <summary>域栈深度。</summary>
        public static int Count => Core.Depth;

        // ============================================================
        // 归属
        // ============================================================

        /// <summary>
        /// 把对象登记到当前域：域关闭时，框架会对它执行"一行清理"（摘事件 / 停计时器 / 摘每帧回调 / 注销服务；界面会被关闭）。
        /// 用在"注册写了显式 owner: this，但仍想让它跟着这个域一起结束"的场合。
        /// <para>★ 没有任何域开着时调用会报错（多半是启动顺序问题：请先 Enter）。</para>
        /// </summary>
        public static void Attach(object owner)
        {
            if (owner == null) { return; }

            RevScopeHandle node = Core.Top;
            if (node == null)
            {
                RevLog.Error("RevScope.Attach 在没有活动域时被调用（此时无域可归属）。这多半是启动顺序问题：请先 RevScope.Enter。", "Scope");
                return;
            }

            Core.AttachTo(node, owner);
        }

        /// <summary>把对象从它所在的域摘出来（比如对象要"跳出这个域"独立活下去）。返回摘了几处（0 或 1）。</summary>
        public static int Detach(object owner) => Core.Detach(owner);

        // ============================================================
        // 每帧开关
        // ============================================================

        /// <summary>
        /// 暂停 / 恢复一个域的每帧逻辑（归本域兜底的每帧回调 + 本域里 RevLogic 的 OnTick）。
        /// <para>★ 这不是回收：对象、状态、监听都还在，恢复后继续。</para>
        /// <para>★ 计时器照常走（它是"时间"，不是"帧回调"）。需要连计时器一起停，请用 Exit，或自己 CancelAllOf。</para>
        /// </summary>
        public static void SetTickEnabled(string domain, bool enabled) => Core.SetTickEnabled(domain, enabled);

        /// <summary>这个域的每帧逻辑是否在跑（域不存在返回 false）。</summary>
        public static bool IsTickEnabled(string domain)
        {
            RevScopeHandle node = Core.Find(domain);
            return node != null && node.TickOn;
        }

        // ============================================================
        // 事件 / 诊断
        // ============================================================

        /// <summary>某个域刚进入时触发（参数 = 域名）。订阅者抛异常会被隔离。</summary>
        public static event Action<string> Entered
        {
            add => Core.Entered += value;
            remove => Core.Entered -= value;
        }

        /// <summary>某个域刚关闭完时触发（参数 = 域名）。订阅者抛异常会被隔离。</summary>
        public static event Action<string> Exited
        {
            add => Core.Exited += value;
            remove => Core.Exited -= value;
        }

        /// <summary>最近一次关域的"体检报告"（还没关过任何域为 null）。</summary>
        public static RevScopeCloseReport LastCloseReport => Core.LastCloseReport;

        /// <summary>
        /// 把当前所有域打成一段人话文本：域栈、谁属于哪个域、是否暂停、挂了几个对象 / Logic、上次关闭的报告。
        /// 排查"回大厅后内存没降"时打一次就够。
        /// </summary>
        public static string Dump() => Core.Dump();

        /// <summary>
        /// 每帧驱动业务单元的 OnTick。<b>框架会自动挂到每帧回调上，业务不用管</b>；
        /// 工程外测试可以手动调它来验证。
        /// </summary>
        public static void Tick(float deltaTime) => Core.TickLogics(deltaTime);

        // ============================================================
        // 内部
        // ============================================================

        /// <summary>界面系统用：有域就把面板登记到域，没有域什么也不做（不报错）。</summary>
        internal static void AttachSilently(object owner) => Core.AttachSilently(owner);

        /// <summary>Support 层补装 Unity 侧的回收步骤（界面 / 每帧回调 / 对象池 / 资源）。</summary>
        internal static void InstallStep(RevScopeStep step, Func<RevScopeHandle, int> fn) => Core.SetStep(step, fn);

        /// <summary>进 Play / 换工程时复位（由 Support\RevScopeUnityHooks 调用，业务不用管）。</summary>
        internal static void ResetForNewSession() => Core.ResetForNewSession();
    }

    /// <summary>纯 C# 内核与 Unity 宿主之间的接缝（Support 层启动时装上，业务不用管）。</summary>
    internal static class RevScopeBridge
    {
        /// <summary>有"重写了 OnTick 的 Logic"启动时调用：确保每帧驱动已经挂上（没有 Unity 宿主时为 null，注册照常可用，只是不会自动 Tick）。</summary>
        internal static Action EnsureTickDriver;

        /// <summary>Logic 停止时摘它的每帧回调（RevMono 在 Unity 侧，内核够不着）。</summary>
        internal static Action<object> RemoveMonoOf;
    }
}

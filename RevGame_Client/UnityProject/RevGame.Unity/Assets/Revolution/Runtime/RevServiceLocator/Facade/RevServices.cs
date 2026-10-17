// ============================================================
// RevServices.cs —— ★ 小白从这里开始：一行注册、直接取、用完注销
//
// 位置：Runtime\RevServiceLocator\Facade\
//
// ┌─ 三步走（全程不用写静态类、不用链式、不用缓存字段）─────────────────────────────────┐
// │ 1) 注册（一行；放在启动入口，或这个服务所属模块的 Awake / OnEnable 里）                │
// │    RevServices.Register<ISoundService, SoundService>();                              │
// │                                                                                      │
// │ 2) 取用（任何地方，一行）                                                              │
// │    RevServices.GetRequired<ISoundService>().Play("ui_click");                        │
// │                                                                                      │
// │ 3) 不用了就注销（释放内存；容器创建的服务会被 Dispose）                                │
// │    RevServices.Unregister<ISoundService>();                                          │
// └──────────────────────────────────────────────────────────────────────────────────────┘
//
// 【和 RevServiceLocator（Create().AddXxx().Build() 那套）怎么选】
//   · 90% 的场景用本类：全局只有一份、想随时注册随时注销 → RevServices
//   · 需要"每局一份 / 每个场景一份"（作用域）、或单测里另建一个隔离容器 → RevServiceLocator
//   两者语义一致（懒创建 / OnInit 钩子 / 循环依赖报错 / 谁创建谁释放），可以放心混用。
//
// 【为什么要有"注销"】只能注册、不能注销的容器，服务一注册就一直占着内存（连同它引用的所有东西）。
//   Unregister = 摘表 + 不再被每帧驱动 + 容器创建的实例会 Dispose。
//
// 【四条铁律】
//   ① 取服务优先用 GetRequired（"忘了注册"当场就炸，错误信息里列出已注册的服务）；只有可选能力才用 Get / TryGet
//   ② 谁创建谁负责释放：Register<I, T>() 和工厂注册 → 注销时容器 Dispose；Register<I>(已有实例) → 注销只摘表，不动你的对象
//   ③ 注销之前想清楚还有谁在用：别人手里已经拿到的引用不会变成 null，但服务本身已经被 Dispose 了
//   ④ 随对象销毁的服务要带 owner：Register<I, T>(owner: this)，对象 OnDestroy 里一行 RevServices.UnregisterAllOf(this)
//
// 【线程】只在主线程使用（和 Unity 一致）。
// 【进 Play 自动复位】关闭 Domain Reload 时，上一局注册的服务会在新一局开始前被清掉（见 Support\RevServicesUnityHooks）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>
    /// 服务定位器的静态门面：一行注册、直接取、可注销。
    /// <para>它背后是一张"活的"服务表（<c>RevServiceHub</c>）：注册不需要先 Build，注销会真正释放内存。</para>
    /// </summary>
    public static class RevServices
    {
        internal static readonly RevServiceHub Hub = new RevServiceHub();

        // ============================================================
        // 注册（一行）
        // ============================================================

        /// <summary>
        /// 注册服务（接口 → 实现）：第一次被取用时才创建，零反射。
        /// <code>RevServices.Register&lt;ISoundService, SoundService&gt;();</code>
        /// <para><typeparamref name="TService"/> 是取服务用的类型（写接口）；<typeparamref name="TImpl"/> 必须有公开无参构造
        /// （需要构造参数请用工厂重载）。容器创建的实例在 <see cref="Unregister{TService}"/> 时会被 Dispose。</para>
        /// </summary>
        /// <param name="owner">谁注册的（可选）：传 <c>this</c>，对象销毁时 <see cref="UnregisterAllOf"/> 一行清掉它注册的全部服务</param>
        /// <exception cref="InvalidOperationException">这个类型已经注册过了（想换实现请先 Unregister）</exception>
        public static void Register<TService, TImpl>(object owner = null)
            where TService : class
            where TImpl : class, TService, new()
        {
            Hub.Register(typeof(TService), _ => new TImpl(), ownsInstance: true, owner: owner,
                         origin: $"Register<{typeof(TService).Name}, {typeof(TImpl).Name}>()");
            EnsureHooks();
        }

        /// <summary>
        /// 注册服务（已有实例）：适合宿主自己创建的对象（配置、MonoBehaviour 适配器……）。
        /// <code>RevServices.Register&lt;IConfigService&gt;(Config.Load());</code>
        /// <para>★ 这个实例<b>由你自己负责释放</b>：注销时容器只把它从表里摘掉，不会 Dispose 它。</para>
        /// </summary>
        public static void Register<TService>(TService instance, object owner = null) where TService : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance), $"Register<{typeof(TService).Name}>(instance) 收到 null");
            }

            Hub.Register(typeof(TService), _ => instance, ownsInstance: false, owner: owner,
                         origin: $"Register<{typeof(TService).Name}>(实例)");
            EnsureHooks();
        }

        /// <summary>
        /// 注册服务（工厂）：需要构造参数、或要先读配置时用它；工厂里可以取其它服务（框架会检测循环依赖）。
        /// <code>RevServices.Register&lt;ISoundService&gt;(s =&gt; new SoundService(s.GetRequired&lt;IConfigService&gt;().SoundPath));</code>
        /// <para>容器创建的实例，注销时会被 Dispose。</para>
        /// </summary>
        public static void Register<TService>(Func<RevIServiceLocator, TService> factory, object owner = null) where TService : class
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            Hub.Register(typeof(TService), services => factory(services), ownsInstance: true, owner: owner,
                         origin: $"Register<{typeof(TService).Name}>(工厂)");
            EnsureHooks();
        }

        // ============================================================
        // 取用（一行）
        // ============================================================

        /// <summary>
        /// 取服务（必须有）：没注册就抛异常，错误信息里会列出"当前已注册的服务"，并告诉你它是否曾被注销过。
        /// <para>用在"没有它就跑不起来"的依赖上。</para>
        /// </summary>
        public static T GetRequired<T>() where T : class => Hub.GetRequired<T>();

        /// <summary>取服务（可能有）：没注册返回 <c>null</c>（用在可选能力上）。</summary>
        public static T Get<T>() where T : class => Hub.Get<T>();

        /// <summary>尝试取服务：拿到返回 true，否则 false 且 <paramref name="service"/> 为 null。</summary>
        public static bool TryGet<T>(out T service) where T : class => Hub.TryGet(out service);

        /// <summary>这个类型注册过吗（只查有没有，不会触发创建）。</summary>
        public static bool IsRegistered<T>() where T : class => Hub.IsRegistered(typeof(T));

        /// <summary>
        /// 把"取服务"的只读入口交给别人（例如交给只依赖 <see cref="RevIServiceLocator"/> 的底层模块）。
        /// </summary>
        public static RevIServiceLocator Locator => Hub;

        // ============================================================
        // 注销（释放内存）
        // ============================================================

        /// <summary>
        /// 注销一个服务：从表里摘掉 + 不再被每帧驱动 + 容器创建的实例会被 Dispose。没注册过返回 false（重复调用安全）。
        /// <code>RevServices.Unregister&lt;ISoundService&gt;();</code>
        /// <para>★ 别人手里已经拿到的引用不会变成 null，但服务已被 Dispose —— 注销前想清楚还有谁在用。</para>
        /// <para>★ 之后再 <see cref="GetRequired{T}"/> 会明确报错（并说明它曾被注销过）。</para>
        /// </summary>
        public static bool Unregister<TService>() where TService : class => Hub.Unregister(typeof(TService));

        /// <summary>
        /// 注销某个 owner 注册的全部服务（逆创建序释放）：对象销毁时的一行清理。
        /// <code>private void OnDestroy() { RevServices.UnregisterAllOf(this); }</code>
        /// </summary>
        /// <returns>实际注销了几个</returns>
        public static int UnregisterAllOf(object owner) => Hub.UnregisterAllOf(owner);

        /// <summary>注销全部服务（逆创建序释放）：回登录 / 退出游戏时用。返回注销个数。</summary>
        public static int UnregisterAll() => Hub.UnregisterAll();

        // ============================================================
        // 驱动 / 诊断
        // ============================================================

        /// <summary>
        /// 每帧驱动（实现了 <see cref="RevITickable"/> 的服务）。<b>框架会自动挂到每帧回调上，业务不用管</b>；
        /// 工程外测试可以手动调它来验证。
        /// </summary>
        public static void Tick(float deltaTime) => Hub.Tick(deltaTime, onError: null);

        /// <summary>已注册的服务个数（调试用）。</summary>
        public static int Count => Hub.Count;

        /// <summary>把当前注册情况打成一段文字：哪些已创建、哪些没创建、谁注册的（排查"谁还占着内存"用）。</summary>
        public static string Dump() => Hub.Describe();

        // ============================================================
        // 内部
        // ============================================================

        /// <summary>
        /// 首次注册时确保"进 Play 复位 + 每帧驱动"已经就位（Support 层补）。
        /// 内核是纯 C#，不知道 Unity；这里只喊一声，具体怎么挂交给 Support\RevServicesUnityHooks。
        /// </summary>
        private static void EnsureHooks() => RevServicesBridge.EnsureInstalled?.Invoke();

        /// <summary>进 Play / 换工程时复位（由 Support\RevServicesUnityHooks 调用，业务不用管）。</summary>
        internal static void ResetForNewSession() => Hub.ResetForNewSession();
    }

    /// <summary>纯 C# 内核与 Unity 宿主之间的接缝（Support 层启动时装上，业务不用管）。</summary>
    internal static class RevServicesBridge
    {
        /// <summary>确保每帧驱动已挂上（没有 Unity 宿主时为 null，注册照常可用，只是不会自动 Tick）。</summary>
        internal static Action EnsureInstalled;
    }
}

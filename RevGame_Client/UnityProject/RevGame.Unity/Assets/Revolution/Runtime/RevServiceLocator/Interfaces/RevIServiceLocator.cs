// RevIServiceLocator.cs —— 取服务的只读契约（底层模块只依赖它，不依赖具体容器）
//
// 【为什么要有这个接口】王者那套的核心动机是"底层框架要调上层能力，但程序集依赖只能单向"：
//   底层只认接口 → 上层把实现注册进来 → 底层运行时就能调到上层。
//   把"取服务"这一个动作收成 3 个方法，底层模块（比如 UI、动作序列）只需要拿到这个接口，
//   至于上层用的是什么容器、怎么装配、有几个作用域，底层完全不需要知道。
//
// 【三个方法的语义分得很清楚，不要混用】
//   GetRequired<T>() —— "必须有"：没注册 / 没创建就抛异常（把'忘了注册'变成启动期就能看到的问题）
//   Get<T>()         —— "可能有"：拿不到返回 null（可选能力 / 裁剪版本 / 编辑器里没装的东西）
//   TryGet<T>()      —— "可能有"且想要 bool + out（用在 if 里最顺）
//   （王者有 SafeGet / Get / ContainSystem / GetSystems 五个近义入口，是它的主要上手成本之一，这里刻意只留 3 个）

namespace Revolution
{
    /// <summary>
    /// 服务定位器的<b>只读</b>契约：只负责"取服务"，不提供注册。
    /// <para>底层模块（UI 框架、动作序列、资源系统……）依赖这个接口即可，实现可替换、可伪造、可多实例。</para>
    /// </summary>
    public interface RevIServiceLocator
    {
        /// <summary>
        /// 取服务（必须有）：没注册就抛 <see cref="InvalidOperationException"/>。
        /// <para>用在"没有它就跑不起来"的依赖上 —— 让装配漏项在启动期暴露，而不是运行期某处空引用。</para>
        /// </summary>
        T GetRequired<T>() where T : class;

        /// <summary>
        /// 取服务（可能有）：没注册返回 <c>null</c>。
        /// <para>用在可选能力上（例如"没接声音系统时就不播声音"）。</para>
        /// </summary>
        T Get<T>() where T : class;

        /// <summary>尝试取服务：拿到返回 true，否则 false 且 <paramref name="service"/> 为 null。</summary>
        bool TryGet<T>(out T service) where T : class;
    }
}

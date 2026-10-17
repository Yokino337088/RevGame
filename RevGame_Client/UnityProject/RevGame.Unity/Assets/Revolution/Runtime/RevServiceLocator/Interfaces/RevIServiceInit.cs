// RevIServiceInit.cs —— 可选钩子①：服务创建完、可以开始用别人了
//
// 【什么时候需要它】服务的构造函数里不要取别的服务（构造期容器还没把它放进表里，互相取会打转），
//   要依赖别人就在 OnInit 里取 —— 这是"构造只负责自己、装配交给框架"的分工。
//
// 【调用时机】服务实例创建后立刻调用一次（Singleton：第一次被取用时；Scoped：该作用域第一次取用时）。
// 【失败行为】OnInit 抛异常 → 框架会把这个实例从表里摘掉（不会留下半初始化的坏实例），异常继续抛给调用方。

namespace Revolution
{
    /// <summary>
    /// 服务初始化钩子（可选实现）：想在自己被创建后取用别的服务，就实现它。
    /// <code>
    /// public sealed class SoundService : ISoundService, RevIServiceInit
    /// {
    ///     private IConfigService _config;
    ///     public void OnInit(RevIServiceLocator services) => _config = services.GetRequired&lt;IConfigService&gt;();
    /// }
    /// </code>
    /// </summary>
    public interface RevIServiceInit
    {
        /// <summary>服务创建完成后调用一次；参数是"取服务的入口"，可继续取其它服务。</summary>
        void OnInit(RevIServiceLocator services);
    }
}

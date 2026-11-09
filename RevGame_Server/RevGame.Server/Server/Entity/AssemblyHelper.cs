using System.Runtime.Loader;
using Fantasy.Helper;

namespace Fantasy
{
    /// <summary>启动时加载 Entity 和 Hotfix，使 Fantasy 能发现两层程序集里由源生成器生成的注册信息。</summary>
    public static class AssemblyHelper
    {
        /// <summary>逻辑程序集的文件名主体，DLL 和调试符号共用这个名称。</summary>
        private const string HotfixDll = "Hotfix";
        /// <summary>保存当前 Hotfix 的可卸载加载上下文，首次启动时为空。</summary>
        private static AssemblyLoadContext? _assemblyLoadContext = null;

        /// <summary>先准备稳定的数据类型，再加载引用这些类型的业务逻辑。由服务端入口调用。</summary>
        public static void Initialize()
        {
            LoadEntityAssembly();
            LoadHotfixAssembly();
        }

        /// <summary>通过本类所在程序集触发 Entity 的生成注册入口。</summary>
        private static void LoadEntityAssembly()
        {
            // .NET 运行时采用延迟加载机制，如果代码中不使用程序集的类型，程序集不会被加载
            // 执行一下，触发运行时强制加载从而自动注册到框架中
            // 因为AssemblyHelper代码在Entity项目里，所以需要获取这个项目的Assembly
            // 然后调用EnsureLoaded方法强制加载一下
            typeof(AssemblyHelper).Assembly.EnsureLoaded();
        }

        /// <summary>从运行目录读取 Hotfix 与符号并注册，返回本次加载的程序集。再次调用时先请求卸载上一次的上下文。</summary>
        public static System.Reflection.Assembly LoadHotfixAssembly()
        {
            // Unload 只是请求卸载，旧上下文要等引用释放并被 GC 回收后才会消失。
            if (_assemblyLoadContext != null)
            {
                _assemblyLoadContext.Unload();
                System.GC.Collect();
            }

            // 第二个参数允许卸载，给下一次重新加载保留回收入口。
            _assemblyLoadContext = new AssemblyLoadContext(HotfixDll, true);
            // 从程序旁读取字节再加载，运行时无需一直打开磁盘文件。缺少构建产物会由启动入口报告。
            var dllBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"{HotfixDll}.dll"));
            var pdbBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"{HotfixDll}.pdb"));
            var assembly = _assemblyLoadContext.LoadFromStream(new MemoryStream(dllBytes), new MemoryStream(pdbBytes));
            // 强制触发 ModuleInitializer 执行
            // AssemblyLoadContext.LoadFromStream 只加载程序集到内存，不会自动触发 ModuleInitializer
            // 必须访问程序集中的类型才能触发初始化，这里通过反射调用生成的 AssemblyMarker
            // 注意：此方法仅用于热重载场景（JIT），Native AOT 不支持动态加载
            // 拿到Assembly就用EnsureLoaded()方法强制触发
            assembly.EnsureLoaded();
            return assembly;
        }
    }
}

// ================================================================================
// Fantasy.Net 服务器应用程序入口
// ================================================================================
// 本文件是 Fantasy.Net 分布式游戏服务器的主入口点
//
// 初始化流程：
//   1. 强制加载引用程序集，触发 ModuleInitializer 执行
//   2. 配置日志基础设施（控制台日志）
//   3. 启动 Fantasy.Net 框架
// ================================================================================

using System.Net.Sockets;
using Fantasy;

try
{
    // 初始化引用的程序集，确保 ModuleInitializer 执行
    // .NET 采用延迟加载机制 - 仅当类型被引用时才加载程序集
    // 通过访问 AssemblyMarker 强制加载程序集并调用 ModuleInitializer
    // 注意：Native AOT 不存在延迟加载问题，所有程序集在编译时打包
    AssemblyHelper.Initialize();
    // 使用项目彩色ILog启动；业务仍走Fantasy.Log统一出口
    // Entry 读取可执行产物旁的 Fantasy.config、创建 Scene 和监听端口，并运行服务端主循环。
    await Fantasy.Platform.Net.Entry.Start(new GameConsoleLog());
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
{
    // 预检与正式绑定之间仍可能有别的进程抢占端口，保留失败退出码供脚本判断。
    Console.Error.WriteLine($"Fantasy 绑定端口时发现地址已占用（{ex.NativeErrorCode}）。请检查上方监听地址，或用启动脚本指定空闲端口。");
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    // 保留完整异常链，部署日志可以定位配置、依赖或初始化失败的实际位置。
    Console.Error.WriteLine($"服务器初始化过程中发生致命错误：{ex}");
    // 启动失败时返回非零状态，供启动脚本和自动化判定失败。
    Environment.Exit(1);
}

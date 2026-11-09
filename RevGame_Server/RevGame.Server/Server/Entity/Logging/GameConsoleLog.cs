using Fantasy.Platform.Net;

namespace Fantasy;

/// <summary>
/// 项目ILog适配：业务仍只调用Fantasy.Log；按PVP事件上色，统一时间与Scene前缀。
/// 锁保护颜色与整行输出，finally恢复原色；输出重定向时仍保留可检索事件标签。
/// 不接收令牌/Key，不记录心跳正文，避免把高频保活当业务日志刷屏。
/// </summary>
public sealed class GameConsoleLog : ILog
{
    /// <summary>保护进程共享的控制台颜色和整行输出，各 Scene 并发打日志时使用同一把锁。</summary>
    private static readonly object OutputLock = new();
    /// <summary>Fantasy 传入的进程标识，放在日志前缀中区分多进程实例。</summary>
    private string _appId = "";
    /// <summary>框架初始化日志时调用。当前适配器缓存进程标识，各启动模式共用输出格式。</summary>
    public void Initialize(string appId, ProcessMode processMode)
    {
        _appId = appId;
    }
    /// <summary>输出服务端级细粒度跟踪日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Trace(string message)
    {
        Write("Trace", "Server", message);
    }
    /// <summary>输出带 Scene 名称的细粒度跟踪日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Trace(string sceneName, string message)
    {
        Write("Trace", sceneName, message);
    }
    /// <summary>填充消息模板并输出服务端级细粒度跟踪日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Trace(string message, params object[] args)
    {
        Write("Trace", "Server", string.Format(message, args));
    }
    /// <summary>填充消息模板并输出带 Scene 名称的细粒度跟踪日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Trace(string sceneName, string message, params object[] args)
    {
        Write("Trace", sceneName, string.Format(message, args));
    }
    /// <summary>输出服务端级开发排查日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Debug(string message)
    {
        Write("Debug", "Server", message);
    }
    /// <summary>输出带 Scene 名称的开发排查日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Debug(string sceneName, string message)
    {
        Write("Debug", sceneName, message);
    }
    /// <summary>填充消息模板并输出服务端级开发排查日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Debug(string message, params object[] args)
    {
        Write("Debug", "Server", string.Format(message, args));
    }
    /// <summary>填充消息模板并输出带 Scene 名称的开发排查日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Debug(string sceneName, string message, params object[] args)
    {
        Write("Debug", sceneName, string.Format(message, args));
    }
    /// <summary>输出服务端级常规运行日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Info(string message)
    {
        Write("Info", "Server", message);
    }
    /// <summary>输出带 Scene 名称的常规运行日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Info(string sceneName, string message)
    {
        Write("Info", sceneName, message);
    }
    /// <summary>填充消息模板并输出服务端级常规运行日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Info(string message, params object[] args)
    {
        Write("Info", "Server", string.Format(message, args));
    }
    /// <summary>填充消息模板并输出带 Scene 名称的常规运行日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Info(string sceneName, string message, params object[] args)
    {
        Write("Info", sceneName, string.Format(message, args));
    }
    /// <summary>输出服务端级可恢复异常日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Warning(string message)
    {
        Write("Warning", "Server", message);
    }
    /// <summary>输出带 Scene 名称的可恢复异常日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Warning(string sceneName, string message)
    {
        Write("Warning", sceneName, message);
    }
    /// <summary>填充消息模板并输出服务端级可恢复异常日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Warning(string message, params object[] args)
    {
        Write("Warning", "Server", string.Format(message, args));
    }
    /// <summary>填充消息模板并输出带 Scene 名称的可恢复异常日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Warning(string sceneName, string message, params object[] args)
    {
        Write("Warning", sceneName, string.Format(message, args));
    }
    /// <summary>输出服务端级错误日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Error(string message)
    {
        Write("Error", "Server", message);
    }
    /// <summary>输出带 Scene 名称的错误日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Error(string sceneName, string message)
    {
        Write("Error", sceneName, message);
    }
    /// <summary>填充消息模板并输出服务端级错误日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Error(string message, params object[] args)
    {
        Write("Error", "Server", string.Format(message, args));
    }
    /// <summary>填充消息模板并输出带 Scene 名称的错误日志，统一交给 Write 添加前缀和颜色。</summary>
    public void Error(string sceneName, string message, params object[] args)
    {
        Write("Error", sceneName, string.Format(message, args));
    }
    /// <summary>颜色只由固定事件前缀决定，不能由玩家昵称中的控制字符决定。</summary>
    private void Write(string level, string sceneName, string message)
    {
        // 先按严重程度选默认色，已知 PvP 事件再按固定分类覆盖。
        ConsoleColor color = level == "Error" ? ConsoleColor.Red : level == "Warning" ? ConsoleColor.Yellow : ConsoleColor.Gray;
        if (message.StartsWith("[PVP/匹配]", StringComparison.Ordinal))
        {
            color = ConsoleColor.Cyan;
        }
        else if (message.StartsWith("[PVP/房间]", StringComparison.Ordinal))
        {
            color = ConsoleColor.Magenta;
        }
        else if (message.StartsWith("[PVP/落子]", StringComparison.Ordinal))
        {
            color = ConsoleColor.Green;
        }
        else if (message.StartsWith("[PVP/结果]", StringComparison.Ordinal))
        {
            color = ConsoleColor.Yellow;
        }
        else if (message.StartsWith("[PVP/连接]", StringComparison.Ordinal))
        {
            color = ConsoleColor.Blue;
        }
        // 改颜色、写一行和恢复颜色必须成组执行，避免另一个 Scene 插进来。
        lock (OutputLock)
        {
            ConsoleColor previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{_appId}/{sceneName}] [{level}] {message}");
            }
            finally
            {
                // 输出抛异常也要恢复全局颜色，后续日志仍能正常显示。
                Console.ForegroundColor = previous;
            }
        }
    }
}

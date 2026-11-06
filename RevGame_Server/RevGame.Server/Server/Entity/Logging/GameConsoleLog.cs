using Fantasy.Platform.Net;

namespace Fantasy;

/// <summary>
/// 项目ILog适配：业务仍只调用Fantasy.Log；按PVP事件上色，统一时间与Scene前缀。
/// 锁保护颜色与整行输出，finally恢复原色；输出重定向时仍保留可检索事件标签。
/// 不接收令牌/Key，不记录心跳正文，避免把高频保活当业务日志刷屏。
/// </summary>
public sealed class GameConsoleLog : ILog
{
    private static readonly object OutputLock = new();
    private string _appId = "";
    public void Initialize(string appId, ProcessMode processMode)
    {
        _appId = appId;
    }
    public void Trace(string message)
    {
        Write("Trace", "Server", message);
    }
    public void Trace(string sceneName, string message)
    {
        Write("Trace", sceneName, message);
    }
    public void Trace(string message, params object[] args)
    {
        Write("Trace", "Server", string.Format(message, args));
    }
    public void Trace(string sceneName, string message, params object[] args)
    {
        Write("Trace", sceneName, string.Format(message, args));
    }
    public void Debug(string message)
    {
        Write("Debug", "Server", message);
    }
    public void Debug(string sceneName, string message)
    {
        Write("Debug", sceneName, message);
    }
    public void Debug(string message, params object[] args)
    {
        Write("Debug", "Server", string.Format(message, args));
    }
    public void Debug(string sceneName, string message, params object[] args)
    {
        Write("Debug", sceneName, string.Format(message, args));
    }
    public void Info(string message)
    {
        Write("Info", "Server", message);
    }
    public void Info(string sceneName, string message)
    {
        Write("Info", sceneName, message);
    }
    public void Info(string message, params object[] args)
    {
        Write("Info", "Server", string.Format(message, args));
    }
    public void Info(string sceneName, string message, params object[] args)
    {
        Write("Info", sceneName, string.Format(message, args));
    }
    public void Warning(string message)
    {
        Write("Warning", "Server", message);
    }
    public void Warning(string sceneName, string message)
    {
        Write("Warning", sceneName, message);
    }
    public void Warning(string message, params object[] args)
    {
        Write("Warning", "Server", string.Format(message, args));
    }
    public void Warning(string sceneName, string message, params object[] args)
    {
        Write("Warning", sceneName, string.Format(message, args));
    }
    public void Error(string message)
    {
        Write("Error", "Server", message);
    }
    public void Error(string sceneName, string message)
    {
        Write("Error", sceneName, message);
    }
    public void Error(string message, params object[] args)
    {
        Write("Error", "Server", string.Format(message, args));
    }
    public void Error(string sceneName, string message, params object[] args)
    {
        Write("Error", sceneName, string.Format(message, args));
    }
    /// <summary>颜色只由固定事件前缀决定，不能由玩家昵称中的控制字符决定。</summary>
    private void Write(string level, string sceneName, string message)
    {
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
                Console.ForegroundColor = previous;
            }
        }
    }
}

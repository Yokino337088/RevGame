// ============================================================
// RevLogDefines.cs —— 级别、条目、上限（纯 C#）
//
// 位置：Runtime\RevLog\Core\
//
// 【★ 级别就是成本契约】这不是"严重程度分类"，而是**调用成本**的契约：
//   Debug   —— 零成本：调用点连同参数表达式被编译器删掉（[Conditional]，只在编辑器/显式开宏时存在）
//   Info    —— 便宜：不采堆栈，正式包也能留
//   Warn    —— 便宜：同上（用法可疑但还能跑）
//   Error   —— 贵一点：默认**采堆栈**（本地栈是微秒级，只在真的出错时才付）
//   Exception —— 最贵：采堆栈 + 可触发上报（RevLog.OnReport，默认不接 = 不上报）
//
//   王者原版把这件事写成"源码注释三遍强调使用纪律"——因为用错级别的代价是隐藏成本。
//   这里换成**结构性保障**：贵信息（堆栈）只在 Error 及以上采，业务不用记纪律。
//
// 【为什么用 [Conditional] 而不是 if (level >= min)】
//   运行时判断的代价已经付过了：`RevLog.Debug("x" + Heavy())` 里的拼接和 ToString 已经执行、GC 已经产生。
//   编译期删除连参数求值都不发生 —— 这是"正式包零开销"的唯一办法（王者哲学一）。
//   ★ 注意：[Conditional] 按**调用方所在程序集**的编译符号判定：业务工程想要 Debug 日志，
//     在自己的 Player Settings 里加 REVLOG_DEBUG 定义即可（框架内部默认编辑器可见）。
//
// 【缺失值用 -1，不用 0】帧号 0 是合法帧，"没采集"必须是 -1（王者系统乙的这条经验写进了这里）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>日志级别。<see cref="Debug"/> 在正式包里不存在（编译期删除），其余四个都在。</summary>
    public enum RevLogLevel
    {
        /// <summary>调试（[Conditional]：正式包零成本，连参数求值都不发生）。</summary>
        Debug = 0,

        /// <summary>普通信息（正式包保留；不采堆栈）。</summary>
        Info = 1,

        /// <summary>告警：用法可疑但还能跑。</summary>
        Warn = 2,

        /// <summary>错误：必须修（默认采堆栈）。</summary>
        Error = 3,

        /// <summary>异常：带 Exception 对象（堆栈总是采）+ 可触发上报。</summary>
        Exception = 4,
    }

    /// <summary>
    /// 一条日志（值类型：进环形缓冲不装箱）。
    /// 字段刻意做成只读 —— 日志一旦产生就是"现场证据"，不该被任何消费者改写。
    /// </summary>
    public readonly struct RevLogEntry
    {
        public readonly RevLogLevel Level;
        public readonly string Tag;
        public readonly string Message;

        /// <summary>堆栈（按级别采：Error/Exception 才有；其余为 null）。</summary>
        public readonly string Stack;

        /// <summary>产生时刻（本地时间）。</summary>
        public readonly DateTime Time;

        /// <summary>Unity 渲染帧号（-1 = 未采集，例如纯 C# 环境）。</summary>
        public readonly int Frame;

        /// <summary>被抑制的重复次数（1 = 没重复；&gt;1 表示"上一行重复了 N 次"）。</summary>
        public readonly int Repeat;

        /// <summary>
        /// 原始异常对象（只有 Exception 级别有；其余为 null）。
        /// ★ 带着它是为了 Unity 控制台能给出**可点击跳转的堆栈**，以及上报出口能拿到真异常 ——
        ///   不要在环形缓冲里长期持有：条目被覆盖时引用自然释放。
        /// </summary>
        public readonly Exception Error;

        internal RevLogEntry(RevLogLevel level, string tag, string message, string stack, DateTime time, int frame, int repeat, Exception error)
        {
            Level = level;
            Tag = tag;
            Message = message;
            Stack = stack;
            Time = time;
            Frame = frame;
            Repeat = repeat;
            Error = error;
        }

        /// <summary>级别单字母（写文件用：D/I/W/E/X）。</summary>
        public char LevelChar => Level switch
        {
            RevLogLevel.Debug => 'D',
            RevLogLevel.Info => 'I',
            RevLogLevel.Warn => 'W',
            RevLogLevel.Error => 'E',
            _ => 'X',
        };

        /// <summary>一行文本（控制台/文件/面板都够用）。</summary>
        public override string ToString()
        {
            string repeat = Repeat > 1 ? $"（重复 {Repeat} 次）" : "";
            string frame = Frame >= 0 ? $" frame:{Frame}" : "";
            return $"{Time:HH:mm:ss.fff} {LevelChar} [{Tag}] {Message}{repeat}{frame}";
        }
    }

    /// <summary>默认值集中在这里（全部可改，但都有硬上限 —— "一切有上限"）。</summary>
    public static class RevLogLimits
    {
        /// <summary>环形缓冲容量（最近日志，给"出事前发生了什么"与将来的日志面板用）。</summary>
        public static int RingCapacity = 2048;

        /// <summary>默认 tag（没传 tag 时用）。</summary>
        public const string DefaultTag = "General";

        /// <summary>写文件时的单文件上限（字节，默认 4MB；超过就轮转，避免单个文件撑爆磁盘）。</summary>
        public const long FileSizeLimit = 4L * 1024 * 1024;

        /// <summary>文件保留份数（超过就删最旧的）。</summary>
        public const int FileKeepCount = 10;

        /// <summary>异常上报的默认开关（不接 <see cref="RevLog.OnReport"/> 时不上报）。</summary>
        public const bool ReportByDefault = false;
    }
}

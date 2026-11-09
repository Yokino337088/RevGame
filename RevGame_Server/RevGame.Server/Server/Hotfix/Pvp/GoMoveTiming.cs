using System.Diagnostics;
using System.Globalization;

namespace Fantasy;

/// <summary>本机压测选配的单 Gate 采样。先在内存记录，最后一手回包入队后再落盘。</summary>
internal sealed class GoMoveTiming
{
    /// <summary>压测采样文件路径，由启动环境提供。环境变量缺失时不启用采样。</summary>
    private static readonly string? OutputPath = Environment.GetEnvironmentVariable("REVGO_MOVE_TRACE");
    /// <summary>进程内缓存的 CSV 行，用于把文件写入推迟到采样结束。</summary>
    private static readonly List<string>? Samples = OutputPath == null ? null : new(17000);
    /// <summary>写文件前需要收集的样本条数，由压测环境指定。</summary>
    private static readonly int Target = int.TryParse(Environment.GetEnvironmentVariable("REVGO_MOVE_TRACE_COUNT"), out int count) ? count : 17000;
    /// <summary>采样期的跨场景批次数和观测到的最大批长。</summary>
    private static int _batches, _maxBatch;

    /// <summary>Handler 建立采样对象时的 Stopwatch 时间戳，单位为计数器刻度。</summary>
    public readonly long Received = Stopwatch.GetTimestamp();
    /// <summary>分别记录发起路由、收到校验结果、业务日志前后和状态推送入队后的时间戳。</summary>
    public long Routed, Returned, LogStart, LogEnd, PushEnd;

    /// <summary>采样打开且还未达到目标时，为当前操作创建记录，否则返回空。</summary>
    public static GoMoveTiming? Start()
    {
        return Samples != null && Samples.Count < Target ? new GoMoveTiming() : null;
    }

    /// <summary>记录一次跨场景请求合并了多少手棋，用于统计合批规模。</summary>
    public static void CountBatch(int size)
    {
        if (Samples != null && Samples.Count < Target)
        {
            _batches++;
            _maxBatch = Math.Max(_maxBatch, size);
        }
    }

    /// <summary>回包入队后追加本手的时间戳。收满目标条数时把内存中的样本一次写出。</summary>
    public void Record(string room, int sequence)
    {
        if (Samples == null || Samples.Count >= Target)
        {
            return;
        }
        // 每列保留原始刻度，离线分析时用 Stopwatch.Frequency 换算为耗时。
        long replyEnd = Stopwatch.GetTimestamp();
        Samples.Add(string.Create(CultureInfo.InvariantCulture, $"{room},{sequence},{Received},{Routed},{Returned},{LogStart},{LogEnd},{PushEnd},{replyEnd}"));
        if (Samples.Count == Target)
        {
            // Samples 已启用意味着路径存在于配置中，最后一个样本负责写出本批记录。
            File.WriteAllLines(OutputPath!, Samples);
            Log.Info($"[PVP/计时] 采样={Target} 跨场景批数={_batches} 最大批={_maxBatch}");
        }
    }
}

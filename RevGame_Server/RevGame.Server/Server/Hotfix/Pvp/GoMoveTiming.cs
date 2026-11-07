using System.Diagnostics;
using System.Globalization;

namespace Fantasy;

/// <summary>本机压测选配的单 Gate 采样。先在内存记录，最后一手回包入队后再落盘。</summary>
internal sealed class GoMoveTiming
{
    private static readonly string? OutputPath = Environment.GetEnvironmentVariable("REVGO_MOVE_TRACE");
    private static readonly List<string>? Samples = OutputPath == null ? null : new(17000);
    private static readonly int Target = int.TryParse(Environment.GetEnvironmentVariable("REVGO_MOVE_TRACE_COUNT"), out int count) ? count : 17000;
    private static int _batches, _maxBatch;

    public readonly long Received = Stopwatch.GetTimestamp();
    public long Routed, Returned, LogStart, LogEnd, PushEnd;

    public static GoMoveTiming? Start()
    {
        return Samples != null && Samples.Count < Target ? new GoMoveTiming() : null;
    }

    public static void CountBatch(int size)
    {
        if (Samples != null && Samples.Count < Target)
        {
            _batches++;
            _maxBatch = Math.Max(_maxBatch, size);
        }
    }

    public void Record(string room, int sequence)
    {
        if (Samples == null || Samples.Count >= Target)
        {
            return;
        }
        long replyEnd = Stopwatch.GetTimestamp();
        Samples.Add(string.Create(CultureInfo.InvariantCulture, $"{room},{sequence},{Received},{Routed},{Returned},{LogStart},{LogEnd},{PushEnd},{replyEnd}"));
        if (Samples.Count == Target)
        {
            File.WriteAllLines(OutputPath!, Samples);
            Log.Info($"[PVP/计时] 采样={Target} 跨场景批数={_batches} 最大批={_maxBatch}");
        }
    }
}

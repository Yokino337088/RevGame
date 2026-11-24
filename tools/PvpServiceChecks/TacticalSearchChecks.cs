using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RevGame.Pvp;

/// <summary>用正式棋核和战术评分计算复跑固定局面，核对提示、诱敌和源棋盘快照。</summary>
internal static class TacticalSearchChecks
{
    /// <summary>每份基准样本预热调用数，给 JIT 和工作线程留出准备时间。</summary>
    private const int Warmups = 20;
    /// <summary>每种路径、每个局面的实测调用数。</summary>
    private const int Samples = 120;
    /// <summary>用于统计慢调用的 60 Hz 单帧预算，单位毫秒，独立搜索耗时并不等于整帧耗时。</summary>
    private const double FrameBudgetMs = 1000d / 60;

    /// <summary>建立隔离的固定棋局，并附上 697409a7 原版搜得的完整排序和诱敌输出。</summary>
    private static (string Name, GoCore Position, string Expected)[] CreatePositions()
    {
        var opening = GoCore.CreateMatch(15);
        Play(opening, 4, 4);

        var middle = GoCore.CreateMatch(15);
        uint generator = 20261124;
        for (int turn = 0; turn < 34; turn++)
        {
            // 固定的一串候选点保证每次得到同一手棋。始终用真实落子入口筛掉占点、自杀和超级劫。
            generator = unchecked(generator * 1664525u + 1013904223u);
            int start = (int)(generator % GoCore.PointCount);
            bool played = false;
            for (int offset = 0; offset < GoCore.PointCount; offset++)
            {
                int point = (start + offset) % GoCore.PointCount;
                if (middle.TryPlay(point % GoCore.BoardSize, point / GoCore.BoardSize, out _))
                {
                    played = true;
                    break;
                }
            }
            Check(played, "固定中盘应能下完 34 手");
        }

        var late = GoCore.CreateMatch(15);
        // 双方在棋盘两侧各填一块，保留中间共用的气。局面距 68 手终局只差一步。
        for (int turn = 0; turn < GoCore.MatchPlacementLimit - 1; turn++)
        {
            int point = turn / 2;
            int x = turn % 2 == 0 ? point % 4 : 8 - point % 4;
            Play(late, x, point / 4);
        }
        var finished = late.Clone();
        Play(finished, 7, 8);
        Check(finished.Phase == GoPhase.Finished, "用终局最后一手覆盖没有建议的分支");

        var ko = new GoCore(15);
        foreach (var (x, y) in new[] { (0, 1), (1, 1), (1, 0), (2, 0), (1, 2), (3, 1), (8, 8), (2, 2), (2, 1) })
        {
            Play(ko, x, y);
        }
        var resumed = ko.Clone();
        Check(resumed.TryPass(GoSide.White, out _) && resumed.TryPass(GoSide.Black, out _)
            && resumed.TryResume(GoSide.Black, out _), "双方停一手后恢复，保留劫历史");
        const string koExpected = "8,7,-61,|7,8,-61,|3,0,-69,|3,2,-78,|6,2,-80,|5,3,-80,|6,3,-80,|4,4,-80,|5,4,-80,|6,4,-80,|/3,2,-78,有反制";
        return new[]
        {
            ("opening", opening, "2,2,-38,|3,2,-38,|5,2,-38,|6,2,-38,|2,3,-38,|6,3,-38,|4,2,-40,|3,3,-40,|4,3,-40,|5,3,-40,|/0,0,0,"),
            ("middle", middle, "2,3,-73,|0,1,-74,|0,2,-74,|5,3,-74,|8,1,-74,|3,1,-75,|4,3,-77,|4,4,-77,|5,5,-77,|3,3,-78,|/0,0,0,"),
            ("late", late, "4,8,336,|5,8,304,|6,8,272,|4,0,240,|4,1,240,|4,2,240,|4,3,240,|4,4,240,|4,5,240,|4,6,240,|/5,8,304,有反制"),
            ("finished", finished, "/0,0,0,"),
            ("ko", ko, koExpected),
            ("resumed", resumed, koExpected)
        };
    }

    /// <summary>沿生产入口提交固定坐标，位置或规则变更时立即报出夹具失败。</summary>
    private static void Play(GoCore position, int x, int y)
    {
        Check(position.TryPlay(x, y, out GoPlayResult result), "固定棋谱落子失败 " + x + "," + y + " " + result.Error);
    }

    /// <summary>未通过立即失败，防止把无意义的输入或结果用作比较依据。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>复跑固定局面，核对旧版排序、并行排序、诱敌和源棋盘，再检查安卓热态与构建隔离。</summary>
    internal static void Run()
    {
        var positions = CreatePositions();
        bool sawParallel = false;
        int checks = CheckAndroidTrialPolicy();
        foreach (var indices in new[] { new[] { 0, 1, 2, 3, 0 }, new[] { 4, 5, 4 } })
        {
            // 同规格的搜索对象常驻并复用。先走密盘和终局，再回头读稀疏盘，防止复用工作区留下旧评分。
            GoCore first = positions[indices[0]].Position;
            var serial = new GoTacticalSearch(first, 1);
            var parallel = new GoTacticalSearch(first, 4);
            foreach (int index in indices)
            {
                var (name, position, expected) = positions[index];
                foreach (int capacity in new[] { 10, 3, 1 })
                {
                    // 电脑只读取前三，诱敌入口只读取第一。覆盖三种缓冲长度，避免只看第一步掩盖后续排序变动。
                    var singleHints = new GoHint[capacity];
                    var multiHints = new GoHint[capacity];
                    ulong before = position.StateHash;
                    int singleCount = serial.Rank(position, singleHints, out GoHint singleBait);
                    int multiCount = parallel.Rank(position, multiHints, out GoHint multiBait);
                    sawParallel |= parallel.LastPartitionCount > 1;

                    Check(singleCount == multiCount, name + " 候选数量不同");
                    Check(SameHint(singleBait, multiBait), name + " 诱敌选择不同");
                    Check(position.StateHash == before, name + " 搜索不得改变棋面、历史或轮次");
                    Check(serial.LastPartitionCount == 1, name + " 强制串行时只使用一组");
                    checks += 4;
                    if (index == 2 || index == 3)
                    {
                        Check(parallel.LastPartitionCount == 1, name + " 即将数子或已终局的轻量路径应直接返回或串行算完");
                        checks++;
                    }
                    for (int i = 0; i < singleCount; i++)
                    {
                        Check(SameHint(singleHints[i], multiHints[i]), name + " 候选顺序或分数不一致 #" + i);
                        Check(position.TryPreview(position.Turn, multiHints[i].X, multiHints[i].Y, out _), name + " 返回了非法候选");
                        checks += 2;
                    }
                    if (capacity == 10)
                    {
                        Check(Signature(multiHints, multiCount, multiBait) == expected, name + " 与旧版完整排序或诱敌不一致");
                        checks++;
                    }
                }
            }
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        checks += CheckAndroidBuild(positions[0].Position, positions[0].Expected);
#elif UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        if (Environment.ProcessorCount > 1)
        {
            Check(sawParallel, "机器有可用核心时，原生测试应实际走到多分片");
            checks++;
        }
#endif
        Console.WriteLine("PASS: " + checks + " tactical assertions; exercised parallel partitions: " + sawParallel + ".");
    }

    /// <summary>用固定输入核对设备组数上限和退回单组的触发条件，不读取机器热状态。</summary>
    private static int CheckAndroidTrialPolicy()
    {
        (int Requested, int Thermal, bool PowerSave, bool Focused, bool ThermallyReady,
            int Processors, int Expected, string Reason)[] cases =
        {
            (1, 0, false, true, true, 8, 1, "主动选择单组"),
            (2, 0, false, true, true, 8, 2, "冷态请求两组"),
            (4, 0, false, true, true, 8, 4, "冷态请求四组"),
            (4, 1, false, true, true, 8, 2, "轻度发热最多两组"),
            (2, 1, false, true, true, 8, 2, "轻度发热保留两组"),
            (4, 2, false, true, true, 8, 1, "中度热状态退回单组"),
            (4, 3, false, true, true, 8, 1, "严重热状态退回单组"),
            (4, -1, false, true, true, 8, 1, "热状态未知退回单组"),
            (4, 0, true, true, true, 8, 1, "省电模式退回单组"),
            (4, 0, false, false, true, 8, 1, "失焦退回单组"),
            (4, 0, false, true, false, 8, 1, "冷却观察未完成时保持单组"),
            (4, 0, false, true, true, 4, 4, "设备恰好能容纳四组"),
            (4, 0, false, true, true, 3, 2, "设备余量只够两组"),
            (4, 1, false, true, true, 1, 1, "轻度发热且只有一个处理器"),
            (4, 0, false, true, true, 0, 1, "处理器数量不可用"),
            (3, 0, false, true, true, 8, 1, "无效组数退回单组"),
            (8, 0, false, true, true, 8, 1, "超出测试组数范围")
        };
        foreach (var input in cases)
        {
            int partitions = GoTacticalSearch.ResolveAndroidTrialPartitions(input.Requested, input.Thermal,
                input.PowerSave, input.Focused, input.ThermallyReady, input.Processors);
            Check(partitions == input.Expected, input.Reason + "，实际组数=" + partitions);
        }
        return cases.Length;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>用同一盘和同一个搜索器模拟热态变化，检查开发版切组和正式版串行隔离。</summary>
    private static int CheckAndroidBuild(GoCore position, string expected)
    {
        var normal = new GoTacticalSearch(position);
        var trial = new GoTacticalSearch(position, 4);
        var hints = new GoHint[10];
        ulong original = position.StateHash;
        normal.Rank(position, hints, out _);
        Check(normal.LastPartitionCount == 1, "安卓常规构造保持单组");

        // 模拟从冷态升温、回温后的观察期，以及观察通过后的下一次搜索。
        (int Requested, int Thermal, bool Ready, int Expected)[] steps =
        {
            (1, 0, true, 1), (2, 0, true, 2), (4, 0, true, 4), (4, 1, true, 2),
            (4, 2, true, 1), (4, 0, false, 1), (4, 0, true, 4)
        };
        foreach (var step in steps)
        {
            int limit = GoTacticalSearch.ResolveAndroidTrialPartitions(step.Requested, step.Thermal,
                powerSave: false, focused: true, thermallyReady: step.Ready, processorCount: 8);
#if DEVELOPMENT_BUILD || REV_AUTOMATION
            int groups = step.Expected;
#else
            const int groups = 1;
#endif
            int count = trial.Rank(position, hints, out GoHint bait, partitionLimit: limit);
            Check(trial.LastPartitionCount == groups, "安卓请求 " + step.Requested + " 组，热状态 " + step.Thermal
                + "，预期 " + groups + " 组，实际 " + trial.LastPartitionCount + " 组");
            Check(Signature(hints, count, bait) == expected, "安卓切组前后完整候选和诱敌结果一致");
            Check(position.StateHash == original, "安卓切组试算不修改实盘");
        }
#if DEVELOPMENT_BUILD || REV_AUTOMATION
        Console.WriteLine("PASS: Android dev search 1→2→4→2→1→1→4 groups, default group=1.");
#else
        Console.WriteLine("PASS: Android release keeps one group for default and explicit search requests.");
#endif
        return 1 + steps.Length * 3;
    }
#endif

    /// <summary>逐个属性比对合法提示或诱敌，保留提示文字的空值差异。</summary>
    private static bool SameHint(GoHint left, GoHint right)
    {
        return left.X == right.X && left.Y == right.Y && left.Score == right.Score && left.Reason == right.Reason;
    }

    /// <summary>在固定的三个局面测量单次搜索，交替串行和多分片顺序，避免先后运行带来的偏差。</summary>
    internal static void Benchmark()
    {
        Run();
        Console.WriteLine("Logical processors: " + Environment.ProcessorCount + "; warmups per path: " + Warmups + "; samples per path: " + Samples);
        var positions = CreatePositions();
        foreach (int index in new[] { 0, 1, 2 })
        {
            var (name, position, _) = positions[index];
            var single = new GoTacticalSearch(position, 1);
            var multi = new GoTacticalSearch(position, 4);
            var singleHints = new GoHint[3];
            var multiHints = new GoHint[3];
            for (int i = 0; i < Warmups; i++)
            {
                single.Rank(position, singleHints, out _);
                multi.Rank(position, multiHints, out _);
            }

            var singleSamples = new double[Samples];
            var multiSamples = new double[Samples];
            long singleBytes = 0;
            long multiBytes = 0;
            // 每个样本只包住一次 Rank，包含工作分派、等待和结果合并。分配量读整个进程，涵盖工作线程。
            for (int i = 0; i < Samples; i++)
            {
                if ((i & 1) == 0)
                {
                    singleSamples[i] = Measure(single, position, singleHints, ref singleBytes);
                    multiSamples[i] = Measure(multi, position, multiHints, ref multiBytes);
                }
                else
                {
                    multiSamples[i] = Measure(multi, position, multiHints, ref multiBytes);
                    singleSamples[i] = Measure(single, position, singleHints, ref singleBytes);
                }
            }
            Print(name, 1, single.LastPartitionCount, singleSamples, singleBytes);
            Print(name, 4, multi.LastPartitionCount, multiSamples, multiBytes);
        }
    }

    /// <summary>返回一次调用的墙钟毫秒并累计进程托管分配字节，采集计数器的开销放在计时区间之外。</summary>
    private static double Measure(GoTacticalSearch search, GoCore position, GoHint[] hints, ref long allocated)
    {
        long bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
        long startedAt = Stopwatch.GetTimestamp();
        search.Rank(position, hints, out _);
        long elapsed = Stopwatch.GetTimestamp() - startedAt;
        allocated += GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
        return elapsed * 1000d / Stopwatch.Frequency;
    }

    /// <summary>输出同一口径的中位数、尾部耗时、最长耗时、超预算次数和每次调用的平均分配。</summary>
    private static void Print(string name, int requestedPartitions, int actualPartitions, double[] milliseconds, long allocated)
    {
        Array.Sort(milliseconds);
        int overBudget = 0;
        foreach (double time in milliseconds)
        {
            if (time > FrameBudgetMs)
            {
                overBudget++;
            }
        }
        int count = milliseconds.Length;
        double median = (milliseconds[(count - 1) / 2] + milliseconds[count / 2]) / 2;
        double p95 = milliseconds[(int)Math.Ceiling(count * .95) - 1];
        double p99 = milliseconds[(int)Math.Ceiling(count * .99) - 1];
        Console.WriteLine(FormattableString.Invariant(
            $"{name} requested={requestedPartitions} partitions={actualPartitions} medianMs={median:F4} p95Ms={p95:F4} p99Ms={p99:F4} maxMs={milliseconds[count - 1]:F4} over16.7ms={overBudget}/{count} bytesPerCall={(double)allocated / count:F0}"));
    }

    /// <summary>把完整提示和诱敌结果转成易比对的行，保留原排序、分数和提示文字。</summary>
    private static string Signature(GoHint[] hints, int count, GoHint bait)
    {
        var row = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            GoHint hint = hints[i];
            row.Append(CultureInfo.InvariantCulture, $"{hint.X},{hint.Y},{hint.Score},{hint.Reason}|");
        }
        return row.Append(CultureInfo.InvariantCulture, $"/{bait.X},{bait.Y},{bait.Score},{bait.Reason}").ToString();
    }
}

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    /// <summary>收集远征诊断的原生帧计时。按原生帧开始时间与秒表窗口相遇，回传晚到或无法对齐时保留缺测。</summary>
    internal sealed class CampaignNativeTimings
    {
        /// <summary>为十秒压测与回传收尾预留的唯一帧数上限。</summary>
        private const int Capacity = 16384;
        /// <summary>轮询最近几条原生结果，减轻异常帧中间一次轮询未命中的影响。</summary>
        private const int PollCapacity = 8;
        /// <summary>最近的主线程秒表采样历史。原生结果几帧后才回传，借此验证时钟是否落在同一帧附近。</summary>
        private const int ClockWindowCapacity = 32;
        /// <summary>至少看到三条与近期帧窗口相容的原生样本后，才允许报告时间对齐。</summary>
        private const int MinimumClockMatches = 3;
        /// <summary>Unity 正常会延后四帧回传。允许少量轮询或编辑器变化，特别远的样本无法作为时钟佐证。</summary>
        private const int MaxMatchedDelay = 12;

        /// <summary>按原生帧开始戳的去重表，容量随整段预留，不在正常采集中扩容。</summary>
        private readonly HashSet<ulong> _seen = new HashSet<ulong>(Capacity);

        /// <summary>一次引擎轮询写入的最新帧数据，启动时分配一次。</summary>
        private readonly FrameTiming[] _poll = new FrameTiming[PollCapacity];

        /// <summary>按到达时点保留的原生数据。结束采样后再搜索，不依赖引擎返回顺序或每帧是否都有结果。</summary>
        private readonly NativeFrame[] _samples = new NativeFrame[Capacity];

        /// <summary>每次轮询所在帧的秒表采样时刻，环形覆盖，仅用于回传延迟和同钟验证。</summary>
        private readonly long[] _clockStamps = new long[ClockWindowCapacity];

        /// <summary>与各秒表采样时刻配对的 Unity 帧号，跨帧缺口不能作为时间对齐证据。</summary>
        private readonly int[] _clockFrames = new int[ClockWindowCapacity];

        /// <summary>Unity 当前平台原生 CPU 时钟一秒计数。零表示不存在可换算的原生时钟。</summary>
        private readonly ulong _cpuFrequency = FrameTimingManager.GetCpuTimerFrequency();

        /// <summary>开始时 FrameTimingManager 是否报告计时功能已启用。编辑器的行为由运行时实测确认。</summary>
        private readonly bool _enabled = FrameTimingManager.IsFeatureEnabled();

        /// <summary>最近已轮询的 Unity 帧号，防止同一帧从两个诊断时点重复请求。</summary>
        private int _lastPollFrame = -1;

        /// <summary>轮询的次数，同时为环形历史决定写入位置。</summary>
        private int _pollCount;

        /// <summary>有原生结果回传的轮询次数，重复结果只影响此计数。</summary>
        private int _pollsWithResult;

        /// <summary>已经存下的不同帧开始戳数量。</summary>
        private int _count;

        /// <summary>没有超出回传延迟、且原生帧开始时间在连续秒表采样之间的不同样本数。</summary>
        private int _clockMatches;

        /// <summary>超过记录容量后被忽略的结果次数，读者可以分辨缺测与样本被截断。</summary>
        private int _overflow;

        /// <summary>一条原生采样和它的回传、配对信息。原生 CPU、渲染与 GPU 可能重叠。</summary>
        private struct NativeFrame
        {
            /// <summary>Unity 原始帧计时结果，保留引擎时钟与毫秒字段。</summary>
            internal FrameTiming Timing;

            /// <summary>按照两个时钟各自的频率换成秒表刻度的帧开始时间。若时钟不同源，此值不能用于比较。</summary>
            internal double StartInStopwatchTicks;

            /// <summary>收到原生结果时的 Unity 帧号。</summary>
            internal int ReceivedOnFrame;

            /// <summary>帧开始点落入的最近连续秒表采样区间的右侧帧号。零表示没通过一致性核对。</summary>
            internal int ClockFrame;
        }

        /// <summary>在每帧同一诊断时点调用一次。停正式采样后再调用数帧，以保留末尾延迟回传。</summary>
        internal void Sample()
        {
            int frame = Time.frameCount;
            if (frame == _lastPollFrame)
            {
                return;
            }
            _lastPollFrame = frame;

            int slot = _pollCount % ClockWindowCapacity;
            _clockStamps[slot] = Stopwatch.GetTimestamp();
            _clockFrames[slot] = frame;
            _pollCount++;

            FrameTimingManager.CaptureFrameTimings();
            int count = (int)FrameTimingManager.GetLatestTimings(PollCapacity, _poll);
            if (count > 0)
            {
                _pollsWithResult++;
            }

            for (int index = 0; index < count; index++)
            {
                FrameTiming timing = _poll[index];
                if (timing.frameStartTimestamp == 0 || _seen.Contains(timing.frameStartTimestamp))
                {
                    continue;
                }
                if (_count == Capacity)
                {
                    _overflow++;
                    continue;
                }

                _seen.Add(timing.frameStartTimestamp);
                double nativeStart = ToStopwatchTicks(timing.frameStartTimestamp);
                int clockFrame = FindClockFrame(nativeStart, frame);
                if (clockFrame > 0)
                {
                    _clockMatches++;
                }
                _samples[_count++] = new NativeFrame
                {
                    Timing = timing,
                    StartInStopwatchTicks = nativeStart,
                    ReceivedOnFrame = frame,
                    ClockFrame = clockFrame
                };
            }
        }

        /// <summary>返回原生计时可用性，区分引擎没有回传、缺少可比较时钟和验证尚不足。</summary>
        internal object DescribeAvailability()
        {
            return new Dictionary<string, object>
            {
                ["featureEnabled"] = _enabled,
                ["cpuTimerFrequency"] = _cpuFrequency,
                ["stopwatchFrequency"] = Stopwatch.Frequency,
                ["polls"] = _pollCount,
                ["pollsWithResult"] = _pollsWithResult,
                ["uniqueSamples"] = _count,
                ["clockMatches"] = _clockMatches,
                ["capacitySkipped"] = _overflow,
                ["clockStatus"] = ClockStatus(), ["gpuWindow"] = DescribeGpuWindow()
            };
        }

        /// <summary>仅在结束后统计这批 Unity 回传的 GPU 正样本，可与慢帧归因分开阅读。</summary>
        private object DescribeGpuWindow()
        {
            var gpu = new double[_count];
            int count = 0;
            for (int i = 0; i < _count; i++)
            {
                double milliseconds = _samples[i].Timing.gpuFrameTime;
                if (milliseconds > 0 && !double.IsInfinity(milliseconds))
                {
                    gpu[count++] = milliseconds;
                }
            }
            System.Array.Sort(gpu, 0, count);
            return new Dictionary<string, object>
            {
                ["positiveSamples"] = count,
                ["p50Ms"] = count > 0 ? (object)gpu[(int)System.Math.Ceiling(count * .50) - 1] : null,
                ["p95Ms"] = count > 0 ? (object)gpu[(int)System.Math.Ceiling(count * .95) - 1] : null,
                ["maxMs"] = count > 0 ? (object)gpu[count - 1] : null
            };
        }

        /// <summary>只输出原生帧开始时间唯一落在给定秒表区间内的原生数据。区间应由同阶段的相邻两个采样点给出。</summary>
        internal object Describe(long intervalStartStopwatch, long intervalEndStopwatch)
        {
            if (intervalStartStopwatch <= 0 || intervalEndStopwatch <= intervalStartStopwatch)
            {
                return Unavailable("invalid-window");
            }
            if (_clockMatches < MinimumClockMatches)
            {
                return Unavailable(ClockStatus());
            }

            int candidate = -1;
            for (int index = 0; index < _count; index++)
            {
                double start = _samples[index].StartInStopwatchTicks;
                if (start <= intervalStartStopwatch || start > intervalEndStopwatch)
                {
                    continue;
                }
                if (candidate >= 0)
                {
                    return Unavailable("multiple-frame-starts-in-window");
                }
                candidate = index;
            }

            if (candidate < 0 || _samples[candidate].ClockFrame == 0)
            {
                return Unavailable(candidate < 0 ? "no-frame-start-in-window" : "clock-window-unverified");
            }

            NativeFrame captured = _samples[candidate];
            FrameTiming timing = captured.Timing;
            return new Dictionary<string, object>
            {
                ["status"] = "frame-start-in-window",
                ["clockFrame"] = captured.ClockFrame,
                ["receivedOnFrame"] = captured.ReceivedOnFrame,
                ["nativeFrameStart"] = timing.frameStartTimestamp.ToString(),
                ["fromWindowStartMs"] = (captured.StartInStopwatchTicks - intervalStartStopwatch) * 1000.0 / Stopwatch.Frequency,
                ["totalCpuMs"] = ValidMilliseconds(timing.cpuFrameTime),
                ["mainThreadMs"] = ValidMilliseconds(timing.cpuMainThreadFrameTime),
                ["renderThreadMs"] = ValidMilliseconds(timing.cpuRenderThreadFrameTime),
                ["mainPresentWaitMs"] = ValidMilliseconds(timing.cpuMainThreadPresentWaitTime, allowZero: timing.cpuMainThreadFrameTime > 0),
                ["gpuMs"] = ValidMilliseconds(timing.gpuFrameTime),
                ["startToFirstSubmitMs"] = OffsetMilliseconds(timing.frameStartTimestamp, timing.firstSubmitTimestamp),
                ["startToPresentCalledMs"] = OffsetMilliseconds(timing.frameStartTimestamp, timing.cpuTimePresentCalled)
            };
        }

        /// <summary>用最近的连续秒表区间确认时间戳落在合理回传范围。官方延迟用于检验，不用于假设每条数据都恰好晚四帧。</summary>
        private int FindClockFrame(double start, int receivedOnFrame)
        {
            int intervals = System.Math.Min(_pollCount - 1, ClockWindowCapacity - 1);
            for (int offset = 0; offset < intervals; offset++)
            {
                int right = (_pollCount - 1 - offset) % ClockWindowCapacity;
                int left = (_pollCount - 2 - offset) % ClockWindowCapacity;
                int frame = _clockFrames[right];
                int delay = receivedOnFrame - frame;
                if (delay > MaxMatchedDelay)
                {
                    break;
                }
                if (delay > 0 && _clockFrames[left] == frame - 1 && start > _clockStamps[left] && start <= _clockStamps[right])
                {
                    return frame;
                }
            }
            return 0;
        }

        /// <summary>统一频率后才比较两个时刻。若底层时钟原点不同，FindClockFrame 校验会拒绝该数据。</summary>
        private double ToStopwatchTicks(ulong timestamp)
        {
            return _cpuFrequency == 0 ? double.NaN : timestamp * ((double)Stopwatch.Frequency / _cpuFrequency);
        }

        /// <summary>原生 CPU 时间戳差先核顺序再换毫秒。不存在对应事件时返回缺测。</summary>
        private object OffsetMilliseconds(ulong start, ulong end)
        {
            return _cpuFrequency != 0 && end != 0 && end >= start ? (object)((end - start) * 1000.0 / _cpuFrequency) : null;
        }

        /// <summary>驱动给的无效或零工作时间不冒充实测，已测主线程的零 Present 等待可以保留。</summary>
        private static object ValidMilliseconds(double milliseconds, bool allowZero = false)
        {
            return !double.IsNaN(milliseconds) && !double.IsInfinity(milliseconds) &&
                (milliseconds > 0 || (allowZero && milliseconds == 0)) ? (object)milliseconds : null;
        }

        /// <summary>按实际采样能力区分各种未对齐状态。</summary>
        private string ClockStatus()
        {
            if (_cpuFrequency == 0)
            {
                return "native-cpu-timer-unavailable";
            }
            if (_count == 0)
            {
                return "no-native-timing-samples";
            }
            return _clockMatches >= MinimumClockMatches ? "matching-frame-windows-observed" : "clock-window-unverified";
        }

        /// <summary>缺测只返回原因，避免把缺失 CPU 或 GPU 耗时记为零。</summary>
        private static object Unavailable(string reason)
        {
            return new Dictionary<string, object> { ["status"] = reason };
        }
    }
}
#endif

using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace Revolution
{
    /// <summary>一次交接在某个工作阶段的性能快照。时间为毫秒，内存为字节。</summary>
    public sealed class RevSceneHandoffStagePerformance
    {
        /// <summary>采样阶段。PreparingGameplay 同时覆盖准备和等待首屏就绪。</summary>
        public RevSceneHandoffStage Stage { get; }
        /// <summary>从阶段开始到下一阶段或请求结案的实时时长。</summary>
        public double DurationMs { get; }
        /// <summary>用于帧时间分布的完整间隔样本数。</summary>
        public int FrameSamples { get; }
        /// <summary>达到本次交接容量后未纳入帧时间分布的样本数。</summary>
        public int DroppedFrameSamples { get; }
        /// <summary>帧间隔中位数。无有效帧或容量不足时为空。</summary>
        public double? FrameP50Ms { get; }
        /// <summary>帧间隔的第 95 百分位。无有效帧或容量不足时为空。</summary>
        public double? FrameP95Ms { get; }
        /// <summary>帧间隔的第 99 百分位。无有效帧或容量不足时为空。</summary>
        public double? FrameP99Ms { get; }
        /// <summary>最大的已观察帧间隔。无有效帧或容量不足时为空。</summary>
        public double? FrameMaxMs { get; }
        /// <summary>本阶段的有效内存采样次数，包含阶段边界。</summary>
        public int MemorySamples { get; }
        /// <summary>采样中 Unity 跟踪的已分配内存峰值。计数不可用时为空。</summary>
        public long? PeakAllocatedBytes { get; }

        /// <summary>仅在交接结束后排序帧样本并冻结结果。丢样时保留计数并拒绝给出不完整的分位数。</summary>
        internal RevSceneHandoffStagePerformance(RevSceneHandoffStage stage, double durationMs, double[] samples,
            int offset, int count, int dropped, int memorySamples, long peakBytes)
        {
            Stage = stage;
            DurationMs = durationMs;
            FrameSamples = count;
            DroppedFrameSamples = dropped;
            MemorySamples = memorySamples;
            PeakAllocatedBytes = memorySamples > 0 ? (long?)peakBytes : null;
            if (count > 0 && dropped == 0)
            {
                Array.Sort(samples, offset, count);
                FrameP50Ms = Percentile(samples, offset, count, 0.50);
                FrameP95Ms = Percentile(samples, offset, count, 0.95);
                FrameP99Ms = Percentile(samples, offset, count, 0.99);
                FrameMaxMs = samples[offset + count - 1];
            }
        }

        /// <summary>用最近秩法从已排序区间取得分位数，输入样本数须大于零。</summary>
        private static double Percentile(double[] samples, int offset, int count, double fraction)
        {
            return samples[offset + (int)Math.Ceiling(count * fraction) - 1];
        }
    }

    /// <summary>跟随交接请求的阶段观测。每次接单独立收集，结案后只保留摘要。</summary>
    public sealed class RevSceneHandoffPerformance
    {
        /// <summary>每次交接的帧样本上限。超限继续计时和观察内存，并标记丢样。</summary>
        private const int FrameCapacity = 8192;
        /// <summary>原地累积一个开放或已结束的阶段，不创建逐帧对象。</summary>
        private struct Segment
        {
            /// <summary>从接单时钟读取的阶段开始时间，单位秒。</summary>
            public double Start;
            /// <summary>关闭区间时保存的阶段耗时，单位毫秒。</summary>
            public double DurationMs;
            /// <summary>本阶段在请求共享帧数组中的起点。</summary>
            public int Offset;
            /// <summary>本阶段写入帧数组的样本数。</summary>
            public int Count;
            /// <summary>本阶段超出容量未写入的样本数。</summary>
            public int Dropped;
            /// <summary>本阶段有效的内存读数次数。</summary>
            public int MemoryCount;
            /// <summary>本阶段最大的有效已分配内存读数，单位字节。</summary>
            public long PeakBytes;
        }

        /// <summary>沿交接阶段编号存放，未进入的阶段由访问掩码跳过。</summary>
        private Segment[] _segments = new Segment[(int)RevSceneHandoffStage.Revealing + 1];
        /// <summary>接单时间，作为总时长的统一起点，单位秒。</summary>
        private readonly double _started = Time.realtimeSinceStartupAsDouble;
        /// <summary>全请求共用的有界帧缓冲。采样关闭或结束时为空。</summary>
        private double[] _frames;
        /// <summary>已写入缓冲的样本数，也是下一段开始时的偏移量。</summary>
        private int _written;
        /// <summary>已记录的最后一个 Unity 帧号，防止阶段切换重复记录。</summary>
        private int _lastFrame = Time.frameCount;
        /// <summary>上一帧首次观察时的阶段。下一个帧间隔结束时归给它，避免激活慢帧记入首屏阶段。</summary>
        private int _frameStage = (int)RevSceneHandoffStage.Covering;
        /// <summary>当前开放区间的编号。接单即进入遮挡阶段。</summary>
        private int _stage = (int)RevSceneHandoffStage.Covering;
        /// <summary>标记实际经过的阶段，允许调用方跳过中间区间。</summary>
        private int _visited = 1 << (int)RevSceneHandoffStage.Covering;

        /// <summary>是否启用了帧与内存采集。壁钟时长始终可用。</summary>
        public bool SamplesEnabled { get; }
        /// <summary>请求从接单到结案的实时时长，正在执行时为空。</summary>
        public double? TotalDurationMs { get; private set; }
        /// <summary>按执行顺序保存的已冻结阶段快照，正在执行时为空。</summary>
        public IReadOnlyList<RevSceneHandoffStagePerformance> Stages { get; private set; }

        /// <summary>以遮挡阶段开始。在 Play 中按调用方选择开启临时采样。</summary>
        internal RevSceneHandoffPerformance(bool sampleFrames)
        {
            _segments[_stage].Start = _started;
            SamplesEnabled = sampleFrames && Application.isPlaying;
            if (SamplesEnabled)
            {
                _frames = ArrayPool<double>.Shared.Rent(FrameCapacity);
                SampleMemory(ref _segments[_stage]);
                SampleUntilDoneAsync().Forget();
            }
        }

        /// <summary>关闭上一个阶段并打开新阶段，等待首屏阶段并入首屏准备。</summary>
        internal void Advance(RevSceneHandoffStage stage)
        {
            int next = (int)(stage == RevSceneHandoffStage.WaitingForGameplay ? RevSceneHandoffStage.PreparingGameplay : stage);
            if (next <= _stage || next >= _segments.Length)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            CloseStage(now);
            _stage = next;
            _visited |= 1 << next;
            ref Segment segment = ref _segments[_stage];
            segment.Start = now;
            SampleMemory(ref segment);
        }

        /// <summary>主线程每帧追加一个间隔及内存读数，仅在交接期间派发续体。</summary>
        private async RevTask SampleUntilDoneAsync()
        {
            while (_frames != null)
            {
                await RevTaskScheduler.NextFrame();
                if (_frames != null)
                {
                    CaptureFrame();
                    SampleMemory(ref _segments[_stage]);
                }
            }
        }

        /// <summary>把完整间隔归给上一帧开始观察时的阶段。跨阶段的帧只归属一次。</summary>
        private void CaptureFrame()
        {
            if (_frames == null || _lastFrame == Time.frameCount)
            {
                return;
            }
            _lastFrame = Time.frameCount;
            // 只有跨到新帧才推进归属，同一帧的阶段切换不能覆盖尚未观测完整的帧间隔。
            ref Segment segment = ref _segments[_frameStage];
            _frameStage = _stage;
            double ms = Time.unscaledDeltaTime * 1000d;
            if (ms <= 0)
            {
                return;
            }
            if (_written == FrameCapacity)
            {
                segment.Dropped++;
                return;
            }
            // 旧阶段的最后一帧可能在新阶段才采到。按每段第一份样本定位，后续样本仍按阶段单调写入。
            if (segment.Count == 0)
            {
                segment.Offset = _written;
            }
            _frames[_written++] = ms;
            segment.Count++;
        }

        /// <summary>更新本段实际观察到的 Unity 分配内存峰值。零读数表示该环境无有效样本。</summary>
        private void SampleMemory(ref Segment segment)
        {
            if (_frames == null)
            {
                return;
            }
            long bytes = Profiler.GetTotalAllocatedMemoryLong();
            if (bytes > 0)
            {
                segment.PeakBytes = Math.Max(segment.PeakBytes, bytes);
                segment.MemoryCount++;
            }
        }

        /// <summary>补采当前帧和阶段末端的内存，并冻结区间实时时长。</summary>
        private void CloseStage(double now)
        {
            ref Segment segment = ref _segments[_stage];
            CaptureFrame();
            SampleMemory(ref segment);
            segment.DurationMs = (now - segment.Start) * 1000d;
        }

        /// <summary>在发布交接终态前关闭采样并生成摘要。失败和取消也保留实际经过的阶段。</summary>
        internal void Finish()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            CloseStage(now);
            double[] frames = _frames;
            _frames = null;
            try
            {
                var result = new List<RevSceneHandoffStagePerformance>(_segments.Length);
                for (int index = 0; index < _segments.Length; index++)
                {
                    if ((_visited & (1 << index)) != 0)
                    {
                        Segment segment = _segments[index];
                        result.Add(new RevSceneHandoffStagePerformance((RevSceneHandoffStage)index, segment.DurationMs,
                            frames, segment.Offset, segment.Count, segment.Dropped, segment.MemoryCount, segment.PeakBytes));
                    }
                }
                Stages = result.AsReadOnly();
                TotalDurationMs = (now - _started) * 1000d;
            }
            finally
            {
                _segments = null;
                if (frames != null)
                {
                    ArrayPool<double>.Shared.Return(frames);
                }
            }
        }
    }
}

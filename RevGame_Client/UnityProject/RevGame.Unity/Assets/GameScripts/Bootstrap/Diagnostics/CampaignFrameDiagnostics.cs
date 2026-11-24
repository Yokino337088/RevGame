#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RevGame.Flow;
using RevGame.View;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    /// <summary>把一个远征 LateUpdate 间隔拆成前帧尾、两轮游戏循环之间和本帧头，分别保留确切帧号。</summary>
    internal sealed class CampaignFrameDiagnostics : IDisposable
    {
        /// <summary>保留的最长间隔数。观察窗内只移动这些值类型，不构造 JSON。</summary>
        private const int TailLength = 10;
        /// <summary>Stopwatch 到毫秒的统一单位。</summary>
        private static readonly double MsPerTick = 1000d / Stopwatch.Frequency;
        /// <summary>真实第三章棋盘。只在本次窗口开启它的 Editor 反馈探针。</summary>
        private readonly BoardView _board;
        /// <summary>结束时恢复原棋盘探针开关。</summary>
        private readonly bool _previousFeedback;
        /// <summary>结束时恢复原战斗步探针开关。</summary>
        private readonly bool _previousBattle;
        /// <summary>实际 Unity PlayerLoop 中的阶段时间戳，只在本轮有响应。</summary>
        private readonly CampaignLoopCapture _loop;
        /// <summary>原生 CPU/GPU 计时回传，按时间戳检查能否与真实帧对应。</summary>
        private readonly CampaignNativeTimings _native = new CampaignNativeTimings();
        /// <summary>按墙钟时间排序保存最慢的那些帧间隔。</summary>
        private readonly Gap[] _tail = new Gap[TailLength];
        /// <summary>第几项之前都有有效候选。</summary>
        private int _tailCount;
        /// <summary>上一帧 LateUpdate 的 Unity 帧号，用于区分本间隔内的前后两帧。</summary>
        private int _previousFrame = -1;
        /// <summary>上一帧 LateUpdate 的实际秒表值。</summary>
        private long _previousAt;
        /// <summary>上上一帧同一采样点，供前一帧原生回传按时间窗口对齐。</summary>
        private long _secondPreviousAt;
        /// <summary>已观察到的一级托管垃圾回收次数。差值只能提示同一间隔发生过回收。</summary>
        private int _gcBefore;
        /// <summary>阶段时间戳可以首尾相接并准确分解的帧间隔数。</summary>
        private int _aligned;
        /// <summary>采样窗口覆盖的总规则步数。</summary>
        private int _battleTicks;
        /// <summary>窗口内所有规则步的总墙钟耗时，包含同步响应。</summary>
        private double _battleMs;
        /// <summary>窗口内所有反馈 LateUpdate 的总毫秒。</summary>
        private double _feedbackMs;
        /// <summary>其中给伤害文字选位置的总毫秒。异步到达的选位亦算入。</summary>
        private double _arrangeMs;
        /// <summary>窗口内入队或合并的伤害事件数。</summary>
        private int _enqueued;
        /// <summary>窗口内显示的伤害文字数。</summary>
        private int _shown;
        /// <summary>窗口观察到的最大等待队列长度。</summary>
        private int _peakDepth;
        /// <summary>结束后停止采样并保证多次归还不重复恢复。</summary>
        private bool _disposed;

        /// <summary>一个完整区间。帧号相邻时渲染在前一帧，战斗步与晚期反馈在当前帧。</summary>
        private struct Gap
        {
            /// <summary>间隔结束时的 Unity 帧号。</summary>
            internal int Frame;
            /// <summary>前一间隔起点，供回读前帧的原生包。</summary>
            internal long PreviousWindowStart;
            /// <summary>当前区间首个 LateUpdate 秒表值。</summary>
            internal long Start;
            /// <summary>当前区间最后一个 LateUpdate 秒表值。</summary>
            internal long End;
            /// <summary>上一帧游戏循环阶段。</summary>
            internal CampaignLoopCapture.LoopFrame Previous;
            /// <summary>本帧已发生的游戏循环阶段。</summary>
            internal CampaignLoopCapture.LoopFrame Current;
            /// <summary>只有时间戳顺序、两帧编号都完整才允许拆分。</summary>
            internal bool Aligned;
            /// <summary>此间隔结束前发生的战斗固定步。</summary>
            internal BattleLogic.CampaignTickFrame Battle;
            /// <summary>此间隔结束前的反馈同步收集、布局与呈现。</summary>
            internal BoardView.CampaignFeedbackFrame Feedback;
            /// <summary>这个时间窗内检测到的托管回收次数，单位次。</summary>
            internal int Collections;
        }

        /// <summary>开启一套观察器。只有实际棋盘存在时允许开始，以免把空画面量成游戏。</summary>
        internal CampaignFrameDiagnostics(BoardView board)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }
            _board = board;
            _loop = new CampaignLoopCapture();
            _previousBattle = BattleLogic.CampaignFrameProbeEnabled;
            _previousFeedback = board.CampaignFeedbackProbeEnabled;
            _gcBefore = GC.CollectionCount(0);
            BattleLogic.CampaignFrameProbeEnabled = true;
            board.CampaignFeedbackProbeEnabled = true;
        }

        /// <summary>每个 Unity 帧请求并读回一批可用原生数据，退场前仍可等尾部回传。</summary>
        internal void SampleNative()
        {
            if (!_disposed)
            {
                _native.Sample();
            }
        }

        /// <summary>只收已有字段和时钟。当前帧尚未渲染，必须用已结束的上一帧测画面尾段。</summary>
        internal void ObserveLateUpdate(int frame, long now)
        {
            int collections = GC.CollectionCount(0);
            if (_previousFrame >= 0 && now > _previousAt)
            {
                bool previousFound = _loop.TryGetFrame(_previousFrame, out var previous);
                bool currentFound = _loop.TryGetFrame(frame, out var current);
                if (!BattleLogic.TryReadCampaignTick(frame, out var battle))
                {
                    battle = default;
                }
                if (!_board.TryReadCampaignFeedback(frame, out var feedback))
                {
                    feedback = default;
                }
                bool aligned = previousFound && currentFound && frame == _previousFrame + 1 && previous.LoopEnd >= _previousAt &&
                    current.LoopBegin >= previous.LoopEnd && now >= current.LoopBegin;
                if (aligned)
                {
                    _aligned++;
                }
                _battleTicks += battle.Count;
                _battleMs += battle.Milliseconds;
                _feedbackMs += feedback.FrameMs;
                _arrangeMs += feedback.ArrangeMs + feedback.OutsideArrangeMs;
                _enqueued += feedback.Enqueued;
                _shown += feedback.Shown;
                _peakDepth = Math.Max(_peakDepth, feedback.Depth);

                var gap = new Gap
                {
                    Frame = frame, PreviousWindowStart = _secondPreviousAt, Start = _previousAt, End = now,
                    Previous = previous, Current = current, Aligned = aligned, Battle = battle,
                    Feedback = feedback, Collections = Math.Max(0, collections - _gcBefore)
                };
                KeepLongest(gap);
            }
            _gcBefore = collections;
            _secondPreviousAt = _previousAt;
            _previousAt = now;
            _previousFrame = frame;
        }

        /// <summary>只在榜单能接纳该间隔时移动少量记录，按最慢在前。</summary>
        private void KeepLongest(Gap gap)
        {
            int position = _tailCount;
            long duration = gap.End - gap.Start;
            while (position > 0 && duration > _tail[position - 1].End - _tail[position - 1].Start)
            {
                position--;
            }
            if (position == TailLength)
            {
                return;
            }
            int last = Math.Min(_tailCount, TailLength - 1);
            for (int i = last; i > position; i--)
            {
                _tail[i] = _tail[i - 1];
            }
            _tail[position] = gap;
            _tailCount = Math.Min(_tailCount + 1, TailLength);
        }

        /// <summary>只在窗口外输出最慢间隔及分段。跨帧或未对齐的原生时间明确留空。</summary>
        internal object Retell()
        {
            var slow = new object[_tailCount];
            for (int i = 0; i < _tailCount; i++)
            {
                Gap gap = _tail[i];
                slow[i] = new Dictionary<string, object>
                {
                    ["previousFrame"] = gap.Previous.Frame, ["currentFrame"] = gap.Frame, ["wallMs"] = Ms(gap.Start, gap.End),
                    ["alignedLoop"] = gap.Aligned,
                    ["previousLoopTailMs"] = gap.Aligned ? Ms(gap.Start, gap.Previous.LoopEnd) : null,
                    ["betweenLoopsMs"] = gap.Aligned ? Ms(gap.Previous.LoopEnd, gap.Current.LoopBegin) : null,
                    ["currentLoopHeadMs"] = gap.Aligned ? Ms(gap.Current.LoopBegin, gap.End) : null,
                    ["previousRenderPhaseMs"] = Ms(gap.Previous.RenderBegin, gap.Previous.RenderEnd),
                    ["previousPresentPhaseMs"] = Ms(gap.Previous.PresentBegin, gap.Previous.PresentEnd),
                    ["currentTimeUpdateMs"] = Ms(gap.Current.BeforeTimeUpdate, gap.Current.AfterTimeUpdate),
                    ["currentUpdateMs"] = Ms(gap.Current.BeforeUpdate, gap.Current.AfterUpdate),
                    ["battleTickMs"] = gap.Battle.Milliseconds, ["battleTicks"] = gap.Battle.Count,
                    ["feedbackLateMs"] = gap.Feedback.FrameMs, ["feedbackArrangeMs"] = gap.Feedback.ArrangeMs,
                    ["feedbackIntakeMs"] = gap.Feedback.IntakeMs, ["outsideArrangeMs"] = gap.Feedback.OutsideArrangeMs,
                    ["damageShown"] = gap.Feedback.Shown, ["damageEnqueued"] = gap.Feedback.Enqueued,
                    ["queueDepth"] = gap.Feedback.Depth, ["managedCollections"] = gap.Collections,
                    ["nativePrevious"] = gap.PreviousWindowStart > 0 ? _native.Describe(gap.PreviousWindowStart, gap.Start) : null,
                    ["nativeCurrent"] = _native.Describe(gap.Start, gap.End)
                };
            }
            return new Dictionary<string, object>
            {
                ["alignedIntervals"] = _aligned, ["battleTicks"] = _battleTicks, ["battleMs"] = _battleMs,
                ["feedbackMs"] = _feedbackMs, ["arrangeMs"] = _arrangeMs, ["enqueued"] = _enqueued,
                ["shown"] = _shown, ["peakDepth"] = _peakDepth,
                ["native"] = _native.DescribeAvailability(), ["slow"] = slow
            };
        }

        /// <summary>换算正向阶段间隔，没收到任一端点就留空。</summary>
        private static double? Ms(long start, long end)
        {
            return start > 0 && end >= start ? (end - start) * MsPerTick : null;
        }

        /// <summary>移除本轮的 Unity 循环标记并归还业务探针开关。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            BattleLogic.CampaignFrameProbeEnabled = _previousBattle;
            if (_board != null)
            {
                _board.CampaignFeedbackProbeEnabled = _previousFeedback;
            }
            _loop.Dispose();
        }
    }
}
#endif

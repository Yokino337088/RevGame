#if UNITY_EDITOR
using System;
using RevGame.Core;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>同一 Unity 帧的棋盘反馈实耗。各字段保留真实调用边界，父段和其中的子段不可相加。</summary>
        public struct CampaignFeedbackFrame
        {
            /// <summary>Unity 帧号，用于与主线程、战斗规则和 GPU 的对应样本对齐。</summary>
            public int Frame;
            /// <summary>反馈 LateUpdate 的总毫秒数，已包含其中同步发生的文字避让。</summary>
            public double FrameMs;
            /// <summary>反馈 LateUpdate 内的文字避让毫秒数，是 FrameMs 的子段。</summary>
            public double ArrangeMs;
            /// <summary>在反馈 LateUpdate 外发生的文字避让毫秒数，例如池对象稍后交付。</summary>
            public double OutsideArrangeMs;
            /// <summary>同步接收攻击和伤害事件的毫秒数，通常属于规则步骤的子段。</summary>
            public double IntakeMs;
            /// <summary>本帧入队或合并的实际伤害命中次数，不含因队列满而略掉的命中。</summary>
            public int Enqueued;
            /// <summary>本帧成功交给飘字对象的伤害字数，不含攻击口号。</summary>
            public int Shown;
            /// <summary>本帧最近一个被观测点之后仍待播的伤害键数量。</summary>
            public int Depth;
            /// <summary>本帧第一条交给飘字对象的目标编号，尚未显示时为零。</summary>
            public int FirstShownTargetId;
            /// <summary>本帧第一条交给飘字对象的伤害种类，Shown 大于零时有效。</summary>
            public BattleDamageKind FirstShownKind;
            /// <summary>第一条字交给表现后的秒表读数，可与本帧渲染结束分开核对。</summary>
            public long FirstShownTimestamp;
        }

        /// <summary>环保存最近八个 Unity 帧号。跨帧读回时可判别变慢、占用或记录已覆盖。</summary>
        private const int CampaignFeedbackRingSize = 8;
        /// <summary>探针第一次启用时分配固定长度，此后只按帧号复用槽位。</summary>
        private CampaignFeedbackFrame[] _campaignFeedbackFrames;
        /// <summary>编辑器采样由测量用例显式开启，普通游玩保持关闭。</summary>
        private bool _campaignFeedbackProbeEnabled;
        /// <summary>当前语句是否在反馈 LateUpdate 的范围内，使避让只归入一个父段。</summary>
        private bool _campaignFeedbackInFrame;

        /// <summary>控制本局编辑器诊断。重新启用时先清旧帧号，关闭后不再改写已有样本。</summary>
        public bool CampaignFeedbackProbeEnabled
        {
            get => _campaignFeedbackProbeEnabled;
            set
            {
                if (_campaignFeedbackProbeEnabled == value)
                {
                    return;
                }
                if (value)
                {
                    _campaignFeedbackFrames ??= new CampaignFeedbackFrame[CampaignFeedbackRingSize];
                    for (int index = 0; index < _campaignFeedbackFrames.Length; index++)
                    {
                        _campaignFeedbackFrames[index] = new CampaignFeedbackFrame { Frame = -1 };
                    }
                }
                _campaignFeedbackProbeEnabled = value;
            }
        }

        /// <summary>读出指定帧的已记录快照。未发生反馈、未启用或记录已被覆盖时返回 false。</summary>
        public bool TryReadCampaignFeedback(int frame, out CampaignFeedbackFrame sample)
        {
            sample = default;
            if (_campaignFeedbackFrames == null || frame < 0)
            {
                return false;
            }

            sample = _campaignFeedbackFrames[frame & (CampaignFeedbackRingSize - 1)];
            return sample.Frame == frame;
        }

        /// <summary>反馈耗时归入三个明确的调用边界。</summary>
        private enum CampaignFeedbackStage
        {
            /// <summary>棋盘反馈的 LateUpdate 外层。</summary>
            Frame,
            /// <summary>单个文字的选位和避让。</summary>
            Arrange,
            /// <summary>从棋盘收到攻击或伤害事件到处理结束。</summary>
            Intake
        }

        /// <summary>在栈上记录一个同步计时作用域，提前返回也能在结束处计入实际时间。</summary>
        private readonly struct CampaignFeedbackTiming : IDisposable
        {
            /// <summary>拥有帧槽的棋盘。默认作用域没有所有者，关闭探针时不读秒表。</summary>
            private readonly BoardView _owner;
            /// <summary>开始测量时的秒表读数。</summary>
            private readonly long _started;
            /// <summary>这个时间区间归属的调用边界。</summary>
            private readonly CampaignFeedbackStage _stage;

            /// <summary>标记同步调用的起点。外层先登记当前处于反馈 LateUpdate，后续子段沿用。</summary>
            internal CampaignFeedbackTiming(BoardView owner, CampaignFeedbackStage stage)
            {
                _owner = owner;
                _stage = stage;
                if (stage == CampaignFeedbackStage.Frame)
                {
                    owner._campaignFeedbackInFrame = true;
                }
                _started = Stopwatch.GetTimestamp();
            }

            /// <summary>离开指定边界后记实际耗时，根据是否嵌套于反馈 LateUpdate 归类。</summary>
            public void Dispose()
            {
                if (_owner != null)
                {
                    double elapsed = (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency;
                    _owner.EndCampaignFeedbackTiming(_stage, elapsed);
                }
            }
        }

        /// <summary>只在探针开启时建立计时作用域。常态游玩不分配计时对象或读取时间。</summary>
        private CampaignFeedbackTiming BeginCampaignFeedbackTiming(CampaignFeedbackStage stage)
        {
            return _campaignFeedbackProbeEnabled ? new CampaignFeedbackTiming(this, stage) : default;
        }

        /// <summary>按调用边界累计本帧反馈。外层结束后保留当前队长并解除嵌套标记。</summary>
        private void EndCampaignFeedbackTiming(CampaignFeedbackStage stage, double elapsed)
        {
            ref CampaignFeedbackFrame sample = ref CurrentCampaignFeedbackFrame();
            switch (stage)
            {
                case CampaignFeedbackStage.Frame:
                    sample.FrameMs += elapsed;
                    _campaignFeedbackInFrame = false;
                    break;
                case CampaignFeedbackStage.Arrange:
                    if (_campaignFeedbackInFrame)
                    {
                        sample.ArrangeMs += elapsed;
                    }
                    else
                    {
                        sample.OutsideArrangeMs += elapsed;
                    }
                    break;
                case CampaignFeedbackStage.Intake:
                    sample.IntakeMs += elapsed;
                    break;
            }
            sample.Depth = _pendingDamage.Count;
        }

        /// <summary>使用当前 Unity 帧号查槽。仅探针钩子可写，旧帧覆盖时先归零所有字段。</summary>
        private ref CampaignFeedbackFrame CurrentCampaignFeedbackFrame()
        {
            int frame = Time.frameCount;
            ref CampaignFeedbackFrame sample = ref _campaignFeedbackFrames[frame & (CampaignFeedbackRingSize - 1)];
            if (sample.Frame != frame)
            {
                sample = new CampaignFeedbackFrame { Frame = frame };
            }
            return ref sample;
        }

        /// <summary>只数成功入队或合并的实际命中，队列已满时不增加。</summary>
        private void RecordCampaignDamageEnqueued()
        {
            if (_campaignFeedbackProbeEnabled)
            {
                ref CampaignFeedbackFrame sample = ref CurrentCampaignFeedbackFrame();
                sample.Enqueued++;
                sample.Depth = _pendingDamage.Count;
            }
        }

        /// <summary>在真正启动伤害字后记首条身份，区分同一波伤害中的分流和合并。</summary>
        private void RecordCampaignDamageShown(int targetId, BattleDamageKind kind)
        {
            if (_campaignFeedbackProbeEnabled)
            {
                long shownAt = Stopwatch.GetTimestamp();
                ref CampaignFeedbackFrame sample = ref CurrentCampaignFeedbackFrame();
                if (sample.Shown == 0)
                {
                    sample.FirstShownTargetId = targetId;
                    sample.FirstShownKind = kind;
                    sample.FirstShownTimestamp = shownAt;
                }
                sample.Shown++;
                sample.Depth = _pendingDamage.Count;
            }
        }
    }
}
#endif

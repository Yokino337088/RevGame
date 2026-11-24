#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    /// <summary>只在远征编辑器测量期间给 Unity 主循环各段打墙钟点。按真实帧号保存，结束时移除自己的节点。</summary>
    internal sealed class CampaignLoopCapture : IDisposable
    {
        /// <summary>环形缓冲的帧数上限，满后覆盖最早的帧。超出可回看的范围时查询失败。</summary>
        private const int Capacity = 16384;

        /// <summary>已完整走到主循环末尾的帧。采样时不分配，帧号只用于选择槽位。</summary>
        private readonly LoopFrame[] _finished = new LoopFrame[Capacity];

        /// <summary>正在推进的帧。进入根循环时先存起点，等到 Update 之前再登记 Unity 的实际帧号。</summary>
        private LoopFrame _active;

        /// <summary>销毁中留在当前循环栈里的回调也只会空返，不会再写样本。</summary>
        private bool _disposed;

        /// <summary>仅作为当前测量添加节点的身份，退出时可用类型准确定位。</summary>
        private sealed class ProbeMarker { }

        /// <summary>同一帧各阶段的 Stopwatch 计数。零代表该点尚未经过或当前循环树没有该阶段。</summary>
        internal struct LoopFrame
        {
            /// <summary>Update 前读到的 Time.frameCount。尚未登记时为负一。</summary>
            internal int Frame;
            /// <summary>根循环的第一个子系统前。记录时尚不依赖 Time.frameCount。</summary>
            internal long LoopBegin;
            /// <summary>根循环进入 TimeUpdate 前。</summary>
            internal long BeforeTimeUpdate;
            /// <summary>根循环完成 TimeUpdate 后，包含引擎自身的更新前等待。</summary>
            internal long AfterTimeUpdate;
            /// <summary>根循环进入 Update 前。</summary>
            internal long BeforeUpdate;
            /// <summary>根循环完成 Update 后。</summary>
            internal long AfterUpdate;
            /// <summary>根循环进入 PreLateUpdate 前，覆盖所有 LateUpdate 调用前的阶段。</summary>
            internal long BeforeLateUpdate;
            /// <summary>根循环进入 PostLateUpdate 前，PreLateUpdate 已完成。</summary>
            internal long AfterLateUpdate;
            /// <summary>PostLateUpdate 的 PlayerUpdateCanvases 前，界定 CPU 侧提交段的起点。</summary>
            internal long RenderBegin;
            /// <summary>FinishFrameRendering 之后。提交段可能包含其他系统及等待，不代表显卡耗时。</summary>
            internal long RenderEnd;
            /// <summary>PresentAfterDraw 前。当前编辑器内部没有这一节点时保持零。</summary>
            internal long PresentBegin;
            /// <summary>PresentAfterDraw 后。只有此调用的墙钟区间，不代表屏幕扫描。</summary>
            internal long PresentEnd;
            /// <summary>根循环的最后一个子系统后。下一帧 LoopBegin 减它可得到两次根循环之间的间隔。</summary>
            internal long LoopEnd;
        }

        /// <summary>在 Unity 主线程安装。保留原顺序与原函数，监测失败时不会提交不完整的循环树。</summary>
        internal CampaignLoopCapture()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var children = loop.subSystemList ?? throw new InvalidOperationException("没有可测的 Unity 主循环");
            var next = new List<PlayerLoopSystem>(children.Length + 8) { Probe(MarkLoopBegin) };
            bool updateFound = false;
            bool preLateFound = false;
            bool postLateFound = false;
            foreach (var system in children)
            {
                if (system.type == typeof(ProbeMarker))
                {
                    throw new InvalidOperationException("远征主循环已经有活跃的测量器");
                }
                if (system.type == typeof(UnityEngine.PlayerLoop.TimeUpdate))
                {
                    next.Add(Probe(MarkBeforeTimeUpdate));
                    next.Add(system);
                    next.Add(Probe(MarkAfterTimeUpdate));
                }
                else if (system.type == typeof(UnityEngine.PlayerLoop.Update))
                {
                    next.Add(Probe(MarkBeforeUpdate));
                    next.Add(system);
                    next.Add(Probe(MarkAfterUpdate));
                    updateFound = true;
                }
                else if (system.type == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
                {
                    next.Add(Probe(MarkBeforeLateUpdate));
                    next.Add(system);
                    preLateFound = true;
                }
                else if (system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                {
                    next.Add(Probe(MarkAfterLateUpdate));
                    next.Add(WrapPostLateUpdate(system));
                    postLateFound = true;
                }
                else
                {
                    next.Add(system);
                }
            }

            if (!updateFound || !preLateFound || !postLateFound)
            {
                throw new InvalidOperationException("Unity 主循环缺少 Update 或 LateUpdate，无法对齐远征帧");
            }
            next.Add(Probe(MarkLoopEnd));
            loop.subSystemList = next.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
        }

        /// <summary>在实际渲染子阶段旁记墙钟时间。找不到某个原生节点时，对应值保持零。</summary>
        private PlayerLoopSystem WrapPostLateUpdate(PlayerLoopSystem postLate)
        {
            var children = postLate.subSystemList;
            if (children == null)
            {
                return postLate;
            }
            var next = new List<PlayerLoopSystem>(children.Length + 4);
            foreach (var system in children)
            {
                if (system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.PlayerUpdateCanvases))
                {
                    next.Add(Probe(MarkRenderBegin));
                }
                if (system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.PresentAfterDraw))
                {
                    next.Add(Probe(MarkPresentBegin));
                }
                next.Add(system);
                if (system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.FinishFrameRendering))
                {
                    next.Add(Probe(MarkRenderEnd));
                }
                if (system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.PresentAfterDraw))
                {
                    next.Add(Probe(MarkPresentEnd));
                }
            }
            postLate.subSystemList = next.ToArray();
            return postLate;
        }

        /// <summary>创建只执行单个计时回调的自定义子系统。</summary>
        private static PlayerLoopSystem Probe(PlayerLoopSystem.UpdateFunction update) =>
            new PlayerLoopSystem { type = typeof(ProbeMarker), updateDelegate = update };

        /// <summary>读取指定帧。若还在该帧的循环内，可返回已经采到的点，LoopEnd 此时为零。</summary>
        internal bool TryGetFrame(int frame, out LoopFrame sample)
        {
            if (frame >= 0)
            {
                sample = _finished[frame & (Capacity - 1)];
                if (sample.Frame == frame && sample.LoopEnd != 0)
                {
                    return true;
                }
                if (_active.Frame == frame && _active.LoopBegin != 0)
                {
                    sample = _active;
                    return true;
                }
            }
            sample = default;
            return false;
        }

        /// <summary>开始一份新帧样本，稍后从 Update 处对齐帧号。</summary>
        private void MarkLoopBegin()
        {
            if (!_disposed)
            {
                _active = new LoopFrame { Frame = -1, LoopBegin = Stopwatch.GetTimestamp() };
            }
        }

        /// <summary>为引擎 TimeUpdate 写入起点。</summary>
        private void MarkBeforeTimeUpdate() => _active.BeforeTimeUpdate = Now();
        /// <summary>为引擎 TimeUpdate 写入终点。</summary>
        private void MarkAfterTimeUpdate() => _active.AfterTimeUpdate = Now();

        /// <summary>在时间更新后登记帧号，再给 Update 起点打点。</summary>
        private void MarkBeforeUpdate()
        {
            if (!_disposed)
            {
                _active.Frame = Time.frameCount;
                _active.BeforeUpdate = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>记录 Update 已执行完。</summary>
        private void MarkAfterUpdate() => _active.AfterUpdate = Now();
        /// <summary>记录 PreLateUpdate 入口。</summary>
        private void MarkBeforeLateUpdate() => _active.BeforeLateUpdate = Now();
        /// <summary>记录 PostLateUpdate 入口。</summary>
        private void MarkAfterLateUpdate() => _active.AfterLateUpdate = Now();
        /// <summary>记录 CPU 提交段入口。</summary>
        private void MarkRenderBegin() => _active.RenderBegin = Now();
        /// <summary>记录 CPU 提交段出口。</summary>
        private void MarkRenderEnd() => _active.RenderEnd = Now();
        /// <summary>记录 PresentAfterDraw 入口。</summary>
        private void MarkPresentBegin() => _active.PresentBegin = Now();
        /// <summary>记录 PresentAfterDraw 出口。</summary>
        private void MarkPresentEnd() => _active.PresentEnd = Now();

        /// <summary>只在完整一帧后保存记录。安装发生在帧中途时，不会把上一帧的半份数据放进环。</summary>
        private void MarkLoopEnd()
        {
            if (_disposed || _active.LoopBegin == 0 || _active.Frame < 0)
            {
                return;
            }
            _active.LoopEnd = Stopwatch.GetTimestamp();
            _finished[_active.Frame & (Capacity - 1)] = _active;
        }

        /// <summary>记录当前墙钟点。卸载后可能还有同帧回调，返回零表示无样本。</summary>
        private long Now() => _disposed ? 0 : Stopwatch.GetTimestamp();

        /// <summary>在 Unity 主线程移除本测量器的节点，保留期间由其他组件改动过的循环结构。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveProbes(ref loop))
            {
                PlayerLoop.SetPlayerLoop(loop);
            }
        }

        /// <summary>从父节点中递归移除本实例的测量回调，仅在有改动的层级替换子系统数组。</summary>
        private bool RemoveProbes(ref PlayerLoopSystem parent)
        {
            var children = parent.subSystemList;
            if (children == null)
            {
                return false;
            }
            List<PlayerLoopSystem> next = null;
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];
                bool remove = child.type == typeof(ProbeMarker) && ReferenceEquals(child.updateDelegate?.Target, this);
                if (remove || RemoveProbes(ref child))
                {
                    if (next == null)
                    {
                        next = new List<PlayerLoopSystem>(children.Length);
                        for (int before = 0; before < i; before++)
                        {
                            next.Add(children[before]);
                        }
                    }
                }
                if (!remove)
                {
                    next?.Add(child);
                }
            }
            if (next == null)
            {
                return false;
            }
            parent.subSystemList = next.ToArray();
            return true;
        }
    }
}
#endif

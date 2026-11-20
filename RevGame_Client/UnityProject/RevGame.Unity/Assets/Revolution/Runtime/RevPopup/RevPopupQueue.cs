using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>弹窗排队或展示的结果。业务只在主动确认时执行后续操作。</summary>
    public enum RevPopupStatus
    {
        /// <summary>尚在等待或正在展示。</summary>
        Pending,
        /// <summary>玩家确认。</summary>
        Confirmed,
        /// <summary>玩家拒绝。</summary>
        Declined,
        /// <summary>已关闭、退域或阶段过期导致撤销。</summary>
        Cancelled,
        /// <summary>排队超过时限。</summary>
        Expired,
        /// <summary>同键已排队或正在展示。</summary>
        Duplicate,
        /// <summary>队列已达容量限制。</summary>
        QueueFull,
        /// <summary>业务展示回调没有成功。</summary>
        Failed
    }

    /// <summary>可等待的弹窗票据，保存结果而不持有任何表现对象。</summary>
    public sealed class RevPopupRequest
    {
        /// <summary>单一结果向所有等待者发布。</summary>
        private readonly RevTaskCompletionSource<RevPopupStatus> _completion = RevTask<RevPopupStatus>.CreateSource();

        /// <summary>这次提交在诊断中的唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>业务语义的去重键。</summary>
        public string Key { get; }
        /// <summary>当前排队或终态。</summary>
        public RevPopupStatus Status { get; private set; }
        /// <summary>稳定结果说明，可用于自动化断言。</summary>
        public string Reason { get; private set; } = string.Empty;

        /// <summary>队列受理或拒绝提交时创建。</summary>
        internal RevPopupRequest(string key)
        {
            Key = key;
        }

        /// <summary>等到弹窗完成、取消或被队列拒绝。</summary>
        public RevTask<RevPopupStatus> WaitAsync()
        {
            return _completion.Task;
        }

        /// <summary>队列清掉相应身份与表现以后发布终态。</summary>
        internal void Finish(RevPopupStatus status, string reason)
        {
            if (Status != RevPopupStatus.Pending)
            {
                return;
            }
            Status = status;
            Reason = reason;
            _completion.SetResult(status);
        }
    }

    /// <summary>拥有一个可见窗口的运行时调度器。宿主负责实际显示，队列负责任务阶段、优先级、去重和收尾。</summary>
    public sealed class RevPopupQueue : IDisposable
    {
        /// <summary>等待窗口的最大数量，超出时显式拒绝。</summary>
        private const int MaxPending = 32;

        /// <summary>只在排队或显示期间持有的工作项。</summary>
        private sealed class Entry
        {
            /// <summary>已接收的票据。</summary>
            internal RevPopupRequest Request;
            /// <summary>空串允许任何阶段，其余只在对应阶段展示。</summary>
            internal string Phase;
            /// <summary>数值越大越优先，同值按入队顺序。</summary>
            internal int Priority;
            /// <summary>最迟出队时刻，单位为实时时钟秒。</summary>
            internal double Deadline;
            /// <summary>业务提供的展示回调。</summary>
            internal Action<RevPopupRequest> Show;
            /// <summary>当前窗口关闭时负责还原表现。</summary>
            internal Action<RevPopupRequest> Hide;
        }

        /// <summary>拥有队列的域；退出会取消所有工作项。</summary>
        private readonly RevScopeHandle _scope;
        /// <summary>按提交顺序保留等待工作。</summary>
        private readonly List<Entry> _pending = new List<Entry>();
        /// <summary>当前正在展示的窗口；没有活动窗口时为空。</summary>
        private Entry _active;
        /// <summary>已挂上运行时驱动，队列空闲时解除。</summary>
        private bool _ticking;
        /// <summary>关闭后不再接受提交。</summary>
        private bool _disposed;

        /// <summary>宿主公开的当前业务阶段。</summary>
        public string Phase { get; private set; } = string.Empty;
        /// <summary>当前展示票据，只供观察和关闭时匹配。</summary>
        public RevPopupRequest Active => _active?.Request;
        /// <summary>尚未展示的请求数。</summary>
        public int PendingCount => _pending.Count;

        /// <summary>创建一个单窗口队列并随当前或指定域退出。无域的队列由创建者 Dispose。</summary>
        public RevPopupQueue(RevScopeHandle scope = null)
        {
            _scope = scope ?? RevScope.Current;
            if (_scope != null)
            {
                RevScope.Exited += OnScopeExited;
            }
        }

        /// <summary>更新业务阶段。当前窗口失去准入时立即取消，排队窗口继续等到就绪或过期。</summary>
        public void SetPhase(string phase)
        {
            if (_disposed)
            {
                return;
            }
            Phase = phase ?? string.Empty;
            if (_active != null && !IsEligible(_active))
            {
                EndActive(RevPopupStatus.Cancelled, "phase-changed");
            }
        }

        /// <summary>提交一次窗口请求。函数只负责接单，具体展示会在后续更新中发生。</summary>
        public RevPopupRequest Enqueue(string key, string phase, int priority, double waitSeconds,
            Action<RevPopupRequest> show, Action<RevPopupRequest> hide = null)
        {
            if (string.IsNullOrEmpty(key) || !(waitSeconds > 0) || double.IsInfinity(waitSeconds) || show == null)
            {
                throw new ArgumentException("弹窗需要去重键、有限的正等待时长和展示回调。");
            }
            var request = new RevPopupRequest(key);
            if (_disposed || (_scope != null && !_scope.IsOpen))
            {
                request.Finish(RevPopupStatus.Cancelled, "queue-closed");
                return request;
            }
            if (HasKey(key))
            {
                request.Finish(RevPopupStatus.Duplicate, "duplicate-key");
                return request;
            }
            if (_pending.Count >= MaxPending)
            {
                request.Finish(RevPopupStatus.QueueFull, "queue-full");
                return request;
            }
            _pending.Add(new Entry
            {
                Request = request,
                Phase = phase ?? string.Empty,
                Priority = priority,
                Deadline = Time.realtimeSinceStartupAsDouble + waitSeconds,
                Show = show,
                Hide = hide
            });
            if (!_ticking)
            {
                _ticking = RevMono.AddUpdate(Tick, owner: this);
                if (!_ticking)
                {
                    _pending.RemoveAt(_pending.Count - 1);
                    request.Finish(RevPopupStatus.Failed, "driver-unavailable");
                }
            }
            return request;
        }

        /// <summary>玩家在当前窗口作答。旧窗口的点击不会完成另一个请求。</summary>
        public bool TryComplete(RevPopupRequest request, bool confirmed)
        {
            if (request == null || Active != request)
            {
                return false;
            }
            EndActive(confirmed ? RevPopupStatus.Confirmed : RevPopupStatus.Declined, confirmed ? "confirmed" : "declined");
            return true;
        }

        /// <summary>取消一个待显示或正在展示的请求。窗口被外部关闭时应调用本入口。</summary>
        public bool Cancel(RevPopupRequest request)
        {
            if (_disposed || request == null)
            {
                return false;
            }
            if (Active == request)
            {
                EndActive(RevPopupStatus.Cancelled, "closed");
                return true;
            }
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Request == request)
                {
                    _pending.RemoveAt(i);
                    request.Finish(RevPopupStatus.Cancelled, "closed");
                    return true;
                }
            }
            return false;
        }

        /// <summary>检查展示和等待区的去重键。</summary>
        private bool HasKey(string key)
        {
            if (Active?.Key == key)
            {
                return true;
            }
            foreach (Entry entry in _pending)
            {
                if (entry.Request.Key == key)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>未声明阶段的窗口可在任意阶段展示。</summary>
        private bool IsEligible(Entry entry)
        {
            return entry.Phase.Length == 0 || entry.Phase == Phase;
        }

        /// <summary>只在有工作时按帧推进。先过期清理，再从有准入的工作中选最优项。</summary>
        private void Tick()
        {
            if (_disposed || (_scope != null && !_scope.IsOpen))
            {
                Dispose();
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                // 过期观察者可以同步取消别的项或关闭队列，要重新夹紧下一次遍历下标。
                if (i < _pending.Count && _pending[i].Deadline <= now)
                {
                    RevPopupRequest expired = _pending[i].Request;
                    _pending.RemoveAt(i);
                    expired.Finish(RevPopupStatus.Expired, "wait-expired");
                }
            }
            if (_disposed || _active != null)
            {
                return;
            }
            int best = -1;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (IsEligible(_pending[i]) && (best < 0 || _pending[i].Priority > _pending[best].Priority))
                {
                    best = i;
                }
            }
            if (best < 0)
            {
                if (_pending.Count == 0)
                {
                    RevMono.RemoveAllOf(this);
                    _ticking = false;
                }
                return;
            }
            Entry next = _pending[best];
            _pending.RemoveAt(best);
            _active = next;
            try
            {
                next.Show(next.Request);
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "弹窗展示失败", "Popup");
                if (_active == next)
                {
                    EndActive(RevPopupStatus.Failed, "show-failed");
                }
            }
        }

        /// <summary>先撤销活动身份，再隐藏窗口。在新窗口接手前可以同步处理旧窗口完成回调。</summary>
        private void EndActive(RevPopupStatus status, string reason)
        {
            Entry entry = _active;
            _active = null;
            try
            {
                entry.Hide?.Invoke(entry.Request);
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "弹窗退场失败", "Popup");
                status = RevPopupStatus.Failed;
                reason = "hide-failed";
            }
            entry.Request.Finish(status, reason);
        }

        /// <summary>作用域退出时用句柄判断原域仍然存活，避免同名重开接管旧队列。</summary>
        private void OnScopeExited(string domain)
        {
            if (!_scope.IsOpen)
            {
                Dispose();
            }
        }

        /// <summary>撤销所有请求和驱动，重复释放安全。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            RevScope.Exited -= OnScopeExited;
            RevMono.RemoveAllOf(this);
            _ticking = false;
            Entry[] pending = _pending.ToArray();
            _pending.Clear();
            if (_active != null)
            {
                EndActive(RevPopupStatus.Cancelled, "queue-closed");
            }
            foreach (Entry entry in pending)
            {
                entry.Request.Finish(RevPopupStatus.Cancelled, "queue-closed");
            }
        }
    }
}

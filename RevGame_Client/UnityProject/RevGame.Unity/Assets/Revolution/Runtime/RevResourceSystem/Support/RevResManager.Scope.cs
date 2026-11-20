// ============================================================
// RevResManager.Scope.cs —— 按域核算真实引用与渐进回收
//
// 一个资源可以同时服务多个域和预加载。账本按缓存代次登记，
// 退域先断回调和引用，再由主线程分批归还已经无人持有的 AB。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Revolution
{
    /// <summary>资源管理器的域账本。主线程写入，资源缓存和 AB 计数继续由原有加载器维护。</summary>
    public static partial class RevResManager
    {
        /// <summary>一个域的持有明细及尚未完成的业务回调。正常完成会摘除回调，退出时归还剩余引用。</summary>
        private sealed class DomainReferences
        {
            /// <summary>按底层缓存实例聚合数量。相同路径换代后另开一条，不会扣到后来缓存。</summary>
            internal readonly Dictionary<RevResHandle, int> Counts = new Dictionary<RevResHandle, int>();
            /// <summary>尚有业务续体的异步加载。退域需立即断开对业务捕获对象的引用。</summary>
            internal HashSet<ScopedLoadCallback> Pending;
            /// <summary>本域持有的引用数，供诊断使用。</summary>
            internal int Total;
        }

        /// <summary>桥接可能同步完成的加载。先完成归属登记，再把有效结果交给仍开放的域。</summary>
        private sealed class ScopedLoadCallback
        {
            /// <summary>提交时捕获的域，清理以后为空。</summary>
            private RevScopeHandle _scope;
            /// <summary>消费者的真实回调，关域即清空。</summary>
            private Action<RevResHandle> _callback;
            /// <summary>已登记到的账本，完成回调前会移出它的待办索引。</summary>
            private DomainReferences _registeredIn;
            /// <summary>同步回调和 LoadAsync 返回只允许登记一次。</summary>
            private bool _registered;

            /// <summary>在请求提交前捕获归属和回调。</summary>
            internal ScopedLoadCallback(RevScopeHandle scope, Action<RevResHandle> callback)
            {
                _scope = scope;
                _callback = callback;
            }

            /// <summary>登记本次引用和待办。同步成功及同步失败也走同一个去重门。</summary>
            internal void Register(RevResHandle handle)
            {
                if (_registered)
                {
                    return;
                }
                _registered = true;
                _registeredIn = TrackScopeReference(_scope, handle);
                if (handle.IsLoading && _registeredIn != null)
                {
                    (_registeredIn.Pending ??= new HashSet<ScopedLoadCallback>()).Add(this);
                }
            }

            /// <summary>资源就绪时先结束本请求的待办，再仅向原本有效的域交付。</summary>
            internal void Finish(RevResHandle handle)
            {
                Register(handle);
                Action<RevResHandle> callback = _callback;
                RevScopeHandle scope = _scope;
                _registeredIn?.Pending?.Remove(this);
                Abandon();
                if (scope != null)
                {
                    InvokeFinished(callback, handle, scope);
                }
            }

            /// <summary>移除所有业务引用。原底层读包可继续完成，以便同路径其它持有者取得资源。</summary>
            internal void Abandon()
            {
                _callback = null;
                _scope = null;
                _registeredIn = null;
            }
        }

        /// <summary>当前会话的访问账本。域实例作键，同名域重开也不会继承旧引用。</summary>
        private static readonly Dictionary<RevScopeHandle, DomainReferences> _scopeReferences =
            new Dictionary<RevScopeHandle, DomainReferences>();

        /// <summary>已归零并可检查的缓存代次。按入队顺序处理，加载中的项目由完成回执补入。</summary>
        private static readonly Queue<RevResHandle> _scopeReclaims = new Queue<RevResHandle>();

        /// <summary>一帧最多检查的回收项数，包含已经重新被持有的条目。</summary>
        private const int ScopeReclaimItemsPerFrame = 8;

        /// <summary>每帧回收预算，单位毫秒。已经开始的一次 AB 归还不可拆分。</summary>
        private const double ScopeReclaimBudgetMs = 1d;

        /// <summary>AB 归还之后仍需一次合并的 Unity 原生资源整理。</summary>
        private static bool _nativeSweepPending;

        /// <summary>已经提交给 Unity 的全局整理操作。飞行期间合并新的整理请求。</summary>
        private static AsyncOperation _nativeSweep;

        /// <summary>交接遮挡期申请的原生整理帧，负数表示没有尚待执行的请求。</summary>
        private static int _handoffSweepFrame = -1;

        /// <summary>把非手动请求路由到实际默认域。没有域时保留原显式计数方式。</summary>
        private static RevScopeHandle ResourceScope(RevResLifetime lifetime)
        {
            return lifetime == RevResLifetime.Automatic ? RevScope.DefaultNode : null;
        }

        /// <summary>记录已经取得的有效缓存引用。底层加载重入关闭原域时，立即归还本次引用。</summary>
        private static DomainReferences TrackScopeReference(RevScopeHandle scope, RevResHandle handle)
        {
            if (scope == null || !IsCurrent(handle))
            {
                return null;
            }
            RevResHandle owner = handle.ReferenceOwner ?? handle;
            if (!scope.IsOpen)
            {
                DecRef(handle);
                ScheduleScopeReclaim(owner);
                return null;
            }
            if (!_scopeReferences.TryGetValue(scope, out DomainReferences ledger))
            {
                ledger = new DomainReferences();
                _scopeReferences.Add(scope, ledger);
            }
            ledger.Counts.TryGetValue(owner, out int count);
            ledger.Counts[owner] = count + 1;
            ledger.Total++;
            owner.ScopeReferences++;
            return ledger;
        }

        /// <summary>按当前有效域和缓存代次提前归还一份引用。返回 false 代表本域从未持有。</summary>
        private static bool ReleaseScopeReference(RevScopeHandle scope, RevResHandle handle, bool removeWhenZero)
        {
            if (scope == null || !_scopeReferences.TryGetValue(scope, out DomainReferences ledger) ||
                !ledger.Counts.TryGetValue(handle, out int count))
            {
                return false;
            }
            if (count == 1)
            {
                ledger.Counts.Remove(handle);
            }
            else
            {
                ledger.Counts[handle] = count - 1;
            }
            ledger.Total--;
            handle.ScopeReferences--;
            ReleaseReferences(handle, 1, removeWhenZero);
            return true;
        }

        /// <summary>先切断本域所有迟到业务回执，再按实例批量归还引用。共享或常驻资源继续保留。</summary>
        internal static int ReleaseScopeReferences(RevScopeHandle scope)
        {
            if (!_scopeReferences.TryGetValue(scope, out DomainReferences ledger))
            {
                return 0;
            }
            _scopeReferences.Remove(scope);
            AbandonScopeCallbacks(ledger);

            int released = 0;
            foreach (KeyValuePair<RevResHandle, int> entry in ledger.Counts)
            {
                RevResHandle handle = entry.Key;
                handle.ScopeReferences -= entry.Value;
                if (IsCurrent(handle))
                {
                    ReleaseReferences(handle, entry.Value, removeWhenZero: true);
                    released += entry.Value;
                }
            }
            return released;
        }

        /// <summary>断开账本里的业务捕获对象。底层加载完成后只会消费空回执。</summary>
        private static void AbandonScopeCallbacks(DomainReferences ledger)
        {
            if (ledger.Pending == null)
            {
                return;
            }
            foreach (ScopedLoadCallback pending in ledger.Pending)
            {
                pending.Abandon();
            }
            ledger.Pending.Clear();
        }

        /// <summary>按域查询实际有多少份引用和未交付的回调，供只读诊断调用。</summary>
        internal static (int resources, int references, int pending) GetScopeResourceCounts(RevScopeHandle scope)
        {
            return _scopeReferences.TryGetValue(scope, out DomainReferences ledger)
                ? (ledger.Counts.Count, ledger.Total, ledger.Pending?.Count ?? 0)
                : default;
        }

        /// <summary>将实际持有此缓存实例的域写进诊断快照，无持有时快速返回。</summary>
        internal static int AppendScopeHolders(RevResHandle handle, Dictionary<string, int> holders)
        {
            if (handle.ScopeReferences == 0)
            {
                return 0;
            }
            foreach (var pair in _scopeReferences)
            {
                if (pair.Value.Counts.TryGetValue(handle, out int references))
                {
                    holders["RevScope/" + pair.Key.Name] = references;
                }
            }
            return handle.ScopeReferences;
        }

        /// <summary>把一条归零资源登记为自动回收候选。正在加载时等该代完成，不中断共享读包。</summary>
        private static void ScheduleScopeReclaim(RevResHandle handle)
        {
            if (!Application.isPlaying || handle.QueuedForScopeReclaim || handle.RefCount != 0 ||
                !handle.HasFlag(RevResInstanceFlag.MarkedUnused) ||
                handle.HasFlag(RevResInstanceFlag.Resident | RevResInstanceFlag.Preloaded))
            {
                return;
            }
            handle.QueuedForScopeReclaim = true;
            if (!handle.IsLoading)
            {
                _scopeReclaims.Enqueue(handle);
                RevResAutoUnloader.EnsureRunning();
            }
        }

        /// <summary>准备下一帧的范围回收。交接时只遍历已归零的表，覆盖场景对象先于域结案释放的租约。</summary>
        internal static void ScheduleHandoffReclaims()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            foreach (RevResHandle handle in _unused.Values)
            {
                ScheduleScopeReclaim(handle);
            }
            // 交接提供遮挡窗口。先让实例及零引用包清理结束，再合并启动一次 Unity 原生整理。
            _handoffSweepFrame = Time.frameCount;
            if (_nativeSweepPending)
            {
                RevResAutoUnloader.EnsureRunning();
            }
        }

        /// <summary>加载完成后，之前标为归零的在途句柄终于可以接受实际回收检查。</summary>
        private static void EnqueueCompletedScopeReclaim(RevResHandle handle)
        {
            if (handle.QueuedForScopeReclaim)
            {
                _scopeReclaims.Enqueue(handle);
                RevResAutoUnloader.EnsureRunning();
            }
        }

        /// <summary>按数量和时间预算归还 AB。每项再次核对代次、引用和常驻状态，以保护新进入的域。</summary>
        internal static void DrainScopeReclaims()
        {
            if (_scopeReclaims.Count == 0)
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < ScopeReclaimItemsPerFrame && _scopeReclaims.Count != 0; i++)
            {
                RevResHandle handle = _scopeReclaims.Dequeue();
                handle.QueuedForScopeReclaim = false;
                if (IsCurrent(handle) && handle.RefCount == 0 && !handle.IsLoading &&
                    !handle.HasFlag(RevResInstanceFlag.Resident | RevResInstanceFlag.Preloaded))
                {
                    ForceRemove(handle.Key);
                    _nativeSweepPending = true;
                }
                if ((Stopwatch.GetTimestamp() - started) * (1000d / Stopwatch.Frequency) >= ScopeReclaimBudgetMs)
                {
                    break;
                }
            }
        }

        /// <summary>交接期间在队列和读包空闲后收回原生对象。一次活动整理结束前继续合并新的请求。</summary>
        internal static void SweepAfterHandoff()
        {
            if (_handoffSweepFrame < 0 || Time.frameCount <= _handoffSweepFrame || _scopeReclaims.Count != 0 ||
                RevAsyncLoadPump.WaitingCount != 0 || RevAsyncLoadPump.LoadingCount != 0 ||
                (_nativeSweep != null && !_nativeSweep.isDone))
            {
                return;
            }
            _handoffSweepFrame = -1;
            SweepNativeAssets();
        }

        /// <summary>汇报等待分帧处理的项目数。已排队的资源仍需重新检查引用。</summary>
        internal static int PendingScopeReclaims => _scopeReclaims.Count;

        /// <summary>程序显式请求整理，或定时空闲维护回收已解绑的 Unity 原生资源。</summary>
        internal static void SweepNativeAssets(bool requested = false)
        {
            _nativeSweepPending |= requested;
            if (!_nativeSweepPending || (_nativeSweep != null && !_nativeSweep.isDone))
            {
                return;
            }
            _nativeSweepPending = false;
            _nativeSweep = Resources.UnloadUnusedAssets();
        }

        /// <summary>整体缓存失效时断开旧消费者、清掉队列，避免在下一轮 Play 持有之前的缓存代次。</summary>
        private static void ResetScopeReclaims()
        {
            foreach (DomainReferences ledger in _scopeReferences.Values)
            {
                AbandonScopeCallbacks(ledger);
            }
            _scopeReferences.Clear();
            _scopeReclaims.Clear();
            _handoffSweepFrame = -1;
        }
    }
}

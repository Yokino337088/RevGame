using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>处理空闲面板归属、裁剪以及实例租约的处置记录。</summary>
    public sealed partial class RevUIManager
    {
        /// <summary>每次会话最多保留的直接回收明细数。</summary>
        private const int CacheReleaseHistoryLimit = 32;
        /// <summary>正常关闭后按 KeepAlive 留在所属域。</summary>
        private const string CacheKeepAlive = "keep-alive";
        /// <summary>缓存被全局额度淘汰。</summary>
        private const string CacheCapacity = "capacity";
        /// <summary>同面板类型的额度已满，关闭的这份实例不再入池。</summary>
        private const string CachePanelLimit = "per-panel-limit";
        /// <summary>面板所属域正在或已经退出。</summary>
        private const string CacheScopeExit = "scope-exit";
        /// <summary>面板的归属与请求的域不同，旧实例直接回收。</summary>
        private const string CacheScopeMismatch = "scope-mismatch";
        /// <summary>没有实际登记的域，关闭后直接回收。</summary>
        private const string CacheNoScope = "unscoped";
        /// <summary>系统告急，先归还空闲 UI 的资源。</summary>
        private const string CacheLowMemory = "low-memory";

        /// <summary>本会话直接回收记录的有界队列，保存数据而非面板引用。</summary>
        private readonly Queue<RevUICacheEntry> _cacheReleases = new Queue<RevUICacheEntry>(CacheReleaseHistoryLimit);
        /// <summary>本会话尝试查池时取得同域空闲实例的次数。</summary>
        private int _cacheHits;
        /// <summary>本会话尝试查池但没有可复用实例的次数。</summary>
        private int _cacheMisses;
        /// <summary>本会话记录到的直接回收总数，可大于明细保留数。</summary>
        private int _cacheReleaseCount;
        /// <summary>低内存处理中的重入门。释放回调里关闭的面板也要归还资源。</summary>
        private bool _trimmingForLowMemory;

        /// <summary>为请求取得同域面板。先把命中交给请求，再释放异域实例以防回调重入。</summary>
        private RevUIPanel TakeCachedPanel(OpenRequest request)
        {
            RevUIPanel panel = _pool.Take(request.Meta.Key, request.Scope, out var discarded);
            if (panel != null)
            {
                _cacheHits++;
                request.Panel = panel;
                request.Ticket.ResourceDisposition = "Panel";
                ClosingResourceLease lease = GetPanelResourceLease(panel);
                lease.OpenId = request.Ticket.Id;
                // 旧票据已经记录回池。这份租约下一次回收由本次开窗的归属负责。
                lease.Replacement = null;
            }
            else
            {
                _cacheMisses++;
            }
            ReleaseIdlePanels(discarded, CacheScopeMismatch);
            return panel;
        }

        /// <summary>正常退场结束后决定是否能在原域内复用。失活回调返回后重新核对归属和额度。</summary>
        private void CompletePanelClose(ClosingResourceLease lease, RevScopeHandle scope, RevUIRoot closingRoot)
        {
            try
            {
                if (!CanFinishClose(lease, closingRoot))
                {
                    return;
                }

                RevUIPanel panel = lease.Panel;
                string reason = GetScopeRejection(panel, scope);
                if (reason != null || _trimmingForLowMemory)
                {
                    ReleaseForCache(lease, scope, reason ?? CacheLowMemory, wasIdle: false);
                    return;
                }
                if (ResolveCacheMode(panel) != RevUICacheMode.KeepAlive)
                {
                    closingRoot.AddPendingRelease(panel);
                    return;
                }

                if (lease.InputGate != null)
                {
                    lease.InputGate.interactable = lease.WasInteractable;
                    lease.InputGate.blocksRaycasts = lease.WasBlocking;
                    lease.InputGate = null;
                }
                panel.gameObject.SetActive(false);
                if (!CanFinishClose(lease, closingRoot))
                {
                    return;
                }

                // OnDisable 可能退出原域、关闭别的面板或调整额度，回到这里再提交缓存。
                reason = GetScopeRejection(panel, scope);
                if (reason == null)
                {
                    if (_trimmingForLowMemory)
                    {
                        reason = CacheLowMemory;
                    }
                    else if (RevUISetting.MaxIdlePanels == 0)
                    {
                        reason = CacheCapacity;
                    }
                    else if (_pool.IsFull(panel.PanelKey))
                    {
                        reason = CachePanelLimit;
                    }
                }
                if (reason != null)
                {
                    ReleaseForCache(lease, scope, reason, wasIdle: false);
                    return;
                }

                _pool.Put(panel, scope);
                if (lease.Replacement != null)
                {
                    lease.Replacement.PreviousDisposition = "Pooled";
                }
                TrimCacheToBudget();
            }
            finally
            {
                _closingResources.Remove(lease);
            }
        }

        /// <summary>检查关闭回调是否还拥有当前 UI 根与租约。根换代时只归还原引用。</summary>
        private bool CanFinishClose(ClosingResourceLease lease, RevUIRoot closingRoot)
        {
            if (lease.Released || lease.Reclaiming)
            {
                return false;
            }
            if (closingRoot == null || _root != closingRoot || lease.Panel == null)
            {
                ReleasePanelResource(lease);
                return false;
            }
            return true;
        }

        /// <summary>只有关闭开始与提交时归属同一个开放域，才具备回池条件。</summary>
        private static string GetScopeRejection(RevUIPanel panel, RevScopeHandle scope)
        {
            if (scope == null)
            {
                return CacheNoScope;
            }
            if (!scope.IsOpen)
            {
                return CacheScopeExit;
            }
            return RevScope.Core.FindOwnerOf(panel) == scope ? null : CacheScopeMismatch;
        }

        /// <summary>裁剪到全局额度，先从池摘除最久未用的实例，再执行会重入的清理钩子。</summary>
        internal void TrimCacheToBudget()
        {
            ReleaseIdlePanels(_pool.RemoveOldestUntil(RevUISetting.MaxIdlePanels), CacheCapacity);
        }

        /// <summary>回收空闲实例，保留活动和未交付的面板。由资源门卫在卸载前调用。</summary>
        internal void TrimIdleForLowMemory()
        {
            if (_trimmingForLowMemory)
            {
                return;
            }
            _trimmingForLowMemory = true;
            try
            {
                ReleaseIdlePanels(_pool.RemoveOldestUntil(0), CacheLowMemory);
            }
            finally
            {
                _trimmingForLowMemory = false;
            }
        }

        /// <summary>在退域的 UI 步骤撤销开窗，并在资源整理前同步收回该域的实例租约。</summary>
        internal int RetireScopePanels(RevScopeHandle scope)
        {
            int reclaimed = CancelScopeOpens(scope);
            reclaimed += ReleaseIdlePanels(_pool.RemoveScope(scope), CacheScopeExit);
            if (scope.Owners.Count == 0)
            {
                return reclaimed;
            }

            // 空闲池中已处置的面板会命中租约重入门。其余在本步立即结束可见状态及关闭动画。
            foreach (object owner in scope.SnapshotOwners())
            {
                if (!(owner is RevUIPanel panel) || panel == null || panel.PanelKey == null ||
                    !scope.Owners.Contains(panel))
                {
                    continue;
                }
                ClosingResourceLease lease = GetPanelResourceLease(panel);
                if (lease == null || lease.Released || lease.Reclaiming)
                {
                    continue;
                }

                OpenRequest opening = UnpublishPanel(panel);
                _pool.Forget(panel);
                if (ReleaseForCache(lease, scope, CacheScopeExit, wasIdle: false))
                {
                    reclaimed++;
                }
                if (opening != null)
                {
                    CompleteOpen(opening, null, RevUIOpenStatus.Cancelled, "scope-closed");
                }
            }
            return reclaimed;
        }

        /// <summary>销毁已从缓存索引摘出的面板，返回本次实际处理的数量。</summary>
        private int ReleaseIdlePanels(List<RevUIPanelPool.IdlePanel> idle, string reason)
        {
            if (idle == null)
            {
                return 0;
            }
            int reclaimed = 0;
            foreach (RevUIPanelPool.IdlePanel entry in idle)
            {
                if (_panelResourceLeases.TryGetValue(entry.InstanceId, out ClosingResourceLease lease) &&
                    ReleaseForCache(lease, entry.Scope, reason, wasIdle: true))
                {
                    reclaimed++;
                }
            }
            return reclaimed;
        }

        /// <summary>同步停用并归还一个面板的租约，Unity 实例在本帧销毁阶段回收。</summary>
        private bool ReleaseForCache(ClosingResourceLease lease, RevScopeHandle scope, string reason, bool wasIdle)
        {
            if (lease.Released || lease.Reclaiming)
            {
                return false;
            }

            lease.Reclaiming = true;
            _closingResources.Remove(lease);
            RevUIPanel panel = lease.Panel;
            try
            {
                if (panel != null)
                {
                    panel.InternalCloseForScope();
                    panel.gameObject.SetActive(false);
                    panel.InternalRelease();
                }
            }
            finally
            {
                // 只放这一个实例的引用。还被其他面板或宿主持有的 AB 依赖继续可用。
                ReleasePanelResource(lease);
                RecordCacheRelease(lease, scope, reason, wasIdle);
                // 让退域后续步骤仍能找到面板 owner 清理事件和计时器，OnDestroy 再从索引脱离。
                if (panel != null)
                {
                    RevUIRoot.DestroyObject(panel.gameObject);
                }
            }
            return true;
        }

        /// <summary>在归还后记录实例处置和预制体的剩余引用，裁掉最旧的历史明细。</summary>
        private void RecordCacheRelease(ClosingResourceLease lease, RevScopeHandle scope, string reason, bool wasIdle)
        {
            _cacheReleaseCount++;
            if (_cacheReleases.Count == CacheReleaseHistoryLimit)
            {
                _cacheReleases.Dequeue();
            }
            _cacheReleases.Enqueue(new RevUICacheEntry(lease.Panel.PanelKey, lease.InstanceId, scope,
                lease.OpenId, reason, Time.realtimeSinceStartupAsDouble, PrefabReferences(lease), wasIdle, lease.Released));
        }

        /// <summary>读取原预制体缓存的总引用数。同路径已换代时，这份旧租约的引用数按 0 报告。</summary>
        private static int PrefabReferences(ClosingResourceLease lease)
        {
            RevResHandle handle = lease.Resource?.Handle;
            return RevResManager.IsCurrent(handle) ? (handle.ReferenceOwner ?? handle).RefCount : 0;
        }

        /// <summary>只在查询时复制当前池和历史，业务无法更改缓存状态。</summary>
        internal RevUICacheSnapshot GetCacheSnapshot()
        {
            var idle = new RevUICacheEntry[_pool.Count];
            int i = 0;
            foreach (RevUIPanelPool.IdlePanel entry in _pool.OldestFirst)
            {
                ClosingResourceLease lease = _panelResourceLeases[entry.InstanceId];
                idle[i++] = new RevUICacheEntry(entry.Key, entry.InstanceId, entry.Scope, lease.OpenId,
                    CacheKeepAlive, entry.SinceSeconds, PrefabReferences(lease), wasIdle: true, leaseReleased: false);
            }
            return new RevUICacheSnapshot(_cacheHits, _cacheMisses, _cacheReleaseCount, idle, _cacheReleases.ToArray());
        }

        /// <summary>新一轮 Play 清空缓存诊断统计，避免混入上次会话的实例 ID。</summary>
        private void ResetCacheDiagnostics()
        {
            _cacheReleases.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
            _cacheReleaseCount = 0;
            _trimmingForLowMemory = false;
        }
    }
}

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>业务持有的一份具名资源引用。仅在主线程使用，释放时只归还取得引用时的那一代缓存。</summary>
    public sealed class RevResLease : IDisposable
    {
        /// <summary>取得引用时所在的缓存实体。失败且未取得引用时为空，释放后清空。</summary>
        private RevResHandle _ownerHandle;

        /// <summary>本次加载的句柄，可观察加载状态和读取内容。持有方用本租约释放。</summary>
        public RevResHandle Handle { get; }
        /// <summary>持有者的业务名称。多个同名租约会在查询中合并计数。</summary>
        public string Holder { get; }

        /// <summary>只接管既有加载取得的一份引用。空或已过期句柄仍可用于报告加载失败。</summary>
        internal RevResLease(RevResHandle handle, string holder)
        {
            Handle = handle;
            Holder = holder;
            if (RevResManager.IsCurrent(handle))
            {
                _ownerHandle = handle.ReferenceOwner ?? handle;
                (_ownerHandle.NamedLeases ??= new List<RevResLease>(2)).Add(this);
            }
        }

        /// <summary>撤销本次具名持有并归还实际引用。重复释放或缓存换代时不会减新资源的引用数。</summary>
        public void Dispose()
        {
            if (_ownerHandle == null)
            {
                return;
            }
            _ownerHandle.NamedLeases.Remove(this);
            _ownerHandle = null;
            RevResManager.DecRef(Handle);
        }
    }

    /// <summary>具名资源加载沿用原有缓存和资源策略，只在明确需要归属的持有点建立租约。</summary>
    public static partial class RevResManager
    {
        /// <summary>异步加载一份独立具名引用。适合预加载和长寿命宿主。回调和返回值共用租约，持有方负责归还。</summary>
        public static RevResLease LoadOwnedAsync(string rootPath, string resName, Type contentType, string holder,
            Action<RevResLease> onFinished = null, RevResGroup group = RevResGroup.Unknown)
        {
            if (string.IsNullOrWhiteSpace(holder))
            {
                throw new ArgumentException("具名资源租约需要持有者名称。", nameof(holder));
            }
            if (onFinished == null)
            {
                return OwnReference(LoadAsync(rootPath, resName, contentType, null, group, lifetime: RevResLifetime.Manual), holder);
            }

            // 缓存命中可能同步回调。回调内先建立租约，在途路径则先返回租约，之后的回调复用它。
            RevResLease lease = null;
            RevResHandle handle = LoadAsync(rootPath, resName, contentType, completed =>
            {
                lease ??= OwnReference(completed, holder);
                onFinished(lease);
            }, group, lifetime: RevResLifetime.Manual);
            return lease ??= OwnReference(handle, holder);
        }

        /// <summary>框架内部把已经取得的引用移交给具名持有者。调用者从此只释放返回的租约，计数保持原值。</summary>
        internal static RevResLease OwnReference(RevResHandle handle, string holder)
        {
            return new RevResLease(handle, holder);
        }
    }
}

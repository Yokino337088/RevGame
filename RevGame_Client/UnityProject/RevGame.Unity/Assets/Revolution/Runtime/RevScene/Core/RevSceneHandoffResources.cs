using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Revolution
{
    /// <summary>一次场景交接声明的资源及其引用实况。快照保存标识和数字，可安全跨场景保留。</summary>
    public sealed class RevSceneResourceSnapshot
    {
        /// <summary>两段式资源路径中的逻辑目录。</summary>
        public string Root { get; }
        /// <summary>资源逻辑名称。</summary>
        public string Name { get; }
        /// <summary>将该资源纳入交接的业务入口名称。</summary>
        public string Introducer { get; }
        /// <summary>该入口在交付首屏前是否等待此资源完成。</summary>
        public bool BlocksFirstFrame { get; }
        /// <summary>采样时缓存中这份资源的实际总引用数。</summary>
        public int ReferenceCount { get; }
        /// <summary>采样时各具名持有者的引用数。</summary>
        public IReadOnlyDictionary<string, int> Holders { get; }
        /// <summary>采样时未声明持有者的现有引用数。</summary>
        public int UnattributedCount { get; }
        /// <summary>采样时资源句柄是否仍持有 AB 引用。其他资源可以独立保留同一 AB。</summary>
        public bool HoldsBundleReference { get; }

        /// <summary>从实际缓存生成只读快照，不加载、续租或保留 Unity 对象。</summary>
        internal RevSceneResourceSnapshot(string root, string name, string introducer, bool blocksFirstFrame)
        {
            Root = root;
            Name = name;
            Introducer = introducer;
            BlocksFirstFrame = blocksFirstFrame;
            RevResHandle handle = RevResManager.Get(root, name);
            ReferenceCount = handle.RefCount;
            HoldsBundleReference = handle.BundleAcquired;
            var holders = new Dictionary<string, int>(StringComparer.Ordinal);
            int namedCount = 0;
            if (handle.NamedLeases != null)
            {
                foreach (RevResLease lease in handle.NamedLeases)
                {
                    holders.TryGetValue(lease.Holder, out int count);
                    holders[lease.Holder] = count + 1;
                    namedCount++;
                }
            }
            Holders = new ReadOnlyDictionary<string, int>(holders);
            UnattributedCount = Math.Max(0, ReferenceCount - namedCount);
        }
    }

    /// <summary>跟随交接 ID 保存资源来源。由业务在依赖入口及进出场边界记录，观察方可即时采样或查看历史。</summary>
    public sealed class RevSceneHandoffResources
    {
        /// <summary>本次交接声明过的资源入口。多个入口可以引入同一个资源。</summary>
        private readonly Dictionary<(string root, string name, string introducer), bool> _requirements =
            new Dictionary<(string root, string name, string introducer), bool>();

        /// <summary>交付首屏时的快照。交付前为空。</summary>
        public IReadOnlyList<RevSceneResourceSnapshot> AtReady { get; private set; }
        /// <summary>完成退场清理后的快照。之后的新一局不会改写该快照。</summary>
        public IReadOnlyList<RevSceneResourceSnapshot> AfterExit { get; private set; }
        /// <summary>产生退场快照的交接 ID。尚未有退场快照时为空。</summary>
        public Guid? ExitRequestId { get; private set; }

        /// <summary>在真实加载或等待入口记录来源。重复声明合并，同一入口有一次阻塞就标记阻塞。</summary>
        public void Require(string root, string name, string introducer, bool blocksFirstFrame)
        {
            var key = (root, name, introducer);
            _requirements.TryGetValue(key, out bool alreadyBlocked);
            _requirements[key] = alreadyBlocked || blocksFirstFrame;
        }

        /// <summary>查询此交接涉及的资源目前由谁持有。每次调用返回一份独立的只读快照。</summary>
        public IReadOnlyList<RevSceneResourceSnapshot> CaptureCurrent()
        {
            var rows = new List<RevSceneResourceSnapshot>(_requirements.Count);
            foreach (var requirement in _requirements)
            {
                var (root, name, introducer) = requirement.Key;
                rows.Add(new RevSceneResourceSnapshot(root, name, introducer, requirement.Value));
            }
            return rows.AsReadOnly();
        }

        /// <summary>首屏交付后一并冻结引用状态，重复报告保留第一次的结果。</summary>
        public void CaptureReady()
        {
            AtReady ??= CaptureCurrent();
        }

        /// <summary>退出交接成功并完成清理后冻结状态，记录对应的退出请求 ID。</summary>
        public void CaptureExit(Guid exitRequestId)
        {
            if (AfterExit != null)
            {
                return;
            }
            AfterExit = CaptureCurrent();
            ExitRequestId = exitRequestId;
        }
    }
}

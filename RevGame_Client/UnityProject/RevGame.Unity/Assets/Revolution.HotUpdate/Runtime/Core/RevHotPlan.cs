// ============================================================
// RevHotPlan.cs —— 版本比对的结果：这次更新到底要下什么
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【它是"远端清单 vs 本地基线"比对的产物】
//   · 远端有、本地没有            → Added   （新增包）
//   · 两边都有、但 hash 不一样    → Changed （内容变了，要重下）
//   · 两边都有、hash 一样         → 跳过    （不下、不校验、不落盘 —— 差量省钱的核心）
//   · 本地有、远端没有            → Removed （记录下来，切完版本后随旧版本目录清理）
//   · 映射表 hash 变了            → ResMapChanged（★ 必须下：否则"新增资源"加载不到）
//
// 【为什么没有"二进制差量"】
//   LZ4 包本身就是块压缩，改一张图会波及多个块，bsdiff 收益不稳定；
//   包级差量（只下变化的包）已经拿走了绝大部分收益 —— 这是技术方案 1.2 定下的边界。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution.HotUpdate
{
    /// <summary>一次更新要做的全部事情（下载什么、跳过什么、之后清理什么）。</summary>
    public sealed class RevHotPlan
    {
        /// <summary>本地没有的包（首次上线 / 新增资源）。</summary>
        public readonly List<RevHotBundleInfo> Added = new List<RevHotBundleInfo>();

        /// <summary>本地有但内容变了的包（hash 不同）。</summary>
        public readonly List<RevHotBundleInfo> Changed = new List<RevHotBundleInfo>();

        /// <summary>远端清单里已经不存在的包（随旧版本目录清理，不在下载列表里）。</summary>
        public readonly List<RevHotBundleInfo> Removed = new List<RevHotBundleInfo>();

        /// <summary>映射表是否需要更新（★ ResMap 变了就必须下，否则新增资源加载不到）。</summary>
        public bool ResMapChanged;

        /// <summary>需要下载的映射表（取自远端清单；ResMapChanged 为 true 时才有意义）。</summary>
        public RevHotFileInfo ResMap;

        /// <summary>本次要下载的总字节数（磁盘预检 / 进度条 / 提示玩家"要下多少"都靠它）。</summary>
        public long TotalBytes;

        /// <summary>要下载的文件数（包 + 映射表）。</summary>
        public int FileCount
        {
            get
            {
                int count = Added.Count + Changed.Count;
                if (ResMapChanged) count++;
                return count;
            }
        }

        /// <summary>是否完全不需要更新（清单一致且映射表也没变）。</summary>
        public bool IsEmpty
        {
            get { return FileCount == 0; }
        }

        /// <summary>给人看的汇总（"新增 1、变更 2、共 17.8 MB"）。</summary>
        public override string ToString()
        {
            var sb = new StringBuilder(64);
            sb.Append("新增 ").Append(Added.Count)
              .Append("、变更 ").Append(Changed.Count)
              .Append("、删除 ").Append(Removed.Count);
            if (ResMapChanged) sb.Append("、映射表 有更新");
            sb.Append(" → 需下载 ").Append(RevHotProgress.FormatBytes(TotalBytes))
              .Append("（").Append(FileCount).Append(" 个文件）");
            return sb.ToString();
        }
    }
}

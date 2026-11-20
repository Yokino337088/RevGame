// ============================================================
// RevHotPlanner.cs —— 版本比对：远端清单 vs 本地基线 → 差量计划
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Pipeline\
//
// 【什么算"本地基线"】（按优先级）
//   ① 当前资源版本目录里的清单副本（已经热更过至少一次）
//   ② 出包时随包体发布的"内置清单"（首包基线 —— 大版本锚定下的"出包状态"）
//   ③ 都没有 → 视为空清单：所有远端包都算"新增"（纯远端 / 首包瘦身模式天然支持）
//
// 【判定只用 hash】
//   "文件在不在磁盘上""大小对不对"都不参与判定 —— 内容变没变只认 hash，
//   否则"改了内容但大小恰好没变"这类巧合会直接造成线上错版。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution.HotUpdate
{
    /// <summary>版本比对器（纯逻辑，可工程外断言）。</summary>
    internal static class RevHotPlanner
    {
        /// <summary>
        /// 生成差量计划。
        /// remote：刚从 CDN 拉下来的清单（一定非空）；local：本地基线清单（可以为 null = 新装机）。
        /// </summary>
        public static RevHotPlan Build(RevHotManifest remote, RevHotManifest local)
        {
            RevHotPlan plan = new RevHotPlan();

            // ---------- ① 逐个远端包：新增 / 变更 / 跳过 ----------
            for (int i = 0; i < remote.Bundles.Count; i++)
            {
                RevHotBundleInfo remoteBundle = remote.Bundles[i];
                RevHotBundleInfo localBundle = local == null ? null : local.Find(remoteBundle.Name);

                if (localBundle == null)
                {
                    plan.Added.Add(remoteBundle);
                    continue;
                }

                // ★ hash 相同 = 内容一样：跳过（不下、不校验、不落盘）。大小不参与判定。
                if (string.Equals(localBundle.Sha256, remoteBundle.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                plan.Changed.Add(remoteBundle);
            }

            // ---------- ② 本地有、远端没有 → 记录待清理 ----------
            if (local != null)
            {
                for (int i = 0; i < local.Bundles.Count; i++)
                {
                    RevHotBundleInfo localBundle = local.Bundles[i];
                    if (remote.Find(localBundle.Name) == null) plan.Removed.Add(localBundle);
                }
            }

            // ---------- ③ 映射表：hash 变了就下（新增资源全靠它） ----------
            if (remote.ResMap != null)
            {
                RevHotFileInfo localResMap = local == null ? null : local.ResMap;
                bool same = localResMap != null
                            && string.Equals(localResMap.Sha256, remote.ResMap.Sha256, StringComparison.OrdinalIgnoreCase);

                if (same == false)
                {
                    plan.ResMapChanged = true;
                    plan.ResMap = remote.ResMap;
                }
            }

            // ---------- ④ 合计要下载的字节 ----------
            long total = 0;
            for (int i = 0; i < plan.Added.Count; i++) total += plan.Added[i].Size;
            for (int i = 0; i < plan.Changed.Count; i++) total += plan.Changed[i].Size;
            if (plan.ResMap != null) total += plan.ResMap.Size;
            plan.TotalBytes = total;

            return plan;
        }
    }
}

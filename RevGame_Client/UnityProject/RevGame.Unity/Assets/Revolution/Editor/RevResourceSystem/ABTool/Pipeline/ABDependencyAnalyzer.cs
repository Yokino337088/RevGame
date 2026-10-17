// ============================================================
// ABDependencyAnalyzer.cs —— 打包后的依赖分析
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【为什么要在"打包之后"分析？】
//   依赖关系是 Unity 打包时才算出来的，结果放在产物 AssetBundleManifest 里。
//   所以流程是"先打包，再读 Manifest 分析"。
//
// 【回答三个关键问题】
//   Q1 每个包依赖哪些包？      → 加载时必须先加载依赖包
//   Q2 哪些资源被多个包共享？   → 会被复制进每个包，体积膨胀
//   Q3 有没有循环依赖？        → A↔B 加载顺序无解，必须修
// ============================================================
using System;                            // StringComparer
using System.Collections.Generic;
using System.Linq;                       // SortedSet.ToArray()
using UnityEditor;
using UnityEngine;      // AssetBundleManifest 在 UnityEngine 里（UnityEngine.AssetBundleModule）

namespace Revolution.Editor
{
    public class ABDependencyReport
    {
        /// <summary>包名 → 它直接依赖的包名数组</summary>
        public readonly Dictionary<string, string[]> directDeps = new Dictionary<string, string[]>();

        /// <summary>被 2 个以上包共享的资源 → 出现在哪些包里</summary>
        public readonly Dictionary<string, List<string>> sharedAssets = new Dictionary<string, List<string>>();

        /// <summary>形成环状依赖的包（正常工程应为空）</summary>
        public readonly List<string> circularBundles = new List<string>();

        /// <summary>
        /// 包名 → 它会"顺带背进包里"的未分包资源总字节（估算）。
        /// 只有编辑器侧分析（AnalyzeFromDatabase）会填：读 AssetBundleManifest 时 Unity 已经把这些
        /// 复制进去算过了，体积从产物文件就能看到。
        /// </summary>
        public readonly Dictionary<string, long> extraBytes = new Dictionary<string, long>();

        public bool HasIssue => sharedAssets.Count > 0 || circularBundles.Count > 0;
    }

    public static class ABDependencyAnalyzer
    {
        public static ABDependencyReport Analyze(AssetBundleManifest manifest)
        {
            var report = new ABDependencyReport();
            if (manifest == null) return report;

            string[] allBundles = manifest.GetAllAssetBundles();

            // ---------- Q1：直接依赖 ----------
            foreach (string bundle in allBundles)
                report.directDeps[bundle] = manifest.GetAllDependencies(bundle);

            // ---------- Q2：共享资源（被多个包引用 → 复制多份）----------
            var assetOwners = new Dictionary<string, List<string>>();
            foreach (string bundle in allBundles)
            {
                foreach (string asset in AssetDatabase.GetAssetPathsFromAssetBundle(bundle))
                {
                    if (!assetOwners.TryGetValue(asset, out List<string> owners))
                    {
                        owners = new List<string>();
                        assetOwners[asset] = owners;
                    }
                    owners.Add(bundle);
                }
            }

            foreach (var kv in assetOwners)
                if (kv.Value.Count >= 2)
                    report.sharedAssets[kv.Key] = kv.Value;

            // ---------- Q3：循环依赖 ----------
            foreach (string bundle in allBundles)
                if (HasCycle(bundle, bundle, report.directDeps, new HashSet<string>()))
                    report.circularBundles.Add(bundle);

            return report;
        }

        /// <summary>资源文件的字节数（拿不到就当 0，不让统计卡住）</summary>
        private static long FileSizeOf(string assetPath)
        {
            string abs = System.IO.Path.GetFullPath(assetPath);
            return System.IO.File.Exists(abs) ? new System.IO.FileInfo(abs).Length : 0;
        }

        /// <summary>深度优先搜索：从 current 出发能否沿依赖链绕回 origin</summary>
        private static bool HasCycle(string origin, string current,
            Dictionary<string, string[]> deps, HashSet<string> visited)
        {
            if (!deps.TryGetValue(current, out string[] list)) return false;

            foreach (string dep in list)
            {
                if (dep == origin) return true;             // 绕回起点 → 有环
                if (!visited.Add(dep)) continue;            // 剪枝，避免死循环
                if (HasCycle(origin, dep, deps, visited)) return true;
            }
            return false;
        }

        // ============================================================
        // 编辑器侧：不打包也能算依赖（改完标记立刻能看）
        // ============================================================

        /// <summary>
        /// 【编辑器侧】用 AssetDatabase 反推"包 → 包"的依赖，不需要先打一次包。
        ///
        /// 【怎么算】对每个包里的每个资源，用 GetDependencies(recursive:true) 列出它引用到的所有资源，
        ///   再看这些资源"归哪个包"（GetImplicitAssetBundleName；文件夹标记也算）：
        ///     · 归【别的包】   → 本包依赖那个包 → directDeps；加载时必须先加载依赖包；
        ///     · 归【任何包都没有】→ 它会被复制进每个引用它的包 → sharedAssets（体积膨胀的元凶）。
        ///
        /// 【和 Analyze(manifest) 的分工】
        ///   · Analyze：读 Unity 真正算出来的 AssetBundleManifest —— 权威，但必须"先打包"；
        ///   · 本方法：编辑器估算，改完分包立刻能看，用来【提前】发现问题（不替代打包后的复核）。
        /// </summary>
        /// <param name="collect">只读扫描结果</param>
        /// <param name="progress">
        /// 可选进度回调（0~1，当前包名）；返回 true = 使用者点了取消 → 本方法返回 null。
        /// ★ 大工程里这一步要对每个资源跑一次 GetDependencies（可能几秒），窗口靠它显示可取消的进度条。
        /// </param>
        public static ABDependencyReport AnalyzeFromDatabase(ABCollectResult collect,
            Func<float, string, bool> progress = null)
        {
            var report = new ABDependencyReport();

            // 资源路径 → 引用它的包（用来找出"没分包、却被多个包引用"的资源）
            var owners = new Dictionary<string, HashSet<string>>();

            int done = 0;
            int total = Math.Max(1, collect.bundleToAssets.Count);

            foreach (var pair in collect.bundleToAssets)
            {
                string bundle = pair.Key;

                if (progress != null && progress(done++ / (float)total, bundle)) return null;   // 取消
                var deps = new SortedSet<string>(StringComparer.Ordinal);

                // 本包"顺带背进包里"的未分包资源（同一个资源只算一次）
                var localUnbundled = new HashSet<string>();
                long extra = 0;

                foreach (string logic in pair.Value)
                {
                    if (!collect.logicToAssetPath.TryGetValue(logic, out string assetPath)) continue;

                    foreach (string dep in AssetDatabase.GetDependencies(assetPath, true))
                    {
                        if (dep == assetPath) continue;                      // 自己不算依赖
                        if (!dep.StartsWith("Assets/")) continue;            // 内置资源 / 包外路径不管
                        if (dep.EndsWith(".cs")) continue;                   // 脚本不进包

                        string owner = AssetDatabase.GetImplicitAssetBundleName(dep);
                        if (!string.IsNullOrEmpty(owner))
                        {
                            if (owner != bundle) deps.Add(owner);            // 跨包依赖
                            continue;
                        }

                        if (!owners.TryGetValue(dep, out HashSet<string> set))
                        {
                            set = new HashSet<string>();
                            owners[dep] = set;
                        }
                        set.Add(bundle);

                        if (localUnbundled.Add(dep)) extra += FileSizeOf(dep);
                    }
                }

                report.directDeps[bundle] = deps.ToArray();
                report.extraBytes[bundle] = extra;
            }

            // 被 2 个以上包引用的"无主"资源 → 一定会被复制多份
            foreach (var pair in owners)
                if (pair.Value.Count >= 2)
                    report.sharedAssets[pair.Key] = new List<string>(pair.Value);

            foreach (string bundle in report.directDeps.Keys)
                if (HasCycle(bundle, bundle, report.directDeps, new HashSet<string>()))
                    report.circularBundles.Add(bundle);

            return report;
        }
    }
}

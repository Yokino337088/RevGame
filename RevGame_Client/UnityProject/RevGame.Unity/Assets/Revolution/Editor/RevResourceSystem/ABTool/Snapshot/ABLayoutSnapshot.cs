// ============================================================
// ABLayoutSnapshot.cs —— 分包布局快照 + 差异对比（纯 C#，不依赖 UnityEditor）
//
// 位置：Editor\RevResourceSystem\ABTool\Snapshot\
//
// 【要解决的问题】
//   "我这次到底动了什么" —— 分包是一堆散在各资源 .meta 里的标记，改完之后光看现状看不出变化：
//   哪个包是新加的、哪个资源换包了、哪个体积涨了、包名是不是改了。快照把"某一刻的分包布局"
//   整体记下来，之后任何时候都能逐包、逐资源地比。
//
// 【快照里存什么（只存"能看出问题"的最小集合）】
//   · 每个包的名字、资源数、字节数；
//   · 包内资源的 Unity 路径（**排序后**存：报告稳定，diff 也稳定）。
//   ★ 字节数取的是**源文件体积**，不是打包后的 AB 体积 —— 它不需要先打一次包就能拿到，
//     适合"边改边比"。要精确到产物，请看「体积对比」页签里的打包结果。
//
// 【差异都报什么】
//   · 新增 / 删除的包；
//   · **改名**（内容一模一样、只是包名变了）—— 单独识别：否则会显示成"删一个 + 加一个"，
//     看报告的人会以为资源被大搬家了；
//   · 资源换包（A 包 → B 包）；★ 配对成"改名"的那些包里的资源不算（那是改名，不是搬家）；
//   · 每个包资源的增删 + 体积变化。
//
// 【为什么这个文件是纯 C#】
//   差异算法是最容易写错、也最值得单独验证的一块。这里不 using UnityEngine，
//   就能脱离 Unity 直接跑断言（序列化 / 落盘 / 与扫描结果对接在 ABLayoutSnapshotStore 里）。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution.Editor
{
    /// <summary>一个包在快照里的样子</summary>
    [Serializable]
    public sealed class ABLayoutBundle
    {
        public string name;
        public int count;

        /// <summary>包内资源的源文件字节数合计（不是打包后的 AB 体积）</summary>
        public long bytes;

        /// <summary>包内资源（Unity 路径，已排序）</summary>
        public List<string> assets = new List<string>();
    }

    /// <summary>某一刻的分包布局</summary>
    [Serializable]
    public sealed class ABLayoutSnapshot
    {
        /// <summary>快照格式版本（以后加字段时用它判断兼容）</summary>
        public int version = 1;

        /// <summary>记录时间（UTC，ISO 8601）</summary>
        public string timeUtc = "";

        /// <summary>怎么来的（如"手动记录基准""仅生成映射""打包"），显示用</summary>
        public string note = "";

        public List<ABLayoutBundle> bundles = new List<ABLayoutBundle>();

        public int BundleCount => bundles.Count;

        public int AssetCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < bundles.Count; i++) total += bundles[i].count;
                return total;
            }
        }

        public long TotalBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < bundles.Count; i++) total += bundles[i].bytes;
                return total;
            }
        }

        public ABLayoutBundle Find(string bundleName)
        {
            for (int i = 0; i < bundles.Count; i++)
                if (bundles[i].name == bundleName) return bundles[i];

            return null;
        }
    }

    /// <summary>某个包自己的增删明细</summary>
    public struct ABBundleDelta
    {
        public string name;
        public List<string> added;
        public List<string> removed;
        public long bytesBefore;
        public long bytesAfter;

        public long ByteDelta => bytesAfter - bytesBefore;
    }

    /// <summary>改名：内容一致、只有包名变了</summary>
    public struct ABBundleRename
    {
        public string before;
        public string after;
        public int count;
    }

    /// <summary>资源换包：从 from 挪到了 to</summary>
    public struct ABAssetMove
    {
        public string asset;
        public string from;
        public string to;
    }

    /// <summary>两次布局的差异（before 为空时只填"当前"的概览，hasBaseline = false）</summary>
    public sealed class ABLayoutDiff
    {
        public bool hasBaseline;

        public string beforeTime, afterTime;
        public int bundlesBefore, bundlesAfter;
        public int assetsBefore, assetsAfter;
        public long bytesBefore, bytesAfter;

        public readonly List<ABBundleRename> renamed = new List<ABBundleRename>();
        public readonly List<ABLayoutBundle> added = new List<ABLayoutBundle>();
        public readonly List<ABLayoutBundle> removed = new List<ABLayoutBundle>();
        public readonly List<ABAssetMove> moved = new List<ABAssetMove>();
        public readonly List<ABBundleDelta> changed = new List<ABBundleDelta>();

        public int BundleDelta => bundlesAfter - bundlesBefore;
        public int AssetDelta => assetsAfter - assetsBefore;
        public long ByteDelta => bytesAfter - bytesBefore;

        /// <summary>
        /// 两次布局是否一致。
        /// ★ 没有基准（<see cref="hasBaseline"/> == false）时永远返回 false ——
        ///   否则界面会显示成"与基准完全一致"，而实际上压根没比过。
        /// </summary>
        public bool IsEmpty
            => hasBaseline
            && renamed.Count == 0 && added.Count == 0 && removed.Count == 0
            && moved.Count == 0 && changed.Count == 0;
    }

    /// <summary>差异算法（纯函数：给两份快照，出一份差异）</summary>
    public static class ABLayoutDiffer
    {
        public static ABLayoutDiff Compare(ABLayoutSnapshot before, ABLayoutSnapshot after)
        {
            var diff = new ABLayoutDiff();

            after = after ?? new ABLayoutSnapshot();

            diff.afterTime = after.timeUtc;
            diff.bundlesAfter = after.BundleCount;
            diff.assetsAfter = after.AssetCount;
            diff.bytesAfter = after.TotalBytes;

            if (before == null)
                return diff;                                       // 还没有基准：只回"当前"概览

            diff.hasBaseline = true;
            diff.beforeTime = before.timeUtc;
            diff.bundlesBefore = before.BundleCount;
            diff.assetsBefore = before.AssetCount;
            diff.bytesBefore = before.TotalBytes;

            Dictionary<string, ABLayoutBundle> oldMap = Index(before);
            Dictionary<string, ABLayoutBundle> newMap = Index(after);

            // ---------- ① 改名配对：先算出来，后面的"资源换包"要把它们排除掉 ----------
            var namesBefore = new List<string>(oldMap.Keys);
            var namesAfter = new List<string>(newMap.Keys);
            namesBefore.Sort(string.CompareOrdinal);
            namesAfter.Sort(string.CompareOrdinal);

            var pairedOld = new HashSet<string>();
            var pairedNew = new HashSet<string>();
            var renamedPairs = new HashSet<string>();              // "旧名\n新名"

            foreach (string oldName in namesBefore)
            {
                if (newMap.ContainsKey(oldName)) continue;         // 两边都有 → 不是改名

                ABLayoutBundle oldBundle = oldMap[oldName];
                if (oldBundle.count == 0) continue;                // 空包之间不值得配对

                foreach (string newName in namesAfter)
                {
                    if (oldMap.ContainsKey(newName) || pairedNew.Contains(newName)) continue;

                    ABLayoutBundle newBundle = newMap[newName];
                    if (newBundle.count != oldBundle.count) continue;
                    if (!SameAssets(oldBundle, newBundle)) continue;

                    pairedOld.Add(oldName);
                    pairedNew.Add(newName);
                    renamedPairs.Add(oldName + "\n" + newName);

                    diff.renamed.Add(new ABBundleRename
                    {
                        before = oldName,
                        after = newName,
                        count = oldBundle.count
                    });
                    break;                                         // 一个旧包只配一个新包（按名字序取第一个，结果稳定）
                }
            }

            diff.renamed.Sort((a, b) => string.CompareOrdinal(a.before, b.before));

            // ---------- ② 新增 / 删除的包（配过对的排除掉）----------
            foreach (string name in namesAfter)
                if (!oldMap.ContainsKey(name) && !pairedNew.Contains(name))
                    diff.added.Add(newMap[name]);

            foreach (string name in namesBefore)
                if (!newMap.ContainsKey(name) && !pairedOld.Contains(name))
                    diff.removed.Add(oldMap[name]);

            diff.added.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            diff.removed.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            // ---------- ③ 资源换包 ----------
            //   改名对里的资源不算（那是改名）；只在"两边都存在"的包里对不上才算换包。
            var ownerBefore = new Dictionary<string, string>();
            foreach (ABLayoutBundle bundle in before.bundles)
                foreach (string asset in bundle.assets)
                    ownerBefore[asset] = bundle.name;

            foreach (ABLayoutBundle bundle in after.bundles)
            {
                foreach (string asset in bundle.assets)
                {
                    if (!ownerBefore.TryGetValue(asset, out string from)) continue;   // 新增的资源
                    if (from == bundle.name) continue;                                 // 没动

                    if (renamedPairs.Contains(from + "\n" + bundle.name)) continue;     // 改名的包里的资源
                    if (pairedOld.Contains(from) || pairedNew.Contains(bundle.name)) continue;

                    diff.moved.Add(new ABAssetMove { asset = asset, from = from, to = bundle.name });
                }
            }

            diff.moved.Sort((a, b) => string.CompareOrdinal(a.asset, b.asset));

            // ---------- ④ 两边都有的包：资源增删 + 体积变化 ----------
            foreach (string name in namesBefore)
            {
                if (!newMap.TryGetValue(name, out ABLayoutBundle newBundle)) continue;

                ABLayoutBundle oldBundle = oldMap[name];

                var oldAssets = new HashSet<string>(oldBundle.assets);
                var newAssets = new HashSet<string>(newBundle.assets);

                List<string> addedAssets = null;
                foreach (string asset in newBundle.assets)
                {
                    if (oldAssets.Contains(asset)) continue;
                    addedAssets ??= new List<string>();
                    addedAssets.Add(asset);
                }

                List<string> removedAssets = null;
                foreach (string asset in oldBundle.assets)
                {
                    if (newAssets.Contains(asset)) continue;
                    removedAssets ??= new List<string>();
                    removedAssets.Add(asset);
                }

                if (addedAssets == null && removedAssets == null && oldBundle.bytes == newBundle.bytes)
                    continue;                                       // 这个包没变

                diff.changed.Add(new ABBundleDelta
                {
                    name = name,
                    added = addedAssets ?? new List<string>(),
                    removed = removedAssets ?? new List<string>(),
                    bytesBefore = oldBundle.bytes,
                    bytesAfter = newBundle.bytes
                });
            }

            // 变化大的排前面（先看体积动得多的），体积相同按名字，保证顺序稳定
            diff.changed.Sort((a, b) =>
            {
                int byBytes = Math.Abs(b.ByteDelta).CompareTo(Math.Abs(a.ByteDelta));
                return byBytes != 0 ? byBytes : string.CompareOrdinal(a.name, b.name);
            });

            return diff;
        }

        private static Dictionary<string, ABLayoutBundle> Index(ABLayoutSnapshot snapshot)
        {
            var map = new Dictionary<string, ABLayoutBundle>(snapshot.bundles.Count);
            foreach (ABLayoutBundle bundle in snapshot.bundles) map[bundle.name] = bundle;
            return map;
        }

        /// <summary>两个包的资源集合是否完全一致（按集合比，不依赖列表顺序）</summary>
        private static bool SameAssets(ABLayoutBundle a, ABLayoutBundle b)
        {
            if (a.assets.Count != b.assets.Count) return false;

            var set = new HashSet<string>(a.assets, StringComparer.Ordinal);
            foreach (string asset in b.assets)
                if (!set.Contains(asset)) return false;

            return true;
        }
    }
}

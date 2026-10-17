// ============================================================
// ABLayoutSnapshotStore.cs —— 快照的"取 / 存 / 落盘"（差异算法在 ABLayoutSnapshot.cs）
//
// 位置：Editor\RevResourceSystem\ABTool\Snapshot\
//
// 【存在哪、为什么存那】
//   Library/Revolution/RevAB/ 下（不进版本库、不污染工程、Unity 清理 Library 时一起没）：
//   （更名前在 Library/Revolution/LiteAB/ —— 第一次用到时自动搬过来，旧的基准不会丢）
//     · layout-baseline.json  —— 手动点「记录当前布局」写的对比基准；
//     · layout-last-build.json —— 每次「仅生成映射」/「打包」成功后自动留底。
//   ★ "上次打包时是什么样"这件事，谁都不可能事后补算出来 —— 必须在那一刻顺手存。
//     所以这两个成功路径里各写一行（失败只记警告，绝不影响打包主流程）。
//
// 【和扫描结果的关系】
//   ABCollectResult 是"当前扫描到的分包"，快照是"某一刻它的精简副本"：
//   只留包名 / 资源路径 / 字节数（源文件体积），去掉逻辑名、校验信息等运行时无关的东西 ——
//   这样文件小、好读、也能直接 diff。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    internal static class ABLayoutSnapshotStore
    {
        internal const string BaselineFile = "layout-baseline.json";
        internal const string LastBuildFile = "layout-last-build.json";

        private static bool _migrated;

        /// <summary>快照目录（Library 下；工程里不会多出文件）</summary>
        internal static string Directory_
        {
            get
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/Revolution/RevAB"));
                if (!_migrated) MigrateLegacyDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 更名前（LiteAB）的快照目录 → 新目录，只搬一次。
        /// ★ 搬不动（被占用 / 权限）只告警、不抛：最坏情况是"少了一份对比基准"，不影响打包。
        /// </summary>
        private static void MigrateLegacyDirectory(string dir)
        {
            _migrated = true;

            string legacy = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/Revolution/LiteAB"));
            if (System.IO.Directory.Exists(dir) || !System.IO.Directory.Exists(legacy)) return;

            try
            {
                System.IO.Directory.Move(legacy, dir);
            }
            catch (Exception e)
            {
                RevABLog.Warn($"[RevAB] 旧快照目录搬迁失败（不影响打包，只是旧的对比基准用不上了）：{legacy}\n{e.Message}");
            }
        }

        internal static string BaselinePath => Path.Combine(Directory_, BaselineFile);
        internal static string LastBuildPath => Path.Combine(Directory_, LastBuildFile);

        internal static bool HasBaseline => File.Exists(BaselinePath);
        internal static bool HasLastBuild => File.Exists(LastBuildPath);

        /// <summary>
        /// 快照文件的"记录时间"文字（给窗口显示）。
        /// ★ 按文件修改时间缓存：窗口每帧都要显示两个基准的时间，不缓存就是每帧读两次 JSON。
        /// </summary>
        internal static string StampOf(string path)
        {
            if (!File.Exists(path)) return null;

            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (_stampCache.TryGetValue(path, out KeyValuePair<DateTime, string> hit) && hit.Key == modified)
                return hit.Value;

            ABLayoutSnapshot snapshot = Load(path);
            string stamp = snapshot == null ? null : snapshot.timeUtc;
            _stampCache[path] = new KeyValuePair<DateTime, string>(modified, stamp);
            return stamp;
        }

        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> _stampCache =
            new Dictionary<string, KeyValuePair<DateTime, string>>();

        /// <summary>把"当前扫描结果"变成快照（不落盘）</summary>
        internal static ABLayoutSnapshot From(ABCollectResult collect, string note)
        {
            var snapshot = new ABLayoutSnapshot
            {
                timeUtc = DateTime.UtcNow.ToString("o"),
                note = note ?? ""
            };

            if (collect == null) return snapshot;

            foreach (KeyValuePair<string, List<string>> pair in collect.bundleToAssets)
            {
                var bundle = new ABLayoutBundle { name = pair.Key };

                foreach (string logic in pair.Value)
                {
                    if (!collect.logicToAssetPath.TryGetValue(logic, out string assetPath)) continue;

                    bundle.assets.Add(assetPath);
                    bundle.bytes += FileSize(assetPath);
                }

                // ★ 排序后存：报告稳定、diff 稳定（改名配对就是按"资源集合完全一致"判断的）
                bundle.assets.Sort(string.CompareOrdinal);
                bundle.count = bundle.assets.Count;
                snapshot.bundles.Add(bundle);
            }

            snapshot.bundles.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return snapshot;
        }

        /// <summary>取当前布局的快照（扫描缓存失效后才会重算，别每帧调）</summary>
        internal static ABLayoutSnapshot Capture(string note)
            => From(ABCollectCache.Get(), note);

        /// <summary>读快照（不存在 / 损坏 → null，不抛）</summary>
        internal static ABLayoutSnapshot Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonUtility.FromJson<ABLayoutSnapshot>(json);
            }
            catch (Exception e)
            {
                RevABLog.Warn($"[RevAB] 快照读取失败（当作没有基准）：{path}\n{e.Message}");
                return null;
            }
        }

        /// <summary>写快照（目录不存在会自动建）</summary>
        internal static void Save(string path, ABLayoutSnapshot snapshot)
        {
            if (snapshot == null) return;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
        }

        /// <summary>
        /// 自动留底（打包 / 生成映射成功后调用）。
        /// ★ 用 try 包住：留底失败只是"少了一次对比基准"，绝不能让打包流程跟着报错。
        /// </summary>
        internal static void WriteLastBuild(ABCollectResult collect, string note)
        {
            try
            {
                Save(LastBuildPath, From(collect, note));
            }
            catch (Exception e)
            {
                RevABLog.Warn($"[RevAB] 「上次打包」快照留底失败（不影响打包结果）：{e.Message}");
            }
        }

        private static long FileSize(string assetPath)
        {
            string abs = Path.GetFullPath(assetPath);
            return File.Exists(abs) ? new FileInfo(abs).Length : 0;
        }
    }
}

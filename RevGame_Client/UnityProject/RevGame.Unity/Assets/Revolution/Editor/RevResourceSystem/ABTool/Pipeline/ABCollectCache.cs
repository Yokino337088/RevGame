// ============================================================
// ABCollectCache.cs —— 窗口共用的三份缓存：只读扫描 / 文件体积 / 依赖分析
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【为什么要缓存】
//   打包窗口的六个页签看的是同一份"当前分包"数据。每帧、每个页签各扫一遍工程，
//   窗口一开编辑器就卡 —— 所以：
//     · 扫描结果只在"标记可能变了"（ABMarkerWatcher 失效它）时重扫一次；
//     · 文件体积（要读磁盘）跟着扫描结果换代，同一代里每个文件只读一次；
//     · 依赖分析最贵（每个资源一次 GetDependencies），同一份扫描只算一次，
//       而且**不在绘制里同步算**：先返回"分析中"，下一帧用可取消的进度条算完再重绘。
//
// 【Version】扫描结果每换一代就 +1。视图用它判断"要不要重建自己的列表"，
//   比拿对象引用比较更直观，也方便多个缓存对齐同一代。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace Revolution.Editor
{
    /// <summary>只读扫描结果的共享缓存（六个页签共用；标记一变就失效）</summary>
    internal static class ABCollectCache
    {
        private static ABCollectResult _collect;
        private static bool _dirty = true;

        /// <summary>扫描结果的代数：每重扫一次 +1（视图据此决定要不要重建列表）</summary>
        public static int Version { get; private set; }

        public static void Invalidate() => _dirty = true;

        public static ABCollectResult Get()
        {
            if (_dirty || _collect == null)
            {
                _collect = ABCollector.CollectReadOnly();
                _dirty = false;
                Version++;
            }
            return _collect;
        }
    }

    /// <summary>
    /// 文件体积缓存：跟着扫描结果换代（同一代里每个文件只 stat 一次）。
    /// ★ 以前「体积」「分包」页签每帧都给每个资源 new FileInfo —— 资源一多窗口就明显发涩。
    /// </summary>
    internal static class ABSizeCache
    {
        private static int _version = -1;
        private static readonly Dictionary<string, long> _files = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _bundles = new Dictionary<string, long>(StringComparer.Ordinal);

        private static void Sync()
        {
            if (_version == ABCollectCache.Version) return;

            _version = ABCollectCache.Version;
            _files.Clear();
            _bundles.Clear();
        }

        /// <summary>资源源文件字节数（文件不存在 = 0）</summary>
        public static long OfAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return 0;
            Sync();

            if (_files.TryGetValue(assetPath, out long size)) return size;

            string abs = Path.GetFullPath(assetPath);
            size = File.Exists(abs) ? new FileInfo(abs).Length : 0;     // 文件夹 / 已删除 → 0
            _files[assetPath] = size;
            return size;
        }

        /// <summary>某个包里所有资源的源文件字节数合计（不是打包后的 AB 体积）</summary>
        public static long OfBundle(ABCollectResult collect, string bundle)
        {
            if (collect == null || string.IsNullOrEmpty(bundle)) return 0;
            Sync();

            if (_bundles.TryGetValue(bundle, out long total)) return total;

            total = 0;
            if (collect.bundleToAssets.TryGetValue(bundle, out List<string> logics))
                foreach (string logic in logics)
                    if (collect.logicToAssetPath.TryGetValue(logic, out string path)) total += OfAsset(path);

            _bundles[bundle] = total;
            return total;
        }
    }

    /// <summary>
    /// 依赖分析结果缓存：同一份扫描只算一次。
    /// 三种取法：
    ///   · <see cref="Peek"/>          已算好就给，没有就 null（绝不触发计算）；
    ///   · <see cref="GetOrSchedule"/>  没算好就安排到下一帧算（带可取消进度条），本帧返回 null ——
    ///                                   视图显示"正在分析"，算完自动重绘；**绘制里用这个**；
    ///   · <see cref="Get"/>           同步算（阻塞，带进度条）—— 只在按钮回调里用。
    /// </summary>
    internal static class ABDependencyCache
    {
        /// <summary>分析超过这么久才弹进度条：小工程一闪而过的进度条比卡一下更烦人</summary>
        private const double ProgressDelaySeconds = 0.25;

        private static ABCollectResult _forCollect;
        private static ABDependencyReport _report;
        private static ABCollectResult _cancelledFor;
        private static bool _scheduled;

        /// <summary>已经算好的结果（没有就 null，不触发计算）</summary>
        public static ABDependencyReport Peek(ABCollectResult collect)
            => _report != null && ReferenceEquals(_forCollect, collect) ? _report : null;

        /// <summary>这份扫描的依赖分析是不是被使用者取消了（视图据此显示"重新分析"按钮，而不是一直重试）</summary>
        public static bool WasCancelled(ABCollectResult collect) => ReferenceEquals(_cancelledFor, collect);

        /// <summary>绘制里用：没算好就安排下一帧算，本帧返回 null</summary>
        public static ABDependencyReport GetOrSchedule(ABCollectResult collect)
        {
            ABDependencyReport ready = Peek(collect);
            if (ready != null || collect == null || WasCancelled(collect) || _scheduled) return ready;

            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                Get(ABCollectCache.Get());               // 用执行那一刻的最新扫描
                ABMarkerWatcher.RepaintOpenWindows();
            };
            return null;
        }

        /// <summary>同步算（按钮回调里用）；取消返回 null</summary>
        public static ABDependencyReport Get(ABCollectResult collect)
        {
            ABDependencyReport ready = Peek(collect);
            if (ready != null || collect == null) return ready;

            var clock = Stopwatch.StartNew();
            bool shown = false;
            ABDependencyReport report;

            try
            {
                report = ABDependencyAnalyzer.AnalyzeFromDatabase(collect, (progress, bundle) =>
                {
                    if (!shown && clock.Elapsed.TotalSeconds < ProgressDelaySeconds) return false;
                    shown = true;
                    return EditorUtility.DisplayCancelableProgressBar("RevAB · 分析依赖",
                        $"正在分析包 {bundle}（{progress:P0}）", progress);
                });
            }
            finally
            {
                if (shown) EditorUtility.ClearProgressBar();
            }

            if (report == null)
            {
                _cancelledFor = collect;                 // 取消：这一代不再自动重试，等使用者点「重新分析」
                return null;
            }

            _cancelledFor = null;
            _forCollect = collect;
            _report = report;
            return report;
        }

        /// <summary>清掉"已取消"标记（「重新分析」按钮用）</summary>
        public static void ClearCancelled() => _cancelledFor = null;
    }

    /// <summary>页签角标用的"问题数"（只数不需要依赖分析的那几类，足够便宜，可以每帧算）</summary>
    internal static class ABIssueCounter
    {
        public static int Count(ABCollectResult collect)
        {
            if (collect == null) return 0;

            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            bool checkUnmarked = cfg == null || cfg.checkUnmarkedAssets;

            return collect.duplicateAssets.Count
                 + collect.duplicateLogicDetail.Count
                 + collect.emptyBundles.Count
                 + (checkUnmarked ? collect.unmarkedAssets.Count : 0);
        }
    }
}

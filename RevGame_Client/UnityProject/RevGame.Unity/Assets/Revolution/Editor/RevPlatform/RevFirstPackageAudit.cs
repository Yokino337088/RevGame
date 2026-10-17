// ============================================================
// RevFirstPackageAudit.cs —— 首包体积审计（Resources 目录扫描）
//
// 位置：Editor\RevPlatform\
//
// 【为什么需要它】
//   Unity 会把**所有** Resources 文件夹里的内容打进包体 —— 不能分包、不能被热更覆盖、
//   也没法"按需下载"。在微信 / 抖音小游戏这种"首包有硬预算"的平台上，
//   一个不小心放进 Resources 的东西就能把首包撑爆，而且**构建时不会提醒你**，
//   往往要到"传上去被平台拒绝"或"玩家加载特别慢"才发现。
//
//   本框架自己只在 Resources 里放一样东西：ResMap.txt（映射表，几十 KB，运行时必需）。
//   其余任何东西出现在 Resources 里，都应该是"知道代价之后的决定"，而不是顺手放进去的。
//
// 【怎么用】
//   菜单 Revolutions.Tools/平台/首包体积审计 (Resources)
//   结果打到 Console：每个 Resources 目录的文件数 / 体积（按体积从大到小），
//   外加"最占地方的 10 个文件"，方便直接定位该把谁挪走。
//
// 【注意】报的是"原始文件体积"的合计，不是打包后的精确体积
//   （纹理会压缩、代码会被裁）—— 但用来找"谁在偷偷吃首包"完全够用，
//   这类问题的量级差异通常是几十倍，不需要精确到字节。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace Revolution.Editor
{
    internal static class RevFirstPackageAudit
    {
        /// <summary>框架自己的 Resources 目录（只有 ResMap，属于"必要支出"，单独标注出来）。</summary>
        private const string FrameworkResourcesPath = "Assets/Revolution/Resources";

        /// <summary>只列最占地方的前 N 个文件。</summary>
        private const int TopFileCount = 10;

        [MenuItem("Revolution.Tools/平台/首包体积审计 (Resources)", false, 42)]
        public static void Audit()
        {
            string assetsRoot = Path.GetFullPath("Assets");
            string[] dirs = Directory.GetDirectories(assetsRoot, "Resources", SearchOption.AllDirectories);

            if (dirs.Length == 0)
            {
                RevABLog.Info("[首包] 工程里没有任何 Resources 文件夹 —— 没有被强制打进首包的资源。");
                return;
            }

            var stats = new List<DirStat>();
            var allFiles = new List<FileEntry>();
            long totalBytes = 0;

            for (int i = 0; i < dirs.Length; i++)
            {
                string relative = ToAssetPath(assetsRoot, dirs[i]);
                var stat = new DirStat { Path = relative, IsFramework = relative.Replace('\\', '/').StartsWith(FrameworkResourcesPath, StringComparison.OrdinalIgnoreCase) };

                Accumulate(dirs[i], assetsRoot, stat, allFiles);
                stats.Add(stat);
                totalBytes += stat.Bytes;
            }

            stats.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            var sb = new StringBuilder();
            sb.AppendLine("[首包] Resources 审计 —— 以下内容会被**强制**打进包体，且不能热更、不能按需下载：");
            for (int i = 0; i < stats.Count; i++)
            {
                DirStat s = stats[i];
                string tag = s.IsFramework ? "（框架必需：ResMap.txt）" : "★ 建议确认：真的需要留在 Resources 吗？";
                sb.AppendLine($"  · {s.Path} —— {s.FileCount} 个文件 / {FormatSize(s.Bytes)} {tag}");
            }

            sb.AppendLine($"[首包] 合计 {FormatSize(totalBytes)}（含框架的 ResMap）；下面是体积最大的 {TopFileCount} 个文件：");

            allFiles.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            int top = Math.Min(TopFileCount, allFiles.Count);
            for (int i = 0; i < top; i++)
            {
                FileEntry entry = allFiles[i];
                sb.AppendLine($"  · {FormatSize(entry.Bytes)}  {entry.Path}");
            }

            sb.AppendLine("[首包] 提醒：小游戏（微信 / 抖音）首包是硬预算 —— 业务资源请走 AB + CDN（框架的 RevResManager 已经支持），" +
                          "别让它们躺在 Resources 里跟着包体一起发出去。");

            RevABLog.Info(sb.ToString());
        }

        // ============================================================
        // 统计
        // ============================================================

        private sealed class DirStat
        {
            public string Path;
            public int FileCount;
            public long Bytes;
            public bool IsFramework;
        }

        private sealed class FileEntry
        {
            public string Path;
            public long Bytes;
        }

        private static void Accumulate(string dir, string assetsRoot, DirStat stat, List<FileEntry> allFiles)
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                // .meta 不进包，统计它只会把结果搞乱
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) { continue; }

                long size = new FileInfo(file).Length;
                stat.FileCount++;
                stat.Bytes += size;
                allFiles.Add(new FileEntry { Path = ToAssetPath(assetsRoot, file), Bytes = size });
            }

            foreach (string sub in Directory.GetDirectories(dir))
            {
                Accumulate(sub, assetsRoot, stat, allFiles);
            }
        }

        private static string ToAssetPath(string assetsRoot, string fullPath)
            => "Assets" + fullPath.Substring(assetsRoot.Length).Replace('\\', '/');

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) { return (bytes / 1024f / 1024f).ToString("F2") + " MB"; }
            if (bytes >= 1024) { return (bytes / 1024f).ToString("F1") + " KB"; }
            return bytes + " B";
        }
    }
}

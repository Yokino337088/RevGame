// ============================================================
// ABManifestWriter.cs —— 写两个"说明书"
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
//   1. ResMap.txt 与压缩副本     逻辑名|包名|资源名，分别供编辑器审计和 Player 引导
//   2. BuildManifest.json 给人/CI 看的：版本、平台、时间、每个包大小
//
// 【为什么 ResMap 不直接读 Manifest？】
//   AssetBundleManifest 是二进制资产，运行时读它要先把主包加载起来；
//   压缩映射从 Resources 读，AB 尚未打开时也可以验证。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Revolution.Editor
{
    public static class ABManifestWriter
    {
        /// <summary>同时生成编辑器文本和 Player 压缩映射，返回完整条目数。</summary>
        public static int WriteResMap(ABCollectResult collect)
        {
            if (collect.duplicateLogics.Count != 0)
            {
                throw new InvalidDataException("ResMap 含重复逻辑路径：" + string.Join("、", collect.duplicateLogics));
            }

            var sb = new StringBuilder();

            // 固定行序和换行，源标记未变时两份产物维持相同字节。
            sb.Append("# 自动生成，请勿手改。格式：逻辑名|包名|资源名\n");
            sb.Append("# 逻辑名来自资源根目录的相对路径，去掉文件扩展名。\n");

            foreach (ResMapEntry e in collect.entries.OrderBy(entry => entry.logic, StringComparer.Ordinal))
            {
                sb.Append(e.logic).Append('|').Append(e.bundle).Append('|').Append(e.asset).Append('\n');
            }

            ABResMapBuildGuard.Write(sb.ToString());
            return collect.entries.Count;
        }

        /// <summary>写产物清单 JSON（不参与运行时，纯记录 / 排查用）</summary>
        public static void WriteBuildManifest(ABBuildResult result)
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": \"{cfg.version}\",");
            sb.AppendLine($"  \"platform\": \"{result.target}\",");        // 实际打的平台（不一定是编辑器当前平台）
            sb.AppendLine($"  \"buildTime\": \"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
            string main = Path.Combine(result.outputDir, Path.GetFileName(result.outputDir.TrimEnd('/', '\\')));
            sb.AppendLine("  \"integrityVersion\": 1,");
            sb.AppendLine($"  \"mainSha256\": \"{ABBuildFingerprint.DigestFile(main)}\",");
            sb.AppendLine($"  \"mainManifestSha256\": \"{ABBuildFingerprint.DigestFile(main + ".manifest")}\",");
            sb.AppendLine("  \"bundles\": [");

            string[] bundles = result.manifest.allBundles.OrderBy(bundle => bundle, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < bundles.Length; i++)
            {
                // 每个包在磁盘上的实际大小（排查"哪个包突然变大"很有用）
                string path = Path.Combine(result.outputDir, bundles[i]);
                long size = new FileInfo(path).Length;
                string comma = (i == bundles.Length - 1) ? "" : ",";

                string hash = result.manifest.raw.GetAssetBundleHash(bundles[i]).ToString();
                string sha = ABBuildFingerprint.DigestFile(path);
                string manifestSha = ABBuildFingerprint.DigestFile(path + ".manifest");
                sb.AppendLine($"    {{ \"name\": \"{bundles[i]}\", \"size\": {size}, \"hash\": \"{hash}\", \"sha256\": \"{sha}\", \"manifestSha256\": \"{manifestSha}\" }}{comma}");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            string manifestPath = Path.Combine(result.outputDir, ABIncrementalPublisher.ManifestName);
            string content = sb.ToString();
            // 保留内容相同的清单及其原始时间，避免无变化时反复覆盖。
            const string timestamp = "(?m)^(  \"buildTime\": )\"[^\"]*\"";
            if (!File.Exists(manifestPath) || Regex.Replace(File.ReadAllText(manifestPath), timestamp, "$1")
                != Regex.Replace(content, timestamp, "$1"))
            {
                File.WriteAllText(manifestPath, content, Encoding.UTF8);
            }
            if (result.inputFingerprint != null)
            {
                File.WriteAllText(Path.Combine(result.outputDir, ABBuildFingerprint.FileName), result.inputFingerprint);
            }
            File.Delete(Path.Combine(result.outputDir, ABIncrementalPublisher.PendingBuild));
        }
    }
}

// ============================================================
// ABManifestWriter.cs —— 写两个"说明书"
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
//   1. ResMap.txt        给运行时的：逻辑名|包名|资源名（RevResBootstrap 读它）
//   2. BuildManifest.json 给人/CI 看的：版本、平台、时间、每个包大小
//
// 【为什么 ResMap 不直接读 Manifest？】
//   AssetBundleManifest 是二进制资产，运行时读它要先把主包加载起来；
//   而纯文本 ResMap 可以直接 Resources.Load 读，速度快且不依赖 AB。
// ============================================================
using System.IO;
using System.Text;
using UnityEditor;

namespace Revolution.Editor
{
    public static class ABManifestWriter
    {
        /// <summary>写运行时映射表，返回条目数。每行格式：逻辑名|包名|资源名</summary>
        public static int WriteResMap(ABCollectResult collect)
        {
            var sb = new StringBuilder();

            // # 开头的行在运行时 RevResBootstrap 里会被跳过（当注释）
            sb.AppendLine("# 自动生成，请勿手改。格式：逻辑名|包名|资源名");
            sb.AppendLine("# 逻辑名 = 资源相对资源根目录的路径（去扩展名）；包名由你在资源上自行设置");

            int count = 0;
            foreach (ResMapEntry e in collect.entries)
            {
                sb.AppendLine($"{e.logic}|{e.bundle}|{e.asset}");
                count++;
            }

            // 目标目录（Assets/Revolution/Resources/ResourceSystem）第一次写时还不存在 → 先建出来
            string dir = Path.GetDirectoryName(ABBuildSetting.MapAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();      // 新目录要先让 AssetDatabase "看见"，否则下面的 ImportAsset 会失败
            }

            File.WriteAllText(ABBuildSetting.MapAssetPath, sb.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(ABBuildSetting.MapAssetPath);   // 立刻让 Unity 识别
            return count;
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
            sb.AppendLine("  \"bundles\": [");

            string[] bundles = result.manifest.allBundles;
            for (int i = 0; i < bundles.Length; i++)
            {
                // 每个包在磁盘上的实际大小（排查"哪个包突然变大"很有用）
                string path = Path.Combine(result.outputDir, bundles[i]);
                long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                string comma = (i == bundles.Length - 1) ? "" : ",";

                sb.AppendLine($"    {{ \"name\": \"{bundles[i]}\", \"size\": {size} }}{comma}");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(Path.Combine(result.outputDir, "BuildManifest.json"), sb.ToString(), Encoding.UTF8);
        }
    }
}

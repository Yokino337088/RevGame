// ============================================================
// ABBuilderCore.cs —— 打包核心（发动机）
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【只做三件事】
//   1. 准备输出目录（按平台分目录，多平台产物互不覆盖）
//      —— 平台可以和编辑器当前平台不同（与 AssetBundle Browser 一样：不必先切平台就能打别的平台）
//   2. 调 BuildPipeline.BuildAssetBundles 真正打包
//   3. 把 Manifest 交给下游（依赖分析 / ResMap / RevResPath）
//
// 【增量是怎么实现的？】
//   BuildPipeline 内部会为每个包算内容 Hash，与上次产物对比，
//   内容没变就不重打 —— 只要不勾 ForceRebuildAssetBundle 就是增量。
// ============================================================
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>打包结果</summary>
    public class ABBuildResult
    {
        public bool success;
        public string outputDir;
        public ABManifestWrap manifest;

        /// <summary>这次打的是哪个平台（产物清单、窗口结果面板用）</summary>
        public BuildTarget target;
    }

    /// <summary>对 Unity 原生 AssetBundleManifest 的轻量包装（便于将来加自定义字段）</summary>
    public class ABManifestWrap
    {
        public AssetBundleManifest raw;    // Unity 原生 Manifest
        public string[] allBundles;        // 本次打出的所有包名（缓存一份）
    }

    public static class ABBuilderCore
    {
        /// <summary>按当前平台（Build Settings 里选的）打包。success = false 表示失败。</summary>
        public static ABBuildResult Build() => Build(EditorUserBuildSettings.activeBuildTarget);

        /// <summary>
        /// 按指定平台打包（不切换编辑器平台）。success = false 表示失败。
        /// ★ 与当前平台不同时，Unity 会按目标平台重新处理一遍资源（首次会慢一些），这是 BuildPipeline 本身的行为。
        /// </summary>
        public static ABBuildResult Build(BuildTarget target)
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;

            // ② 产物目录：AssetBundles/Android、AssetBundles/PC ...
            string outDir = ABBuildSetting.GetOutputDir(target);

            if (Directory.Exists(outDir) && cfg.cleanOutputBeforeBuild)
                Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            // ③ 真正打包（按资源身上的 assetBundleName 分包）
            AssetBundleManifest raw = BuildPipeline.BuildAssetBundles(
                outDir,
                cfg.GetBuildOptions(),
                target);

            if (raw == null)
                return new ABBuildResult { success = false, outputDir = outDir, target = target };

            return new ABBuildResult
            {
                success = true,
                outputDir = outDir,
                target = target,
                manifest = new ABManifestWrap
                {
                    raw = raw,
                    allBundles = raw.GetAllAssetBundles()
                }
            };
        }

        /// <summary>
        /// 把产物拷到 StreamingAssets（免手动拖文件）。
        /// StreamingAssets 里的文件会随 APK/IPA 一起发布，运行时可直接读。
        /// </summary>
        public static void CopyToStreamingAssets(string outDir)
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;
            if (!cfg.copyToStreamingAssets) return;

            string dest = Path.Combine("Assets", cfg.copyTarget, Path.GetFileName(outDir));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);

            CopyDir(outDir, dest);
            AssetDatabase.Refresh();
        }

        /// <summary>递归拷贝目录（含子目录）</summary>
        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);

            foreach (string file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);

            foreach (string dir in Directory.GetDirectories(src))
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}

// ============================================================
// RevHotUrlBuilder.cs —— URL 拼装（整个系统里唯一知道"远端目录长什么样"的地方）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Download\
//
// 【远端目录约定（技术方案 4.1 / 16.3）】
//   清单 ：{root}/{环境?}/{平台}/{大版本}/{渠道?}/RevHotManifest.txt        ← 不带资源版本！
//   资源 ：{root}/{环境?}/{平台}/{大版本}/{渠道?}/{资源版本}/bundles/&lt;包名&gt;  ← 带版本段
//   映射表：{root}/{环境?}/{平台}/{大版本}/{渠道?}/{资源版本}/ResMap.txt
//
// 【两条纪律都藏在这一个文件里】
//   ① 清单 URL 绝不带资源版本 —— 清单是"发现新版本"的入口，带了版本段就永远只能拿到旧清单；
//   ② 资源 URL 必带资源版本 —— 内容不可变（immutable），CDN 可以放心长缓存，回滚也只是换个 URL。
// ============================================================
using System.Collections.Generic;
using System.Text;

namespace Revolution.HotUpdate
{
    /// <summary>URL 拼装器（纯逻辑，可工程外断言）。</summary>
    internal static class RevHotUrlBuilder
    {
        /// <summary>清单 URL（注意：不含资源版本段 —— 见文件头两条纪律）。</summary>
        public static string ManifestUrl(RevHotConfig config, string sourceRoot)
        {
            return Compose(sourceRoot, config, null, config.ManifestFileName);
        }

        /// <summary>内置清单（首包基线）在 StreamingAssets 里的读取地址：Android 是 jar: URL，桌面 / iOS 是文件路径。</summary>
        public static string BuiltinManifestPath(RevHotConfig config)
        {
            return UnityEngine.Application.streamingAssetsPath + "/" + config.Platform + "/" + config.BuiltinManifestFileName;
        }

        /// <summary>内置 AB 包在 StreamingAssets 里的读取地址（Android 是 jar: URL）。</summary>
        public static string BuiltinBundlePath(RevHotConfig config, string bundleName)
        {
            return UnityEngine.Application.streamingAssetsPath + "/" + config.Platform + "/" + bundleName;
        }

        /// <summary>AB 包 URL / 下载地址（文件型平台返回"完整文件路径"由下载器落盘，URL 型平台直接给加载器用）。</summary>
        public static string BundleUrl(RevHotConfig config, string sourceRoot, string resVersion, string bundleName)
        {
            return Compose(sourceRoot, config, resVersion, "bundles/" + bundleName);
        }

        /// <summary>映射表 URL（与包同目录：表与包同批更新，保证"表里有的包"都已就位）。</summary>
        public static string ResMapUrl(RevHotConfig config, string sourceRoot, string resVersion)
        {
            return Compose(sourceRoot, config, resVersion, "ResMap.txt");
        }

        /// <summary>
        /// 拼一个远端地址：跳过空段（环境 / 渠道没配就不出现在 URL 里）。
        /// </summary>
        private static string Compose(string sourceRoot, RevHotConfig config, string resVersion, string lastSegment)
        {
            var segments = new List<string>(8) { sourceRoot };
            AddIfNotEmpty(segments, config.Env);
            AddIfNotEmpty(segments, config.Platform);
            AddIfNotEmpty(segments, config.AppVersion);
            AddIfNotEmpty(segments, config.Channel);
            AddIfNotEmpty(segments, resVersion);

            var sb = new StringBuilder(160);
            for (int i = 0; i < segments.Count; i++)
            {
                sb.Append(segments[i]);
                sb.Append('/');
            }

            sb.Append(lastSegment);
            return sb.ToString();
        }

        private static void AddIfNotEmpty(List<string> segments, string segment)
        {
            if (string.IsNullOrEmpty(segment)) return;
            string trimmed = segment.Trim().TrimEnd('/');
            if (trimmed.Length > 0) segments.Add(trimmed);
        }
    }
}

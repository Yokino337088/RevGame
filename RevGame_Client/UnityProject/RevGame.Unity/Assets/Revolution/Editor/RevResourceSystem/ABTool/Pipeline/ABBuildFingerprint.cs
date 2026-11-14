using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>增量基线包含包归属与会影响序列化的输入。版本变更时，旧基线自然失效。</summary>
    internal static class ABBuildFingerprint
    {
        internal const string FileName = ".revab-build-fingerprint";

        internal static string Capture(BuildTarget target, BuildAssetBundleOptions options)
        {
            var inputs = new StringBuilder();
            inputs.AppendLine("RevAB baseline 1");
            inputs.AppendLine(Application.unityVersion);
            inputs.AppendLine(target.ToString());
            inputs.AppendLine(EditorUserBuildSettings.activeBuildTarget.ToString());
            inputs.AppendLine((options & ~BuildAssetBundleOptions.ForceRebuildAssetBundle).ToString());
            foreach (string bundle in AssetDatabase.GetAllAssetBundleNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                inputs.AppendLine(bundle);
                foreach (string path in AssetDatabase.GetAssetPathsFromAssetBundle(bundle).OrderBy(path => path, StringComparer.Ordinal))
                {
                    inputs.AppendLine(path);
                    // 文本内容交给 Unity 的增量检测。归属变化仍会触发全量；被非文本引用的文本也参与下方依赖哈希。
                    if (!IsTextData(path))
                    {
                        inputs.AppendLine(AssetDatabase.GetAssetDependencyHash(path).ToString());
                    }
                }
            }

            // 构建回调、Shader 裁剪和脚本类型变化可能不出现在任何资源的依赖表。
            foreach (string path in AssetDatabase.GetAllAssetPaths().Where(IsBuildCode).OrderBy(path => path, StringComparer.Ordinal))
            {
                inputs.AppendLine(path);
                inputs.AppendLine(AssetDatabase.GetAssetDependencyHash(path).ToString());
            }
            foreach (string path in Directory.GetFiles("ProjectSettings").Concat(Directory.GetFiles("Packages"))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                inputs.AppendLine(path);
                inputs.AppendLine(DigestFile(path));
            }
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(inputs.ToString())));
            }
        }

        private static bool IsTextData(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return (extension == ".txt" || extension == ".json" || extension == ".csv" || extension == ".tsv")
                && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(TextAsset);
        }

        private static bool IsBuildCode(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".cs" || extension == ".dll" || extension == ".asmdef"
                || extension == ".asmref" || extension == ".rsp";
        }

        /// <summary>流式计算文件的 SHA-256，结束后释放句柄，供缓存和发布使用。</summary>
        internal static string DigestFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}

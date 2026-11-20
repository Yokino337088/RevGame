// RevAssetImportPlan.cs — 在标记 AB 或复制正式素材之前，从 AssetDatabase 预览可复用资产。
// 字节相同仍需核对用途和导入规则。保留现有 GUID、资源路径和唯一分包由调用方应用。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace Revolution.Editor
{
    /// <summary>面向自动化的只读资源预检，实际素材仍经项目现有归属和发布入口。</summary>
    public static class RevAssetImportPlan
    {
        /// <summary>预检参数。目标平台须与当前激活的导入平台一致。</summary>
        [Serializable]
        public sealed class Input
        {
            /// <summary>当前工程 Assets 内已导入的候选源文件。</summary>
            public string path;
            /// <summary>要核对的 BuildTarget，例如 StandaloneWindows64。</summary>
            public string target;
            /// <summary>候选生产资产的扫描目录，省略时使用 Assets。</summary>
            [RevAutomationOptional] public string searchFolder;
        }

        /// <summary>同字节的正式资产候选，包含真实资源归属与模板相容性。</summary>
        [Serializable]
        public sealed class Candidate
        {
            /// <summary>保留这个资产的 AssetDatabase 路径。</summary>
            public string path;
            /// <summary>候选资产已有 GUID。</summary>
            public string guid;
            /// <summary>候选当前已配置的隐式或显式包名，空值表示还需检查映射。</summary>
            public string bundle;
            /// <summary>导入类型和实际序列化配置是否一致。</summary>
            public bool importerMatches;
        }

        /// <summary>本次预检结果。只在比对找到唯一有正式分包的相容资产时建议直接复用。</summary>
        [Serializable]
        public sealed class Result
        {
            /// <summary>本次验证的真实导入平台。</summary>
            public string target;
            /// <summary>源资源内容哈希，用于验证之后的资产变动。</summary>
            public string contentSha256;
            /// <summary>平台与目标依赖哈希，供后续判断是否重新构建。</summary>
            public string dependencyHash;
            /// <summary>扫描得到的原始字节相同候选。</summary>
            public Candidate[] candidates;
            /// <summary>reuse、review 或 import，表示本次预检建议。</summary>
            public string decision;
            /// <summary>唯一可安全复用的资产路径。其它情况下为空。</summary>
            public string reusePath;
            /// <summary>包含当前源 Sprite 的图集资产路径，包括文件夹式收集。</summary>
            public string[] sourceAtlases;
        }

        /// <summary>实际从当前 Unity 项目读取 GUID、分包与导入配置。该动作只生成方案。</summary>
        [RevAutomationAction("asset.import-plan", Description = "核对重复内容、导入设置与 AB 归属并给出资源接入方案",
            ReadOnly = true, InputType = typeof(Input), OutputType = typeof(Result))]
        public static Result Inspect(RevAutomationRequest request)
        {
            Input input = JsonUtility.FromJson<Input>(request.payloadJson);
            if (!Enum.TryParse(input.target, false, out BuildTarget target) || target.ToString() != input.target ||
                target != EditorUserBuildSettings.activeBuildTarget)
            {
                throw new ArgumentException("预检平台须等于当前 Unity BuildTarget：" + EditorUserBuildSettings.activeBuildTarget);
            }
            RevAutomationActions.RequireAssetPath(input.path);
            string source = input.path.Replace('\u005c', '/');
            string folder = string.IsNullOrWhiteSpace(input.searchFolder) ? "Assets" : input.searchFolder.Replace('\u005c', '/').TrimEnd('/');
            if (folder != "Assets")
            {
                RevAutomationActions.RequireAssetPath(folder);
            }
            if (!AssetDatabase.IsValidFolder(folder) || AssetImporter.GetAtPath(source) == null)
            {
                throw new ArgumentException("需要有效扫描目录和已经导入的源资产。");
            }
            string hash = ContentHash(source);
            string import = ImportSettings(source);
            string extension = Path.GetExtension(source);
            long length = new FileInfo(source).Length;
            var candidates = new List<Candidate>();
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }).Distinct())
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == source || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path) || new FileInfo(path).Length != length || ContentHash(path) != hash)
                {
                    continue;
                }
                candidates.Add(new Candidate
                {
                    path = path,
                    guid = guid,
                    bundle = AssetDatabase.GetImplicitAssetBundleName(path),
                    importerMatches = import == ImportSettings(path)
                });
            }
            Candidate[] reusable = candidates.Where(candidate => candidate.importerMatches && !string.IsNullOrEmpty(candidate.bundle)).ToArray();
            string[] atlases = FindAtlases(source);
            return new Result
            {
                target = target.ToString(),
                contentSha256 = hash,
                dependencyHash = AssetDatabase.GetAssetDependencyHash(source).ToString(),
                candidates = candidates.OrderBy(candidate => candidate.path, StringComparer.Ordinal).ToArray(),
                decision = atlases.Length > 1 ? "review" : reusable.Length == 1 ? "reuse" : candidates.Count == 0 ? "import" : "review",
                reusePath = atlases.Length <= 1 && reusable.Length == 1 ? reusable[0].path : string.Empty,
                sourceAtlases = atlases
            };
        }

        /// <summary>查 SpriteAtlas 的真实打包项，包含以目录整体纳入图集的资产。</summary>
        private static string[] FindAtlases(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter texture) || texture.textureType != TextureImporterType.Sprite)
            {
                return Array.Empty<string>();
            }
            var found = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
            {
                string atlasPath = AssetDatabase.GUIDToAssetPath(guid);
                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                {
                    continue;
                }
                foreach (UnityEngine.Object asset in atlas.GetPackables())
                {
                    string member = AssetDatabase.GetAssetPath(asset);
                    if (member == path || (AssetDatabase.IsValidFolder(member) && path.StartsWith(member + "/", StringComparison.Ordinal)))
                    {
                        found.Add(atlasPath);
                        break;
                    }
                }
            }
            found.Sort(StringComparer.Ordinal);
            return found.Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>流式 SHA-256，拒绝通过重解析点读取工程以外内容。</summary>
        private static string ContentHash(string path)
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("资源文件指向重解析点：" + path);
            }
            for (DirectoryInfo dir = info.Directory; dir != null; dir = dir.Parent)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("资源目录指向重解析点：" + dir.FullName);
                }
            }
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>比较序列化导入设置，包括多 Sprite 切片与平台覆盖。GUID、标记和导入时刻另行记录。</summary>
        private static string ImportSettings(string path)
        {
            return string.Join("\n", File.ReadLines(path + ".meta").Where(line =>
            {
                string text = line.TrimStart();
                return !text.StartsWith("guid:", StringComparison.Ordinal) &&
                    !text.StartsWith("timeCreated:", StringComparison.Ordinal) &&
                    !text.StartsWith("assetBundleName:", StringComparison.Ordinal) &&
                    !text.StartsWith("assetBundleVariant:", StringComparison.Ordinal);
            }));
        }
    }
}

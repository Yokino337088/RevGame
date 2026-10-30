// 只在编辑器分析引用；不删除美术源文件，也不把“图集收了这张图”当作游戏需要这张图的证据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    /// <summary>
    /// 项目的美术打包可达性检查。正式场景、可动态加载的预制体/配置/材质是入口，源图和图集不是入口。
    /// 每次配置AB都重新计算，新增引用会自动把图片纳回打包范围，取消引用后则自动剔除。
    /// 这里的“未使用”限定为当前项目入口不可达，不代表素材没有未来用途，所以始终保留源文件及GUID。
    /// </summary>
    public static class GameResourceUsage
    {
        /// <summary>只用于编辑器资产查询；正式游戏继续使用RevResManager的逻辑路径。</summary>
        private const string ResourceRoot = "Assets/GameRes";
        private const string ArtRoot = ResourceRoot + "/Art";
        private const string AtlasRoot = ResourceRoot + "/Atlases";

        /// <summary>一次扫描的独立快照；不静态缓存，避免改完预制体后仍用旧引用结果打包。</summary>
        public sealed class Snapshot
        {
            public readonly HashSet<string> Dependencies = new HashSet<string>(StringComparer.Ordinal);
            public readonly SortedDictionary<string, List<string>> Reasons = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            public readonly List<string> Roots = new List<string>();

            /// <summary>记录真实使用者。报告保留入口路径，方便新手从图片反查哪个预制体/配置正在用它。</summary>
            internal void Add(string path, string owner)
            {
                Dependencies.Add(path);

                if (!Reasons.TryGetValue(path, out List<string> owners))
                {
                    owners = new List<string>();
                    Reasons.Add(path, owners);
                }
                if (!owners.Contains(owner))
                {
                    owners.Add(owner);
                }
            }
        }
        /// <summary>
        /// 获取Unity真实序列化依赖，并补齐代码按名字取Sprite的情况。
        /// 不能从Atlases/Art自身出发，否则每张闲置图片都会因为图集或自己引用自己而被误判为正在使用。
        /// 全部非图片GameRes资产保守地视为可动态加载入口，保护字符串加载的面板、棋子动画配置与共享材质。
        /// </summary>
        public static Snapshot Scan()
        {
            var snapshot = new Snapshot();
            snapshot.Roots.AddRange(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path));
            snapshot.Roots.AddRange(AssetDatabase.FindAssets("", new[] { ResourceRoot }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p) && !p.StartsWith(ArtRoot + "/", StringComparison.Ordinal)
                    && !p.StartsWith(AtlasRoot + "/", StringComparison.Ordinal) && !(AssetImporter.GetAtPath(p) is TextureImporter)
                    && !p.EndsWith(".cs", StringComparison.Ordinal)));
            foreach (string root in snapshot.Roots.Distinct().OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (string dependency in AssetDatabase.GetDependencies(root, true))
                {
                    snapshot.Add(dependency, root);
                }
            }
            // 当前唯一的GetSprite业务入口是第一章背景：名字写在代码和场景参数中，场景不直接引用Sprite。
            // 同时扫描其余业务源码的图片名/逻辑路径字面量，保护直接按名称动态加载的图片。
            // Generated目录是打包的输出，绝不能把生成的资源清单再当作输入，否则旧条目会永远删不掉。
            string[] textures = AssetDatabase.FindAssets("t:Texture2D", new[] { ResourceRoot }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            foreach (string source in Directory.GetFiles("Assets/GameScripts", "*.cs", SearchOption.AllDirectories))
            {
                string path = source.Replace('\\', '/');

                if (path.Contains("/Generated/"))
                {
                    continue;
                }
                string code = File.ReadAllText(path);
                foreach (string texture in textures)
                {
                    string name = Path.GetFileNameWithoutExtension(texture);
                    string logical = texture.Substring(ResourceRoot.Length + 1);
                    logical = logical.Substring(0, logical.Length - Path.GetExtension(logical).Length);

                    if (code.Contains("\"" + name + "\"") || code.Contains("\"" + logical + "\""))
                    {
                        snapshot.Add(texture, "代码动态加载：" + path);
                    }
                }
            }
            return snapshot;
        }
        /// <summary>只读审计结果，可经RevMCP取得。sourceBytes是PNG等源文件大小，不冒充压缩AB的实际节省。</summary>
        [Serializable]
        public sealed class AuditResult
        {
            public int roots, artTotal, artUsed, artExcluded, atlasTotal, atlasPacked;
            public long excludedSourceBytes;
            public AssetRow[] assets;
        }
        /// <summary>一行资源的去留及证据；markedBundle为空表示没有显式或继承的AB标记。</summary>
        [Serializable]
        public sealed class AssetRow
        {
            public string path, markedBundle;
            public bool required;
            public long sourceBytes;
            public string[] referencedBy;
        }
        /// <summary>输出逐资源清单，不以“设置成功”代替引用核查；未引用图集不会反过来保活其中的图片。</summary>
        public static AuditResult Audit()
        {
            Snapshot snapshot = Scan();
            var rows = new List<AssetRow>();
            string[] art = AssetDatabase.FindAssets("", new[] { ArtRoot }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in art)
            {
                snapshot.Reasons.TryGetValue(path, out List<string> owners);
                rows.Add(new AssetRow
                {
                    path = path,
                    required = snapshot.Dependencies.Contains(path),
                    markedBundle = AssetDatabase.GetImplicitAssetBundleName(path),
                    sourceBytes = new FileInfo(path).Length,
                    referencedBy = owners == null ? Array.Empty<string>() : owners.ToArray()
                });
            }
            int packed = 0;
            string[] atlases = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasRoot }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in atlases)
            {
                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                string[] used = atlas.GetPackables().Select(AssetDatabase.GetAssetPath).Where(snapshot.Dependencies.Contains).ToArray();
                string bundle = AssetDatabase.GetImplicitAssetBundleName(path);

                if (!string.IsNullOrEmpty(bundle))
                {
                    packed++;
                }
                rows.Add(new AssetRow
                {
                    path = path,
                    required = used.Length > 0 || snapshot.Dependencies.Contains(path),
                    markedBundle = bundle,
                    sourceBytes = new FileInfo(path).Length,
                    referencedBy = used
                });
            }
            int usedArt = art.Count(snapshot.Dependencies.Contains);
            return new AuditResult
            {
                roots = snapshot.Roots.Count,
                artTotal = art.Length,
                artUsed = usedArt,
                artExcluded = art.Length - usedArt,
                atlasTotal = atlases.Length,
                atlasPacked = packed,
                assets = rows.ToArray(),
                excludedSourceBytes = rows.Where(r => r.path.StartsWith(ArtRoot + "/", StringComparison.Ordinal) && !r.required).Sum(r => r.sourceBytes)
            };
        }
        /// <summary>
        /// 打包前检查真实标记和图集闭包。禁止只关闭框架漏标检查来消除警告：该进包的图片仍必须有标记，
        /// 不该进包的图片既不能有标记，也不能藏在已打包图集的packables中。
        /// </summary>
        public static string Validate()
        {
            AuditResult report = Audit();
            foreach (AssetRow row in report.assets)
            {
                bool marked = !string.IsNullOrEmpty(row.markedBundle);

                if (row.required != marked)
                {
                    throw new InvalidOperationException("资源引用与AB标记不一致：" + row.path + "，需要=" + row.required + "，包=" + row.markedBundle);
                }
                if (marked && row.path.EndsWith(".spriteatlasv2", StringComparison.Ordinal))
                {
                    SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(row.path);

                    if (atlas.GetPackables().Length != row.referencedBy.Length)
                    {
                        throw new InvalidOperationException("已打包图集仍包含未引用源图：" + row.path);
                    }
                }
            }
            return "保留" + report.artUsed + "/" + report.artTotal + "个Art资源，排除" + report.artExcluded + "个，打包" + report.atlasPacked + "/" + report.atlasTotal + "张图集";
        }
    }
}

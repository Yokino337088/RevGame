// 项目图集入口：编辑器只负责归组/校验，真正AB构建仍交给Revolution。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    /// <summary>
    /// 把AB可达的图片和场景背景统一纳入SpriteAtlas。
    /// 图集是“共用一张GPU贴图”的布局，不是把PNG文件简单放到同一个AB文件里。
    /// 源图和所属图集归同一个包，预制体/配置只引用它，避免多个包各带一份源图。
    /// </summary>
    public static class GameSpriteAtlasBuild
    {
        /// <summary>图集资产存放位置；运行时通过生成的RevResPath.Atlases加载，不能使用此编辑器路径。</summary>
        private const string AtlasRoot = "Assets/GameRes/Atlases";
        /// <summary>保持旧编辑器配方仍能找到已迁移资源；移动必须由AssetDatabase完成以保留GUID。</summary>
        private const string MigrationFile = "Assets/Editor/RevGame/SpriteMigration.tsv";

        /// <summary>
        /// 收集真实依赖、迁移旧散图、创建分组图集并打标。重复执行不会创建第二份资源。
        /// 在GameResourceBuild计算并应用目录标记后调用；不能在Play里改资源布局。
        /// </summary>
        public static void Configure()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("图集配置必须退出Play"); }
            EnsureFolder(AtlasRoot);
            // 工程使用Unity 2022的Sprite Atlas V2；必须保存.spriteatlasv2源资产并让Importer烘焙。
            // 仅CreateAsset(new SpriteAtlas)会得到V1原生对象，不能把空的打包数据当作V2图集交付。
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
            var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string original in CollectTextures())
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(original);
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    // 法线图/LUT/字体运行时生成的纹理不能冒充Sprite打包。新增这类资产须单独评审用途。
                    throw new InvalidOperationException("AB图片不是Sprite，需明确采样方式后再接入：" + original);
                }
                string path = MigrateLegacyTexture(original);
                importer = (TextureImporter)AssetImporter.GetAtPath(path);
                // GPU负责采样，C#不读像素；关Read/Write避免CPU常驻副本，2D固定视图不用mipmap链。
                if (importer.isReadable || importer.mipmapEnabled)
                {
                    importer.isReadable = false;
                    importer.mipmapEnabled = false;
                    importer.SaveAndReimport();
                }
                string group = GroupFor(path);
                List<string> paths;
                if (!groups.TryGetValue(group, out paths)) { paths = new List<string>(); groups.Add(group, paths); }
                paths.Add(path);
            }
            foreach (var pair in groups)
            {
                ConfigureAtlas(pair.Key, pair.Value);
            }
            // 旧的备用图集保留文件供以后复用，但不能保留包标记或Include In Build，否则仍可能增加包体。
            // 这里也支持单独调用Configure，不依赖外层恰好已经清过标记。
            var included = new HashSet<string>(groups.Keys, StringComparer.Ordinal);
            var assignedSources = new HashSet<string>(groups.Values.SelectMany(paths => paths), StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (included.Contains(Path.GetFileNameWithoutExtension(path))) { continue; }
                // 备用素材转为在用后，必须从旧备用图集移除：即使旧图集不进包，重复归属也会令Unity绑定和审计不确定。
                // 只移除已经分到新图集的源图，其余备用素材及GUID继续保留。
                var oldAtlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                string[] oldSources = oldAtlas.GetPackables().Select(AssetDatabase.GetAssetPath).ToArray();
                string[] remaining = oldSources.Where(source => !assignedSources.Contains(source)).ToArray();
                if (remaining.Length != oldSources.Length) { ConfigureAtlas(Path.GetFileNameWithoutExtension(path), remaining.ToList()); }
                var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(path);
                GameResourceBuild.SetBundleIfChanged(importer, string.Empty);
                if (importer.includeInBuild) { importer.includeInBuild = false; importer.SaveAndReimport(); }
            }
            // 图集归属变化后剔除闲置源图旧标记，不能因跳过“全量清空”而把无引用图片继续带进包。
            var usedSources = new HashSet<string>(groups.Values.SelectMany(paths => paths), StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets/GameRes/Art" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!usedSources.Contains(path)) { GameResourceBuild.SetBundleIfChanged(AssetImporter.GetAtPath(path), string.Empty); }
            }
            WriteCatalog(groups.Keys);
            AssetDatabase.SaveAssets();
        }

        /// <summary>生成白名单与场景预热名单。运行时不扫目录，不相信任意atlasRequested标签。</summary>
        private static void WriteCatalog(IEnumerable<string> names)
        {
            string[] all = names.ToArray();
            string quoted = string.Join(", ", all.Select(n => "\"" + n + "\""));
            string scenes = string.Join(", ", all.Where(n => n.StartsWith("SceneBackdrop_", StringComparison.Ordinal)).Select(n => "\"" + n + "\""));
            string source = "// 自动生成：GameSpriteAtlasBuild。只改图集归组规则，不手改此清单。\nnamespace RevGame.Generated\n{\n    public static class GameSpriteAtlases\n    {\n        /// <summary>打包允许加载的图集标签，与文件名一一对应。</summary>\n        public static readonly string[] Names = { " + quoted + " };\n        /// <summary>场景背景可能早于Awake请求绑定，因此启动时主动预热。</summary>\n        public static readonly string[] SceneNames = { " + scenes + " };\n    }\n}\n";
            const string path = "Assets/GameScripts/Generated/GameSpriteAtlases.cs";
            if (!File.Exists(path) || File.ReadAllText(path) != source) { File.WriteAllText(path, source); }
        }

        /// <summary>只收业务入口可达的图片；图集和图片自身不作为入口，避免备用素材永久被打回来。</summary>
        public static string[] CollectTextures()
        {
            return GameResourceUsage.Scan().Dependencies
                // 章节背景是独立 GPU 纹理（显式 _ChapterTex 采样），按需租约，不纳入 Sprite 图集预热。
                // 第一章也归档进 ChapterBackground，但仍是 Sprite，必须保留图集；其余五章是独立采样的 Default 纹理。
                .Where(p => AssetImporter.GetAtPath(p) is TextureImporter importer
                    // 激光是LineRenderer非Sprite采样纹理，保持完整0～1UV；禁止误归Sprite图集。
                    && !p.StartsWith("Assets/GameRes/HelperTextures/", StringComparison.Ordinal)
                    && (!p.StartsWith("Assets/GameRes/ChapterBackground/", StringComparison.Ordinal) || importer.textureType == TextureImporterType.Sprite))
                .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        /// <summary>归组即生命周期边界。大背景独占图集，避免打开一个小按钮却加载整幅背景。</summary>
        private static string GroupFor(string path)
        {
            if (path.Contains("/Art/Pieces/Helper/")) { return "HelperFrames"; }
            if (path.Contains("/Art/Boss/"))
            {
                // 每章8帧独立图集，默认棋子/手牌配置不引用它，普通回合不预加载Boss美术。
                string folder = Path.GetFileName(Path.GetDirectoryName(path));
                return folder.Substring(0, 9).Replace("Chapter", "BossChapter") + "Frames";
            }
            if (path.Contains("/Background/") || path.Contains("/ChapterBackground/")) { return Path.GetFileNameWithoutExtension(path) + "Atlas"; }
            if (path.Contains("/Art/Story/")) { return "StoryGuide"; }
            if (path.Contains("/Art/Pieces/")) { return "PieceFrames"; }
            if (path.Contains("/Art/Combat/")) { return "CombatFrames"; }
            if (path.Contains("/Art/Board/") || path.Contains("/Art/Status/")) { return "BoardStatus"; }
            if (path.Contains("/Art/UI/"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                // 未在战斗使用的长竖棋盘图与酒馆大底板不能拖大六张手牌的常驻贴图。
                if (name == "UI_chessBoard_chessBoard" || name == "UI_Tavern_imgBK") { return name + "Atlas"; }
                if (name.StartsWith("UI_roleItem") || name.StartsWith("UI_Game") || name.StartsWith("UI_Pvp") || name.StartsWith("UI_Setting")) { return "UIBattle"; }
                return "UIMenus";
            }
            throw new InvalidOperationException("图片缺少图集归组规则：" + path);
        }

        /// <summary>旧中文路径仅在编辑器迁移一次。名字带稳定GUID短码避免重名，旧序列帧内部Sprite名不变。</summary>
        private static string MigrateLegacyTexture(string original)
        {
            if (original.StartsWith("Assets/GameRes/", StringComparison.Ordinal)) { return original; }
            string category = original.Contains("/特效/") ? "Combat" : original.Contains("/背景/") ? "Background" : "Board";
            string folder = "Assets/GameRes/Art/" + category;
            EnsureFolder(folder);
            string guid = AssetDatabase.AssetPathToGUID(original);
            string name = category == "Background" ? "SceneBackdrop_" : category == "Combat" ? "CombatFrame_" : "BoardSprite_";
            string destination = folder + "/" + name + guid.Substring(0, 8) + Path.GetExtension(original);
            string error = AssetDatabase.MoveAsset(original, destination);
            if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
            File.AppendAllText(MigrationFile, original + "\t" + destination + "\t" + guid + "\n");
            return destination;
        }

        /// <summary>
        /// 图集源图与图集同包。禁旋转/紧密裁切保证九宫格Image、矩形弹道和背景UV保持正确。
        /// PC用BC3，Android用ETC2_RGBA8保留透明；这里不启用会丢失alpha的RGB格式。
        /// </summary>
        private static void ConfigureAtlas(string name, List<string> paths)
        {
            string atlasPath = AtlasRoot + "/" + name + ".spriteatlasv2";
            string legacy = AtlasRoot + "/" + name + ".spriteatlas";
            if (AssetDatabase.LoadAssetAtPath<SpriteAtlas>(legacy) != null)
            {
                string error = AssetDatabase.MoveAsset(legacy, atlasPath);
                if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
            }
            SpriteAtlas existing = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            // 内容相同就保留V2源文件。每次新建再保存会触发Importer重建，既增加导入时间，也会覆盖正在保存的包标记。
            string[] members = existing == null ? Array.Empty<string>() : existing.GetPackables().Select(AssetDatabase.GetAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (!members.SequenceEqual(paths.OrderBy(p => p, StringComparer.Ordinal)))
            {
                var atlas = new SpriteAtlasAsset();
                try
                {
                    atlas.Add(paths.Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p)).Cast<UnityEngine.Object>().ToArray());
                    SpriteAtlasAsset.Save(atlas, atlasPath);
                }
                finally { UnityEngine.Object.DestroyImmediate(atlas); }
                AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            // 2160高的备用竖棋盘图独占4096页；常规图集仍限制2048，不能为一张大图放大全部常驻页。
            int maxSize = paths.Any(p => { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); return t.width + 8 > 2048 || t.height + 8 > 2048; }) ? 4096 : 2048;
            // 第一章图片必须保留图集，但整个图集同样属于第一章独立包，不能留在常驻公共资源里。
            string bundle = name == "SceneBackdrop_0fc7e394Atlas" ? "background_chapter01_tutorial" : "atlas_" + name.ToLowerInvariant();
            // V2以Importer设置为准；只改源资产而不改Importer，旧.meta会在导入时覆盖Include In Build等设置。
            var atlasImporter = (SpriteAtlasImporter)AssetImporter.GetAtPath(atlasPath);
            string before = EditorJsonUtility.ToJson(atlasImporter);
            atlasImporter.includeInBuild = false;
            atlasImporter.packingSettings = new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 };
            atlasImporter.textureSettings = new SpriteAtlasTextureSettings { readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear };
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "DefaultTexturePlatform", maxTextureSize = maxSize, textureCompression = TextureImporterCompression.Compressed });
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.DXT5 });
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.ETC2_RGBA8 });
            if (name == "HelperFrames")
            {
                // 仅四张小图使用512无损图集，防DXT/ETC色块软化脸部与腿部边缘；不扩大其它图集内存。
                atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name="DefaultTexturePlatform", maxTextureSize=512, textureCompression=TextureImporterCompression.Uncompressed });
                atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name="Standalone", overridden=true, maxTextureSize=512, format=TextureImporterFormat.RGBA32 });
                atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name="Android", overridden=true, maxTextureSize=512, format=TextureImporterFormat.RGBA32 });
            }
            if (before != EditorJsonUtility.ToJson(atlasImporter)) { atlasImporter.SaveAndReimport(); }
            // 导入完成后重新取Importer，再写最终AB标记，避免旧Importer的保存覆盖标记。
            GameResourceBuild.SetBundleIfChanged(AssetImporter.GetAtPath(atlasPath), bundle);
            foreach (string path in paths) { GameResourceBuild.SetBundleIfChanged(AssetImporter.GetAtPath(path), bundle); }
        }

        /// <summary>每张待打包图片必须恰好属于一张图集，并且源图/图集包名一致；漏图会中止构建。</summary>
        public static string Validate()
        {
            var membership = new Dictionary<string, int>(StringComparer.Ordinal);
            int atlases = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // 未打标备用图集不参与打包唯一性统计；否则它保存的历史packables会干扰使用中图片的归属。
                if (string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(path))) { continue; }
                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                if (atlas.IsIncludeInBuild() || atlas.GetPackingSettings().enableRotation || atlas.GetTextureSettings().readable)
                {
                    throw new InvalidOperationException("图集设置不符合项目约定：" + path);
                }
                foreach (UnityEngine.Object item in atlas.GetPackables())
                {
                    string source = AssetDatabase.GetAssetPath(item);
                    int count; membership.TryGetValue(source, out count); membership[source] = count + 1;
                    if (AssetDatabase.GetImplicitAssetBundleName(source) != AssetDatabase.GetImplicitAssetBundleName(path))
                    {
                        throw new InvalidOperationException("源图和图集必须同包：" + source);
                    }
                }
                atlases++;
            }
            foreach (string path in CollectTextures())
            {
                int count;
                if (!membership.TryGetValue(path, out count) || count != 1) { throw new InvalidOperationException("图片图集归属不是唯一：" + path); }
            }
            return atlases + "张图集，" + membership.Count + "张图片唯一覆盖";
        }

        /// <summary>按层创建目录，所有资产操作通过AssetDatabase保留Unity的GUID与导入状态。</summary>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) { return; }
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}

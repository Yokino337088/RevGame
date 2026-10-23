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
        /// 在GameResourceBuild清理旧标记之后调用；不能在Play里改资源布局。
        /// </summary>
        public static void Configure()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("图集配置必须退出Play"); }
            EnsureFolder(AtlasRoot);
            // 工程使用Unity 2022的Sprite Atlas V2；必须保存.spriteatlasv2源资产并让Importer烘焙。
            // 仅CreateAsset(new SpriteAtlas)会得到V1原生对象，不能把空的打包数据当作V2图集交付。
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
            // 同目录里有尚未启用的新版美术。它们仍按要求打包，但不能挤进常驻按钮/手牌图集。
            // 从预制体/配置/场景反查真实使用者；不从图集反查，否则图集自己的源图会使所有图片都变成“正在用”。
            var activeRoots = AssetDatabase.FindAssets("", new[] { "Assets/GameRes" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab", StringComparison.Ordinal) || p.EndsWith(".asset", StringComparison.Ordinal)).ToList();
            activeRoots.Add("Assets/Scenes/GameScene.unity");
            var activeTextures = new HashSet<string>(AssetDatabase.GetDependencies(activeRoots.ToArray(), true), StringComparer.Ordinal);
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
                string group = GroupFor(path, activeTextures.Contains(original));
                List<string> paths;
                if (!groups.TryGetValue(group, out paths)) { paths = new List<string>(); groups.Add(group, paths); }
                paths.Add(path);
            }
            foreach (var pair in groups)
            {
                ConfigureAtlas(pair.Key, pair.Value);
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

        /// <summary>只扫描游戏实际打包范围和场景依赖；不把整个原版美术素材库误装进手机包。</summary>
        public static string[] CollectTextures()
        {
            var roots = AssetDatabase.FindAssets("", new[] { "Assets/GameRes" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p)).ToList();
            roots.Add("Assets/Scenes/GameScene.unity");
            return AssetDatabase.GetDependencies(roots.ToArray(), true)
                // 章节背景是独立 GPU 纹理（显式 _ChapterTex 采样），按需租约，不纳入 Sprite 图集预热。
                // 第一章也归档进 ChapterBackground，但仍是 Sprite，必须保留图集；其余五章是独立采样的 Default 纹理。
                .Where(p => AssetImporter.GetAtPath(p) is TextureImporter importer
                    && (!p.StartsWith("Assets/GameRes/ChapterBackground/", StringComparison.Ordinal) || importer.textureType == TextureImporterType.Sprite))
                .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        /// <summary>归组即生命周期边界。大背景独占图集，避免打开一个小按钮却加载整幅背景。</summary>
        private static string GroupFor(string path, bool activelyUsed)
        {
            if (path.Contains("/Background/") || path.Contains("/ChapterBackground/")) { return Path.GetFileNameWithoutExtension(path) + "Atlas"; }
            if (path.Contains("/Art/Pieces/")) { return "PieceFrames"; }
            if (path.Contains("/Art/Combat/")) { return "CombatFrames"; }
            if (path.Contains("/Art/Board/") || path.Contains("/Art/Status/")) { return "BoardStatus"; }
            if (path.Contains("/Art/UI/"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                // 未在战斗使用的长竖棋盘图与酒馆大底板不能拖大六张手牌的常驻贴图。
                if (name == "UI_chessBoard_chessBoard" || name == "UI_Tavern_imgBK") { return name + "Atlas"; }
                if (!activelyUsed) { return "UIReserve"; }
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
            var atlas = new SpriteAtlasAsset();
            atlas.Add(paths.Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p)).Cast<UnityEngine.Object>().ToArray());
            atlas.SetPackingSettings(new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 });
            atlas.SetTextureSettings(new SpriteAtlasTextureSettings { readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear });
            // 2160高的备用竖棋盘图独占4096页；常规图集仍限制2048，不能为一张大图放大全部常驻页。
            int maxSize = paths.Any(p => { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); return t.width + 8 > 2048 || t.height + 8 > 2048; }) ? 4096 : 2048;
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings { name = "DefaultTexturePlatform", maxTextureSize = maxSize, textureCompression = TextureImporterCompression.Compressed });
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.DXT5 });
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.ETC2_RGBA8 });
            // 不让Player场景再自动携带一份图集；运行时atlasRequested通过RevResManager加载所属AB。
            atlas.SetIncludeInBuild(false);
            SpriteAtlasAsset.Save(atlas, atlasPath);
            UnityEngine.Object.DestroyImmediate(atlas);
            AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            // 第一章图片必须保留图集，但整个图集同样属于第一章独立包，不能留在常驻公共资源里。
            string bundle = name == "SceneBackdrop_0fc7e394Atlas" ? "background_chapter01_tutorial" : "atlas_" + name.ToLowerInvariant();
            // V2以Importer设置为准；只改源资产而不改Importer，旧.meta会在导入时覆盖Include In Build等设置。
            var atlasImporter = (SpriteAtlasImporter)AssetImporter.GetAtPath(atlasPath);
            atlasImporter.includeInBuild = false;
            atlasImporter.packingSettings = new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 };
            atlasImporter.textureSettings = new SpriteAtlasTextureSettings { readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear };
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "DefaultTexturePlatform", maxTextureSize = maxSize, textureCompression = TextureImporterCompression.Compressed });
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.DXT5 });
            atlasImporter.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = maxSize, format = TextureImporterFormat.ETC2_RGBA8 });
            atlasImporter.SetAssetBundleNameAndVariant(bundle, string.Empty);
            atlasImporter.SaveAndReimport();
            foreach (string path in paths) { AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant(bundle, string.Empty); }
        }

        /// <summary>每张待打包图片必须恰好属于一张图集，并且源图/图集包名一致；漏图会中止构建。</summary>
        public static string Validate()
        {
            var membership = new Dictionary<string, int>(StringComparer.Ordinal);
            int atlases = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
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

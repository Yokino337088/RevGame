// ============================================================
// GameResourceBuild.cs —— 项目资源分包约定与Revolution打包入口。
// 这里只给项目资源设标记、串联框架已有工具；不复制打包算法、不修改Revolution快照。
// 文件夹名表达用途，AB名表达加载边界；共享UI图片和大字体单独打包，避免每个面板重复包含它们。
// ============================================================
using System;
using System.IO;
using Revolution.Editor;
using UnityEditor;

namespace RevGame.Editor
{
    public static class GameResourceBuild
    {
        // Data保持导表工具约定的根目录：它只放生成表；手工美术配置已独立到Config/Art。
        // 配置由团队入库，下一次新增同类资源会自动继承所在目录的分包，不需要每个文件重新打标。
        private static readonly string[,] Groups =
        {
            { "UI/Panel", "ui_panels" }, { "UI/Item", "ui_items" },
            { "Art/UI", "art_ui" }, { "Fonts/CJK", "font_cjk" },
            { "Board", "battle_board" }, { "Pieces/Unit", "battle_units" },
            { "Pieces/Boss", "battle_boss" }, { "Config/Art", "config_piece_art" },
            { "Effects/Combat", "battle_feedback" }, { "Data", "data_tables" },
            { "Material/Cards", "material_cards" }, { "Material/UI", "material_ui" },
            { "Material/Background", "material_background" },
            { "Material/Pieces", "material_pieces" },
            { "Art/Pieces", "shared_piece_frames" }, { "Art/Status", "shared_status" },
            { "Audio/Bgm", "audio_bgm" }, { "Audio/Sfx", "audio_sfx" }
        };

        /// <summary>幂等标记并用框架生成ResMap、路径和音效目录常量；有漏标/重名/非法名就中止，不掩盖错误。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("请退出Play再更改分包"); }
            ABBuildConfig config = ABBuildConfig.Instance;
            config.resRoot = "Assets/GameRes";
            config.markMode = ABMarkMode.ScanExisting;
            config.checkUnmarkedAssets = true;
            EditorUtility.SetDirty(config);
            foreach (string guid in AssetDatabase.FindAssets("", new[] { config.resRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer != null) { importer.SetAssetBundleNameAndVariant(string.Empty, string.Empty); }
            }
            for (int i = 0; i < Groups.GetLength(0); i++)
            {
                string path = config.resRoot + "/" + Groups[i, 0];
                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer == null) { throw new InvalidOperationException("分包目录不存在：" + path); }
                importer.SetAssetBundleNameAndVariant(Groups[i, 1], string.Empty);
            }
            // Shader仍按用户要求放在GameShader，而不是复制到每个材质目录。
            // 显式归入共享包后，依赖它的材质/预制体只引用一份编译结果，避免Shader再次隐式重复。
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/GameShader" }))
            {
                AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)).SetAssetBundleNameAndVariant("shared_shaders", string.Empty);
            }
            AssetDatabase.RemoveUnusedAssetBundleNames();
            AssetDatabase.SaveAssets();
            ABCollectResult collect = ABCollector.Collect();
            ABValidateResult validation = ABValidator.Validate(collect);
            ABValidator.ValidateAndroidShaders(collect, validation, BuildTarget.Android);
            if (!validation.CanBuild) { throw new InvalidOperationException(string.Join("\n", validation.errors)); }
            ABDependencyReport dependencies = ABDependencyAnalyzer.AnalyzeFromDatabase(collect);
            if (dependencies.HasIssue)
            {
                throw new InvalidOperationException("分包依赖门禁：隐式重复" + dependencies.sharedAssets.Count + "，循环依赖" + dependencies.circularBundles.Count);
            }
            int count = ABManifestWriter.WriteResMap(collect);
            ABResPathGenerator.Generate();
            ABSoundPathGenerator.Generate();
            AssetDatabase.SaveAssets();
            return count + "条映射，" + validation.bundleCount + "个包，隐式重复/循环/漏标/重名/Android Shader错误均0条";
        }

        /// <summary>实际构建二进制并记录清单。供MCP调用，不调用会退出Unity的CI入口。</summary>
        public static string Build(BuildTarget target)
        {
            Configure();
            ABBuildResult built = ABBuilderCore.Build(target);
            if (!built.success) { throw new InvalidOperationException("AB构建失败：" + target); }
            ABManifestWriter.WriteBuildManifest(built);
            return target + "：" + built.manifest.allBundles.Length + "个包 → " + Path.GetFullPath(built.outputDir);
        }

        /// <summary>构建并使用框架工具发布到允许入库的StreamingAssets；外部AssetBundles仍是可再生成产物。</summary>
        public static string BuildAndCopy(BuildTarget target)
        {
            string result = Build(target);
            ABBuildConfig config = ABBuildConfig.Instance;
            bool previousCopy = config.copyToStreamingAssets;
            string previousTarget = config.copyTarget;
            string platform = target == BuildTarget.StandaloneWindows64 ? "PC" : target.ToString();
            string output = Path.GetFullPath("AssetBundles/" + platform);
            string allowed = Path.GetFullPath("Assets/StreamingAssets") + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(allowed, platform));
            // 框架复制会重建目标平台目录，所以在调用前验证最终绝对路径；不能拿任意配置字符串作递归清理目标。
            if (!destination.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("StreamingAssets发布目标越界：" + destination);
            }
            try
            {
                config.copyToStreamingAssets = true;
                config.copyTarget = "StreamingAssets";
                ABBuilderCore.CopyToStreamingAssets(output);
            }
            finally
            {
                config.copyToStreamingAssets = previousCopy;
                config.copyTarget = previousTarget;
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssets();
            }
            return result + "；已复制到 " + destination;
        }
    }
}

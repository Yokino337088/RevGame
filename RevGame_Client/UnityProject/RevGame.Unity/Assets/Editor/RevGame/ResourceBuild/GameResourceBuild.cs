// ============================================================
// GameResourceBuild.cs —— 项目资源分包约定与Revolution打包入口。
// 这里只给项目资源设标记、串联框架已有工具；不复制打包算法、不修改Revolution快照。
// 文件夹名表达用途，AB名表达加载边界；共享UI图片和大字体单独打包，避免每个面板重复包含它们。
// ============================================================
using System;
using System.Collections.Generic;
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
            // 过场只依赖字体/程序星雾；单独分包，不能为显示加载条先拉入所有战斗界面的依赖。
            { "UI/Panel/Loading", "ui_loading" },
            { "Fonts/CJK", "font_cjk" },
            { "Board", "battle_board" }, { "Pieces/Unit", "battle_units" },
            { "Pieces/Boss", "battle_boss" }, { "Config/Art", "config_piece_art" },
            { "Effects/Combat", "battle_feedback" }, { "Data", "data_tables" },
            { "Material/Cards", "material_cards" }, { "Material/UI", "material_ui" },
            { "Material/Background", "material_background" },
            { "Material/Pieces", "material_pieces" }, { "Material/Combat", "material_combat" },
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
            // 章节大图由背景 Shader 独立采样、逐章加载，不进常驻 SpriteAtlas，也不共包拖入其它章节。
            const string chapterRoot = "Assets/GameRes/ChapterBackground";
            if (AssetDatabase.IsValidFolder(chapterRoot))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { chapterRoot }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string bundle = "background_" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant(bundle, string.Empty);
                }
            }
            // Shader仍按用户要求放在GameShader，而不是复制到每个材质目录。
            // 显式归入共享包后，依赖它的材质/预制体只引用一份编译结果，避免Shader再次隐式重复。
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/GameShader" }))
            {
                AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)).SetAssetBundleNameAndVariant("shared_shaders", string.Empty);
            }
            // 图集必须在目录默认打标之后执行：源图和图集归同一个包，不能被上面的粗目录标记覆盖。
            GameSpriteAtlasBuild.Configure();
            string atlasReport = GameSpriteAtlasBuild.Validate();
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
            return count + "条映射，" + validation.bundleCount + "个包，" + atlasReport + "；隐式重复/循环/漏标/重名/Android Shader错误均0条";
        }

        /// <summary>实际构建二进制并记录清单。供MCP调用，不调用会退出Unity的CI入口。</summary>
        public static string Build(BuildTarget target)
        {
            Configure();
            ABBuildResult built = ABBuilderCore.Build(target);
            if (!built.success) { throw new InvalidOperationException("AB构建失败：" + target); }
            PruneObsoleteBundles(built);
            ABManifestWriter.WriteBuildManifest(built);
            return target + "：" + built.manifest.allBundles.Length + "个包 → " + Path.GetFullPath(built.outputDir);
        }

        /// <summary>
        /// 增量构建不会自动删除已改名的旧包。若直接复制整个目录，旧散图包仍占安装空间。
        /// 这里只清理“输出目录内、有同名.manifest、但不在本次主清单”的文件对；不递归删除目录。
        /// </summary>
        private static void PruneObsoleteBundles(ABBuildResult built)
        {
            string output = Path.GetFullPath(built.outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string allowed = Path.GetFullPath("AssetBundles") + Path.DirectorySeparatorChar;
            if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("旧包清理只允许项目AssetBundles平台子目录：" + output);
            }
            var keep = new HashSet<string>(built.manifest.allBundles, StringComparer.OrdinalIgnoreCase);
            keep.Add(Path.GetFileName(output)); // PC/Android主Manifest不是普通业务包，必须保留。
            foreach (string candidate in Directory.GetFiles(output))
            {
                string name = Path.GetFileName(candidate);
                if (Path.HasExtension(name) || keep.Contains(name) || !File.Exists(candidate + ".manifest")) { continue; }
                // candidate来自已验证目录的非递归枚举，不能带外部用户路径；仅删除可再生成的旧二进制和清单。
                File.Delete(candidate);
                File.Delete(candidate + ".manifest");
            }
        }

        /// <summary>构建并使用框架工具发布到允许入库的StreamingAssets；外部AssetBundles仍是可再生成产物。</summary>
        public static string BuildAndCopy(BuildTarget target)
        {
            string result = Build(target);
            return result + PublishBuilt(target);
        }

        /// <summary>
        /// 只发布已经构建成功的平台目录，供复制被文件占用打断后恢复，不需要再次构建所有AB。
        /// 常规路径仍调用Revolution复制工具；编辑器持有未改动字体包时使用逐文件校验补齐，避免关闭Unity。
        /// </summary>
        public static string PublishBuilt(BuildTarget target)
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("不能在Play时覆盖正在使用的AB"); }
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
            if (!File.Exists(Path.Combine(output, platform))) { throw new InvalidOperationException("没有已构建的平台主清单：" + output); }
            try
            {
                config.copyToStreamingAssets = true;
                config.copyTarget = "StreamingAssets";
                try { ABBuilderCore.CopyToStreamingAssets(output); }
                catch (IOException)
                {
                    // Unity原生字体可能继续持有文件，整目录删除因而失败；旧字体与新字体字节相同就无需碰它。
                    // 只跳过逐字节相同的文件，发生变化又被占用时仍报错，不能假装发布成功。
                    CopyChangedFiles(output, destination);
                }
            }
            finally
            {
                config.copyToStreamingAssets = previousCopy;
                config.copyTarget = previousTarget;
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssets();
            }
            AssetDatabase.Refresh();
            return "；已复制到 " + destination;
        }

        /// <summary>仅用于上方已验证的两个平台目录；保留Unity的.meta，补齐失败复制并删除已经过时的生成包。</summary>
        private static void CopyChangedFiles(string output, string destination)
        {
            Directory.CreateDirectory(destination);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in Directory.GetFiles(output))
            {
                string name = Path.GetFileName(source);
                expected.Add(name);
                string target = Path.Combine(destination, name);
                if (!SameBytes(source, target)) { File.Copy(source, target, true); }
            }
            foreach (string target in Directory.GetFiles(destination))
            {
                string name = Path.GetFileName(target);
                if (expected.Contains(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) { continue; }
                // 只删除AB工具生成的无扩展名包/manifest/构建清单，不递归删任意目录或其他项目文件。
                if (!Path.HasExtension(name) || name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || name == "BuildManifest.json")
                {
                    File.Delete(target);
                    if (File.Exists(target + ".meta")) { File.Delete(target + ".meta"); }
                }
            }
        }

        /// <summary>流式比较，最多两个64KB缓冲；不把大字体包整份读入内存。长度相同仍须比较内容。</summary>
        private static bool SameBytes(string source, string destination)
        {
            if (!File.Exists(destination) || new FileInfo(source).Length != new FileInfo(destination).Length) { return false; }
            using (FileStream left = File.OpenRead(source))
            using (FileStream right = File.OpenRead(destination))
            {
                var a = new byte[65536];
                var b = new byte[65536];
                int count;
                while ((count = left.Read(a, 0, a.Length)) > 0)
                {
                    if (right.Read(b, 0, count) != count) { return false; }
                    for (int i = 0; i < count; i++) { if (a[i] != b[i]) { return false; } }
                }
                return true;
            }
        }
    }
}

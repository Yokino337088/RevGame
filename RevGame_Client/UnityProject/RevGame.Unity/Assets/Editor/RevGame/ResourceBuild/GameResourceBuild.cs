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
using UnityEngine;

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
            { "UI/Panel/Story", "ui_story" }, { "Config/Story", "config_story" },
            { "UI/Panel/Settings", "ui_settings" },
            { "UI/Panel/Victory", "ui_victory" },
            { "Fonts/CJK", "font_cjk" },
            { "Board", "battle_board" }, { "Pieces/Unit", "battle_units" },
            { "Pieces/Boss", "battle_boss" }, { "Config/Art", "config_piece_art" },
            { "Effects/Combat", "battle_feedback" }, { "Data", "data_tables" },
            { "Material/Cards", "material_cards" }, { "Material/UI", "material_ui" },
            { "Material/Background", "material_background" },
            { "Material/Pieces", "material_pieces" }, { "Material/Combat", "material_combat" },
            { "Audio/Bgm", "audio_bgm" }, { "Audio/Sfx", "audio_sfx" },
            { "Audio/Sfx/Helper", "audio_helper" }, { "Audio/Sfx/Boss/Common", "audio_boss_common" },
            { "Audio/Sfx/Boss/Chapter01", "audio_boss_chapter01" }, { "Audio/Sfx/Boss/Chapter02", "audio_boss_chapter02" },
            { "Audio/Sfx/Boss/Chapter03", "audio_boss_chapter03" }, { "Audio/Sfx/Boss/Chapter04", "audio_boss_chapter04" },
            { "Audio/Sfx/Boss/Chapter05", "audio_boss_chapter05" }, { "Audio/Sfx/Boss/Chapter06", "audio_boss_chapter06" }
        };

        /// <summary>幂等标记并用框架生成ResMap、路径和音效目录常量；有漏标/重名/非法名就中止，不掩盖错误。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请退出Play再更改分包");
            }
            // 资源制作约定随正式构建执行，防止旧导入菜单或重建预制体再次带回未压缩背景/旧字体。
            GameResourceCompressionBuild.Configure();
            // 今天新增的数据场装配挂在既有构建总入口，确保菜单/自动化/重新构建都得到相同绑定和导入规则。
            GameBackgroundFieldBuild.Configure();
            // 文案或配置变化后必须先重新制作字库；不能继续使用旧子集发布缺字版本。
            GameFontSubsetBuild.Validate();
            ABBuildConfig config = ABBuildConfig.Instance;
            config.resRoot = "Assets/GameRes";
            config.markMode = ABMarkMode.ScanExisting;
            config.checkUnmarkedAssets = true;
            // Art/Atlases是素材库，不再要求每个闲置文件都进包。ScanExisting仍会收集其中明确打标的使用中资产；
            // 此排除只免除素材库的“无标记即报错”，不能代替下面GameResourceUsage的逐张引用/图集闭包校验。
            foreach (string folder in new[] { "Art", "Atlases" })
            {
                if (!config.excludeFolders.Contains(folder))
                {
                    config.excludeFolders.Add(folder);
                }
            }
            EditorUtility.SetDirty(config);
            // 先计算最终标记再一次写入，避免旧流程先清空再恢复数百个meta，减少重复保存与资源重导入。
            // 不变的资产完全不写；Art/Atlases由下方唯一归属配置负责增删，不经历临时空包名。
            var desired = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("", new[] { config.resRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AssetImporter importer = AssetImporter.GetAtPath(path);

                if (importer != null && !path.StartsWith("Assets/GameRes/Art/", StringComparison.Ordinal)
                    && !path.StartsWith("Assets/GameRes/Atlases/", StringComparison.Ordinal))
                {
                    desired[path] = string.Empty;
                }
            }
            for (int i = 0; i < Groups.GetLength(0); i++)
            {
                string path = config.resRoot + "/" + Groups[i, 0];
                AssetImporter importer = AssetImporter.GetAtPath(path);

                if (importer == null)
                {
                    throw new InvalidOperationException("分包目录不存在：" + path);
                }
                desired[path] = Groups[i, 1];
            }
            // 章节大图由背景 Shader 独立采样、逐章加载，不进常驻 SpriteAtlas，也不共包拖入其它章节。
            const string chapterRoot = "Assets/GameRes/ChapterBackground";

            if (AssetDatabase.IsValidFolder(chapterRoot))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { chapterRoot }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    // 第一章迁入同一目录后仍由 SpriteAtlas 管理，其余五章的材质+Default纹理逐章同包。
                    var textureImporter = AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;

                    if (textureImporter == null || textureImporter.textureType == UnityEditor.TextureImporterType.Sprite)
                    {
                        continue;
                    }
                    // 噪声/冰胞场是背景内部数据，不能按其文件名创建额外场景背景或独立全局包。
                    // 按用途显式归第四/第五章背景包；不能套“文件名→独立背景”的一般规则，否则会创建不存在的噪声背景材质。
                    // 随对应章资源加载，而不是放入全局包或所有Sprite图集。
                    if (path.EndsWith("Chapter04_SwampNoise.png", StringComparison.Ordinal))
                    {
                        desired[path] = "background_chapter04_swamp";
                        continue;
                    }
                    if (path.EndsWith("Chapter05_IceField.png", StringComparison.Ordinal))
                    {
                        desired[path] = "background_chapter05_ice";
                        continue;
                    }
                    string bundle = "background_" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    desired[path] = bundle;
                    string materialPath = "Assets/GameRes/Material/Background/" + Path.GetFileNameWithoutExtension(path) + "_Material.mat";
                    AssetImporter materialImporter = AssetImporter.GetAtPath(materialPath);

                    if (materialImporter == null)
                    {
                        throw new InvalidOperationException("章节图片缺少对应的独立材质：" + path);
                    }
                    desired[materialPath] = bundle;
                }
            }

            // Shader仍按用户要求放在GameShader，而不是复制到每个材质目录。
            // 显式归入共享包后，依赖它的材质/预制体只引用一份编译结果，避免Shader再次隐式重复。
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/GameShader" }))
            {
                desired[AssetDatabase.GUIDToAssetPath(guid)] = "shared_shaders";
            }
            // 每章的图/材质/Shader归同一个包。UI使用的ArcaneBackdrop保留在公共Shader包，
            // 第一章改用Chapter01_Tutorial，避免大厅因共享Shader把第一章包作为依赖带入。
            string[] chapterShaders = { "Chapter01_Tutorial", "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
            for (int i = 0; i < chapterShaders.Length; i++)
            {
                string bundle = "background_" + chapterShaders[i].ToLowerInvariant();
                string shaderPath = "Assets/GameShader/Background/" + chapterShaders[i] + "_Shader.shader";
                string materialName = i == 0 ? "ArcaneScene" : chapterShaders[i] + "_Material";
                string materialPath = "Assets/GameRes/Material/Background/" + materialName + ".mat";
                foreach (string path in new[] { shaderPath, materialPath })
                {
                    AssetImporter importer = AssetImporter.GetAtPath(path);

                    if (importer == null)
                    {
                        throw new InvalidOperationException("章节独立资源缺失：" + path);
                    }
                    desired[path] = bundle;
                }
            }
            // 棋盘常驻材质与专属Shader随同章背景打包；预制体只在运行时借当前章，不硬引用六章。
            for (int i = 0; i < chapterShaders.Length; i++)
            {
                string name = chapterShaders[i];
                string bundle = "background_" + name.ToLowerInvariant();
                desired["Assets/GameShader/Board/" + name + "_SurfaceShader.shader"] = bundle;
                desired["Assets/GameRes/Material/Board/" + name + "_Surface.mat"] = bundle;
            }
            desired["Assets/GameRes/Material/Board/PlacementMagic.mat"] = "material_board";
            // 长BGM逐曲独立分包：选择一章不会把其它歌曲一起装进内存。原MP3保留，由Unity只转码一次。
            desired["Assets/GameRes/Pieces/Helper"] = "battle_helper";
            desired["Assets/GameRes/Effects/Helper"] = "battle_helper_fx";
            desired["Assets/GameRes/HelperTextures"] = "battle_helper_fx";
            desired["Assets/GameRes/Material/Helper/HelperBlueBeam.mat"] = "battle_helper_fx";
            desired["Assets/GameShader/Helper/HelperBlueBeam_Shader.shader"] = "battle_helper_fx";
            desired["Assets/GameRes/Material/Helper/HelperGoldReflect.mat"] = "battle_helper";
            desired["Assets/GameShader/Helper/HelperGoldReflect_Shader.shader"] = "battle_helper";
            desired["Assets/GameRes/Material/Helper/HelperRune.mat"] = "battle_helper_fx";
            desired["Assets/GameShader/Helper/HelperRune_Shader.shader"] = "battle_helper_fx";
            desired["Assets/GameRes/Material/Helper/HelperBeamImpact.mat"] = "battle_helper_fx";
            desired["Assets/GameRes/Material/Helper/HelperStorm.mat"] = "battle_helper_fx";
            desired["Assets/GameShader/Helper/HelperStorm_Shader.shader"] = "battle_helper_fx";
            desired["Assets/GameShader/Helper/HelperBeamImpact_Shader.shader"] = "battle_helper_fx";
            // 治疗线与蘑菇云是共享短效，单独归帮手特效包，不把六章背景一起拉入内存。
            foreach (string effect in new[] { "HelperHealingBeam", "HelperHealingImpact", "HelperMushroomImpact" })
            {
                desired["Assets/GameRes/Material/Helper/" + effect + ".mat"] = "battle_helper_fx";
                desired["Assets/GameShader/Helper/" + effect + "_Shader.shader"] = "battle_helper_fx";
            }
            for (int i = 0; i < chapterShaders.Length; i++)
            {
                string name = "HelperChapter" + (i + 1).ToString("00");
                string bundle = "background_" + chapterShaders[i].ToLowerInvariant();
                desired["Assets/GameRes/Material/Helper/" + name + ".mat"] = bundle;
                desired["Assets/GameShader/Helper/" + name + "_Shader.shader"] = bundle;
            }
            GameChapterAudioBuild.Configure(desired);
            // 每章Boss配置只引用该章8帧，配置和图集同包；普通棋子预制体不能引用六章配置。
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                desired["Assets/GameRes/Config/Boss/BossChapter" + chapter.ToString("D2") + ".asset"]
                    = "atlas_bosschapter" + chapter.ToString("D2") + "frames";
                // 死亡Shader与身体/光晕材质随本章Boss同包，避免一场Boss把六章特效全加载。
                string[] deathNames = { "Chapter01_Stardust", "Chapter02_Sunlight", "Chapter03_Water", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Cross" };
                string deathName = deathNames[chapter - 1];
                string bossBundle = "atlas_bosschapter" + chapter.ToString("D2") + "frames";
                desired["Assets/GameShader/BossDeath/" + deathName + ".shader"] = bossBundle;
                desired["Assets/GameRes/Material/BossDeath/" + deathName + "_Body.mat"] = bossBundle;
                desired["Assets/GameRes/Material/BossDeath/" + deathName + "_Aura.mat"] = bossBundle;
            }
            foreach (var pair in desired)
            {
                SetBundleIfChanged(AssetImporter.GetAtPath(pair.Key), pair.Value);
            }
            // 图集必须在目录默认打标之后执行：源图和图集归同一个包，不能被粗目录标记覆盖。
            GameSpriteAtlasBuild.Configure();
            string atlasReport = GameSpriteAtlasBuild.Validate();
            string usageReport = GameResourceUsage.Validate();
            AssetDatabase.RemoveUnusedAssetBundleNames();
            AssetDatabase.SaveAssets();
            ABCollectResult collect = ABCollector.Collect();
            ABValidateResult validation = ABValidator.Validate(collect);
            ABValidator.ValidateAndroidShaders(collect, validation, BuildTarget.Android);

            if (!validation.CanBuild)
            {
                throw new InvalidOperationException(string.Join("\n", validation.errors));
            }
            ABDependencyReport dependencies = ABDependencyAnalyzer.AnalyzeFromDatabase(collect);

            if (dependencies.HasIssue)
            {
                throw new InvalidOperationException("分包依赖门禁：隐式重复" + dependencies.sharedAssets.Count + "，循环依赖" + dependencies.circularBundles.Count);
            }
            int count = ABManifestWriter.WriteResMap(collect);
            ABResPathGenerator.Generate();
            ABSoundPathGenerator.Generate();
            AssetDatabase.SaveAssets();
            return count + "条映射，" + validation.bundleCount + "个包，" + atlasReport + "；" + usageReport + "；隐式重复/循环/漏标/重名/Android Shader错误均0条";
        }
        /// <summary>项目侧幂等打标；不改框架，不关闭漏标/重复依赖校验。</summary>
        internal static void SetBundleIfChanged(AssetImporter importer, string bundle)
        {
            if (importer.assetBundleName != bundle || !string.IsNullOrEmpty(importer.assetBundleVariant))
            {
                importer.SetAssetBundleNameAndVariant(bundle, string.Empty);
            }
        }

        /// <summary>实际构建二进制并记录清单。供MCP调用，不调用会退出Unity的CI入口。</summary>
        public static string Build(BuildTarget target)
        {
            Configure();
            ABBuildResult built = ABBuilderCore.Build(target);

            if (!built.success)
            {
                throw new InvalidOperationException("AB构建失败：" + target);
            }
            ValidateChapterBundleClosure(built.manifest.raw);
            PruneObsoleteBundles(built);
            ABManifestWriter.WriteBuildManifest(built);
            return target + "：" + built.manifest.allBundles.Length + "个包；六章背景包依赖为0，其它包不引用背景包 → " + Path.GetFullPath(built.outputDir);
        }
        /// <summary>查真正构建出来的Manifest，而非仅看编辑器标记，防止图集/材质隐式把其它章节一起带入。</summary>
        private static void ValidateChapterBundleClosure(AssetBundleManifest manifest)
        {
            string[] chapterBundles = { "background_chapter01_tutorial", "background_chapter02_village", "background_chapter03_lake", "background_chapter04_swamp", "background_chapter05_ice", "background_chapter06_temple" };
            var expected = new HashSet<string>(chapterBundles, StringComparer.Ordinal);
            int chapters = 0;
            foreach (string bundle in manifest.GetAllAssetBundles())
            {
                string[] dependencies = manifest.GetAllDependencies(bundle);

                if (expected.Contains(bundle))
                {
                    chapters++;

                    if (dependencies.Length != 0)
                    {
                        throw new InvalidOperationException("章节包必须自包含：" + bundle + " → " + string.Join(",", dependencies));
                    }
                }

                else
                {
                    foreach (string dependency in dependencies)
                    {
                        if (expected.Contains(dependency))
                        {
                            throw new InvalidOperationException("非章节资源包不应依赖背景：" + bundle + " → " + dependency);
                        }
                    }
                }
            }
            if (chapters != chapterBundles.Length)
            {
                throw new InvalidOperationException("实际构建未包含全部六个章节背景包");
            }
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

                if (Path.HasExtension(name) || keep.Contains(name) || !File.Exists(candidate + ".manifest"))
                {
                    continue;
                }
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
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("不能在Play时覆盖正在使用的AB");
            }
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
            if (!File.Exists(Path.Combine(output, platform)))
            {
                throw new InvalidOperationException("没有已构建的平台主清单：" + output);
            }
            try
            {
                config.copyToStreamingAssets = true;
                config.copyTarget = "StreamingAssets";
                try
                {
                    ABBuilderCore.CopyToStreamingAssets(output);
                }
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

                if (!SameBytes(source, target))
                {
                    File.Copy(source, target, true);
                }
            }
            foreach (string target in Directory.GetFiles(destination))
            {
                string name = Path.GetFileName(target);

                if (expected.Contains(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // 只删除AB工具生成的无扩展名包/manifest/构建清单，不递归删任意目录或其他项目文件。
                if (!Path.HasExtension(name) || name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || name == "BuildManifest.json")
                {
                    File.Delete(target);

                    if (File.Exists(target + ".meta"))
                    {
                        File.Delete(target + ".meta");
                    }
                }
            }
        }
        /// <summary>流式比较，最多两个64KB缓冲；不把大字体包整份读入内存。长度相同仍须比较内容。</summary>
        private static bool SameBytes(string source, string destination)
        {
            if (!File.Exists(destination) || new FileInfo(source).Length != new FileInfo(destination).Length)
            {
                return false;
            }
            using (FileStream left = File.OpenRead(source))
            using (FileStream right = File.OpenRead(destination))
            {
                var a = new byte[65536];
                var b = new byte[65536];
                int count;
                while ((count = left.Read(a, 0, a.Length)) > 0)
                {
                    if (right.Read(b, 0, count) != count)
                    {
                        return false;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        if (a[i] != b[i])
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }
    }
}

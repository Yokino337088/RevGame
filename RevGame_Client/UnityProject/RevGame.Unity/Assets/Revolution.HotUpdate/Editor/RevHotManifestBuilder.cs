// ============================================================
// RevHotManifestBuilder.cs —— 生成热更清单（扫打包产物 → RevHotManifest.txt / RevHotBuiltin.txt）
//
// 位置：Assets\Revolution.HotUpdate\Editor\
//
// 【它做什么】
//   框架的打包工具打出 AB 产物后（AssetBundles/<平台>/），这份工具负责"看一遍产物"并生成：
//   ① <产物目录>/RevHotManifest.txt ：要上传到 CDN 的版本清单（唯一真相，最后上传）；
//   ② <产物目录>/RevHotBuiltin.txt  ：首包基线清单（原样拷进 StreamingAssets，客户端拿它算差量）；
//   ③ <产物目录>/ResMap.txt 副本    ：映射表跟着清单一起走（表与包同批更新）。
//
// 【数据从哪来（不重复造轮子）】
//   包列表   ：扫产物目录（排除主包 / .manifest / json / 我们自己的文件）；
//   大小     ：文件长度；
//   sha256   ：RevHotVerifier.ComputeSha256（运行时校验用的同一个算法）；
//   依赖     ：Unity 为每个包生成的 <包名>.manifest 文本里的 Dependencies 段；
//   unityHash / unityCrc：同一段文本里的 AssetFileHash / CRC（小游戏上交给引擎做缓存与校验）。
//   ★ Unity 的 .manifest 是文本，跨平台可读 —— 不需要"加载另一个平台的 AB"才能拿到依赖。
// ============================================================
using Revolution.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.HotUpdate.Editor
{
    /// <summary>清单生成器（窗口与 CI 都调它）。</summary>
    public static class RevHotManifestBuilder
    {
        /// <summary>映射表文件名（与框架 ABBuildSetting.MapAssetPath 指向的文件同名）。</summary>
        public const string ResMapFileName = "ResMap.txt";

        /// <summary>首包基线清单文件名（随包体发布的那份）。</summary>
        public const string BuiltinManifestFileName = "RevHotBuiltin.txt";

        /// <summary>生成清单。任何问题返回 null 并给出 error（人话）。</summary>
        public static RevHotManifest Build(BuildTarget target, string outputDir, string appVersion, string resVersion, string channel, string env, out string error)
        {
            error = null;

            if (Directory.Exists(outputDir) == false)
            {
                error = "产物目录不存在：" + outputDir + "（请先用 Revolution.Tools 的打包工具打一次 AB）";
                return null;
            }

            if (string.IsNullOrEmpty(appVersion) || string.IsNullOrEmpty(resVersion))
            {
                error = "AppVersion / ResVersion 不能为空（大版本锚定与差量计算都靠它们）";
                return null;
            }

            string platform = ABBuildSetting.GetPlatformName(target);
            string mainBundleName = platform;                       // Unity 用输出目录名给主包命名（主包只装 Manifest，不是业务包）
            if (!string.Equals(Path.GetFileName(Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    platform, StringComparison.Ordinal))
            {
                error = "产物目录名与构建平台不一致：应为 " + platform + "，实际为 " + outputDir;
                return null;
            }

            error = CheckMainBundle(Path.Combine(outputDir, mainBundleName));
            if (error != null) return null;

            var manifest = new RevHotManifest
            {
                AppVersion = appVersion,
                ResVersion = resVersion,
                MinAppVersion = appVersion,                         // 默认只兼容本大版本（补丁包语义）
                Platform = platform,
                Channel = channel,
                Env = env,
                HashAlgo = "sha256",
                BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BaseManifestFile = "Baseline.txt",
            };

            foreach (string file in Directory.GetFiles(outputDir))
            {
                string name = Path.GetFileName(file);

                if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;   // Unity 的清单文本
                if (string.Equals(name, "BuildManifest.json", StringComparison.OrdinalIgnoreCase)) continue; // 框架的打包摘要
                if (string.Equals(name, "RevHotManifest.txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, BuiltinManifestFileName, StringComparison.OrdinalIgnoreCase)) continue; // 只排除自己的清单，不误排 revhotdemo 等合法包
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".revhottmp", StringComparison.OrdinalIgnoreCase)) continue; // 上次进程中断遗留的发布临时文件
                if (string.Equals(name, mainBundleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ResMapFileName, StringComparison.OrdinalIgnoreCase)) continue;   // 上次 WriteOutputs 拷进来的映射表副本
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;                   // 隐藏文件

                var bundle = new RevHotBundleInfo
                {
                    Name = name,
                    Size = new FileInfo(file).Length,
                    Sha256 = RevHotVerifier.ComputeSha256(file),
                };

                string unityManifestFile = file + ".manifest";
                if (File.Exists(unityManifestFile))
                {
                    List<string> dependencies;
                    string unityHash;
                    string unityCrc;
                    try
                    {
                        ParseUnityManifest(unityManifestFile, out dependencies, out unityHash, out unityCrc);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        error = "AB 包的 Unity 依赖清单不可读取或不完整：" + unityManifestFile + "（" + e.Message + "）";
                        return null;
                    }
                    bundle.SetDependencies(string.Join(",", dependencies.ToArray()));
                    bundle.UnityHash = unityHash;
                    bundle.UnityCrc = unityCrc;
                }
                else
                {
                    error = "AB 包缺少 Unity 依赖清单，不能安全发布：" + unityManifestFile;
                    return null;
                }

                manifest.Bundles.Add(bundle);
            }

            if (manifest.Bundles.Count == 0)
            {
                error = "产物目录里没有扫到任何 AB 包：" + outputDir;
                return null;
            }

            string resMapPath = ABBuildSetting.MapAssetPath;
            if (!File.Exists(resMapPath))
            {
                error = "找不到映射表（" + resMapPath + "）：不能发布缺少 ResMap 的清单";
                return null;
            }
            manifest.ResMap = new RevHotFileInfo
            {
                Name = ResMapFileName,
                Sha256 = RevHotVerifier.ComputeSha256(resMapPath),
                Size = new FileInfo(resMapPath).Length,
            };

            error = manifest.ValidateForActivation(platform, channel);
            if (error != null) return null;
            error = CheckResMapReferences(resMapPath, manifest);
            if (error != null) return null;

            return manifest;
        }

        /// <summary>内容先就绪，再发布清单；源文件变化时拒绝覆盖已有产物。</summary>
        public static void WriteOutputs(RevHotManifest manifest, string outputDir)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            string error = manifest.ValidateForActivation(manifest.Platform, manifest.Channel);
            if (error != null) throw new InvalidOperationException("发布清单无效：" + error);
            if (!string.Equals(Path.GetFileName(Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    manifest.Platform, StringComparison.Ordinal))
                throw new InvalidOperationException("发布目录名与清单平台不一致：" + outputDir + " / " + manifest.Platform);
            foreach (RevHotBundleInfo bundle in manifest.Bundles)
                if (string.Equals(bundle.Name, "RevHotManifest.txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bundle.Name, BuiltinManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("AB 包名与发布清单文件冲突：" + bundle.Name);
            error = CheckMainBundle(Path.Combine(outputDir, manifest.Platform));
            if (error != null) throw new InvalidOperationException(error);

            var problems = new List<string>();
            for (int i = 0; i < manifest.Bundles.Count; i++)
            {
                RevHotBundleInfo bundle = manifest.Bundles[i];
                CheckFile(Path.Combine(outputDir, bundle.Name), bundle.Size, bundle.Sha256, "AB 包 " + bundle.Name, problems);
            }
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("；", problems.ToArray()));

            string resMapSource = ABBuildSetting.MapAssetPath;
            if (!File.Exists(resMapSource)) throw new InvalidOperationException("发布所需 ResMap 来源不存在：" + resMapSource);
            error = CheckResMapReferences(resMapSource, manifest);
            if (error != null) throw new InvalidOperationException(error);
            CheckFile(resMapSource, manifest.ResMap.Size, manifest.ResMap.Sha256, "ResMap 来源", problems);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("；", problems.ToArray()));

            string resMapTarget = Path.Combine(outputDir, manifest.ResMap.Name);
            string stagedMap = resMapTarget + ".revhottmp";
            try
            {
                File.Copy(resMapSource, stagedMap, true);
                CheckFile(stagedMap, manifest.ResMap.Size, manifest.ResMap.Sha256, "ResMap 待发布副本", problems);
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("；", problems.ToArray()));
                if (File.Exists(resMapTarget)) File.Replace(stagedMap, resMapTarget, null);
                else File.Move(stagedMap, resMapTarget);
            }
            finally
            {
                if (File.Exists(stagedMap)) File.Delete(stagedMap);
            }

            string text = manifest.Serialize();
            WriteTextSafely(Path.Combine(outputDir, BuiltinManifestFileName), text);

            // StreamingAssets 已存在时才同步首包基线；最后发布 CDN 清单，避免清单先于内容可见。
            string streamingDir = Path.Combine(Application.streamingAssetsPath, manifest.Platform);
            if (Directory.Exists(streamingDir))
                WriteTextSafely(Path.Combine(streamingDir, BuiltinManifestFileName), text);
            WriteTextSafely(Path.Combine(outputDir, "RevHotManifest.txt"), text);
        }

        /// <summary>对最终上传目录做完整性自检；空字符串表示所有发布门禁通过。</summary>
        public static string SelfCheck(RevHotManifest manifest, string outputDir)
        {
            var problems = new List<string>();
            if (manifest == null) return "1. 没有可检查的清单";
            if (!Directory.Exists(outputDir)) return "1. 发布目录不存在：" + outputDir;

            try
            {
                string manifestPath = Path.Combine(outputDir, "RevHotManifest.txt");
                if (!File.Exists(manifestPath)) return "1. 发布目录没有 RevHotManifest.txt";
                string text = File.ReadAllText(manifestPath);
                string parseError;
                RevHotManifest published = RevHotManifest.Parse(text, out parseError);
                if (published == null) return "1. 发布目录清单解析失败：" + parseError;
                if (!string.Equals(published.Serialize(), manifest.Serialize(), StringComparison.Ordinal))
                    problems.Add("内存清单与发布目录的 RevHotManifest.txt 不一致，请重新加载后自检");

                string error = published.ValidateForActivation(manifest.Platform, manifest.Channel);
                if (error != null)
                {
                    problems.Add("清单发布门禁未通过：" + error);
                    return FormatProblems(problems); // 路径未通过验证，不能用于 Path.Combine。
                }
                if (!string.Equals(Path.GetFileName(Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        published.Platform, StringComparison.Ordinal))
                    problems.Add("发布目录名与清单平台不一致：" + outputDir + " / " + published.Platform);

                var diskNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (string file in Directory.GetFiles(outputDir)) diskNames.Add(Path.GetFileName(file));
                if (!diskNames.Contains("RevHotManifest.txt")) problems.Add("发布清单文件名大小写不一致：RevHotManifest.txt");
                if (!diskNames.Contains(published.Platform)) problems.Add("平台主包文件名大小写不一致或文件缺失：" + published.Platform);
                error = CheckMainBundle(Path.Combine(outputDir, published.Platform));
                if (error != null) problems.Add(error);

                foreach (RevHotBundleInfo bundle in published.Bundles)
                {
                    if (!diskNames.Contains(bundle.Name)) problems.Add("AB 包文件名大小写不一致或文件缺失：" + bundle.Name);
                    else CheckFile(Path.Combine(outputDir, bundle.Name), bundle.Size, bundle.Sha256, "AB 包 " + bundle.Name, problems);
                }

                string resMapPath = Path.Combine(outputDir, published.ResMap.Name);
                if (!diskNames.Contains(published.ResMap.Name)) problems.Add("ResMap 文件名大小写不一致或文件缺失：" + published.ResMap.Name);
                else
                {
                    CheckFile(resMapPath, published.ResMap.Size, published.ResMap.Sha256, "ResMap", problems);
                    error = CheckResMapReferences(resMapPath, published);
                    if (error != null) problems.Add(error);
                }

                string baselinePath = Path.Combine(outputDir, BuiltinManifestFileName);
                if (!diskNames.Contains(BuiltinManifestFileName)) problems.Add("发布目录缺少首包基线清单：" + BuiltinManifestFileName);
                else if (!string.Equals(File.ReadAllText(baselinePath), text, StringComparison.Ordinal))
                    problems.Add("首包基线清单与发布清单不一致：" + BuiltinManifestFileName);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                problems.Add("发布目录读取失败：" + e.Message);
            }
            return FormatProblems(problems);
        }

        private static void WriteTextSafely(string path, string text)
        {
            string temp = path + ".revhottmp";
            try
            {
                File.WriteAllText(temp, text);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static string FormatProblems(List<string> problems)
        {
            if (problems.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder(256);
            for (int i = 0; i < problems.Count; i++) sb.AppendLine((i + 1) + ". " + problems[i]);
            return sb.ToString();
        }

        private static void CheckFile(string path, long expectedSize, string expectedHash, string label, List<string> problems)
        {
            try
            {
                if (!File.Exists(path)) { problems.Add(label + " 不存在：" + path); return; }
                long size = new FileInfo(path).Length;
                if (size != expectedSize)
                {
                    problems.Add(label + " 大小不一致（清单 " + expectedSize + " / 磁盘 " + size + "）：" + path);
                    return;
                }
                if (!string.Equals(RevHotVerifier.ComputeSha256(path), expectedHash, StringComparison.OrdinalIgnoreCase))
                    problems.Add(label + " SHA-256 不一致：" + path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problems.Add(label + " 无法读取：" + path + "（" + e.Message + "）");
            }
        }

        private static string CheckMainBundle(string path)
        {
            if (!File.Exists(path)) return "平台主包基线不存在：" + path;
            try
            {
                // 仅检查字节内容可读且具有 Unity AB 头；跨平台包不能依赖编辑器当前平台直接 LoadFromFile。
                using (FileStream stream = File.OpenRead(path))
                {
                    if (stream.Length < 8) return "平台主包基线为空或已截断：" + path;
                    byte[] header = new byte[8];
                    if (stream.Read(header, 0, header.Length) != header.Length) return "平台主包基线不可读：" + path;
                    string magic = System.Text.Encoding.ASCII.GetString(header);
                    if (!magic.StartsWith("UnityFS", StringComparison.Ordinal) &&
                        !magic.StartsWith("UnityRaw", StringComparison.Ordinal) &&
                        !magic.StartsWith("UnityWeb", StringComparison.Ordinal) &&
                        !magic.StartsWith("UnityArc", StringComparison.Ordinal))
                        return "平台主包基线不是有效的 Unity AB 文件头：" + path;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "平台主包基线无法读取：" + path + "（" + e.Message + "）";
            }
            return null;
        }

        private static string CheckResMapReferences(string path, RevHotManifest manifest)
        {
            var bundles = new HashSet<string>(StringComparer.Ordinal);
            foreach (RevHotBundleInfo bundle in manifest.Bundles) bundles.Add(bundle.Name);
            var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] fields = line.Split('|');
                if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0]) ||
                    string.IsNullOrWhiteSpace(fields[1]) || string.IsNullOrWhiteSpace(fields[2]) ||
                    fields[0] != fields[0].Trim() || fields[1] != fields[1].Trim() || fields[2] != fields[2].Trim())
                    return "ResMap 第 " + (i + 1) + " 行格式错误：应为逻辑名|包名|资源名，且字段不能为空或带首尾空格";
                if (!logicalNames.Add(fields[0])) return "ResMap 第 " + (i + 1) + " 行逻辑名重复：" + fields[0];
                if (!bundles.Contains(fields[1]))
                    return "ResMap 第 " + (i + 1) + " 行引用了清单中不存在（或大小写不一致）的包：" + fields[1];
                count++;
            }
            return count == 0 ? "ResMap 没有有效资源映射" : null;
        }

        // ============================================================
        // Unity .manifest 文本解析（CRC / AssetFileHash / Dependencies）
        // ============================================================

        /// <summary>
        /// 解析 Unity 生成的 .manifest 文本。
        /// ★ 字段名以实测为准（不同 Unity 版本可能有出入）—— 第一次使用时先打开产物里的 .manifest 看一眼。
        /// </summary>
        private static void ParseUnityManifest(string path, out List<string> dependencies, out string assetFileHash, out string crc)
        {
            dependencies = new List<string>();
            assetFileHash = string.Empty;
            crc = string.Empty;

            string section = string.Empty;
            bool hasDependenciesSection = false;
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i].Trim();

                if (text.StartsWith("CRC:", StringComparison.OrdinalIgnoreCase))
                {
                    crc = text.Substring(4).Trim();
                    continue;
                }

                if (text.StartsWith("Hashes:", StringComparison.OrdinalIgnoreCase)) { section = "Hashes"; continue; }
                if (text.StartsWith("AssetFileHash:", StringComparison.OrdinalIgnoreCase)) { section = "AssetFileHash"; continue; }
                if (text.StartsWith("TypeTreeHash:", StringComparison.OrdinalIgnoreCase)) { section = "TypeTreeHash"; continue; }
                if (text.StartsWith("Assets:", StringComparison.OrdinalIgnoreCase)) { section = "Assets"; continue; }
                if (text.StartsWith("Dependencies:", StringComparison.OrdinalIgnoreCase))
                {
                    string inline = text.Substring("Dependencies:".Length).Trim();
                    if (inline.Length > 0 && inline != "[]") throw new InvalidDataException("不支持的 Dependencies 内联格式：" + inline);
                    section = "Dependencies";
                    hasDependenciesSection = true;
                    continue;
                }

                if (text.StartsWith("Hash:", StringComparison.OrdinalIgnoreCase))
                {
                    // 只取 AssetFileHash 段里那个 Hash（它就是"这个包内容"的指纹）
                    if (section == "AssetFileHash" && assetFileHash.Length == 0)
                    {
                        assetFileHash = text.Substring(5).Trim();
                    }

                    continue;
                }

                if (text.StartsWith("- ", StringComparison.Ordinal) && section == "Dependencies")
                {
                    dependencies.Add(text.Substring(2).Trim());
                }
            }
            if (!hasDependenciesSection) throw new InvalidDataException("缺少 Dependencies 段");
        }
    }
}

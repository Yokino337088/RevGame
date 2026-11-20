// ============================================================
// RevHotStore.cs —— 盘上布局 / 版本指针 / 原子落盘 / 首包落地 / 加载路径解析
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Pipeline\
//
// 【目录布局（文件型平台：Android / iOS / PC）】
//   {persistentDataPath}/RevHotUpdate/
//   ├── current.txt                              ← 当前生效的资源版本（原子替换）
//   ├── _builtin/{平台}/&lt;包名&gt;                  ← 首包落地（Android 从 APK 里拷出来的那一份）
//   └── {平台}/{大版本}/{资源版本}/
//       ├── .stamp                               ← 完成标记（有它才允许被加载）
//       ├── RevHotManifest.txt                   ← 该版本清单副本（回滚 / 排查用）
//       ├── ResMap.txt                           ← 热更映射表
//       ├── bundles/&lt;包名&gt;                       ← AB 包
//       └── bundles/&lt;包名&gt;.part                  ← 下载中的半成品（断点续传就靠它）
//
// 【五条纪律（每一条都在防一个真实故障）】
//   ① 版本目录写完才打 .stamp —— 半新半旧永远不可见（加载只认 current 指向且带 stamp 的版本）；
//   ② current.txt 用"写临时文件 + 替换"—— 防止断电写出半行文本；
//   ③ 下载一律写 .part，完成并校验通过才 rename —— 绝不会出现"半个包被当成好包"；
//   ④ 覆盖已存在的文件前先删再移 —— File.Move 在目标存在时会抛异常；
//   ⑤ 小游戏（WebGL）没有文件系统 —— 所有文件相关操作都必须先看 SupportsLocalFiles。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using Revolution;
using UnityEngine;
using UnityEngine.Networking;

namespace Revolution.HotUpdate
{
    /// <summary>本地存储：热更包的"磁盘管家"（同时也是框架那两个钩子的实现方）。</summary>
    internal static class RevHotStore
    {
        private static RevHotConfig _config;
        private static string _platform = "";
        private static string _appVersion = "";
        private static string _localRoot = "";
        private static string _builtinDir = "";
        private static string _activeResVersion = "";
        private static bool _configured;

        // URL 型平台（WebGL/小游戏）没有本地文件，"热更映射表"放在内存里，资源系统 Init 时取走
        private static Dictionary<string, string> _memoryResMap;

        // URL 型平台：当前版本每个包的"引擎缓存标识"（unityHash / unityCrc）。
        // ★ 为什么也要寄存在内存里：那边的 AB 是按 URL 下载的，引擎判断"本地缓存过没有"全靠
        //   调用方把 hash/crc 传进 UnityWebRequestAssetBundle.GetAssetBundle —— 不传就是每次进游戏重下全部包。
        //   清单每次 Check 都会拉下来，顺手把这两列抽出来放着即可（文件型平台用不上，保持 null）。
        private static Dictionary<string, RevBundleCacheKey> _bundleKeys;

        // 文件型平台的"历史版本回退链"（新 → 旧，只含带 .stamp 的完整版本目录的 bundles 路径）。
        // ★ 为什么必须回退：差量更新只把"新增 / 变更"的包写进新版本目录，未变化的包还留在旧版本目录里 ——
        //   加载只认当前版本目录的话，热更一次后其余包全部加载失败（这是差量方案能够成立的前提）。
        //   WebGL 不需要它：那边的引擎缓存以 Hash128 为 key，hash 相同就命中缓存，URL 变了也无所谓。
        private static string[] _fallbackBundleDirs = Array.Empty<string>();

        // 当前生效的资源清单（SetActiveBundleKeys 时一并钉住，所有平台都需要）。
        // ★ 用途：给 RevABLoader.DependenciesOverride 提供依赖图 —— 主包 Manifest 是出包时的静态快照，
        //   不认识热更新增的包、记不住变更包的新依赖；没有这份清单，新增/变更包的依赖闭包不会被自动加载。
        private static RevHotManifest _activeManifest;

        private const string PlayerPrefsKeyPrefix = "RevHotUpdate.Current.";

        // ============================================================
        // 配置与状态
        // ============================================================

        /// <summary>装配置（InitializeAsync 第一步；之后的路径都从这里算）。</summary>
        public static void Configure(RevHotConfig config)
        {
            _config = config;
            _platform = config.Platform;
            _appVersion = config.AppVersion;
            _localRoot = config.LocalRoot;
            _builtinDir = Path.Combine(_localRoot, "_builtin", _platform);
            _configured = true;
        }

        /// <summary>当前生效的资源版本（加载路径解析就按它定位目录）。</summary>
        public static string ActiveResVersion { get { return _activeResVersion; } }

        /// <summary>切换前保留的完整清单，用于资源系统重装失败时恢复依赖与缓存键。</summary>
        public static RevHotManifest ActiveManifest { get { return _activeManifest; } }

        /// <summary>URL 平台重装失败时恢复内存映射表。</summary>
        public static Dictionary<string, string> ActiveMemoryResMap { get { return _memoryResMap; } }

        /// <summary>把某个资源版本设为"生效"（之后 ResolveBundlePath 就指向它的 bundles 目录）。</summary>
        public static void SetActiveVersion(string resVersion)
        {
            _activeResVersion = resVersion ?? string.Empty;
            RebuildFallbackChain();
        }

        /// <summary>
        /// 重建历史版本回退链：当前版本之外、带 .stamp 的完整版本目录，按版本号新 → 旧排好。
        /// ★ 只认完整版本：半截目录（.stamp 还没打上）里的包没经过校验，绝不能进回退链。
        /// </summary>
        private static void RebuildFallbackChain()
        {
            _fallbackBundleDirs = Array.Empty<string>();
            if (RevHotPlatform.SupportsLocalFiles == false) return;
            if (_configured == false || string.IsNullOrEmpty(_activeResVersion)) return;

            try
            {
                string appDir = AppVersionDir();
                if (Directory.Exists(appDir) == false) return;

                List<string> versions = new List<string>(Directory.GetDirectories(appDir));

                Comparison<string> newestFirst = delegate(string a, string b)
                {
                    return RevHotVersion.Compare(Path.GetFileName(b), Path.GetFileName(a));
                };
                versions.Sort(newestFirst);

                List<string> dirs = new List<string>(versions.Count);
                for (int i = 0; i < versions.Count; i++)
                {
                    string name = Path.GetFileName(versions[i]);
                    if (string.Equals(name, _activeResVersion, StringComparison.Ordinal)) continue;   // 当前版本永远第一优先，不进回退链
                    if (File.Exists(Path.Combine(versions[i], ".stamp")) == false) continue;          // 不完整 = 不可信

                    dirs.Add(Path.Combine(versions[i], "bundles"));
                }

                _fallbackBundleDirs = dirs.ToArray();
            }
            catch (Exception e)
            {
                // 链建不起来就退化为"只用当前版本 + 首包"：加载仍能走，只是未变化包可能要等下次重建
                RevLog.Warn("历史版本回退链构建失败（忽略）：" + e.Message, LogTag());
            }
        }

        /// <summary>URL 型平台：把"下载阶段拿到的热更映射表"先寄存在内存里（资源系统 Init 时取走）。</summary>
        public static void SetActiveResMap(Dictionary<string, string> resMap)
        {
            _memoryResMap = resMap;
        }

        /// <summary>
        /// URL 型平台：内存映射表是否已就位。★ 静态字段在重启 / 快速进入 Play 时会被清空 ——
        /// 每个新会话都必须重新拉一次表，否则热更新增的资源加载不到（门面用它在收尾时补拉）。
        /// </summary>
        public static bool HasMemoryResMap { get { return _memoryResMap != null; } }

        /// <summary>
        /// URL 型平台：把"生效清单里每个包的引擎缓存标识（unityHash / unityCrc）"抽出来寄存进内存。
        ///
        /// ★ 为什么必须在"切版本"时同步做：这个表是**版本相关**的 —— 换了资源版本，
        ///   同一个包名对应的 hash 也变了；漏更新会让引擎拿着旧 hash 去校验新包，直接加载失败。
        ///
        /// ★ 只对 WebGL / 小游戏有意义（文件型平台的包在本地磁盘，加载不走这套缓存机制）；
        ///   老清单里没有这两列时整表留空 —— 退化为"无缓存标识"，加载照常，只是引擎不缓存。
        /// </summary>
        public static void SetActiveBundleKeys(RevHotManifest manifest)
        {
            // ★ 清单本身所有平台都要存（依赖解析钩子用），必须在平台分支之前赋值
            _activeManifest = manifest;

            if (RevHotPlatform.IsWebGL == false) { _bundleKeys = null; return; }
            if (manifest == null || manifest.Bundles.Count == 0) { _bundleKeys = null; return; }

            var keys = new Dictionary<string, RevBundleCacheKey>(manifest.Bundles.Count);
            for (int i = 0; i < manifest.Bundles.Count; i++)
            {
                RevHotBundleInfo bundle = manifest.Bundles[i];
                if (string.IsNullOrEmpty(bundle.UnityHash)) { continue; }   // 老清单没这两列：跳过

                Hash128 hash;
                try
                {
                    hash = Hash128.Parse(bundle.UnityHash);
                }
                catch (Exception e)
                {
                    // 清单是外部数据：一条坏 hash 只跳过这一条，绝不让整个版本切换失败
                    RevLog.Warn("包的 UnityHash 解析失败，该包本轮不做引擎缓存：" + bundle.Name + "（" + e.Message + "）", LogTag());
                    continue;
                }

                keys[bundle.Name] = new RevBundleCacheKey
                {
                    Hash = hash,
                    Crc = ParseCrc(bundle.UnityCrc),      // 空 / 非法 → 0（0 = 不做加载期 CRC 校验，缓存仍然生效）
                    IsValid = true,
                };
            }

            _bundleKeys = keys.Count > 0 ? keys : null;
        }

        private static uint ParseCrc(string text)
        {
            if (string.IsNullOrEmpty(text)) { return 0; }
            return uint.TryParse(text, out uint crc) ? crc : 0;
        }

        /// <summary>
        /// 解析映射表文本（"逻辑名|包名|资源名"，# 注释、空行忽略）。
        /// 与框架 RevResBootstrap.LoadResMap 的解析规则保持一致 —— 改一处必须改另一处。
        /// </summary>
        public static Dictionary<string, string> ParseResMapText(string text)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return map;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string[] parts = line.Split('|');
                if (parts.Length != 3) continue;

                map[parts[0]] = parts[1] + "|" + parts[2];
            }

            return map;
        }

        /// <summary>排查用：当前装配与路径快照（RevHotUpdate.Dump 的一部分）。</summary>
        public static string Describe()
        {
            if (_configured == false) return "（RevHotStore 尚未装配）";
            return "平台 " + _platform + " · 大版本 " + _appVersion
                 + " · 生效资源版本 " + (string.IsNullOrEmpty(_activeResVersion) ? "（无）" : _activeResVersion)
                 + "\n  本地根 " + _localRoot
                 + "\n  内置目录 " + _builtinDir;
        }

        /// <summary>
        /// P0：把差量目录补成可独立加载的完整快照，且每个来源必须与目标清单 hash 一致。
        /// 老版本目录即使还存在，也绝不能只凭“同名文件存在”就取用：它可能是不同资源版本。
        /// 这里强制 SHA-256，不受调试用 SizeOnly 配置影响；内容不匹配即拒绝激活。
        /// </summary>
        public static async RevTask ValidateSnapshotAsync(RevHotManifest manifest, string version, RevCancellationToken token)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;
            string error = manifest.ValidateForActivation(_platform, _config.Channel, _config.Env);
            if (error != null || !string.Equals(manifest.ResVersion, version, StringComparison.Ordinal))
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestInvalid, error ?? "清单资源版本与候选目录不一致"), false);

            string targetDir = BundlesDir(version);
            Directory.CreateDirectory(targetDir);
            foreach (RevHotBundleInfo bundle in manifest.Bundles)
            {
                token.ThrowIfCancelled();
                string target = Path.Combine(targetDir, bundle.Name);
                // 已下载的目标文件也不能只看存在：若内容损坏，尝试用其他同 hash 来源修复。
                bool targetValid = await MatchesBundleAsync(target, bundle, token);
                if (!targetValid)
                {
                    string source = await FindMatchingSourceAsync(bundle, token);
                    if (source == null)
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed,
                            "候选版本缺包或找不到与目标 SHA-256 相同的来源：" + bundle.Name), false);
                    string temp = target + ".snapshot";
                    try
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                        await CopySnapshotAsync(source, temp, token);
                        CommitFile(temp, target);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                await RevHotVerifier.VerifyAsync(target, bundle.Size, bundle.Sha256, RevHotVerifyMode.Hash, _config, token);
            }

            if (!File.Exists(ResMapPath(version)))
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "候选版本缺少 ResMap.txt"), false);
            await RevHotVerifier.VerifyAsync(ResMapPath(version), manifest.ResMap.Size,
                manifest.ResMap.Sha256, RevHotVerifyMode.Hash, _config, token);
            Dictionary<string, string> mappings = ParseResMapText(File.ReadAllText(ResMapPath(version)));
            if (mappings.Count == 0)
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestInvalid, "候选版本 ResMap 为空"), false);
            foreach (KeyValuePair<string, string> entry in mappings)
            {
                string[] parts = entry.Value.Split('|');
                if (parts.Length != 2 || manifest.Find(parts[0]) == null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestInvalid,
                        "ResMap 引用了清单中不存在的包：" + entry.Key), false);
            }

            // Unity 主包装着 AssetBundleManifest；编辑器生成清单时它被排除，因此必须单独检查。
            // Android 上从 APK 拷到 _builtin；其它文件平台直接使用内置 StreamingAssets。
            string main = Path.Combine(_builtinDir, _platform);
            if (!File.Exists(main)) main = RevHotUrlBuilder.BuiltinBundlePath(_config, _platform);
            if (!File.Exists(main))
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "缺少平台主包 Manifest：" + main), false);
            AssetBundle probe = AssetBundle.LoadFromFile(main);
            if (probe == null)
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "平台主包无法加载：" + main), false);
            try
            {
                if (probe.LoadAsset<AssetBundleManifest>("AssetBundleManifest") == null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "平台主包中没有 AssetBundleManifest"), false);
            }
            finally { probe.Unload(false); }
        }

        /// <summary>
        /// URL 平台无文件系统：逐包通过 Unity 的 Hash128 + CRC 下载/校验到引擎缓存，
        /// 再验证随应用发布的主包含 Manifest。只有全部成功才能切换资源版本。
        /// 真机 CDN 仍需测试域名/CORS；这不是 SHA-256 文件校验。
        /// </summary>
        public static async RevTask ValidateWebGLCandidateAsync(RevHotManifest manifest, string version, RevCancellationToken token)
        {
            if (!RevHotPlatform.IsWebGL) return;
            string error = manifest.ValidateForActivation(_platform, _config.Channel, _config.Env);
            if (error != null) throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestInvalid, error), false);

            using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(RevHotUrlBuilder.BuiltinBundlePath(_config, _platform)))
            {
                await SendBundleRequestAsync(request, token);
                AssetBundle main = DownloadHandlerAssetBundle.GetContent(request);
                if (main == null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "内置平台主包无法加载"), false);
                try
                {
                    if (main.LoadAsset<AssetBundleManifest>("AssetBundleManifest") == null)
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "内置平台主包缺少 AssetBundleManifest"), false);
                }
                finally { main.Unload(false); }
            }

            foreach (RevHotBundleInfo bundle in manifest.Bundles)
            {
                token.ThrowIfCancelled();
                string url = RevHotUrlBuilder.BundleUrl(_config, _config.RemoteRoot, version, bundle.Name);
                using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(url,
                    Hash128.Parse(bundle.UnityHash), uint.Parse(bundle.UnityCrc)))
                {
                    await SendBundleRequestAsync(request, token);
                    AssetBundle loaded = DownloadHandlerAssetBundle.GetContent(request);
                    if (loaded == null)
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "WebGL 候选 AB 无法加载：" + bundle.Name), false);
                    loaded.Unload(false);
                }
            }
            if (_memoryResMap == null || _memoryResMap.Count == 0)
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "WebGL 候选 ResMap 未就绪"), false);
            foreach (KeyValuePair<string, string> entry in _memoryResMap)
            {
                string[] parts = entry.Value.Split('|');
                if (parts.Length != 2 || manifest.Find(parts[0]) == null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestInvalid,
                        "WebGL ResMap 引用了清单外的包：" + entry.Key), false);
            }
        }

        /// <summary>用候选 ResMap 和包依赖打开代表资源。验证期间保持旧版本指针与资源管理器。</summary>
        internal static async RevTask ProbeRequiredAssetsAsync(RevHotManifest manifest, string version, string[] requested, RevCancellationToken token)
        {
            if (requested == null || requested.Length == 0)
            {
                return;
            }
            // 复制业务输入，避免调用方在跨帧检查期间更改验收目标。
            string[] keys = (string[])requested.Clone();
            var map = RevHotPlatform.SupportsLocalFiles ? ParseResMapText(File.ReadAllText(ResMapPath(version))) : _memoryResMap;
            foreach (string key in keys)
            {
                token.ThrowIfCancelled();
                if (string.IsNullOrWhiteSpace(key) || map == null || !map.TryGetValue(key, out string mapping))
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "代表资源缺少候选映射：" + key), false);
                }
                string[] parts = mapping.Split('|');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "代表资源的候选映射无效：" + key), false);
                }
                var opened = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
                var releaseOrder = new List<AssetBundle>();
                try
                {
                    AssetBundle bundle = await OpenProbeBundleAsync(manifest, version, parts[0], opened, releaseOrder, token);
                    AssetBundleRequest load = bundle.LoadAssetAsync<UnityEngine.Object>(parts[1]);
                    // Unity 不能撤销已派发的资源请求。结束后再卸载临时探针持有。
                    while (!load.isDone)
                    {
                        await RevTaskScheduler.NextFrame();
                    }
                    token.ThrowIfCancelled();
                    if (load.asset == null)
                    {
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "候选包无法打开代表资源：" + key), false);
                    }
                }
                finally
                {
                    for (int i = releaseOrder.Count - 1; i >= 0; i--)
                    {
                        releaseOrder[i].Unload(true);
                    }
                }
            }
        }

        /// <summary>按已验清单先加载依赖，再加载目标包。只归还此探针打开的句柄。</summary>
        private static async RevTask<AssetBundle> OpenProbeBundleAsync(RevHotManifest manifest, string version, string name,
            Dictionary<string, AssetBundle> opened, List<AssetBundle> releaseOrder, RevCancellationToken token)
        {
            token.ThrowIfCancelled();
            if (opened.TryGetValue(name, out AssetBundle cached))
            {
                return cached;
            }
            RevHotBundleInfo row = manifest.Find(name);
            if (row == null)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "代表资源引用候选清单外的包：" + name), false);
            }
            foreach (string dependency in row.Dependencies)
            {
                await OpenProbeBundleAsync(manifest, version, dependency, opened, releaseOrder, token);
            }
            token.ThrowIfCancelled();
            AssetBundle bundle;
            if (RevHotPlatform.SupportsLocalFiles)
            {
                AssetBundleCreateRequest load = AssetBundle.LoadFromFileAsync(Path.Combine(BundlesDir(version), row.Name));
                while (!load.isDone)
                {
                    await RevTaskScheduler.NextFrame();
                }
                bundle = load.assetBundle;
            }
            else
            {
                string url = RevHotUrlBuilder.BundleUrl(_config, _config.RemoteRoot, version, row.Name);
                using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(url, Hash128.Parse(row.UnityHash), uint.Parse(row.UnityCrc)))
                {
                    await SendBundleRequestAsync(request, token);
                    bundle = DownloadHandlerAssetBundle.GetContent(request);
                }
            }
            if (bundle == null)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "无法打开代表资源的候选包：" + name), false);
            }
            opened.Add(name, bundle);
            releaseOrder.Add(bundle);
            token.ThrowIfCancelled();
            return bundle;
        }

        /// <summary>按配置等待候选包请求，取消时中止本次连接并保留原版本。</summary>
        private static async RevTask SendBundleRequestAsync(UnityWebRequest request, RevCancellationToken token)
        {
            request.timeout = _config.TimeoutSeconds;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            try
            {
                while (!operation.isDone)
                {
                    token.ThrowIfCancelled();
                    await RevTask.Yield();
                }
                if (request.result != UnityWebRequest.Result.Success)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed,
                        "候选 AB 加载失败（" + request.error + "）", request.url), false);
            }
            finally { if (!operation.isDone) request.Abort(); }
        }

        private static async RevTask<string> FindMatchingSourceAsync(RevHotBundleInfo bundle, RevCancellationToken token)
        {
            // 先当前版本，再查其它已完成版本和出包基线；不能把“存在同名文件”当成内容正确。
            if (!string.IsNullOrEmpty(_activeResVersion))
            {
                string active = Path.Combine(BundlesDir(_activeResVersion), bundle.Name);
                if (await MatchesBundleAsync(active, bundle, token)) return active;
            }
            foreach (string dir in _fallbackBundleDirs)
            {
                string path = Path.Combine(dir, bundle.Name);
                if (await MatchesBundleAsync(path, bundle, token)) return path;
            }
            string builtin = Path.Combine(_builtinDir, bundle.Name);
            if (await MatchesBundleAsync(builtin, bundle, token)) return builtin;
            string streaming = RevHotUrlBuilder.BuiltinBundlePath(_config, bundle.Name);
            return await MatchesBundleAsync(streaming, bundle, token) ? streaming : null;
        }

        private static async RevTask CopySnapshotAsync(string source, string target, RevCancellationToken token)
        {
            byte[] buffer = new byte[1 << 20];
            using (FileStream input = File.OpenRead(source))
            using (FileStream output = File.Create(target))
            {
                int read;
                int chunks = 0;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancelled();
                    output.Write(buffer, 0, read);
                    if (++chunks % 8 == 0) await RevTask.Yield();
                }
            }
        }

        private static async RevTask<bool> MatchesBundleAsync(string path, RevHotBundleInfo bundle, RevCancellationToken token)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != bundle.Size) return false;
            try
            {
                await RevHotVerifier.VerifyAsync(path, bundle.Size, bundle.Sha256, RevHotVerifyMode.Hash, _config, token);
                return true;
            }
            catch (RevHotException e) when (e.Error.Code == RevHotErrorCode.VerifyFailed) { return false; }
        }

        // ============================================================
        // ★ 钩子一：加载路径解析（RevABLoader.BundlePathResolver 的实现）
        //   覆盖式语义：热更版本 → 首包落地 → 交还框架默认（StreamingAssets）
        // ============================================================

        /// <summary>
        /// 解析一个 AB 包从哪读：
        /// ① 当前资源版本目录里有 → 用它（热更生效）；
        /// ② 历史完整版本目录里有 → 用它（★ 差量更新后"未变化的包"只在旧版本目录里 —— 没有这一级，热更一次其余包全挂）；
        /// ③ 首包落地目录里有 → 用它（没更新过的内置包，Android 全靠这一层）；
        /// ④ 都没有 → 返回 null，让框架走默认的 StreamingAssets（PC / iOS 的内置包就在那）。
        /// </summary>
        public static string ResolveBundlePath(string bundleName)
        {
            if (_configured == false || string.IsNullOrEmpty(_activeResVersion)) return null;

            // ---------- WebGL / 小游戏：没有"本地"，只有 URL（版本段就是它的"目录"） ----------
            if (RevHotPlatform.IsWebGL)
            {
                // 主包不在热更清单里，发布目录也不包含它：始终使用随应用发布的主包。
                if (string.Equals(bundleName, _platform, StringComparison.OrdinalIgnoreCase)) return null;
                return RevHotUrlBuilder.BundleUrl(_config, _config.RemoteRoot, _activeResVersion, bundleName);
            }

            // 主包只来自该 App 的内置基线；热更清单从不包含主包。
            if (string.Equals(bundleName, _platform, StringComparison.OrdinalIgnoreCase))
            {
                string main = Path.Combine(_builtinDir, _platform);
                return File.Exists(main) ? main : null;
            }

            // 只有验证通过的完整快照才能按当前清单加载，禁止任意同名历史包混入。
            if (_activeManifest != null && _activeManifest.Find(bundleName) != null)
                return Path.Combine(BundlesDir(_activeResVersion), bundleName);

            // 老版兼容：没有可用清单时保持原回退链，待启动验证/迁移后切为快照。
            string hot = Path.Combine(BundlesDir(_activeResVersion), bundleName);
            if (File.Exists(hot)) return hot;

            // ★ 回退链：差量更新的另一半。新版本目录只有"新增 / 变更"的包，
            //   未变化的包还留在旧版本目录里 —— 不回退查找，热更一次后其余包全部加载失败。
            string[] fallback = _fallbackBundleDirs;
            for (int i = 0; i < fallback.Length; i++)
            {
                string older = Path.Combine(fallback[i], bundleName);
                if (File.Exists(older)) return older;
            }

            if (string.IsNullOrEmpty(_builtinDir) == false)
            {
                string builtin = Path.Combine(_builtinDir, bundleName);
                if (File.Exists(builtin)) return builtin;
            }

            // 都没有 → 交还框架默认（典型场景：PC/iOS 上没动过的内置包，本来就在 StreamingAssets）
            return null;
        }

        // ============================================================
        // ★ 钩子二：热更映射表（RevResBootstrap.ResMapOverride 的实现）
        // ============================================================

        /// <summary>
        /// 提供热更映射表（"逻辑名 → 包名|资源名"）。没有热更表返回 null（框架继续用内置表）。
        /// 返回 null 时框架的合并逻辑会跳过 —— 这是刻意的：表缺失绝不能让资源系统初始化失败。
        /// </summary>
        public static Dictionary<string, string> LoadHotResMap()
        {
            // URL 型平台：清单声明了新表却没有准备好时必须失败，而不能误报 Ready。
            if (RevHotPlatform.IsWebGL)
            {
                if (_activeManifest?.ResMap != null && _memoryResMap == null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "当前 WebGL 资源版本缺少 ResMap"), false);
                return _memoryResMap;
            }

            if (_configured == false || string.IsNullOrEmpty(_activeResVersion)) return null;

            // ① 当前资源版本目录；② 首包落地目录（Android 上"还没热更过"的那一次就靠它）
            string path = ResMapPath(_activeResVersion);
            if (File.Exists(path) == false && string.IsNullOrEmpty(_builtinDir) == false)
            {
                string builtin = Path.Combine(_builtinDir, "ResMap.txt");
                if (File.Exists(builtin)) path = builtin;
            }

            if (File.Exists(path) == false)
            {
                if (_activeManifest?.ResMap != null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "当前资源版本的 ResMap 丢失：" + path), false);
                return null;
            }

            try
            {
                // 解析规则与框架 RevResBootstrap.LoadResMap 严格一致（见 ParseResMapText 的注释）
                Dictionary<string, string> map = ParseResMapText(File.ReadAllText(path));
                if (map.Count == 0 && _activeManifest?.ResMap != null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "当前资源版本 ResMap 为空"), false);
                return map.Count == 0 ? null : map;
            }
            catch (RevHotException) { throw; }
            catch (Exception e)
            {
                if (_activeManifest?.ResMap != null)
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed,
                        "当前资源版本 ResMap 读取失败：" + e.Message), false);
                RevLog.Warn("热更映射表读取失败，本次继续用内置表：" + e.Message, LogTag());
                return null;
            }
        }

        // ============================================================
        // ★ 钩子三：AB 的引擎缓存标识（RevABLoader.BundleCacheKeyResolver 的实现）
        // ============================================================

        /// <summary>
        /// 给出某个包的 hash / crc（引擎缓存与校验用）。
        /// ★ 查不到就返回 default（IsValid = false）—— 缓存标识只是**优化**，
        ///   缺失时必须让加载照常进行，绝不能因此失败。
        /// </summary>
        public static RevBundleCacheKey ResolveBundleCacheKey(string bundleName)
        {
            if (_bundleKeys != null && _bundleKeys.TryGetValue(bundleName, out RevBundleCacheKey key)) { return key; }
            return default;
        }

        // ============================================================
        // ★ 钩子四：依赖解析（RevABLoader.DependenciesOverride 的实现）
        // ============================================================

        /// <summary>
        /// 给出某个包的依赖列表（依赖闭包加载用）。
        /// ★ 数据源是"当前生效清单"—— 它和落盘的包严格同批生成，比出包时的主包 Manifest 新鲜：
        ///   主包 Manifest 不认识热更新增的包、记不住变更包的新依赖，用它解析热更包的依赖必然漏加载。
        /// 清单里查不到该包（主包本身、或清单与磁盘不一致）→ 返回 null 回退主包 Manifest（默认行为）。
        /// </summary>
        public static string[] ResolveDependencies(string bundleName)
        {
            RevHotManifest manifest = _activeManifest;
            if (manifest == null) return null;

            RevHotBundleInfo bundle = manifest.Find(bundleName);
            if (bundle == null) return null;

            return bundle.Dependencies.ToArray();
        }

        // ============================================================
        // 版本指针（current.txt / PlayerPrefs）
        // ============================================================

        /// <summary>
        /// 生效事务日志：写指针前保存上一个可用版本。若进程在“改指针 → 重装资源”之间退出，
        /// 下次启动先按日志恢复旧指针；不能仅靠 .stamp 推断新版本已经可用。
        /// </summary>
        public static void BeginActivation(string previous)
        {
            if (RevHotPlatform.IsWebGL) return; // WebGL 没有真实文件系统，由业务重新拉远端清单
            string pending = CurrentPath() + ".pending";
            string temp = pending + ".tmp";
            File.WriteAllText(temp, previous ?? string.Empty);
            CommitFile(temp, pending);
        }

        public static void CommitActivation()
        {
            if (RevHotPlatform.IsWebGL) return;
            string pending = CurrentPath() + ".pending";
            if (File.Exists(pending)) File.Delete(pending);
        }

        public static void RecoverInterruptedActivation()
        {
            if (RevHotPlatform.IsWebGL) return;
            string pending = CurrentPath() + ".pending";
            if (!File.Exists(pending)) return;
            string previous = File.ReadAllText(pending).Trim();
            if (previous.Length > 0 && !RevHotManifest.IsSafeName(previous))
                throw new InvalidDataException("未完成的资源激活记录包含非法版本名，已拒绝恢复");
            WriteCurrentVersion(previous);
            File.Delete(pending);
            RevLog.Warn("检测到未完成的资源激活，已恢复前一资源版本：" + previous, LogTag());
        }

        /// <summary>读当前生效的资源版本（没有返回空串）。</summary>
        public static string ReadCurrentVersion()
        {
            if (RevHotPlatform.IsWebGL)
            {
                return PlayerPrefs.GetString(PlayerPrefsKey(), string.Empty);
            }

            string path = CurrentPath();
            if (File.Exists(path) == false) return string.Empty;
            try
            {
                string version = File.ReadAllText(path).Trim();
                if (version.Length > 0 && !RevHotManifest.IsSafeName(version))
                    throw new InvalidDataException("当前资源版本号包含非法路径字符");
                return version;
            }
            catch (Exception e)
            {
                RevLog.Warn("current.txt 读取失败（按未更新处理）：" + e.Message, LogTag());
                return string.Empty;
            }
        }

        /// <summary>
        /// 原子地写版本指针：先写临时文件，再用 File.Replace / Move 覆盖。
        /// ★ 直接 WriteAllText 到 current.txt，断电瞬间可能留下半行文本 —— 版本指针一坏，整个热更就"失忆"了。
        /// </summary>
        public static void WriteCurrentVersion(string resVersion)
        {
            if (RevHotPlatform.IsWebGL)
            {
                PlayerPrefs.SetString(PlayerPrefsKey(), resVersion ?? string.Empty);
                PlayerPrefs.Save();
                return;
            }

            string path = CurrentPath();
            string tmp = path + ".tmp";
            string dir = Path.GetDirectoryName(path);
            // 版本文件通常带目录，例如“游戏目录/current.txt”；但调用方也可能只给文件名。
            // 只有文件名时 GetDirectoryName 会返回空，CreateDirectory(空字符串) 会抛异常，导致版本指针写入失败。
            // 所以只有确实有父目录时才创建；没有目录段就直接在当前目录写入。
            if (string.IsNullOrEmpty(dir) == false) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, (resVersion ?? string.Empty) + "\n");

            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private static string PlayerPrefsKey()
        {
            return PlayerPrefsKeyPrefix + _platform + "." + _appVersion;
        }

        // ============================================================
        // 目录与标记
        // ============================================================

        /// <summary>建好所有需要的目录（幂等；WebGL 上什么都不做）。</summary>
        public static void PrepareLocalRoot()
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;

            Directory.CreateDirectory(_localRoot);
            Directory.CreateDirectory(AppVersionDir());
            if (RevHotPlatform.NeedsBuiltinCopy || _config.FirstPackageMode == RevHotFirstPackageMode.CopyToLocal)
            {
                Directory.CreateDirectory(_builtinDir);
            }

            ClearStaleParts();
        }

        /// <summary>{本地根}/{平台}/{大版本} 目录。</summary>
        public static string AppVersionDir()
        {
            return Path.Combine(_localRoot, Path.Combine(_platform, _appVersion));
        }

        /// <summary>{…}/{资源版本} 目录。</summary>
        public static string VersionDir(string resVersion)
        {
            return Path.Combine(AppVersionDir(), resVersion);
        }

        /// <summary>版本指针文件（current.txt）。</summary>
        public static string CurrentPath()
        {
            return Path.Combine(_localRoot, "current.txt");
        }

        /// <summary>某资源版本的 AB 包目录。</summary>
        public static string BundlesDir(string resVersion)
        {
            return Path.Combine(VersionDir(resVersion), "bundles");
        }

        /// <summary>某资源版本的清单副本。</summary>
        public static string ManifestPath(string resVersion)
        {
            return Path.Combine(VersionDir(resVersion), _config.ManifestFileName);
        }

        /// <summary>某资源版本的热更映射表。</summary>
        public static string ResMapPath(string resVersion)
        {
            return Path.Combine(VersionDir(resVersion), "ResMap.txt");
        }

        /// <summary>某资源版本是否已"完整"（.stamp 在 = 所有文件校验通过）。</summary>
        public static bool IsVersionComplete(string resVersion)
        {
            if (RevHotPlatform.IsWebGL) return ReadCurrentVersion() == resVersion;
            if (string.IsNullOrEmpty(resVersion)) return false;
            return File.Exists(Path.Combine(VersionDir(resVersion), ".stamp"));
        }

        /// <summary>给版本目录打完成标记（打上之后这个版本才允许被 current 指向）。</summary>
        public static void MarkVersionComplete(string resVersion)
        {
            // WebGL 没有版本目录或 .stamp；指针统一由提交阶段写，不能在打标时提前生效。
            if (RevHotPlatform.IsWebGL) return;

            string stamp = Path.Combine(VersionDir(resVersion), ".stamp");
            Directory.CreateDirectory(VersionDir(resVersion));
            File.WriteAllText(stamp, "ok\n");
        }

        /// <summary>首包落地目录（Android 从 APK 拷出来的那份；PC/iOS 配了 CopyToLocal 也会有）。</summary>
        public static string BuiltinDir()
        {
            return _builtinDir;
        }

        /// <summary>清理临时残留：超过 7 天没动过的 .part（上次没下完又再也不可能续上的）。</summary>
        public static void ClearStaleParts()
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;

            try
            {
                string appDir = AppVersionDir();
                if (Directory.Exists(appDir) == false) return;

                string[] parts = Directory.GetFiles(appDir, "*.part", SearchOption.AllDirectories);
                DateTime threshold = DateTime.UtcNow.AddDays(-7);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (File.GetLastWriteTimeUtc(parts[i]) < threshold)
                    {
                        File.Delete(parts[i]);
                        string metadata = parts[i] + ".meta";
                        if (File.Exists(metadata)) File.Delete(metadata);
                    }
                }
                // 若断电发生在“删除分片→删除身份文件”之间，下次也要回收孤儿元数据。
                foreach (string metadata in Directory.GetFiles(appDir, "*.part.meta", SearchOption.AllDirectories))
                    if (!File.Exists(metadata.Substring(0, metadata.Length - ".meta".Length)) &&
                        File.GetLastWriteTimeUtc(metadata) < threshold) File.Delete(metadata);
            }
            catch (Exception e)
            {
                // 清理失败不影响主流程（下次还会再试）
                RevLog.Warn("清理临时下载文件失败（忽略）：" + e.Message, LogTag());
            }
        }

        // ============================================================
        // 文件级原子操作
        // ============================================================

        /// <summary>半成品路径（与目标同目录、多一个 .part 后缀 —— 断点续传就从它的长度继续）。</summary>
        public static string PartPath(string finalPath)
        {
            return finalPath + ".part";
        }

        /// <summary>已存在的半成品长度（没有就是 0）。</summary>
        public static long ExistingLength(string path)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return 0;
            try
            {
                if (File.Exists(path) == false) return 0;
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>删除文件（不存在不算错误）。</summary>
        public static void DeleteFile(string path)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                RevLog.Warn("删除文件失败：" + path + "，" + e.Message, LogTag());
            }
        }

        /// <summary>
        /// 原子提交：半成品 → 正式文件。
        /// ★ 先删目标再 Move —— File.Move 在目标存在时会抛异常；而此时该版本尚未生效，不存在"删掉正在用的包"。
        /// </summary>
        public static void CommitFile(string tmpPath, string finalPath)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;

            string dir = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(dir) == false && Directory.Exists(dir) == false)
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
        }

        /// <summary>磁盘预检：可用空间必须比"要下载的量"多出一截（临时文件 + 系统余量）。</summary>
        public static void EnsureDiskSpace(long requiredBytes)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;

            try
            {
                string full = Path.GetFullPath(_localRoot);
                string rootPath = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(rootPath)) return;

                DriveInfo drive = new DriveInfo(rootPath);
                long need = requiredBytes + 64L * 1024 * 1024;               // 多留 64MB：解压/临时文件/系统
                if (drive.AvailableFreeSpace < need)
                {
                    string reason = "磁盘空间不足：需要 " + RevHotProgress.FormatBytes(requiredBytes) + "，可用 " + RevHotProgress.FormatBytes(drive.AvailableFreeSpace);
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.DiskFull, reason + "，请清理后重试"), false);
                }
            }
            catch (RevHotException)
            {
                throw;                                                       // 磁盘满是我们抛的，原样往上送
            }
            catch
            {
                // 拿不到磁盘信息（部分平台 / 特殊目录）→ 跳过预检，让后面的下载自己暴露问题
            }
        }

        // ============================================================
        // 首包落地（Android 的命门：APK 里的包不落地，LoadFromFile 读不到）
        // ============================================================

        /// <summary>内置清单（首包基线）的读取地址：Android 是 jar: URL，桌面 / iOS 是文件路径。</summary>
        public static string BuiltinManifestSource()
        {
            return RevHotUrlBuilder.BuiltinManifestPath(_config);
        }

        /// <summary>读一段文本（自动区分 URL 与本地文件）：Android 的 StreamingAssets 只能用 UnityWebRequest 读。</summary>
        public static async RevTask<string> ReadTextAsync(string pathOrUrl, RevCancellationToken token)
        {
            bool isUrl = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                         || pathOrUrl.StartsWith("jar:", StringComparison.OrdinalIgnoreCase);

            if (isUrl == false)
            {
                return File.ReadAllText(pathOrUrl);
            }

            using (UnityWebRequest www = UnityWebRequest.Get(pathOrUrl))
            {
                www.timeout = _config.TimeoutSeconds;
                var op = www.SendWebRequest();
                while (op.isDone == false)
                {
                    token.ThrowIfCancelled();
                    await RevTask.Yield();
                }

                if (www.result != UnityWebRequest.Result.Success)
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestFetchFailed, "读取内置文件失败（" + www.error + "）", pathOrUrl), false);
                }

                return www.downloadHandler.text;
            }
        }

        /// <summary>
        /// 首包落地：把 StreamingAssets 里的内置包拷到 persistentDataPath（幂等 —— 大小一致的直接跳过）。
        /// Android 用 UnityWebRequest 从 APK 里读；桌面 / iOS 直接 File.Copy。
        /// </summary>
        public static async RevTask CopyBuiltinPackagesAsync(RevHotManifest builtin, RevHotConfig config, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;
            if (builtin == null || builtin.Bundles.Count == 0) return;

            Directory.CreateDirectory(_builtinDir);

            // 主包名等于平台名，生成器不把它算进普通 AB 清单；但 Android 的
            // StreamingAssets 位于 APK jar 中，RevABLoader.LoadFromFile 不能直接加载。
            // 首次落地后，候选版本验证还会实际读取其中的 AssetBundleManifest。
            string main = Path.Combine(_builtinDir, _platform);
            if (!File.Exists(main))
                await CopyBuiltinFileAsync(RevHotUrlBuilder.BuiltinBundlePath(config, _platform), main, config, token);

            long total = 0;
            for (int i = 0; i < builtin.Bundles.Count; i++) total += builtin.Bundles[i].Size;
            var progress = new RevHotProgressAggregator(RevHotState.Applying, total, builtin.Bundles.Count, onProgress, LogTag());

            for (int i = 0; i < builtin.Bundles.Count; i++)
            {
                RevHotBundleInfo bundle = builtin.Bundles[i];
                token.ThrowIfCancelled();
                progress.BeginFile("内置/" + bundle.Name);

                string target = Path.Combine(_builtinDir, bundle.Name);

                // 幂等：已在且大小一致 → 跳过（二次启动 / 断点续装都靠这一条）
                if (File.Exists(target) && new FileInfo(target).Length == bundle.Size)
                {
                    progress.CompleteFile("内置/" + bundle.Name, bundle.Size);
                    continue;
                }

                string source = RevHotUrlBuilder.BuiltinBundlePath(config, bundle.Name);
                await CopyBuiltinFileAsync(source, target, config, token);

                // 内置包来自自家 APK / 安装目录，默认只比大小（快）；要更严可以打开 VerifyBuiltinCopy
                if (config.VerifyBuiltinCopy)
                {
                    await RevHotVerifier.VerifyAsync(target, bundle.Size, bundle.Sha256, config.VerifyMode, config, token);
                }

                progress.CompleteFile("内置/" + bundle.Name, bundle.Size);
            }

            // ---------- 映射表也一并落地（Android 没有它，"未热更的客户端"读不到表） ----------
            if (builtin.ResMap != null)
            {
                string resMapTarget = Path.Combine(_builtinDir, builtin.ResMap.Name);
                if (File.Exists(resMapTarget) == false || new FileInfo(resMapTarget).Length != builtin.ResMap.Size)
                {
                    string resMapSource = RevHotUrlBuilder.BuiltinBundlePath(config, builtin.ResMap.Name);
                    await CopyBuiltinFileAsync(resMapSource, resMapTarget, config, token);
                }
            }
        }

        /// <summary>
        /// 拷一个内置文件到目标：Android 用 UnityWebRequest 读 APK（jar: URL），桌面 / iOS 直接 File.Copy。
        /// </summary>
        private static async RevTask CopyBuiltinFileAsync(string source, string target, RevHotConfig config, RevCancellationToken token)
        {
            bool sourceIsUrl = source.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)
                               || source.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            if (sourceIsUrl == false)
            {
                File.Copy(source, target, true);
                await RevTask.Yield();                                   // 每个文件让一帧：大拷贝不冻结 loading
                return;
            }

            string tmp = PartPath(target);
            using (UnityWebRequest www = UnityWebRequest.Get(source))
            {
                www.downloadHandler = new DownloadHandlerFile(tmp, false);
                www.timeout = config.TimeoutSeconds;

                var op = www.SendWebRequest();
                while (op.isDone == false)
                {
                    token.ThrowIfCancelled();
                    await RevTask.Yield();
                }

                if (www.result != UnityWebRequest.Result.Success)
                {
                    DeleteFile(tmp);
                    string reason = "内置资源拷贝失败（" + www.error + "）—— 请重启游戏重试";
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, reason, source), true);
                }
            }

            CommitFile(tmp, target);
        }

        // ============================================================
        // 旧版本清理
        // ============================================================

        /// <summary>
        /// 只保留当前版本 + 最新的 N 个版本，其余删掉（回滚能力就来自"多留的这几份"）。
        /// ★ 只删当前大版本目录下的内容 —— 跨大版本的目录等"切换大版本"时整体处理，这里绝不越界。
        /// </summary>
        public static void DeleteOldVersions(int keep, string currentVersion)
        {
            if (RevHotPlatform.SupportsLocalFiles == false) return;

            string appDir = AppVersionDir();
            if (Directory.Exists(appDir) == false) return;

            List<string> dirs = new List<string>(Directory.GetDirectories(appDir));

            // 排序规则先起个名字，Sort 调用就能写成单行（可读性也更好：一眼看出"按版本号倒序"）
            Comparison<string> newestFirst = delegate(string a, string b)
            {
                string va = Path.GetFileName(a);
                string vb = Path.GetFileName(b);
                return RevHotVersion.Compare(vb, va);                    // 新的在前
            };
            dirs.Sort(newestFirst);

            int kept = 0;
            bool deleted = false;
            for (int i = 0; i < dirs.Count; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                bool isCurrent = string.Equals(name, currentVersion, StringComparison.Ordinal);

                if (isCurrent)
                {
                    kept++;
                    continue;
                }

                if (kept < keep)
                {
                    kept++;
                    continue;
                }

                try
                {
                    Directory.Delete(dirs[i], true);
                    RevLog.Info("清理旧资源版本：" + name, LogTag());
                    deleted = true;
                }
                catch (Exception e)
                {
                    // 删不掉（被占用等）不算失败：下次启动还会再试
                    RevLog.Warn("清理旧版本失败（忽略）：" + name + "，" + e.Message, LogTag());
                }
            }

            // ★ 有目录被删就要重建回退链：链是"切版本时"的快照，刚删掉的目录还挂在链上，
            //   虽然加载端 File.Exists 实时查询不会真用到已删目录，但链脏了不如重建（幂等、零成本）。
            if (deleted) RebuildFallbackChain();
        }

        // ============================================================
        // 重置（进 Play / 域重载时清静态状态 —— 不动磁盘上的任何文件）
        // ============================================================

        public static void ResetForNewSession()
        {
            _config = null;
            _platform = "";
            _appVersion = "";
            _localRoot = "";
            _builtinDir = "";
            _activeResVersion = "";
            _memoryResMap = null;
            _bundleKeys = null;              // ★ 必须一起清：关闭 Domain Reload 的二次 Play 里，
            _fallbackBundleDirs = Array.Empty<string>();   //   残留的旧版本 hash 表 / 回退链会让加载用错版本的数据
            _activeManifest = null;          //   残留的旧清单会让依赖解析给出过期的依赖图
            _configured = false;
        }

        private static string LogTag()
        {
            return _config == null ? "HotUpdate" : _config.LogTag;
        }
    }
}

// ============================================================
// RevABLoader.cs —— AB 加载器
// 承担四件事：
//   ① 从 streamingAssetsPath 加载 AB（默认行为；装了 RevHotUpdate 扩展包时由 BundlePathResolver 钩子重定向到
//      persistentDataPath 的版本目录或 CDN 的版本化 URL —— 本框架自身永不设置这个钩子，所以默认行为不变）
//      · Android：包在 APK 内，File.Exists 不可用 → 直接 LoadFromFile
//      · WebGL （含微信/抖音小游戏）：不能阻塞 + 路径是 URL → 同步加载不可用，必须走 LoadAsync
//   ② 主包 Manifest 依赖解析
//   ③ 包级引用计数（归零卸载）
//   ④ ★ 失败重试 + 超时兜底（MaxAttempts / RetryDelayMs / AttemptTimeoutSeconds），
//      以及 URL 型平台的"引擎缓存标识"（BundleCacheKeyResolver → hash/crc，让引擎不用每次重下）
// 资源级引用计数不在这里，由 RevResManager 统一管。
//
// 异步统一用自研 RevTask（见 Runtime\RevTask），不依赖 UniTask、也不用协程。
// ============================================================
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
#define REV_AB_BROWSER_PARALLEL
#endif
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME) && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

namespace Revolution
{
    
    /// <summary>负责原生 AB 的读取、请求合并和包引用归还。</summary>
    public class RevABLoader : IRevResLoader
    {
        /// <summary>可选后端每次装配提供一代只读快照。</summary>
        private readonly IRevBundleSource _source;

        /// <summary>创建使用框架自带来源的加载器。</summary>
        public RevABLoader() { }

        /// <summary>创建仅从本代快照读取的加载器。</summary>
        internal RevABLoader(IRevBundleSource source)
        {
            _source = source;
        }

        /// <summary>
        /// 一个已加载 AB 包的登记记录（_bundles 的 value）。
        /// 把「包本体」和「引用计数」捆成一个对象 —— 两者永远同步，
        /// 不会出现拆成两个字典时"一边有值、一边没值"的不同步脏状态。
        /// </summary>
        private class RevBundleEntry
        {
            /// <summary>
            /// 包本体：加载资源、Unload 卸载都靠它
            /// </summary>
            public AssetBundle bundle;

            /// <summary>
            /// 引用计数：Acquire +1、Release -1，减到 0 才真正卸载
            /// </summary>
            public int refCount;        
        }

        /// <summary>
        ///已加载的包：包名 → (bundle, 引用计数)
        /// </summary>
        private readonly Dictionary<string,RevBundleEntry> _bundles = new Dictionary<string,RevBundleEntry>();

        /// <summary>等待读取的同名包。完成前保留请求总数，包入缓存后一次登记所有引用。</summary>
        private sealed class PendingBundle
        {
            /// <summary>发起者和所有后续请求共用同一个异步结果。</summary>
            public readonly RevTaskCompletionSource<AssetBundle> Completion = new RevTaskCompletionSource<AssetBundle>();

            /// <summary>尚未交付的引用数，发起者占第一份。</summary>
            public int References = 1;
        }

        /// <summary>当前代按包名去重的请求。退代后旧请求只能清理原来的对象。</summary>
        private readonly Dictionary<string, PendingBundle> _loading = new Dictionary<string, PendingBundle>();

#if REV_AB_BROWSER_PARALLEL
        /// <summary>一个资源可同时发起的浏览器依赖数。</summary>
        private const int BrowserDependencyBatchSize = 3;

        /// <summary>本加载器的实际在途 HTTP 上限，包含清单、普通包和重试。</summary>
        private const int BrowserRequestLimit = 4;

        /// <summary>已取得名额的 HTTP 请求。旧代也必须走自己的 finally 归还。</summary>
        private int _browserRequests;
#endif

        // 主包 / Manifest：并发首次请求共享同一加载任务。
        private AssetBundle _mainBundle;
        private AssetBundleManifest _manifest;
        private RevTaskCompletionSource<bool> _manifestSource;
        private int _generation;

        // AB 的根目录：StreamingAssets/<平台名>/
        //   ★ 必须带平台子目录 —— 打包工具把产物拷到 Assets/StreamingAssets/<平台名>/（见 ABBuilderCore.CopyToStreamingAssets），
        //     而 Unity 又拿 BuildAssetBundles 的输出目录名当主包名，所以"平台目录名"同时也是"主包文件名"。
        //     少了这段，主包和所有分包都会找不到（报 BundleLoadFail）。
        private static string StreamingRoot => Application.streamingAssetsPath + "/" + MainName + "/";

        /// <summary>
        /// 主包名 = 打包产物目录名。
        /// Unity 会用 BuildAssetBundles 的输出目录名给 Manifest 主包命名，
        /// 而目录名由 ABBuildSetting.GetPlatformName(target) 决定
        /// —— ★ 两边必须一致，改这里要同步改那边（还有热更包的 RevHotPlatform.Name）。
        ///   iOS / Android / WebGL（微信、抖音等小游戏同为 WebGL 构建）
        ///   桌面 Windows / macOS / Linux → 统一叫 "PC"
        ///
        /// ★ 小游戏为什么归到 "WebGL" 这一档：它们的资源格式与 WebGL 完全相同，
        ///   打包侧也会把小游戏构建目标（XxxMiniGame）归一到 "WebGL"（见 ABBuildSetting.GetPlatformName）。
        ///   所以这里把小游戏宏与 UNITY_WEBGL 并列判断 —— 即便某个引擎版本不定义 UNITY_WEBGL，
        ///   只要是已知的小游戏平台，名字依然对得上。
        /// </summary>
        private static string MainName
        {
            get
            {
#if UNITY_EDITOR
                // 编辑器使用桌面图形 API。Build Target 切到 Android 等平台时，Play 仍需读取 PC 包。
                return "PC";
#elif UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME || UNITY_WEBGL
                return "WebGL";
#elif UNITY_IOS
                return "iOS";
#elif UNITY_ANDROID
                return "Android";
#else
                return "PC";        // 桌面（Windows / macOS / Linux）统一 PC；其他平台按需在此补分支
#endif
            }
        }

        // ============================================================
        // 包路径重定向钩子（RevHotUpdate 热更包使用；本框架自身永不设置它）
        // ============================================================

        /// <summary>
        /// 包路径重定向钩子：参数 = 包名，返回 = 该包的完整文件路径（本地）或 URL（WebGL/小游戏）；
        /// 返回 null / 空串 → 走默认的 StreamingAssets/&lt;平台名&gt;/&lt;包名&gt;。
        /// <para>★ 不设置时（默认 null）行为与从前完全一致 —— 对不装热更包的工程是零影响。</para>
        /// <para>★ 热更包用它把加载指到 persistentDataPath 的版本目录（Android）或 CDN 的版本 URL（小程序）。</para>
        /// <para>★ 用静态属性是有意的：两种平台形态（文件型 / URL 型）都只需要"换一个根"，不需要按包各配一套。</para>
        /// </summary>
        public static Func<string, string> BundlePathResolver { get; set; }

        /// <summary>解析一个包的读取位置：先问钩子，钩子不接（返回 null/空）再走默认的 StreamingAssets 路径。</summary>
        private string ResolveBundlePath(string abName)
        {
            if (_source != null)
            {
                return _source.GetPath(abName);
            }
            Func<string, string> resolver = BundlePathResolver;          // 先取快照：属性可能在别的线程被清空
            string custom = resolver == null ? null : resolver(abName);
            return string.IsNullOrEmpty(custom) ? StreamingRoot + abName : custom;
        }

        /// <summary>
        /// 包缓存标识钩子（可选，默认 null）：参数 = 包名，返回 = 该包的 hash / crc。
        /// <para>★ 只对"按 URL 下载"的平台有意义（WebGL / 微信小游戏 / 抖音小游戏）：
        ///   引擎正是靠这个标识判断"本地缓存过没有、要不要重新下载"。</para>
        /// <para>★ 热更包从清单里查到 UnityHash / UnityCrc 后设置它；不设置时会退化为"无缓存标识"
        ///   —— 功能照常，浏览器仍可通过 HTTP 缓存头复用稳定 URL。</para>
        /// </summary>
        public static Func<string, RevBundleCacheKey> BundleCacheKeyResolver { get; set; }

        /// <summary>
        /// 依赖解析覆盖钩子（可选，默认 null）：参数 = 包名，返回 = 该包的依赖包名列表。
        /// <para>★ 返回 null → 回退主包 Manifest 的 GetAllDependencies（默认行为，不装热更包零影响）。</para>
        /// <para>★ 为什么需要它：主包 Manifest 是**出包时**的静态依赖快照 —— 它不认识热更新增的包，
        ///   也记不住热更变更包的新依赖。没有这个钩子，热更新增/变更包的依赖闭包不会被自动加载，
        ///   表现为资产加载失败 / missing，且"依赖包恰好被别的资源加载过"时侥幸通过 —— 时好时坏最难排查。
        ///   热更包用"当前生效清单"的 Dependencies 列提供新鲜的依赖图。</para>
        /// </summary>
        public static Func<string, string[]> DependenciesOverride { get; set; }

        /// <summary>
        /// 解析一个包的依赖列表：先问覆盖钩子，钩子不接（返回 null）再走主包 Manifest。
        /// ★ 加载（AcquireBundle）与释放（ReleaseBundle）必须走同一个来源，+1/-1 才能配对。
        /// </summary>
        private string[] ResolveDependencies(string abName)
        {
            if (_source != null)
            {
                return _source.GetDependencies(abName);
            }
            Func<string, string[]> source = DependenciesOverride;        // 先取快照：属性可能在别的时机被清空
            string[] custom = source == null ? null : source(abName);
            if (custom != null) return custom;
            if (_manifest == null) return Array.Empty<string>();         // 主包未就绪（理论上调用方已 EnsureManifest）
            return _manifest.GetAllDependencies(abName);
        }

        // ============================================================
        // 加载策略（可按项目调整；都是"全局一次"的旋钮，不改默认行为）
        // ============================================================

        /// <summary>
        /// 一次加载最多尝试几次（含首次，默认 2）。
        /// ★ 为什么要重试：小游戏与移动网络下"偶发失败"是常态（切网 / DNS / CDN 抖动），
        ///   失败一次就把首屏判死刑，玩家看到的就是"进不去"。
        /// ★ 为什么不上限重试：网络真不通时，要让业务**尽快**拿到失败自己决定（提示重进 / 走弱网兜底）。
        /// </summary>
        public static int MaxAttempts { get; set; } = 2;

        /// <summary>两次尝试之间的等待（毫秒，按尝试次数线性递增：base、base×2…）。</summary>
        public static int RetryDelayMs { get; set; } = 300;

        /// <summary>
        /// 单次尝试的超时秒数（&lt;= 0 = 不限时；默认 30 秒）。
        /// ★ 为什么不用 UnityWebRequest.timeout：WebGL / 小游戏的底层是浏览器 XHR，那个属性在那边不生效 ——
        ///   请求一旦卡住就会一直挂着，玩家看到"一直在加载"，而代码里连失败都拿不到。
        /// </summary>
        public static float AttemptTimeoutSeconds { get; set; } = 30f;

        // ==================== 同步路径 ====================

        /// <summary>确保主包 + Manifest 已加载</summary>
        private bool EnsureManifest()
        {
            if (_source != null) return true;
            if (_manifest != null) return true;
            // 同步请求不能和异步首次初始化并行再加载一个主包。
            if (_manifestSource != null) return false;

            AssetBundle mainBundle = LoadBundle(MainName);
            if (mainBundle == null) return false;

            AssetBundleManifest manifest = mainBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            if (manifest == null)
            {
                mainBundle.Unload(false);
                return false;
            }

            _mainBundle = mainBundle;
            _manifest = manifest;
            return true;
        }


        /// <summary>从本代指定的位置同步加载原生包。异步平台由下面的分支处理。</summary>
        private AssetBundle LoadBundle(string abName)
        {
#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME) && !UNITY_EDITOR
            // WebGL（含小游戏）：单线程、不能阻塞，File.Exists / LoadFromFile 都不可用 → 同步加载无解。
            // 只能走 LoadBundleAsync（UnityWebRequest）。返回 null 让上层报 BundleLoadFail，而不是假死。
            return null;
#elif UNITY_ANDROID && !UNITY_EDITOR
            // Android：streamingAssetsPath 在 APK 内，File.Exists 不可用，直接尝试加载。
            // ★ 走钩子：热更包会把"内置包"重定向到 persistentDataPath 的基线目录（File.Copy 后的文件路径，
            //   LoadFromFile 能读），把"热更包"重定向到版本目录 —— 没有钩子时这里仍是默认的 StreamingAssets。
            return AssetBundle.LoadFromFile(ResolveBundlePath(abName));
#else
            string path = ResolveBundlePath(abName);   // ★ 同上：先问钩子（本地路径），没有再走 StreamingAssets
            if (File.Exists(path))
                return AssetBundle.LoadFromFile(path);
            return null;
#endif
        }

        /// <summary>加载包（含依赖），并给每个包 +1 引用</summary>
        private AssetBundle AcquireBundle(string abName, out string[] acquiredDependencySnapshot)
        {
            acquiredDependencySnapshot = Array.Empty<string>();
            if (!EnsureManifest()) return null;

            // 依赖加载是一个事务：任何依赖或目标包失败，都归还本次已经取得的依赖引用。
            var acquiredDependencies = new List<string>();
            foreach (string dep in ResolveDependencies(abName))
            {
                if (AcquireSingle(dep) == null)
                {
                    for (int i = acquiredDependencies.Count - 1; i >= 0; i--) ReleaseSingle(acquiredDependencies[i]);
                    return null;
                }
                acquiredDependencies.Add(dep);
            }

            AssetBundle target = AcquireSingle(abName);
            if (target == null)
            {
                for (int i = acquiredDependencies.Count - 1; i >= 0; i--) ReleaseSingle(acquiredDependencies[i]);
                return null;
            }

            acquiredDependencySnapshot = acquiredDependencies.ToArray();
            return target;
        }

        /// <summary>
        /// 取用「单个」AB 包：包级引用计数的唯一 +1 入口（与 ReleaseSingle 的 -1 配对）。
        ///
        ///   · 已在 _bundles 里 → 不重复加载，只 refCount++，复用同一个 bundle；
        ///   · 还没有           → 真加载一次，并登记为 refCount = 1。
        ///
        /// 复用的意义：同一个包被 N 个资源用到时，磁盘只读一次、内存只存一份；
        /// 而且只要还有人在用，计数就压不到 0，包就不会被 Unload 卸掉。
        ///
        /// 不查依赖（那是 AcquireBundle 的事），只管这个包本身。
        /// </summary>
        private AssetBundle AcquireSingle(string abName)
        {
            if (_bundles.TryGetValue(abName, out RevBundleEntry e) && e.bundle != null)
            {
                // 已有 → 复用，仅计数 +1
                e.refCount++;
                return e.bundle;
            }
            AssetBundle bundle = LoadBundle(abName);
            // 该 AB 包在 StreamingAssets 下不存在
            if (bundle == null) 
                return null;

            // 首次加载 → 登记为 1
            _bundles[abName] = new RevBundleEntry { bundle = bundle, refCount = 1 };
            return bundle;
        }

        /// <summary>释放包（引用归零则 Unload）</summary>
        public void ReleaseBundle(string abName)
        {
            if (_bundles.Count == 0) return;              // ReleaseAll 已清账时，不能为一次迟到 Release 重载主包
            if (!EnsureManifest()) return;
            ReleaseBundle(abName, ResolveDependencies(abName));
        }

        internal void ReleaseBundle(string abName, string[] acquiredDependencies, int generation)
        {
            if (generation != _generation) { return; }
            ReleaseBundle(abName, acquiredDependencies);
        }

        internal void ReleaseBundle(string abName, string[] acquiredDependencies)
        {
            if (_bundles.Count == 0) return;
            if (acquiredDependencies != null)
                for (int i = acquiredDependencies.Length - 1; i >= 0; i--) ReleaseSingle(acquiredDependencies[i]);
            ReleaseSingle(abName);
        }

        /// <summary>
        /// 归还「单个」AB 包：与 AcquireSingle 的 +1 配对的 -1 出口。
        ///
        ///   · 减完不为0 → 还有人在用，什么都不做；
        ///   · 减到0 → 从 _bundles 移除，原生 AB 仍存活时再卸载；下次取用会重新加载。
        ///     关闭域重载时，Unity 可能先销毁原生 AB，下一次初始化仍须归还它的账面引用。
        ///
        /// ★ Unload(false) 的 false 是重点：「不销毁从该包里 Load 出来的资源对象」。
        ///   传 true 会连别人正在用的贴图 / 预制体一起销毁（表现为空白、空引用）；
        ///   而资源实例的生命周期由 RevResManager 统一管（它另有一套资源级引用计数），
        ///   所以这里只卸"包"这一层：省的是包的内存，不碰资源的内存。
        /// </summary>
        private void ReleaseSingle(string abName)
        {
            if (!_bundles.TryGetValue(abName, out RevBundleEntry entry))
            {
                return;
            }

            if (--entry.refCount > 0)
            {
                return;
            }

            // 无论原生对象是否已销毁，最后一份引用归还时都要移除缓存记录。
            _bundles.Remove(abName);
            // 用 Unity 对象的判空识别已销毁的 AssetBundle。仍存活时仅卸载包，已加载资源由 RevResManager 管理。
            if (entry.bundle != null)
            {
                entry.bundle.Unload(false);
            }
        }

        /// <summary>全部释放。迟到结果按旧代次归还，主包已被 Unity 销毁时仍清掉托管引用。</summary>
        public void ReleaseAll()
        {
            // 递增代际：仍在等待 Unity 异步 IO 的旧任务完成后会自行卸载结果，不能再写回新缓存。
            _generation++;
            _manifestSource = null;

            foreach (RevBundleEntry e in _bundles.Values)
                if (e.bundle != null) e.bundle.Unload(false);

            _bundles.Clear();
            _loading.Clear();

            AssetBundle mainBundle = _mainBundle;
            _mainBundle = null;
            _manifest = null;
            if (mainBundle != null)
            {
                mainBundle.Unload(false);
            }
        }

        /// <summary>
        /// 获取ab包的引用计数
        /// </summary>
        /// <param name="abName"></param>
        /// <returns></returns>
        public int GetBundleRefCount(string abName) => _bundles.TryGetValue(abName, out RevBundleEntry e) ? e.refCount : 0;


        // ==================== IRevResLoader接口实现 ====================

        /// <summary>同步读取已查到的 AB 地址，并将实际取得的包引用记到句柄。</summary>
        public object Load(RevResHandle handle, out RevResLoadErrorReason err)
        {
            RevABAddress address = ResolveAddress(handle);
            if (address == null)
            {
                err = RevResLoadErrorReason.PathNotMapped;
                return null;
            }
            // 加载器直接使用映射保留的包名和包内名称。
            AssetBundle bundle = AcquireBundle(address.BundleName, out string[] acquiredDependencies);
            if (bundle == null)
            {
                err = RevResLoadErrorReason.BundleLoadFail;
                return null;
            }
            handle.BundleAcquired = true;
            handle.BundleLoader = this;
            handle.BundleGeneration = _generation;
            handle.BundleName = address.BundleName;
            handle.BundleDependencies = acquiredDependencies;
            //加载ab包中的资源
            UnityEngine.Object asset = bundle.LoadAsset(address.AssetName, handle.ContentType);
            ReportAndroidShaderIssues(asset, address.BundleName, address.AssetName);
            err = asset != null ? RevResLoadErrorReason.None : RevResLoadErrorReason.AssetLoadFail;
            return asset;

        }

        /// <summary>异步取依赖、目标包与资产，成功、失败或取消都向资源泵回调一次。</summary>
        public void LoadAsync(RevResHandle handle, Action<RevResHandle> onFinished, RevCancellationToken token = null)
        {
            LoadAsyncInternal(handle, onFinished, token).Forget();
        }

        /// <summary>先确认清单，再收齐依赖，最后读取目标资产。失败与取消归还已获得的临时引用。</summary>
        private async RevTask LoadAsyncInternal(RevResHandle handle, Action<RevResHandle> onFinished, RevCancellationToken token)
        {
            int generation = _generation;
            var acquiredDependencies = new List<string>();
            try
            {
                RevABAddress address = ResolveAddress(handle);
                if (address == null)
                {
                    handle.ErrorReason = RevResLoadErrorReason.PathNotMapped;
                    handle.MarkError();
                    return;
                }

                token?.ThrowIfCancelled();
                bool manifestReady = await EnsureManifestAsync();
                if (generation != _generation)
                {
                    throw new RevOperationCanceledException();
                }
                token?.ThrowIfCancelled();
                if (!manifestReady)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    RevLog.Error($"Android AB 主包无法加载：{ResolveBundlePath(MainName)}。请确认 Player 与 AB 均为 Android 构建，且 APK 内包可读取。", "Res");
#endif
                    handle.ErrorReason = RevResLoadErrorReason.BundleLoadFail;
                    handle.MarkError();
                    return;
                }
                string failedDependency = await AcquireDependenciesAsync(ResolveDependencies(address.BundleName), acquiredDependencies, generation, token);
                if (generation != _generation)
                {
                    throw new RevOperationCanceledException();
                }
                token?.ThrowIfCancelled();
                if (failedDependency != null)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    RevLog.Error($"Android AB 依赖包加载失败：目标 {address.BundleName} / 依赖 {failedDependency} / 路径 {ResolveBundlePath(failedDependency)}。请检查平台产物、热更版本与包文件。", "Res");
#endif
                    ReleaseDependencies(acquiredDependencies, generation);
                    acquiredDependencies.Clear();
                    handle.ErrorReason = RevResLoadErrorReason.BundleLoadFail;
                    handle.MarkError();
                    return;
                }

                AssetBundle bundle = await AcquireSingleAsync(address.BundleName);
                if (generation != _generation)
                {
                    throw new RevOperationCanceledException();
                }
                if (bundle == null)
                {
                    // 清理或换包发生在等待之间，旧请求应结束为取消，资源门面也不会尝试后续兜底策略。
                    token?.ThrowIfCancelled();
#if UNITY_ANDROID && !UNITY_EDITOR
                    RevLog.Error($"Android AB 目标包加载失败：{address.BundleName} / 路径 {ResolveBundlePath(address.BundleName)}。先检查包来源，不能当作材质 Shader 缺失处理。", "Res");
#endif
                    ReleaseDependencies(acquiredDependencies, generation);
                    acquiredDependencies.Clear();
                    handle.ErrorReason = RevResLoadErrorReason.BundleLoadFail;
                    handle.MarkError();
                    return;
                }

                handle.BundleAcquired = true;
                handle.BundleLoader = this;
                handle.BundleGeneration = _generation;
                handle.BundleName = address.BundleName;
                // 加载这个资源时，代码已经逐个给它的依赖包增加了引用计数；把这次实际取得的依赖名单交给句柄保存。
                // 随后清空本地列表，表示“释放责任已经转交给句柄”，这样取消/异常清理就不会和句柄再次释放同一依赖。
                // 若重复减引用，共享该依赖的其他资源还在使用时也可能被提前卸载，导致资源突然加载失败。
                handle.BundleDependencies = acquiredDependencies.ToArray();
                acquiredDependencies.Clear();       // 所有权转交给句柄，清理端统一只归还一次
                if (generation != _generation) { throw new RevOperationCanceledException(); }
                token?.ThrowIfCancelled();

                AssetBundleRequest req = bundle.LoadAssetAsync(address.AssetName, handle.ContentType);
                await req;
                if (generation != _generation) { throw new RevOperationCanceledException(); }
                token?.ThrowIfCancelled();
                ReportAndroidShaderIssues(req.asset, address.BundleName, address.AssetName);
                handle.ErrorReason = req.asset != null ? RevResLoadErrorReason.None : RevResLoadErrorReason.AssetLoadFail;
                handle.SetContent(req.asset);
            }
            catch (RevOperationCanceledException)
            {
                if (!handle.BundleAcquired) ReleaseDependencies(acquiredDependencies, generation);
                handle.ErrorReason = RevResLoadErrorReason.Cancelled;
                handle.MarkError();
            }
            catch (Exception)
            {
                if (!handle.BundleAcquired) ReleaseDependencies(acquiredDependencies, generation);
                handle.ErrorReason = RevResLoadErrorReason.BundleLoadFail;
                handle.MarkError();
            }
            finally
            {
                onFinished?.Invoke(handle);
            }
        }

        /// <summary>优先使用策略已查到的地址。旧自定义策略仍可传入包名与资源名的两段文本。</summary>
        private static RevABAddress ResolveAddress(RevResHandle handle)
        {
            RevABAddress address = handle.ABAddress;
            if (address != null)
            {
                return address;
            }
            string path = handle.RealPath;
            int separator = path?.IndexOf('|') ?? -1;
            if (separator < 0 || path.IndexOf('|', separator + 1) >= 0)
            {
                return null;
            }
            return new RevABAddress(path.Substring(0, separator), path.Substring(separator + 1));
        }

        /// <summary>
        /// 紫色不等于“AB 没加载”：Prefab 已加载成功时还可能是 Shader 没打进 Player/依赖包，
        /// 或 Android 图形 API/变体不支持。仅在真机检查，不修改材质或偷偷替换 Shader。
        /// </summary>
        private static void ReportAndroidShaderIssues(UnityEngine.Object asset, string bundleName, string assetName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (asset is Material material)
            {
                CheckAndroidShader(material, bundleName, assetName);
            }
            else if (asset is GameObject prefab)
            {
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (Material entry in renderer.sharedMaterials)
                        CheckAndroidShader(entry, bundleName, assetName);
                foreach (UnityEngine.UI.Graphic graphic in prefab.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    CheckAndroidShader(graphic.material, bundleName, assetName);
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void CheckAndroidShader(Material material, string bundleName, string assetName)
        {
            if (material == null) return;
            Shader shader = material.shader;
            if (shader != null && shader.isSupported && shader.name != "Hidden/InternalErrorShader") return;
            RevLog.Error($"Android AB Shader 不可用：包 {bundleName} / 资源 {assetName} / 材质 {material.name} / Shader " +
                         $"{(shader == null ? "<missing>" : shader.name)}。检查 Player 的 Always Included Shaders、Shader 所在依赖包、" +
                         "目标 Android 构建平台、图形 API/渲染管线及运行时 Shader 变体；请重新构建 Player 和 AB。", "Res");
        }
#endif

        /// <summary>
        /// 按清单取得依赖并记录每一份引用。浏览器一批可发多个独立请求，批内失败也等其余已发请求收尾。
        /// 文件加载及小游戏沿用串行。返回非空包名表示失败项，外层负责取消判定和回滚。
        /// </summary>
        private async RevTask<string> AcquireDependenciesAsync(string[] dependencies, List<string> acquired, int generation, RevCancellationToken token)
        {
#if REV_AB_BROWSER_PARALLEL
            if (dependencies.Length > 1)
            {
                var batch = new RevTask<AssetBundle>[Math.Min(dependencies.Length, BrowserDependencyBatchSize)];
                for (int start = 0; start < dependencies.Length; start += batch.Length)
                {
                    if (generation != _generation)
                    {
                        throw new RevOperationCanceledException();
                    }
                    token?.ThrowIfCancelled();

                    // 调用立即开始同一批请求。完成结果逐个接收，因此第一项失败也不会丢掉后面取得的引用。
                    int count = Math.Min(batch.Length, dependencies.Length - start);
                    for (int i = 0; i < count; i++)
                    {
                        batch[i] = AcquireSingleAsync(dependencies[start + i]);
                    }

                    string failed = null;
                    for (int i = 0; i < count; i++)
                    {
                        string dependency = dependencies[start + i];
                        AssetBundle bundle;
                        try
                        {
                            bundle = await batch[i];
                        }
                        catch (Exception)
                        {
                            bundle = null;
                        }
                        if (generation == _generation && bundle != null)
                        {
                            acquired.Add(dependency);
                        }
                        else
                        {
                            failed ??= dependency ?? string.Empty;
                        }
                    }

                    if (generation != _generation)
                    {
                        throw new RevOperationCanceledException();
                    }
                    token?.ThrowIfCancelled();
                    if (failed != null)
                    {
                        return failed;
                    }
                }
                return null;
            }
#endif
            foreach (string dependency in dependencies)
            {
                AssetBundle bundle = await AcquireSingleAsync(dependency);
                if (generation != _generation)
                {
                    throw new RevOperationCanceledException();
                }
                if (bundle == null)
                {
                    return dependency;
                }
                acquired.Add(dependency);
                token?.ThrowIfCancelled();
            }
            return null;
        }

        /// <summary>归还本次已取得的依赖，避免旧代请求减掉新代同名包的引用。</summary>
        private void ReleaseDependencies(List<string> dependencies, int generation)
        {
            // ReleaseAll 已替旧代清账；旧请求的回滚不得触碰新代同名包。
            if (generation != _generation) { return; }
            for (int i = dependencies.Count - 1; i >= 0; i--) ReleaseSingle(dependencies[i]);
        }

        /// <summary>
        /// 同一代的调用方共享主包请求。退代后仍到达的旧主包在这里卸载。
        /// </summary>
        private async RevTask<bool> EnsureManifestAsync()
        {
            if (_source != null) return true;
            if (_manifest != null) return true;
            if (_manifestSource != null) return await _manifestSource.Task;

            var source = new RevTaskCompletionSource<bool>();
            _manifestSource = source;
            int generation = _generation;
            bool success = false;
            AssetBundle mainBundle = null;

            try
            {
                mainBundle = await LoadBundleAsync(MainName, generation);
                if (generation != _generation)
                {
                    if (mainBundle != null) mainBundle.Unload(false);
                    return false;
                }

                if (mainBundle == null) return false;
                AssetBundleManifest manifest = mainBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (manifest == null)
                {
                    mainBundle.Unload(false);
                    return false;
                }

                _mainBundle = mainBundle;
                _manifest = manifest;
                success = true;
                return true;
            }
            catch
            {
                if (mainBundle != null && !ReferenceEquals(_mainBundle, mainBundle)) mainBundle.Unload(false);
                return false;
            }
            finally
            {
                source.SetResult(success);
                if (ReferenceEquals(_manifestSource, source)) _manifestSource = null;
            }
        }

        /// <summary>
        /// 异步加载本代的 AB（WebGL 走 UnityWebRequest，其余走 LoadFromFileAsync）。代次失效后停止重试。
        ///
        /// 预期的传输失败和退代返回 null。调用方负责处理异常并归还已加载的结果。
        /// WebGL URL 和 Android APK 路径都直接交给各自的异步加载器。
        ///
        /// 【为什么这里要重试】小游戏 / 移动网络下"偶发失败"很常见（切网、DNS、CDN 抖动），
        ///   失败一次就把首屏判死刑，玩家看到的就是"进不去"。
        ///   次数与间隔见 MaxAttempts / RetryDelayMs；每次重试都**新建请求**
        ///   （被超时 Abort 过的 UnityWebRequest 不能复用）。
        /// </summary>
        private async RevTask<AssetBundle> LoadBundleAsync(string abName, int generation)
        {
            int attempts = MaxAttempts < 1 ? 1 : MaxAttempts;

            for (int i = 0; generation == _generation && i < attempts; i++)
            {
                AssetBundle bundle = await LoadBundleOnceAsync(abName, generation);
                if (bundle != null)
                {
                    // 原生加载恰在切代时完成，由持有请求的调用方卸载这份旧包。
                    return bundle;
                }
                if (generation != _generation || i >= attempts - 1)
                {
                    return null;
                }

                // 重试前先把"失败了、还要再试"报到日志：真机上排查网络问题时，这条是关键线索
                RevLog.Warn($"[Res] 加载 AB 包失败，准备重试（第 {i + 2}/{attempts} 次）：{abName}", "Res");

                int delay = RetryDelayMs * (i + 1);
                if (delay > 0)
                {
                    await RevTask.Delay(delay);
                }
            }
            return null;
        }

        /// <summary>执行单次 AB 读取。普通浏览器先等共享名额，实际发出后才开始下载超时计时。</summary>
        private async RevTask<AssetBundle> LoadBundleOnceAsync(string abName, int generation)
        {
#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME) && !UNITY_EDITOR
#if UNITY_WEBGL && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            // 浏览器后端也可能提供已安装的虚拟文件系统对象。内置 URL 仍走引擎 HTTP 缓存。
            if (_source != null)
            {
                string local = _source.GetPath(abName);
                if (!local.Contains("://") && File.Exists(local))
                {
                    AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(local);
                    await request;
                    return request.assetBundle;
                }
            }
#endif
#if REV_AB_BROWSER_PARALLEL
            while (generation == _generation && _browserRequests >= BrowserRequestLimit)
            {
                await RevTaskScheduler.NextFrame();
            }
            if (generation != _generation)
            {
                return null;
            }

            _browserRequests++;
            try
            {
                return await LoadUrlBundleOnceAsync(abName, generation);
            }
            finally
            {
                _browserRequests--;
            }
#else
            return await LoadUrlBundleOnceAsync(abName, generation);
#endif
#else
            AssetBundleCreateRequest req = AssetBundle.LoadFromFileAsync(ResolveBundlePath(abName));
            await req;
            return req.assetBundle;
#endif
        }

#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME) && !UNITY_EDITOR
        /// <summary>发出并等待一次 URL 请求。所有结果都经 using 归还浏览器请求对象。</summary>
        private async RevTask<AssetBundle> LoadUrlBundleOnceAsync(string abName, int generation)
        {
            using (UnityWebRequest www = CreateBundleRequest(abName))
            {
                UnityWebRequestAsyncOperation op = www.SendWebRequest();
                if (!await WaitForBundleRequestAsync(www, op, abName, generation))
                {
                    return null;
                }
                if (www.result != UnityWebRequest.Result.Success)
                {
                    if (generation == _generation)
                    {
                        // 记录包名和传输结果，CDN 的完整签名地址不进入日志。
                        RevLog.Warn($"WebGL AB 下载失败：{abName}，{www.result}，HTTP {www.responseCode}", "Res");
                    }
                    return null;
                }
                return DownloadHandlerAssetBundle.GetContent(www);
            }
        }

        /// <summary>
        /// 建一个 AB 下载请求；能从钩子拿到缓存标识（hash / crc）就带上。
        ///
        /// ★ 带上它的意义：引擎据此决定"这份包本地缓存过没有、要不要重新下载" ——
        ///   浏览器仍可通过 HTTP 缓存头复用稳定 URL。引擎缓存标识则来自清单里的 hash 和 CRC，
        ///   热更包通过 BundleCacheKeyResolver 把它交过来。
        /// </summary>
        private UnityWebRequest CreateBundleRequest(string abName)
        {
            string url = ResolveBundlePath(abName);

            Func<string, RevBundleCacheKey> resolver = BundleCacheKeyResolver;   // 先取快照（与路径钩子同一条纪律）
            RevBundleCacheKey key = _source != null ? _source.GetCacheKey(abName) : resolver == null ? default : resolver(abName);

            if (key.IsValid)
            {
                return UnityWebRequestAssetBundle.GetAssetBundle(url, key.Hash, key.Crc);
            }

            return UnityWebRequestAssetBundle.GetAssetBundle(url);
        }

        /// <summary>
        /// 按帧等请求完成。配置超时或整个加载器退代时中止请求并返回 false。
        ///
        /// 实时时钟仍受页面暂停后的帧调度影响，恢复运行时按当前时间处理超时。
        /// </summary>
        private async RevTask<bool> WaitForBundleRequestAsync(UnityWebRequest www, UnityWebRequestAsyncOperation op,
            string abName, int generation)
        {
            float timeout = AttemptTimeoutSeconds;
            double deadline = timeout > 0 ? Time.realtimeSinceStartupAsDouble + timeout : double.PositiveInfinity;

            while (!op.isDone)
            {
                if (generation != _generation)
                {
                    // 这里检查的是共享加载器代次。单个消费者取消时由各自作用域归还引用。
                    www.Abort();
                    return false;
                }
                if (Time.realtimeSinceStartupAsDouble >= deadline)
                {
                    www.Abort();
                    RevLog.Warn($"WebGL AB 下载超时：{abName}，等待 {timeout} 秒", "Res");
                    return false;
                }

                await RevTaskScheduler.NextFrame();
            }

            return true;
        }
#endif

        /// <summary>返回当前代的一份包引用。缓存命中直接累加，等待同名包先登记引用份数再等待。</summary>
        private async RevTask<AssetBundle> AcquireSingleAsync(string abName)
        {
            int generation = _generation;
            if (_bundles.TryGetValue(abName, out RevBundleEntry entry) && entry.bundle != null)
            {
                entry.refCount++;
                return entry.bundle;
            }

            if (_loading.TryGetValue(abName, out PendingBundle pending))
            {
                pending.References++;
            }
            else
            {
                pending = new PendingBundle();
                _loading.Add(abName, pending);
                CompleteAcquireSingleAsync(abName, generation, pending).Forget();
            }

            AssetBundle bundle = await pending.Completion.Task;
            return generation == _generation ? bundle : null;
        }

        /// <summary>登记全部等候者的引用再唤醒。任何一个续体先归还，其他请求的引用仍已生效。</summary>
        private async RevTask CompleteAcquireSingleAsync(string abName, int generation, PendingBundle pending)
        {
            AssetBundle bundle = null;
            try
            {
                bundle = await LoadBundleAsync(abName, generation);
            }
            catch (Exception)
            {
                bundle = null;
            }

            if (generation != _generation)
            {
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
                bundle = null;
            }
            else if (bundle != null)
            {
                _bundles[abName] = new RevBundleEntry { bundle = bundle, refCount = pending.References };
            }

            if (_loading.TryGetValue(abName, out PendingBundle current) && ReferenceEquals(current, pending))
            {
                _loading.Remove(abName);
            }
            pending.Completion.SetResult(bundle);
        }
    }
}

// ============================================================
// RevHotConfig.cs —— 热更新配置（一次装配，全程只读）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【它是什么】
//   你要告诉热更系统的所有事情，都在这个类里：从哪下、下到哪、怎么校验、失败怎么办。
//   它只是一袋"设置"，没有任何逻辑 —— 所有逻辑都在门面（RevHotUpdate）与管线里。
//
// 【怎么用（最省事）】
//   new RevHotConfig { RemoteRoot = "https://cdn.example.com/gameA" }   // 其余全用默认值
//
// 【三条纪律】
//   ① RemoteRoot 必填，且必须 https（内网联调才允许 AllowHttp = true）；
//   ② 环境用 RemoteRoot/Env 区分（测试桶 / 正式桶），不要用"改代码"的方式切环境；
//   ③ 小游戏平台合法域名数量有限 → 多源降级请配置在 CDN 的多源站上，
//      客户端只用一个域名（FallbackRoots 主要给 Android 用）。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution.HotUpdate
{
    /// <summary>校验强度：越靠后越严（Hash 与 SizeOnly 的差别就是"要不要算哈希"）。</summary>
    public enum RevHotVerifyMode
    {
        /// <summary>只比文件大小（最快，仅适合内网联调）。</summary>
        SizeOnly = 0,

        /// <summary>尺寸 + 哈希（默认；哈希算法取清单头部的 @hashAlgo）。</summary>
        Hash = 1,

        /// <summary>尺寸 + 哈希，另外把 Unity 的 CRC 交给 AssetBundle.LoadFromFile 做加载期校验（预留，暂未启用）。</summary>
        HashAndCrc = 2,
    }

    /// <summary>首包（内置资源）怎么处理：Android 的 StreamingAssets 在 APK 里不是文件路径，处理方式与桌面不同。</summary>
    public enum RevHotFirstPackageMode
    {
        /// <summary>首次启动把内置包拷到 persistentDataPath（Android 必选；拷贝幂等、按 hash/大小跳过）。</summary>
        CopyToLocal = 0,

        /// <summary>不拷贝，直接读 StreamingAssets（仅桌面 / iOS 是真实文件目录，可用）。</summary>
        ReadFromStreamingAssets = 1,
    }

    /// <summary>热更新配置。字段全部有默认值 —— 只需要填 RemoteRoot 就能跑。</summary>
    public sealed class RevHotConfig
    {
        // ============================================================
        // 从哪下（下载路径）
        // ============================================================

        /// <summary>
        /// 远端根地址（必填）。最终 URL 会拼成：
        /// <code>{RemoteRoot}/{Env?}/{平台}/{AppVersion}/{Channel?}/{ResVersion}/bundles/&lt;包名&gt;</code>
        /// 换环境 = 换这一个字符串（测试桶 / 正式桶 / 直连对象存储都行）。
        /// </summary>
        public string RemoteRoot = "";

        /// <summary>备用源（按顺序降级）：主源连续失败后自动切换，例如对象存储直连域名。</summary>
        public string[] FallbackRoots = Array.Empty<string>();

        /// <summary>环境段（可空）：dev / test / prod 之类，拼在路径里，用于目录隔离。</summary>
        public string Env = "";

        /// <summary>渠道段（可空）：分渠道资源时用，拼在路径里。</summary>
        public string Channel = "";

        /// <summary>候选版本切换前必须实际打开的 ResMap 键。空列表沿用完整文件校验。</summary>
        public string[] RequiredAssets = Array.Empty<string>();

        /// <summary>在候选检查完成后复核业务是否允许激活，例如当前是否仍在大厅。</summary>
        public Func<RevHotManifest, bool> CanActivate;

        /// <summary>
        /// 附加请求头（可空）：临时令牌之类。★ 客户端永远不要放云厂商的长期密钥；
        /// 私有桶请由你们的服务端签发"临时 URL"，这里只负责把它带上。
        /// </summary>
        public Dictionary<string, string> ExtraHeaders;

        // ============================================================
        // 版本（大版本锚定：见技术方案第十六章）
        // ============================================================

        /// <summary>
        /// 大版本（App 版本）。默认取 <c>Application.version</c>（Player Settings 里填的那个）。
        /// ★ 远端清单的 @appVersion 与它不一致时会拒绝更新并触发强更回调 —— 这是"跨大版本必须重出包"的纪律。
        /// </summary>
        public string AppVersionOverride = "";

        /// <summary>强制指定资源版本（调试用：忽略本地基线、按全量重新拉该版本）。留空 = 正常比对。</summary>
        public string ResVersionOverride = "";

        /// <summary>平台目录名覆盖（默认按运行平台自动判定：PC / Android / iOS / WebGL）。</summary>
        public string PlatformOverride = "";

        /// <summary>首包基线清单文件名（一般不用改）。</summary>
        public string BuiltinManifestFileName = "RevHotBuiltin.txt";

        /// <summary>首包落地时是否做哈希校验（默认 false：源是自家 APK，只比大小更快）。</summary>
        public bool VerifyBuiltinCopy = false;

        // ============================================================
        // 下到哪（本地路径）
        // ============================================================

        /// <summary>
        /// 本地根目录覆盖。默认 <c>{persistentDataPath}/RevHotUpdate</c>。
        /// 目录布局：{root}/{平台}/{大版本}/{资源版本}/… + _builtin/{平台}/（首包落地）+ current.txt（版本指针）。
        /// </summary>
        public string LocalRootOverride = "";

        /// <summary>保留几个历史资源版本（当前版本之外）。回滚就靠它们；多留一份多占一份磁盘。</summary>
        public int KeepVersions = 2;

        /// <summary>首包处理方式：Android 必须 CopyToLocal（APK 内不是文件路径）；桌面 / iOS 可用 ReadFromStreamingAssets 省磁盘。</summary>
        public RevHotFirstPackageMode FirstPackageMode = RevHotFirstPackageMode.CopyToLocal;

        // ============================================================
        // 下载行为
        // ============================================================

        /// <summary>
        /// 同时下载的文件数。
        /// ★ 默认值按平台给：小游戏（微信 / 抖音）的请求走浏览器 XHR 那一套，并发上限低、
        ///   多开的请求往往挤在一条连接上 —— 给 2 就够，开大了整体反而更慢；
        ///   手机 App 3 个（抢 IO、发热、CDN 限速都还压得住），桌面随意但别超 8。
        /// </summary>
        public int Concurrency = RevHotPlatform.IsWebGL ? 2 : 3;

        /// <summary>单个请求的超时秒数（覆盖 UnityWebRequest.timeout）。</summary>
        public int TimeoutSeconds = 30;

        /// <summary>单个文件的重试次数（每次失败后按指数退避等待）。</summary>
        public int RetryCount = 3;

        /// <summary>重试退避基数（毫秒）：第 1 次等 500ms、第 2 次 1s、第 3 次 2s……</summary>
        public int RetryBackoffMs = 500;

        // ============================================================
        // 校验与清理
        // ============================================================

        /// <summary>校验强度（默认 Hash：尺寸 + SHA-256，分帧计算不卡帧）。</summary>
        public RevHotVerifyMode VerifyMode = RevHotVerifyMode.Hash;

        // ============================================================
        // 杂项
        // ============================================================

        /// <summary>允许 http://（默认 false）。只在内网联调时打开 —— 正式环境必须 https。</summary>
        public bool AllowHttp = false;

        /// <summary>清单文件名（一般不用改）。</summary>
        public string ManifestFileName = "RevHotManifest.txt";

        /// <summary>日志 tag（走 RevLog，Console 里按 tag 过滤）。</summary>
        public string LogTag = "HotUpdate";

        /// <summary>
        /// 大版本不匹配时的回调（远端清单的 @appVersion ≠ 当前包体版本）。
        /// ★ 框架只负责"识别 + 回调"，跳商店 / 弹公告 / 下整包都是你们发行层的事。
        /// </summary>
        public Action<RevHotForceUpdateInfo> OnForceUpdateRequired;

        // ============================================================
        // 派生值（下面这些是"算出来的"，不要手动设置）
        // ============================================================

        /// <summary>生效的大版本：优先用覆盖值，否则取 Application.version。</summary>
        public string AppVersion { get { return string.IsNullOrEmpty(AppVersionOverride) ? UnityEngine.Application.version : AppVersionOverride; } }

        /// <summary>生效的平台目录名：PC / Android / iOS / WebGL（与打包工具 ABBuildSetting.GetPlatformName 的规则一致）。</summary>
        public string Platform { get { return string.IsNullOrEmpty(PlatformOverride) ? RevHotPlatform.Name : PlatformOverride; } }

        /// <summary>生效的本地根目录。</summary>
        public string LocalRoot { get { return string.IsNullOrEmpty(LocalRootOverride) ? RevHotPlatform.DefaultLocalRoot : LocalRootOverride; } }

        /// <summary>
        /// 校验配置：返回错误原因（人话）；返回 null 表示通过。
        /// ★ 在 InitializeAsync 的第一步调用 —— 配置错误应该在"还没开始下载"时就暴露。
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(RemoteRoot) || RemoteRoot.Trim().Length == 0)
            {
                return "RemoteRoot 没有配置（从哪个 CDN / 对象存储下载？）—— 请填远端根地址，例如 https://cdn.example.com/gameA";
            }

            string root = RemoteRoot.Trim();
            if (root.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !AllowHttp)
            {
                return "RemoteRoot 用了 http:// —— 正式环境必须 https（内网联调请显式设置 AllowHttp = true）";
            }

            if (!root.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !root.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "RemoteRoot 必须是 http(s) 地址，实际收到：「" + RemoteRoot + "」";
            }

            // ★ 小游戏 / WebGL 上比其它平台更严：那边的请求由浏览器的 XHR 发出，
            //   明文 http 会被平台直接拦掉（微信、抖音只放行在后台登记过的 https 合法域名），
            //   所以不看 AllowHttp —— 允许了也只会得到真机上一片"网络错误"，不如配置期就说清。
            if (RevHotPlatform.IsWebGL && root.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "小游戏 / WebGL 平台不允许明文 http（只放行已登记的 https 合法域名），RemoteRoot 请改用 https";
            }

            if (Concurrency < 1 || Concurrency > 8)
            {
                return "Concurrency 应在 1~8 之间（手机上并发太大会抢 IO 并触发 CDN 限速）";
            }

            // ★ 小游戏并发上限：XHR 的并发能力远不如原生网络栈，开大了只会互相排队、还要触发平台限流。
            //   默认值是 2（见字段注释）；这里只拦"明显开大"的情况，避免有人从别的平台抄配置过来。
            if (RevHotPlatform.IsWebGL && Concurrency > 4)
            {
                return "小游戏 / WebGL 平台的 Concurrency 不能超过 4（建议保持默认的 2），当前是 " + Concurrency;
            }

            if (RetryCount < 0)
            {
                return "RetryCount 不能是负数";
            }

            if (TimeoutSeconds < 1)
            {
                // timeout ≤ 0 时 UnityWebRequest 视为"不限时"——弱网下单个请求会永远挂住，整个热更流程跟着卡死
                return "TimeoutSeconds 必须 ≥ 1（0 / 负数 = 请求没有超时兜底，网络异常时热更会永远卡住）";
            }

            // 退避是“失败后逐渐多等一会儿再重试”，例如先等 0.5 秒、再等 1 秒，让网络或 CDN 有时间恢复。
            // 负数会让等待时间失去这个含义，可能变成几乎不等待、连续重复请求，所以配置阶段直接拒绝。
            if (RetryBackoffMs < 0)
            {
                return "RetryBackoffMs 不能是负数（重试退避基数，毫秒）";
            }

            if (RetryBackoffMs > 60000)
            {
                // 退避按指数翻倍（500ms → 1s → 2s…），基数太大玩家要干等很久
                return "RetryBackoffMs 过大（上限 60000ms）：重试等待按指数翻倍，基数太大会让玩家等很久";
            }

            // ★ Android 的 StreamingAssets 在 APK 内（jar: 路径），AssetBundle.LoadFromFile 读不了，
            //   所以"直读 StreamingAssets"在 Android 上等于内置资源全部加载失败 —— 配置期就拦下。
            if (RevHotPlatform.NeedsBuiltinCopy && FirstPackageMode == RevHotFirstPackageMode.ReadFromStreamingAssets)
            {
                return "Android 平台的 FirstPackageMode 不能是 ReadFromStreamingAssets（APK 内的包不是文件路径），请改为 CopyToLocal";
            }

            return null;
        }

        /// <summary>把主源与备用源归一化成"去尾斜杠、去空"的列表（第一个是主源）。</summary>
        public List<string> BuildSourceRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, RemoteRoot);
            for (int i = 0; i < FallbackRoots.Length; i++)
            {
                AddRoot(roots, FallbackRoots[i]);
            }

            return roots;
        }

        private static void AddRoot(List<string> roots, string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            string trimmed = root.Trim().TrimEnd('/');
            if (trimmed.Length == 0) return;

            // 去重（大小写不敏感；List<string>.IndexOf 没有带比较器的重载，手写循环）
            for (int i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i], trimmed, StringComparison.OrdinalIgnoreCase)) return;
            }

            roots.Add(trimmed);
        }
    }

    /// <summary>
    /// "需要更新客户端"的信息（大版本不匹配时由 CheckAsync 填好并回调 OnForceUpdateRequired）。
    /// 框架不负责跳商店 —— 怎么引导玩家，是你们发行层的事。
    /// </summary>
    public sealed class RevHotForceUpdateInfo
    {
        /// <summary>远端资源要求的大版本（清单里的 @appVersion）。</summary>
        public string RequiredAppVersion = "";

        /// <summary>当前包体的大版本（Application.version）。</summary>
        public string CurrentAppVersion = "";

        /// <summary>给人看的一句话（可直接放进弹窗）。</summary>
        public string Message = "";
    }
}

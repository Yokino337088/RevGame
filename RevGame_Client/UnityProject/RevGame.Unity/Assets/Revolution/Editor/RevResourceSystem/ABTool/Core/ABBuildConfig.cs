// ============================================================
// ABBuildConfig.cs —— 打包配置（ScriptableObject）
//
// 位置：Editor\RevResourceSystem\ABTool\Core\
//
// 【为什么用 ScriptableObject 而不是 json / ini？】
//   1. 它是 Unity 资产：能在 Inspector 里直接勾选，可被版本管理；
//   2. 团队成员共享同一份，不会出现"我这能打包、他那不能"；
//   3. 有类型检查（json 写错字段名是静默失败，SO 是编译期报错）。
//
// 【便携性】第一次使用时会自动创建默认配置，不需要手动建资产。
// ============================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>压缩方式（直接对应 Unity 的 BuildAssetBundleOptions）</summary>
    public enum ABCompressMode
    {
        /// <summary>块压缩：加载快、体积中等 —— 推荐，王者同款</summary>
        LZ4,
        /// <summary>整体压缩：体积最小、首次加载最慢 —— 适合首包/下载包</summary>
        LZMA,
        /// <summary>不压缩：加载最快、体积最大 —— 只用于调试</summary>
        None
    }

    /// <summary>AB 标记来源模式：决定"分包由谁决定"</summary>
    public enum ABMarkMode
    {
        /// <summary>
        /// 扫描已有标记（默认）：
        ///   分包由你在 Inspector / 自有工具设好，本工具只"读取"标记生成映射，
        ///   **绝不改动任何 AssetBundleName**。
        /// </summary>
        ScanExisting,

        /// <summary>
        /// 按目录自动标记（托管模式）：
        ///   工具按资源根目录下的顶层目录自动分包（会先清空所有旧标记再重打）。
        /// </summary>
        AutoByFolder
    }

    public class ABBuildConfig : ScriptableObject
    {
        // ---------------- 收集规则 ----------------
        [Tooltip("资源根目录（必填）：它下面的资源按'相对此目录的路径'生成逻辑名。" +
                 "在窗口里拖拽文件夹或点「选择…」指定；代码里没有任何默认路径")]
        public string resRoot = "";

        // ---------------- 音效目录（RevSoundSystem 用：只认"相对资源根目录"的逻辑段） ----------------
        // 【为什么存"逻辑段"而不是"Assets/xxx"全路径？】
        //   运行期要用的是逻辑目录段（如 "Audio/Sfx"），它与 RevResManager 的 rootPath 参数、
        //   ResMap 里的逻辑名完全一致；存全路径还会因为换工程 / 换盘符而失效。
        //   窗口里那两个"文件夹槽"只是输入方式：拖进来会**自动转成**逻辑段（见 NormalizeSubRoot）。
        //
        // 【改完会怎样】窗口里改一次 → 重新生成 RevSoundPath.cs（Runtime 程序集内的常量）→
        //   音效系统下次编译就用新目录，写错编译不过（和 RevResPath 同样的"编译期保护"）。

        /// <summary>音效目录默认值（没配置时用它；窗口里的「默认」按钮也是回到这里）</summary>
        public const string DefaultSfxRoot = "Audio/Sfx";

        /// <summary>BGM 目录默认值</summary>
        public const string DefaultBgmRoot = "Audio/Bgm";

        [Tooltip("音效根目录（相对资源根目录的逻辑段，如 Audio/Sfx）。在打包窗口里选；改完会重新生成 RevSoundPath 常量")]
        public string sfxRoot = DefaultSfxRoot;

        [Tooltip("背景音乐根目录（相对资源根目录的逻辑段，如 Audio/Bgm）")]
        public string bgmRoot = DefaultBgmRoot;

        [Tooltip("AB 标记来源：ScanExisting = 只用你已设好的分包（默认）；AutoByFolder = 按目录自动分包")]
        public ABMarkMode markMode = ABMarkMode.ScanExisting;

        // ★ 目前没有任何代码读它（AutoByFolder 始终按顶层目录分包），所以打包窗口里不再显示；
        //   字段保留只是为了不破坏已有的配置资产序列化。
        [Tooltip("（保留字段，当前未使用）AutoByFolder 模式始终按顶层目录分包")]
        [HideInInspector]
        public bool autoGroupByFolder = true;

        [Tooltip("不参与打包的目录名。AutoByFolder 用它决定哪些目录不打标记；" +
                 "手动模式下也用它把目录排除在「漏标检测」之外（如存放源文件/参考图的目录）")]
        public List<string> excludeFolders = new List<string> { "Editor", "Raw" };

        // .cs 是脚本（不该进包），.meta 根本不是资源。
        // ★ 这里【没有】.txt：数据表就是 .txt（<资源根目录>/Data/*.txt），
        //   把它排除掉就等于把配置表踢出包 —— 运行时只表现为 PathNotMapped，极难往"后缀"上想。
        [Tooltip("不参与打包的文件后缀。★ 别把 .txt 加进来：数据表就是 .txt，排除掉会打不进 ResMap")]
        public List<string> excludeExtensions = new List<string> { ".cs", ".meta" };

        [Tooltip("打包前检查「资源根目录下没有任何 AB 标记的资源」：这类资源不会进包，运行时必然加载失败。" +
                 "确实有资源不该进包时，用 excludeFolders / excludeExtensions 排除，或关掉本项")]
        public bool checkUnmarkedAssets = true;

        // ---------------- 打包参数 ----------------
        [Tooltip("压缩方式")]
        public ABCompressMode compressMode = ABCompressMode.LZ4;

        [Tooltip("是否强制全部重打（关闭 = 走增量 Hash 对比，只重打内容变化的包）")]
        public bool forceRebuild = false;

        [Tooltip("确定性打包：相同输入产出完全相同的字节（跨机器 / 跨次可复现）。" +
                 "收益：CI 能缓存产物、团队之间 diff 干净、热更的差异比对更准。默认开。")]
        public bool deterministic = true;

        [Tooltip("去掉包里的 Unity 版本号。★ 收益很小（每个包几十字节），但有个代价：升级 Unity 版本后" +
                 "若忘记强制重打全部 AB，引擎不再能靠版本号发现「包是旧版本打的」，问题会以奇怪的方式暴露。" +
                 "小游戏对包体敏感可以开；开了就必须记住：换 Unity 版本 = 先 ForceRebuild 一次全部 AB。")]
        public bool stripUnityVersion = false;

        [Tooltip("打包前是否清空输出目录（排查问题时可勾）")]
        public bool cleanOutputBeforeBuild = false;

        // ---------------- 产物处理 ----------------
        [Tooltip("打包后是否自动把产物拷到 StreamingAssets（免手动拖文件）")]
        public bool copyToStreamingAssets = false;

        [Tooltip("拷贝目标目录名（相对 Assets，如 StreamingAssets）")]
        public string copyTarget = "StreamingAssets";

        [Tooltip("版本号，会写进产物清单 BuildManifest.json")]
        public string version = "0.1.0";

        // ============================================================
        // 配置读取：全工具唯一入口，保证"只有一个配置来源"
        // ============================================================
        private static ABBuildConfig _instance;

        public static ABBuildConfig Instance
        {
            get
            {
                if (_instance != null) return _instance;

                // ① 先尝试读取工程里已有的配置资产
                _instance = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);

                // ② 没有就自动创建一份默认配置（便携：使用者零配置即可开始）
                if (_instance == null)
                {
                    string dir = System.IO.Path.GetDirectoryName(ABBuildSetting.ConfigAssetPath);
                    if (!System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    _instance = CreateInstance<ABBuildConfig>();
                    AssetDatabase.CreateAsset(_instance, ABBuildSetting.ConfigAssetPath);
                    AssetDatabase.SaveAssets();
                }

                // ③ 把根目录同步给运行时策略（让编辑器直读也知道根目录在哪）
                _instance.ApplyToRuntime();
                return _instance;
            }
        }

        /// <summary>
        /// 取规范化后的资源根目录（去掉结尾 "/"，避免拼路径出现双斜杠）。
        /// ★ 没配置时返回【空字符串】—— 调用方必须自己处理"未配置"，不要替使用者猜一个路径。
        /// </summary>
        public string GetResRoot() => string.IsNullOrEmpty(resRoot) ? "" : resRoot.TrimEnd('/');

        /// <summary>是否已经配置过资源根目录</summary>
        public bool HasResRoot => GetResRoot().Length > 0;

        // ============================================================
        // 音效 / BGM 目录（相对资源根目录的逻辑段）
        // ============================================================

        /// <summary>音效根目录（逻辑段，不带结尾 "/"；取不到时回落到默认值）</summary>
        public string GetSfxRoot()
        {
            string segment = NormalizeSubRoot(sfxRoot);
            return segment.Length > 0 ? segment : DefaultSfxRoot;
        }

        /// <summary>背景音乐根目录（逻辑段，不带结尾 "/"；取不到时回落到默认值）</summary>
        public string GetBgmRoot()
        {
            string segment = NormalizeSubRoot(bgmRoot);
            return segment.Length > 0 ? segment : DefaultBgmRoot;
        }

        /// <summary>音效目录的完整工程路径（如 "Assets/GameRes/Audio/Sfx"；资源根目录没配时返回 ""）</summary>
        public string GetSfxFolderPath() => GetFolderPath(GetSfxRoot());

        /// <summary>BGM 目录的完整工程路径（如 "Assets/GameRes/Audio/Bgm"）</summary>
        public string GetBgmFolderPath() => GetFolderPath(GetBgmRoot());

        private string GetFolderPath(string segment)
            => HasResRoot && segment.Length > 0 ? GetResRoot() + "/" + segment : "";

        /// <summary>
        /// 把"使用者可能粘进来的各种写法"规范成**相对资源根目录的逻辑段**：
        ///   "Audio/Sfx" ／ "Audio/Sfx/" ／ "Assets/GameRes/Audio/Sfx"（resRoot = Assets/GameRes）／
        ///   "\\Audio\\Sfx" → 一律变成 "Audio/Sfx"。
        /// ★ 若目录不在资源根目录之内，这里**不硬塞前缀**、保持原样返回：
        ///   是否接受由调用方（打包窗口 / 生成器）判断并提示，绝不静默改语义。
        /// </summary>
        public string NormalizeSubRoot(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string p = value.Replace('\\', '/').Trim().Trim('/');
            if (p.Length == 0) return "";

            if (p.StartsWith("Assets/", System.StringComparison.Ordinal))
                p = p.Substring("Assets/".Length);

            string root = GetResRoot();
            if (root.StartsWith("Assets/", System.StringComparison.Ordinal))
                root = root.Substring("Assets/".Length);

            if (root.Length > 0)
            {
                if (p == root) p = "";
                else if (p.StartsWith(root + "/", System.StringComparison.Ordinal)) p = p.Substring(root.Length + 1);
            }

            return p.Trim('/');
        }

        /// <summary>
        /// 把"本实例"的资源根目录推给运行时策略 RevEditorResPolicy。
        /// （编辑器程序集可以引用运行时程序集，反过来不行 —— 所以由这里"推"过去。）
        ///
        /// ★ 未配置时**什么都不做**：绝不能把"空根目录"推过去，
        ///   那会让编辑器直读去拼 "UI/Icon/xxx" 这种相对路径，报一堆莫名其妙的 FileNotExist。
        /// </summary>
        public void ApplyToRuntime()
        {
            string root = GetResRoot();

            if (root.Length == 0)
            {
                WarnResRootMissing();
                return;
            }

            _warnedResRoot = false;                       // 配好了 → 以后再次丢失时可以重新提醒
            Revolution.RevEditorResPolicy.ResRoot = root + "/";
        }

        // 未配置只提醒一次：ApplyToRuntime 会在每次脚本重编译后自动跑，不去重会刷屏
        private static bool _warnedResRoot;

        private static void WarnResRootMissing()
        {
            if (_warnedResRoot) return;
            _warnedResRoot = true;

            RevABLog.Warn(
                "[RevAB] 还没设置「资源根目录」，编辑器直读（不打包直接跑）无法工作。\n" +
                "请在菜单 Revolution.Tools/资源/RevAB 打包工具 →「打包」页签里指定资源根目录（拖拽文件夹或点「选择…」）。");
        }

        /// <summary>
        /// 只读取"已存在"的配置并同步，**不会创建配置资产**。
        /// 供编辑器启动 / 资源导入回调这类"不该有副作用"的场景使用。
        /// </summary>
        public static void SyncRuntimeRoot()
        {
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            cfg?.ApplyToRuntime();
        }

        /// <summary>
        /// 把本配置翻译成 Unity 打包 API 需要的"选项位"（[Flags] 枚举可叠加）。
        /// </summary>
        public BuildAssetBundleOptions GetBuildOptions()
        {
            BuildAssetBundleOptions opt = BuildAssetBundleOptions.None;

            switch (compressMode)
            {
                case ABCompressMode.LZ4:
                    opt |= BuildAssetBundleOptions.ChunkBasedCompression;      // 开 LZ4
                    break;
                case ABCompressMode.LZMA:
                    break;                                                      // 默认即 LZMA
                case ABCompressMode.None:
                    opt |= BuildAssetBundleOptions.UncompressedAssetBundle;     // 不压缩
                    break;
            }

            if (forceRebuild)
                opt |= BuildAssetBundleOptions.ForceRebuildAssetBundle;

            // ★ 确定性：让"相同输入 → 相同字节"。
            //   Unity 5 起的新构建管线默认就是确定性输出，旧的 DeterministicAssetBundle
            //   开关已标记过时（显式置位只会刷 CS0618 警告），因此这里不再设置；
            //   deterministic 字段保留是为了兼容已有的配置资产（新管线恒定满足该语义）。
            //   于是 CI 能缓存产物、团队之间 diff 干净、热更的"内容变没变"也更准。

            // ★ 去掉 Unity 版本号：省下的字节很少，主要代价是"跨 Unity 版本混用包"时失去一道护栏
            //   （升级引擎后没重打 AB，引擎不再能发现），所以默认关闭，见字段上的 Tooltip。
#if UNITY_2018_3_OR_NEWER
            if (stripUnityVersion)
                opt |= BuildAssetBundleOptions.AssetBundleStripUnityVersion;
#endif

            return opt;
        }
    }

    /// <summary>
    /// 编辑器加载 / 重编译后自动把配置里的根目录同步给运行时策略，
    /// 这样"直接按 Play"（没打开过工具窗口）也能拿到正确的根目录。
    /// </summary>
    [InitializeOnLoad]
    internal static class ABBuildConfigAutoSync
    {
        static ABBuildConfigAutoSync()
        {
            // 延后到编辑器空闲时执行：避免在 AssetDatabase 尚未就绪时访问它
            EditorApplication.delayCall += () =>
            {
                ABBuildConfig.SyncRuntimeRoot();

                // 保证 RevSoundPath.cs 与配置一致：新克隆的仓库 / 手改过配置 / 生成过又被删掉，都能自动补上
                // （音效系统编译依赖这个文件，缺了会编译不过）
                ABSoundPathGenerator.GenerateSafe();
            };
        }
    }
}

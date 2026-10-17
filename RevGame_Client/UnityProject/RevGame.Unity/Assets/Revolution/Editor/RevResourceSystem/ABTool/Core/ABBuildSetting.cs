// ============================================================
// ABBuildSetting.cs —— 工程级"技术路径"常量
//
// 位置：Editor\RevResourceSystem\ABTool\Core\
//
// 【哪些放这里，哪些放配置？】
//   ★ 放这里：有"技术约束"、一般不该乱动的路径
//       - 产物输出目录 OutputRoot（相对工程根）
//       - ResMap.txt 路径（必须落在某个 Resources 文件夹下：运行时靠 Resources.Load 读它）
//       - RevResPath.cs 路径（生成的代码必须落在 Assets 下才会被编译）
//   ★ 放 ABBuildConfig：由"项目结构 / 使用者"决定的
//       - 资源根目录 resRoot（可在编辑器窗口里拖拽 / 选择，不用改代码）
//
// 【为什么单独抽一个文件？】
//   避免这些字符串散落在十几个文件里，改一次要全局搜索替换、极易漏改。
// ============================================================
using System.IO;
using UnityEngine;

namespace Revolution.Editor
{
    public static class ABBuildSetting
    {
        // 【这里为什么没有"资源根目录"常量？】
        //   资源根目录由使用者决定（项目结构不同、随时可能改），所以它**只存在于配置资产里**
        //   （ABBuildConfig.resRoot），代码里一个默认路径都不留 ——
        //   否则"猜"一个 Assets/GameRes 出来，既会让人以为框架写死了它，
        //   又会把"没配置"悄悄变成"文件找不到"，把排查方向带偏。

        /// <summary>打包产物输出根目录（其下还会按平台再分子目录，如 AssetBundles/Android）</summary>
        public const string OutputRoot = "AssetBundles";

        /// <summary>
        /// 运行时映射表路径：放在**框架自己的 Resources 文件夹**里，
        /// 而不是工程根的 Assets/Resources —— 框架的东西归框架，不占用使用者的目录。
        ///
        /// ★ 它与运行时 RevResBootstrap.LoadResMap 里的 Resources.Load 路径是一对：
        ///   Resources.Load 的路径"相对任意 Resources 文件夹"、且不带扩展名，
        ///   所以这里是 ".../Resources/ResourceSystem/ResMap.txt" → 那边写 "ResourceSystem/ResMap"。
        ///   改一个必须改另一个。
        /// </summary>
        public const string MapAssetPath = "Assets/Revolution/Resources/ResourceSystem/ResMap.txt";

        /// <summary>
        /// 自动生成的 RevResPath 常量类路径。
        /// 放在框架自带的 Generation 程序集里（Revolution.Generation.asmdef，
        /// 它已引用 Revolution.Runtime 且 autoReferenced，业务程序集能直接用到）。
        /// </summary>
        public const string ResPathCodePath = "Assets/Revolution/Generation/RevResPath.cs";

        /// <summary>
        /// 自动生成的音效路径常量类路径（音效 / BGM 的资源目录，在打包窗口①配置里选）。
        ///
        /// ★ 为什么单独生成一份、而不是复用 Generation 里的 RevResPath？
        ///   Revolution.Runtime.asmdef 的 references 是空的，而 Generation 反过来引用了 Runtime ——
        ///   音效系统的代码在 Runtime 程序集里，反向引用 Generation 会成环。
        ///   所以音效目录的常量必须生成在 **Runtime 程序集内部**（RevSoundSystem\Generated\）。
        ///   它与 RevResPath 的分工：RevResPath 是"整个资源根目录的目录表"（业务用），
        ///   这里是"音效系统要用的那两个目录"（框架自己用，业务也能顺手用）。
        /// </summary>
        public const string SoundPathCodePath = "Assets/Revolution/Runtime/RevSoundSystem/Generated/RevSoundPath.cs";

        /// <summary>打包配置资产路径（跟着工程走，团队成员共享同一份）</summary>
        public const string ConfigAssetPath = "Assets/Editor/ABBuildConfig.asset";

        /// <summary>取当前平台的产物目录，例如 "AssetBundles/PC"</summary>
        public static string GetOutputDir(UnityEditor.BuildTarget target)
            => Path.Combine(OutputRoot, GetPlatformName(target));

        /// <summary>
        /// 平台名：既是产物目录名，也是运行时主包名
        /// （Unity 用输出目录名给 AssetBundleManifest 主包命名）。
        ///
        /// ★★ 必须与运行时两侧保持一致，改这里就要同步改：
        ///      · Revolution.Runtime  → RevABLoader.MainName
        ///      · Revolution.HotUpdate → RevHotPlatform.Name
        ///    它们一个是编译期宏（运行时）、一个是 target 参数（编辑器），没法共用一份代码，
        ///    只能靠这条注释互相指路 —— 名字差一个字符 = 首屏全 BundleLoadFail。
        ///
        /// ★ 小游戏（微信 / 抖音…）的约定：**一律用 WebGL 构建目标打 AB**，
        ///   资源格式与 WebGL 完全相同，运行时的平台段也统一是 "WebGL"。
        ///   若用了引擎自带的小游戏构建目标（WeixinMiniGame / ByteDanceMiniGame…），
        ///   这里也会归一到 "WebGL" —— 否则产物目录名（WeixinMiniGame）与运行时找的目录名
        ///   （WebGL）对不上，真机上一片 404。
        /// </summary>
        public static string GetPlatformName(UnityEditor.BuildTarget target)
        {
            switch (target)
            {
                case UnityEditor.BuildTarget.StandaloneWindows:
                case UnityEditor.BuildTarget.StandaloneWindows64:
                case UnityEditor.BuildTarget.StandaloneOSX:
                case UnityEditor.BuildTarget.StandaloneLinux64:
                    return "PC";                        // 桌面统一 PC

                case UnityEditor.BuildTarget.Android:
                    return "Android";

                case UnityEditor.BuildTarget.iOS:
                    return "iOS";

                case UnityEditor.BuildTarget.WebGL:
                    return "WebGL";

                default:
                    // ★ 小游戏目标归一到 WebGL：用字符串判断而不是 case 枚举 ——
                    //   小游戏枚举是否存在取决于引擎版本 / 发行版（团结引擎、Unity 中国版…），
                    //   写死枚举名在老版本引擎上直接编译不过；按名字含 "MiniGame" 识别则永远安全。
                    if (target.ToString().IndexOf("MiniGame", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "WebGL";
                    }

                    // 其余平台（tvOS / Switch…）框架暂未适配：仍然按枚举名出目录，
                    // 但运行时的 MainName 没有对应分支（会落到 PC），务必先补运行时再打这个平台。
                    return target.ToString();
            }
        }

        // ============================================================
        // 本机偏好（EditorPrefs：每人一份，不进版本库）
        // ============================================================

        /// <summary>
        /// 读一个布尔偏好；新键没有、旧键（LiteAB 时代）有 → 沿用旧值并写到新键上（只迁移一次）。
        /// ★ 工具从 LiteAB 更名为 RevAB 后，偏好键也换了前缀 —— 不迁移的话，
        ///   用户关掉过的「自动同步」「Project 显示包名」会在升级后被悄悄打开。
        /// </summary>
        internal static bool GetPrefBool(string key, string legacyKey, bool defaultValue)
        {
            if (UnityEditor.EditorPrefs.HasKey(key)) return UnityEditor.EditorPrefs.GetBool(key, defaultValue);
            if (string.IsNullOrEmpty(legacyKey) || !UnityEditor.EditorPrefs.HasKey(legacyKey)) return defaultValue;

            bool value = UnityEditor.EditorPrefs.GetBool(legacyKey, defaultValue);
            UnityEditor.EditorPrefs.SetBool(key, value);
            UnityEditor.EditorPrefs.DeleteKey(legacyKey);
            return value;
        }

        private const string BuildTargetPrefKey = "Revolution.RevAB.BuildTarget";

        /// <summary>
        /// 打包窗口里选的目标平台（null = 跟随当前平台 EditorUserBuildSettings.activeBuildTarget）。
        /// ★ 为什么是"本机偏好"而不是写进共享配置：打哪个平台是"这次想做什么"，
        ///   不该因为 A 同学打了一次 iOS，B 同学拉代码后默认也变成 iOS。
        ///   CI（ABCIBuild）永远用当前平台，不读它。
        /// </summary>
        internal static UnityEditor.BuildTarget? PreferredBuildTarget
        {
            get
            {
                int value = UnityEditor.EditorPrefs.GetInt(BuildTargetPrefKey, -1);
                return value < 0 ? (UnityEditor.BuildTarget?)null : (UnityEditor.BuildTarget)value;
            }
            set => UnityEditor.EditorPrefs.SetInt(BuildTargetPrefKey, value.HasValue ? (int)value.Value : -1);
        }

        /// <summary>这次打包实际用的平台：选了就用选的，没选就跟随当前平台</summary>
        internal static UnityEditor.BuildTarget ResolveBuildTarget()
            => PreferredBuildTarget ?? UnityEditor.EditorUserBuildSettings.activeBuildTarget;

        /// <summary>打包窗口「目标平台」下拉里列出的常用平台（与 AssetBundle Browser 的常用项一致）</summary>
        internal static readonly UnityEditor.BuildTarget[] CommonBuildTargets =
        {
            UnityEditor.BuildTarget.StandaloneWindows64,
            UnityEditor.BuildTarget.StandaloneOSX,
            UnityEditor.BuildTarget.StandaloneLinux64,
            UnityEditor.BuildTarget.Android,
            UnityEditor.BuildTarget.iOS,
            UnityEditor.BuildTarget.WebGL,
        };

        /// <summary>这台机器装没装这个平台的构建支持（没装就打不了包，窗口里会提示去 Unity Hub 装模块）</summary>
        internal static bool IsBuildTargetInstalled(UnityEditor.BuildTarget target)
            => UnityEditor.BuildPipeline.IsBuildTargetSupported(
                UnityEditor.BuildPipeline.GetBuildTargetGroup(target), target);

        // ============================================================
        // 生成物写入守卫（框架被当作"只读包"安装时用）
        // ============================================================

        /// <summary>
        /// 框架本体是否以**只读包**方式安装（UPM 从 git / Registry 装的包，落在 Library/PackageCache）。
        ///
        /// 【为什么要判断】两个生成物（<c>RevResPath.cs</c>、<c>RevSoundPath.cs</c>）必须落在**框架自己的程序集**里
        ///   （Runtime 不能反向引用 Generation，所以音效路径常量只能生成在 Runtime 内部）——
        ///   也就是说它们只能写在框架目录里：框架在 <c>Assets</c> 下时随时可写，
        ///   在包缓存里则是只读的（硬写会报错，或被下一次包更新覆盖掉）。
        ///   所以这里识别出来，让生成器**明确跳过并提示**，而不是留下一个"看起来生成了"的假象。
        /// </summary>
        internal static bool IsInstalledAsReadOnlyPackage()
        {
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ABBuildSetting).Assembly);

            if (info == null) return false;             // 框架在工程 Assets 下 → 可写

            // Embedded（工程内 Packages/ 下的包）与 Local（本地路径）都可写；其余（Git / Registry / 压缩包）都在只读缓存里
            return info.source != UnityEditor.PackageManager.PackageSource.Embedded
                && info.source != UnityEditor.PackageManager.PackageSource.Local;
        }

        /// <summary>
        /// 写生成物之前的守卫：只读包安装 → 跳过并说明怎么办。返回 true = 可以写。
        /// </summary>
        /// <param name="what">生成物名（用于日志，如 "RevResPath"）</param>
        /// <param name="targetPath">本来要写的路径</param>
        internal static bool EnsureWritableForGeneratedCode(string what, string targetPath)
        {
            if (!IsInstalledAsReadOnlyPackage()) return true;

            RevABLog.Warn(
                $"[{what}] 检测到框架是以 UPM 包（只读缓存）方式安装的，已跳过生成：{targetPath}\n" +
                "  · 包里的常量文件是随包发布的版本，直接用即可；\n" +
                "  · 要按自己的目录结构重新生成，请改用「把框架放进工程 Assets」的安装方式：\n" +
                "    git clone -b package --depth 1 https://github.com/Yokino337088/Revolution.git Assets/Revolution");
            return false;
        }
    }
}

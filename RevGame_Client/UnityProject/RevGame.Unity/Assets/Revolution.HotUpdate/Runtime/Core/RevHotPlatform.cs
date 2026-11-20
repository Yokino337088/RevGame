// ============================================================
// RevHotPlatform.cs —— 平台能力表（三个平台三条路，全都收敛在这里）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【为什么需要它】
//   同样是"下载 AB 再加载"，三个平台的物理形态完全不同：
//     · Android   ：内置包在 APK 里（jar: 路径，不是文件）→ 必须先"落地"到 persistentDataPath
//     · 小程序/WebGL：根本没有文件系统 → AB 只能走 CDN 的 URL，缓存与校验交给引擎
//     · 桌面 / iOS：StreamingAssets 是真实文件目录 → 直接 LoadFromFile 就行
//   把这些"平台差异"收在一个文件里，其余代码只问三个问题：
//     是不是 WebGL？有没有本地文件？内置包要不要落地？
//
// 【注意】平台目录名（PC / Android / iOS / WebGL）必须与打包工具
//   ABBuildSetting.GetPlatformName 的规则保持一致 —— 那边是"输出目录名 = 主包名"，
//   这边是"下载路径里的一段"，两边对不上就会 404。
// ============================================================
using UnityEngine;

namespace Revolution.HotUpdate
{
    /// <summary>平台能力表（只读；运行期间不变）。</summary>
    internal static class RevHotPlatform
    {
        /// <summary>
        /// 平台目录名：既是"远端下载路径里的一段"，也要与打包工具的输出目录名一致。
        /// 桌面（Windows / macOS / Linux）统一叫 PC；其余平台按 Unity 的目标平台命名。
        ///
        /// ★ 小游戏（微信 / 抖音…）归到 "WebGL" 这一档：资源格式与 WebGL 相同，
        ///   打包侧（ABBuildSetting.GetPlatformName）也会把 XxxMiniGame 目标归一到 "WebGL"，
        ///   运行时 RevABLoader.MainName 同样如此 —— 三处必须一起改，名字差一个字符就是 404。
        /// </summary>
        public static string Name
        {
            get
            {
#if (UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_WEBGL) && !UNITY_EDITOR
                return "WebGL";
#elif UNITY_IOS && !UNITY_EDITOR
                return "iOS";
#elif UNITY_ANDROID && !UNITY_EDITOR
                return "Android";
#else
                return "PC";
#endif
            }
        }

        /// <summary>是不是 WebGL / 小游戏：这类平台没有文件系统，AB 只能走 CDN 的 URL。</summary>
        public static bool IsWebGL
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>有没有"本地文件"可用（能 System.IO、能 LoadFromFile）。WebGL 上所有文件相关逻辑都要跳过。</summary>
        public static bool SupportsLocalFiles
        {
            get { return IsWebGL == false; }
        }

        /// <summary>
        /// 内置包要不要"落地"（从 StreamingAssets 拷到 persistentDataPath）。
        /// ★ 只有 Android 必须落地：APK 里的包对 LoadFromFile 来说不是文件，不落地就永远读不到内置资源。
        ///   桌面 / iOS 的 StreamingAssets 本来就是真实文件，直接读更省磁盘。
        /// </summary>
        public static bool NeedsBuiltinCopy
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>默认本地根目录：{persistentDataPath}/RevHotUpdate。</summary>
        public static string DefaultLocalRoot
        {
            get { return Application.persistentDataPath + "/RevHotUpdate"; }
        }
    }
}

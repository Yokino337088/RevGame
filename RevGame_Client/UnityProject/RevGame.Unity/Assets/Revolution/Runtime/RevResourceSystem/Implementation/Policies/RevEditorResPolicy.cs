// ============================================================
// RevEditorResPolicy.cs —— 编辑器策略（开发期零打包）
//
// 位置：Runtime\资源加载\Sub\
//
// 【它什么时候被注册？】
//   只在编辑器下、且"AB 模式"未开启时注册（见 RevResBootstrap.Init）。
//   此时它是唯一策略 → Match 恒 true → 所有资源一律直读工程。
//
// 【它为什么不允许兜底？】
//   直读工程都找不到，说明资源真的不存在（路径写错 / 被删），
//   再兜底到 Resources / AB 只会掩盖问题，不如直接失败暴露出来。
//
// 【关于资源根目录】
//   根目录可在编辑器窗口里配置（ABBuildConfig.resRoot）。
//   编辑器侧会在保存配置时把最新值写入下面的静态字段 ResRoot，
//   运行时（此处）只负责使用它 —— 这样配置就能被本类感知到。
// ============================================================
using System.Collections.Generic;

namespace Revolution
{
    public class RevEditorResPolicy : IRevResPolicy
    {
        /// <summary>
        /// 编辑器资源根目录（带结尾 "/"）。
        ///
        /// ★ 这里**故意没有默认值**：资源根目录由使用者决定，在编辑器工具里指定
        ///   （Revolution.Tools/资源/RevAB 打包工具 → 资源根目录），再由编辑器侧同步进来
        ///   （见 ABBuildConfig.ApplyToRuntime）。
        ///   代码里预置 "Assets/GameRes/" 只会让人以为框架写死了这个目录，
        ///   还会把"没配置"掩盖成"文件找不到"，把排查方向带偏。
        ///
        /// 空字符串 = 未配置。
        /// </summary>
        public static string ResRoot = "";

        private readonly RevEditorLoader _loader = new RevEditorLoader();

        // 逻辑路径 → 真实工程路径。查一次文件系统就够，之后直接命中（键里带上 ResRoot，配置换根目录时自动失效）
        private readonly Dictionary<string, string> _pathCache = new Dictionary<string, string>();

        // 唯一的策略，恒匹配 → 接管全部资源
        public bool Match(string standardPath) => true;

        /// <summary>
        /// 逻辑路径 → 工程内真实路径。
        ///
        /// 【为什么要"找扩展名"？】逻辑路径是按约定【不带扩展名】的（见 ABCollector.BuildLogicPath，
        /// 生成的 RevResPath 常量、ResMap.txt 的键都不带），而 AssetDatabase.LoadAssetAtPath 必须带扩展名。
        /// 所以这里在目标目录里按"同名 + 任意扩展名"找一次，并把结果缓存下来 ——
        /// 否则编辑器直读模式下所有资源都会报 FileNotExist。
        /// </summary>
        public string MapPath(string standardPath, System.Type contentType)
        {
            if (ResRoot.Length == 0)
            {
                // 未配置根目录 → 逻辑路径根本没法变成真实工程路径。
                // 明确报错，而不是拼一个必然找不到的路径 —— 否则只会看到 FileNotExist，
                // 让人以为是资源丢了，其实是没配置。
                WarnRootNotConfigured();
                return standardPath;
            }

            string cacheKey = ResRoot + standardPath;
            if (_pathCache.TryGetValue(cacheKey, out string hit)) return hit;

            string guess = ResRoot + standardPath;

            // ① 逻辑路径本身就带扩展名 → 直接用
            if (System.IO.File.Exists(guess)) return Cache(cacheKey, guess);

            // ② 在目录里找"同名 + 任意扩展名"的文件（跳过 .meta）
            string dir = System.IO.Path.GetDirectoryName(guess);
            string name = System.IO.Path.GetFileName(guess);

            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                foreach (string file in System.IO.Directory.GetFiles(dir, name + ".*"))
                {
                    if (file.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                    return Cache(cacheKey, file.Replace('\\', '/'));
                }
            }

            // ③ 确实找不到 → 原样返回，让 RevEditorLoader 报 FileNotExist（不掩盖问题）
            return guess;
        }

        // 未配置只报一次，免得每个资源都刷一条错误
        private static bool _warnedRoot;

        private static void WarnRootNotConfigured()
        {
            if (_warnedRoot) return;
            _warnedRoot = true;

            RevLog.Error(
                "[RevEditorResPolicy] 未配置「资源根目录」，编辑器直读（不打包直接跑）无法工作。" +
                "请在菜单 Revolution.Tools/资源/RevAB 打包工具 里设置「资源根目录」。", "Res");
        }

        private string Cache(string key, string realPath)
        {
            _pathCache[key] = realPath;
            return realPath;
        }

        public IRevResLoader CreateLoader() => _loader;

        // 编辑器直读失败 = 资源真的不存在，不需要再兜底
        public bool AllowFallback => false;
    }
}

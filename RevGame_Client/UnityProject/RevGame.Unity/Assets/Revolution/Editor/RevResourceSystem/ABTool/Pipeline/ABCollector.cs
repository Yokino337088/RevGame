// ============================================================
// ABCollector.cs —— 只读扫描 AB 标记，生成"逻辑路径 → 包名|资源名"映射
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【核心概念：AB 标记（AssetBundle Name）】
//   Unity 打包不是"你指定哪些文件要打"，而是"资源自己身上写着属于哪个包"。
//   标记存在 .meta 里 —— 读取它【完全使用 Unity 官方接口】，本类不维护自造标记表：
//       · AssetDatabase.GetAllAssetBundleNames()              工程里所有 AB 名
//       · AssetDatabase.GetAssetPathsFromAssetBundle(name)    某个包里有哪些资源
//       · AssetDatabase.GetImplicitAssetBundleName(path)      资源实际属于哪个包（含文件夹继承）
//       · AssetDatabase.GetImplicitAssetBundleVariantName(path)  资源的变体
//
// 【混合模式：分包由你定，逻辑路径不暴露包名】
//   逻辑路径 = 资源相对"资源根目录"的工程路径（去掉扩展名）
//   例：Assets/GameRes/Hero/1001.prefab（AB 名 battle_hero）
//       → 逻辑路径 "Hero/1001" → 映射表一行 Hero/1001|battle_hero|1001
// ============================================================
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Revolution.Editor
{
    /// <summary>一条映射记录：逻辑名 → 包名 + 资源名</summary>
    public struct ResMapEntry
    {
        public string logic;    // 逻辑路径（相对资源根目录的工程路径），如 "Hero/1001"
        public string bundle;   // AB 包名（带变体时形如 "ui.hd"），如 "battle_hero"
        public string asset;    // 包内资源名（不含扩展名），如 "1001"
    }

    /// <summary>扫描结果：把"扫到了什么"完整记录，供校验 / 映射 / 代码生成 / 窗口使用</summary>
    public class ABCollectResult
    {
        /// <summary>逻辑名（"Hero/1001"） → Unity 资源路径（"Assets/GameRes/Hero/1001.prefab"）</summary>
        public readonly Dictionary<string, string> logicToAssetPath = new Dictionary<string, string>();

        /// <summary>包名 → 该包包含的逻辑名列表</summary>
        public readonly Dictionary<string, List<string>> bundleToAssets = new Dictionary<string, List<string>>();

        /// <summary>全部映射记录（生成 ResMap.txt / RevResPath.cs 用）</summary>
        public readonly List<ResMapEntry> entries = new List<ResMapEntry>();

        /// <summary>逻辑路径冲突：不同资源算出了同一个逻辑名（如 1001.prefab 与 1001.png）</summary>
        public readonly List<string> duplicateLogics = new List<string>();

        /// <summary>
        /// 逻辑路径冲突的明细：逻辑名 → 撞在一起的那几个资源路径。
        /// （光有逻辑名没法排查 —— 重名检测视图要靠它把"是哪两个文件撞了"列出来并一键定位。）
        /// </summary>
        public readonly Dictionary<string, List<string>> duplicateLogicDetail =
            new Dictionary<string, List<string>>();

        /// <summary>同包内资源重名（"包名 → 资源名"），会导致 LoadAsset 无法区分</summary>
        public readonly List<string> duplicateAssets = new List<string>();

        /// <summary>空包：有包名但一个资源都没有（通常是资源被删/改名残留）</summary>
        public readonly List<string> emptyBundles = new List<string>();

        /// <summary>内部去重用：已出现过的 "生效包名|资源名"</summary>
        public readonly HashSet<string> assetKeys = new HashSet<string>();

        /// <summary>被跳过的资源（命中了排除规则）</summary>
        public readonly List<string> skipped = new List<string>();

        /// <summary>
        /// 漏标：在资源根目录下、却没有任何 AB 标记的资源的 Unity 路径（"Assets/GameRes/..."）。
        ///
        /// ★ 这类资源**不会进任何包** —— 而它偏偏在编辑器直读（AssetDatabase）下用得好好的，
        ///   直到出真机才暴露成 FileNotExist，是最难往"忘了标标记"上想的一类问题。
        ///   手动分包（ScanExisting）模式下最常见，所以单独记一份，交给校验器在打包前报出来。
        /// </summary>
        public readonly List<string> unmarkedAssets = new List<string>();

        /// <summary>收集到的资源数量</summary>
        public int markedCount;
    }

    public static class ABCollector
    {
        /// <summary>
        /// 主入口：收集 AB 标记 → 生成逻辑路径映射。
        /// 默认（ScanExisting）**不会改动任何标记**，纯读取。
        /// </summary>
        public static ABCollectResult Collect()
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;

            if (!cfg.HasResRoot)
            {
                // 没配根目录时，逻辑路径只能退化成"相对 Assets/" 计算 —— 结果不可用。
                // 明确报错，别让它悄悄打出一堆"运行时装不上"的包。
                RevABLog.Error(
                    "[RevAB] 未配置「资源根目录」，逻辑路径会退化成相对 Assets/ 计算，结果不可用。\n" +
                    "请在打包窗口里指定资源根目录（拖拽文件夹或点「选择…」）。");
            }

            // 只有"托管模式"才需要先清空旧标记重打；ScanExisting 绝对不动你已设好的分包
            if (cfg.markMode == ABMarkMode.AutoByFolder)
                AutoMarkByFolder(cfg);

            return ScanExistingMarks(cfg);
        }

        /// <summary>
        /// 【只读】扫描已有标记，**绝不触碰任何标记**。
        ///
        /// 与 Collect() 的区别：Collect() 在 AutoByFolder 模式下会先清空、再按目录重打全部分包；
        /// 而"分包浏览 / 手动改标记"这类窗口只是**看和改**，绝不能顺手把全工程的分包重写一遍 ——
        /// 所以它走这条只读路径。
        /// </summary>
        public static ABCollectResult CollectReadOnly() => ScanExistingMarks(ABBuildConfig.Instance);

        // ============================================================
        // 模式一：扫描已有标记（默认）
        // ============================================================
        private static ABCollectResult ScanExistingMarks(ABBuildConfig cfg)
        {
            var result = new ABCollectResult();

            // ① 遍历工程里所有 AB 包名
            //    ★ GetAllAssetBundleNames() 返回的是"基础包名"（不含变体）
            foreach (string bundleName in AssetDatabase.GetAllAssetBundleNames())
            {
                if (string.IsNullOrEmpty(bundleName)) continue;

                // ② 反查这个包里包含的所有资源（含"给文件夹设标记"带进来的资源）
                string[] assetPaths = AssetDatabase.GetAssetPathsFromAssetBundle(bundleName);

                // ③ 空包：有包名却一个资源都没有 → 记录给校验器
                if (assetPaths.Length == 0)
                {
                    result.emptyBundles.Add(bundleName);
                    continue;
                }

                foreach (string assetPath in assetPaths)
                {
                    string fileName = Path.GetFileName(assetPath);
                    if (IsExcludedFile(fileName, cfg)) { result.skipped.Add(assetPath); continue; }

                    // ④ 用官方接口取"这个资源实际属于哪个包 / 哪个变体"
                    //    用 GetImplicit* 能覆盖"给文件夹设的标记"这种情况
                    string implicitBundle = AssetDatabase.GetImplicitAssetBundleName(assetPath);
                    string implicitVariant = AssetDatabase.GetImplicitAssetBundleVariantName(assetPath);

                    string baseName = string.IsNullOrEmpty(implicitBundle) ? bundleName : implicitBundle;
                    string effectiveBundle = string.IsNullOrEmpty(implicitVariant)
                        ? baseName
                        : baseName + "." + implicitVariant;      // 带变体时文件名叫 "包名.变体"

                    // ⑤ 逻辑路径 = 资源相对"资源根目录"的工程路径（去掉扩展名）
                    string logic = BuildLogicPath(assetPath, cfg);
                    string assetName = Path.GetFileNameWithoutExtension(assetPath);

                    // ⑥ 逻辑路径撞车（同目录同名不同扩展名）→ 记录并跳过
                    if (result.logicToAssetPath.ContainsKey(logic))
                    {
                        result.duplicateLogics.Add(logic);

                        // 记明细：连"先来的那个"一起记上，重名检测视图才能把两边都列出来
                        if (!result.duplicateLogicDetail.TryGetValue(logic, out List<string> collided))
                        {
                            collided = new List<string> { result.logicToAssetPath[logic] };
                            result.duplicateLogicDetail[logic] = collided;
                        }
                        collided.Add(assetPath);
                        continue;
                    }
                    result.logicToAssetPath[logic] = assetPath;

                    // ⑦ 同包内资源重名（不同目录的同名文件进了同一个包）→ 记录给校验器
                    if (!result.assetKeys.Add(effectiveBundle + "|" + assetName))
                        result.duplicateAssets.Add($"{effectiveBundle} → {assetName}");

                    result.entries.Add(new ResMapEntry
                    {
                        logic = logic,
                        bundle = effectiveBundle,
                        asset = assetName
                    });

                    if (!result.bundleToAssets.TryGetValue(effectiveBundle, out List<string> list))
                    {
                        list = new List<string>();
                        result.bundleToAssets[effectiveBundle] = list;
                    }
                    list.Add(logic);
                    result.markedCount++;
                }
            }

            // ⑧ 漏标检测（只读）：资源根目录下"一个包都没进"的资源
            CollectUnmarked(cfg, result);

            return result;
        }

        // ============================================================
        // 模式一的补充：漏标检测（只读）
        // ============================================================
        /// <summary>
        /// 找出"资源根目录下、却没有任何 AB 标记"的资源。
        ///
        /// 【为什么要反向再扫一遍】
        ///   上面是"从包名反查资源"（GetAllAssetBundleNames → 包里的资源），
        ///   没标标记的资源根本不在包名列表里 —— 那条路永远看不见它。
        ///   而它恰恰最危险：工程里在、编辑器直读也能读到，但永远不进包，
        ///   运行时（AB 模式 / 真机）只会得到 FileNotExist。
        ///
        /// 【口径】与打包口径保持一致：.meta、excludeExtensions、excludeFolders 里的都跳过 ——
        ///   它们本来就不该进包，报出来只会是噪声，把真正的问题淹掉。
        /// </summary>
        private static void CollectUnmarked(ABBuildConfig cfg, ABCollectResult result)
        {
            string resRoot = cfg.GetResRoot();
            if (resRoot.Length == 0 || !Directory.Exists(resRoot)) return;

            foreach (string fullPath in Directory.GetFiles(resRoot, "*.*", SearchOption.AllDirectories))
            {
                string assetPath = fullPath.Replace('\\', '/');
                if (assetPath.EndsWith(".meta")) continue;

                // 相对资源根目录的路径，用于套用排除规则（与 AutoByFolder 同一套判断）
                string logicPath = assetPath.Substring(resRoot.Length + 1);
                if (IsExcluded(logicPath, cfg)) continue;

                // ★ 用 GetImplicit* 而不是 GetAssetBundleName：它会把"给文件夹标标记、子文件继承"
                //   算进来 —— 只给文件夹标了标记的资源不会被误报成漏标。
                if (!string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(assetPath)))
                    continue;

                result.unmarkedAssets.Add(assetPath);
            }

            // 排序：报告稳定（否则每次扫描顺序都可能不同，日志没法逐行对比）
            result.unmarkedAssets.Sort(string.CompareOrdinal);
        }

        // ============================================================
        // 模式二：按顶层目录自动标记（托管模式，会先清空所有旧标记！）
        // ============================================================
        private static void AutoMarkByFolder(ABBuildConfig cfg)
        {
            // 清空旧标记：避免资源改名/移动后留下"幽灵包"
            foreach (string name in AssetDatabase.GetAllAssetBundleNames())
                AssetDatabase.RemoveAssetBundleName(name, true);   // true = 连带移除变体

            string resRoot = cfg.GetResRoot();                      // ★ 根目录来自配置，不写死
            if (!Directory.Exists(resRoot)) return;

            foreach (string fullPath in Directory.GetFiles(resRoot, "*.*", SearchOption.AllDirectories))
            {
                string assetPath = fullPath.Replace('\\', '/');
                if (assetPath.EndsWith(".meta")) continue;

                string logicPath = assetPath.Substring(resRoot.Length + 1);
                if (IsExcluded(logicPath, cfg)) continue;

                // 包名 = 顶层目录名
                int slash = logicPath.IndexOf('/');
                string bundleName = slash < 0 ? "default" : logicPath.Substring(0, slash);

                AssetImporter importer = AssetImporter.GetAtPath(assetPath);
                if (importer == null) continue;

                importer.assetBundleName = bundleName;
                importer.assetBundleVariant = string.Empty;
            }
        }

        // ============================================================
        // 逻辑路径推导
        // ============================================================

        /// <summary>
        /// 由资源路径算出逻辑路径（混合模式的核心规则）：
        ///   ① 在"资源根目录"下 → 去掉 "&lt;resRoot&gt;/" 前缀
        ///   ② 其它目录（如第三方库）→ 去掉 "Assets/" 前缀
        ///   ③ 去掉扩展名
        ///
        /// 例：Assets/GameRes/Hero/1001.prefab (resRoot=Assets/GameRes) → "Hero/1001"
        ///     Assets/ThirdParty/Foo/Bar.png   (不在根目录下)             → "ThirdParty/Foo/Bar"
        /// </summary>
        public static string BuildLogicPath(string assetPath, ABBuildConfig cfg)
        {
            // 未配置根目录时 root 会退化成 "/"（谁都匹配不上）→ 走下面的规则②按 "Assets/" 前缀算。
            // 这是"不至于算错到崩"，但结果并不是使用者想要的 ——
            // 所以 ABCollector.Collect 一进来就会报错提示去配置资源根目录。
            string root = cfg.GetResRoot() + "/";
            string p = assetPath.Replace('\\', '/');

            if (p.StartsWith(root)) p = p.Substring(root.Length);                 // ①
            else if (p.StartsWith("Assets/")) p = p.Substring("Assets/".Length);  // ②

            int dot = p.LastIndexOf('.');                                          // ③
            return dot > 0 ? p.Substring(0, dot) : p;
        }

        /// <summary>排除规则（目录名 + 后缀），AutoByFolder 模式使用</summary>
        private static bool IsExcluded(string logicPath, ABBuildConfig cfg)
        {
            foreach (string dir in cfg.excludeFolders)
                if (logicPath.StartsWith(dir + "/") || logicPath.Contains("/" + dir + "/"))
                    return true;

            return IsExcludedFile(logicPath, cfg);
        }

        /// <summary>后缀排除（不区分大小写，避免 .PNG / .png 漏判）</summary>
        private static bool IsExcludedFile(string path, ABBuildConfig cfg)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (string e in cfg.excludeExtensions)
                if (ext == e.ToLowerInvariant()) return true;

            return false;
        }

        /// <summary>反向查询：某资源被标进了哪个包（窗口/校验用）</summary>
        public static string GetBundleOf(string assetPath)
            => AssetImporter.GetAtPath(assetPath)?.assetBundleName;
    }
}

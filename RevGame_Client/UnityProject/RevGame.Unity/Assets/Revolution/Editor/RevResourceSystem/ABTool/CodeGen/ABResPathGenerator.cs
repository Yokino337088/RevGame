// ============================================================
// ABResPathGenerator.cs —— 生成 RevResPath.cs（资源目录前缀常量）
//
// 位置：Editor\RevResourceSystem\ABTool\CodeGen\
//
// 【解决什么问题？】
//   资源系统的入口是"根目录 + 资源名"两段：
//       RevResManager.Load<GameObject>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//   其中"根目录"这一段总得有人保证它写对：
//     · 手写长路径容易拼错，而且只有运行时才发现"资源找不到"；
//     · 若改成给"每个资源"生成一条全路径常量，那每加一个资源都要重新生成一遍，很烦。
//   所以这里生成「目录前缀常量」：扫描资源根目录下的文件夹，一个目录一条 const。
//   目录前缀有 IDE 补全、能查引用、写错编译不过；资源名随手写 ——
//   新增资源不用重新生成，因为目录结构没变。
//
// 【生成到哪？】
//   ABBuildSetting.ResPathCodePath = Assets/Revolution/Generation/RevResPath.cs
//   （Generation 是框架自带的程序集，专门放"生成物"，见 Revolution.Generation.asmdef）
//
// 【为什么要"防抖"？】
//   本文件会在资源变化时被自动调用（见文件底部的 AssetPostprocessor）。
//   如果每次无脑写文件 → 触发脚本重编译 → 又触发资源变化回调 → 再写文件……
//   就会进入死循环。所以：内容没变就不写。
//
// 【扫描 / 命名 / 拼源码】这些纯逻辑都在 ResPathNaming.cs（不依赖 Unity，可单独编译验证）。
// ============================================================
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public static class ABResPathGenerator
    {
        /// <summary>扫描资源根目录下的所有文件夹，生成 RevResPath 常量类</summary>
        public static void Generate()
        {
            string root = ABBuildConfig.Instance.GetResRoot();

            if (root.Length == 0)
            {
                // 没配置根目录 → 不生成。写出一个空常量类，只会让人以为"工具跑了、但没扫到东西"，
                // 真正的问题其实在配置上。
                RevABLog.Warn("[RevResPath] 还没设置「资源根目录」，已跳过生成。\n" +
                                 "请在菜单 Revolution.Tools/资源/RevAB 打包工具 →「打包」页签里指定资源根目录。");
                return;
            }

            Generate(root);
        }

        /// <summary>
        /// 按指定资源根目录生成。
        /// （窗口里换了根目录后立刻调一次，避免常量还停留在旧根目录上。）
        /// </summary>
        public static void Generate(string resRoot)
        {
            if (string.IsNullOrEmpty(resRoot)) return;      // 空根目录没什么可扫的（提示由调用方负责）

            // ★ 框架被当作只读包安装（UPM）时跳过：包目录不可写，硬写只会留下"假生成"
            if (!ABBuildSetting.EnsureWritableForGeneratedCode("RevResPath", ABBuildSetting.ResPathCodePath)) return;

            List<string> dirs = ResPathNaming.CollectFolders(resRoot);
            string code = ResPathNaming.BuildCode(resRoot, dirs, out List<string> conflicts);

            foreach (string c in conflicts)
                RevABLog.Warn($"[RevResPath] 常量名冲突，已跳过：{c} —— 建议给其中一个目录改名");

            // ★ 防抖：内容没变直接返回，否则会触发"写文件 → 重编译 → 回调 → 再写"的死循环
            if (File.Exists(ABBuildSetting.ResPathCodePath) &&
                File.ReadAllText(ABBuildSetting.ResPathCodePath) == code)
                return;

            string dir = Path.GetDirectoryName(ABBuildSetting.ResPathCodePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(ABBuildSetting.ResPathCodePath, code, Encoding.UTF8);
            AssetDatabase.ImportAsset(ABBuildSetting.ResPathCodePath);

            if (dirs.Count == 0)
                RevABLog.Warn($"[RevResPath] 资源根目录 \"{resRoot}\" 下没扫到任何文件夹，" +
                                 $"生成的是空常量类。请在打包窗口里确认资源根目录是否正确。");
            else
                RevABLog.Info($"[RevResPath] 已生成 {dirs.Count} 个目录常量 → {ABBuildSetting.ResPathCodePath}");
        }

        /// <summary>菜单入口：不想开打包窗口时也能单独刷新常量</summary>
        [MenuItem("Revolution.Tools/资源/生成资源路径常量 (RevResPath)", false, 3)]
        private static void MenuGenerate() => Generate();
    }

    /// <summary>
    /// 资源根目录下的文件 / 文件夹发生增删改名时，自动重生成 RevResPath。
    ///
    /// 【为什么判断条件和以前不一样了？】
    ///   以前常量来自"AB 标记"，所以要盯着标记变化；
    ///   现在常量来自「目录结构」，和 AB 标记无关 —— 只关心资源根目录下有没有东西变化。
    ///   文件变化也一起管：删掉某目录下最后一个文件之后，目录往往也该跟着清理，
    ///   这样下次回调里重扫一遍，常量表和实际目录结构就不会脱节。
    ///
    /// 【为什么延后一帧？】Generate() 内部会写文件并 ImportAsset，
    ///   在导入回调里直接写文件容易引发重入；用 delayCall 合并到下一帧只跑一次更稳。
    /// </summary>
    public class ABResPathPostprocessor : AssetPostprocessor
    {
        private static bool _pending;

        static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (_pending) return;                       // 同一帧内多次回调只处理一次

            if (TouchesResRoot(imported) || TouchesResRoot(deleted) ||
                TouchesResRoot(moved) || TouchesResRoot(movedFrom))
            {
                _pending = true;
                EditorApplication.delayCall += () =>
                {
                    _pending = false;
                    ABResPathGenerator.Generate();
                };
            }
        }

        /// <summary>这批路径里有没有落在资源根目录下的</summary>
        private static bool TouchesResRoot(string[] paths)
        {
            string resRoot = GetResRootSafe();
            if (resRoot.Length == 0) return false;          // 未配置根目录 → 不做自动重生成

            string root = resRoot.Replace('\\', '/').TrimEnd('/') + "/";

            foreach (string p in paths)
                if (p.Replace('\\', '/').StartsWith(root)) return true;

            return false;
        }

        /// <summary>
        /// 安全读取资源根目录：**只读、不创建配置资产**（导入回调里不该有副作用）。
        /// 配置还没建过 / 使用者还没设置根目录 → 返回空串，此时不触发任何自动重生成。
        /// </summary>
        private static string GetResRootSafe()
        {
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            return cfg != null ? cfg.GetResRoot() : "";
        }
    }
}

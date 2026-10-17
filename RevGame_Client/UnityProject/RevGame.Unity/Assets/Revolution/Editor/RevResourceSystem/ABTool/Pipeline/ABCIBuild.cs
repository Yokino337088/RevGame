// ============================================================
// ABCIBuild.cs —— 无头打包入口（给 CI / 批处理用）
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【为什么需要它？】
//   打包机没有 GUI，不能点窗口按钮，只能靠命令行调用静态方法。Unity 的约定是：
//     Unity.exe -quit -batchmode -projectPath <工程> ^
//               -executeMethod Revolution.Editor.ABCIBuild.Build ^
//               -logFile build.log
//   -executeMethod 后面跟的必须是 public static 无参方法。
//
// 【平台】永远用命令行里 -buildTarget 指定的 / 工程当前的平台（不读窗口里的"目标平台"本机偏好），
//   这样同一条流水线在谁的机器上跑结果都一样。
// ============================================================
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public static class ABCIBuild
    {
        /// <summary>CI 打包入口（public static + 无参，才能被 -executeMethod 调用）</summary>
        public static void Build()
        {
            RevABLog.Info("[RevAB][CI] 开始打包...");

            // ① 收集 AB 标记
            ABCollectResult collect = ABCollector.Collect();

            // ② 校验：CI 上必须严格，有错就失败退出（让流水线红掉）
            ABValidateResult validate = ABValidator.Validate(collect);
            ABValidator.ValidateAndroidShaders(collect, validate, EditorUserBuildSettings.activeBuildTarget);
            if (!validate.CanBuild)
            {
                RevABLog.Error($"[RevAB][CI] 校验失败 {validate.errors.Count} 项");
                foreach (string e in validate.errors) RevABLog.Error(e);

                EditorApplication.Exit(1);      // 非 0 退出码 = 流水线判定失败
                return;
            }

            // ③ 打包
            ABBuildResult build = ABBuilderCore.Build();
            if (!build.success)
            {
                RevABLog.Error("[RevAB][CI] 打包失败");
                EditorApplication.Exit(1);
                return;
            }

            // ④ 依赖分析（有循环依赖直接失败）
            ABDependencyReport dep = ABDependencyAnalyzer.Analyze(build.manifest.raw);
            if (dep.circularBundles.Count > 0)
            {
                RevABLog.Error($"[RevAB][CI] 存在循环依赖：{string.Join(", ", dep.circularBundles)}");
                EditorApplication.Exit(1);
                return;
            }

            // ⑤ 产物：ResMap + 清单 + RevResPath + 拷贝
            ABManifestWriter.WriteResMap(collect);
            ABManifestWriter.WriteBuildManifest(build);
            ABResPathGenerator.Generate();
            ABBuilderCore.CopyToStreamingAssets(build.outputDir);

            AssetDatabase.Refresh();
            RevABLog.Info($"[RevAB][CI] 打包成功：{build.manifest.allBundles.Length} 个包");
            EditorApplication.Exit(0);          // 0 = 成功
        }
    }
}

// ============================================================
// ABMapGenerator.cs —— "生成映射"的唯一实现（打包窗口「仅生成映射」与导表工具共用）
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【为什么抽出来】生成映射不只是写 ResMap.txt，还要：留一份"上次打包"快照、重生成 RevResPath、刷新资源库。
//   以前这几步只写在打包窗口里；导表工具导完新表也要生成映射 —— 两边各写一份，
//   以后谁加一步、另一边就漏一步。所以收拢到这里。
// ============================================================
using UnityEditor;

namespace Revolution.Editor
{
    internal static class ABMapGenerator
    {
        /// <summary>
        /// 收集 → 校验 → 写映射（非交互）。
        /// 返回写入的映射条数；资源校验有错误时**不写**，返回 -1（原因在 validate.errors 里）。
        /// ★ 按目录自动分包模式下，收集会先重新标记全部资源 —— 与「仅生成映射」按钮完全一致。
        /// </summary>
        internal static int Generate(string note, out ABValidateResult validate)
        {
            ABCollectResult collect = ABCollector.Collect();
            validate = ABValidator.Validate(collect);

            if (ABBuildConfig.Instance.markMode == ABMarkMode.AutoByFolder)
                ABMarkerWatcher.RefreshNow("自动分包重新标记");

            if (!validate.CanBuild) return -1;
            return WriteOutputs(collect, note);
        }

        /// <summary>按已收集好的结果写映射及其附带产物，返回映射条数</summary>
        internal static int WriteOutputs(ABCollectResult collect, string note)
        {
            int count = ABManifestWriter.WriteResMap(collect);
            ABLayoutSnapshotStore.WriteLastBuild(collect, note);     // 「快照」页签"与上次打包对比"靠它
            ABResPathGenerator.Generate();
            AssetDatabase.Refresh();

            RevABLog.Info($"[RevAB] 已生成映射：{count} 条 → {ABBuildSetting.MapAssetPath}（{note}）");
            return count;
        }
    }
}

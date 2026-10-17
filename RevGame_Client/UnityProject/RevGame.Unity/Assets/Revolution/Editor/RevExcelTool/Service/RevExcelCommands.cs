// ============================================================
// RevExcelCommands.cs —— 不开窗口的两个入口：菜单"快速导出"、CI 批处理
//
// 位置：Editor\RevExcelTool\Service\
//
// 【快速导出】菜单 Revolution.Tools/配置表/快速导出（用上次的 Excel 源）：
//   策划改完表、回到 Unity 点一下就行，不用每次开窗口。有错 / 要确认的情况才弹框。
//
// 【CI】打包机上：
//   Unity.exe -quit -batchmode -projectPath <工程> ^
//             -executeMethod Revolution.Editor.ExcelTool.RevExcelCI.Export ^
//             -excelSource D:\Tables ^            ← 可重复写多个；不写就用本机设置里的源
//             -logFile export.log
//   有表出错 / 读不了文件 → 退出码 1（流水线变红）；成功 → 0。
//   ★ 输出目录用的是 ProjectSettings 里团队共享的那份设置 —— 和大家在编辑器里导出的位置一致。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Revolution.Editor.ExcelTool
{
    internal static class RevExcelQuickExport
    {
        [MenuItem("Revolution.Tools/配置表/快速导出（用上次的 Excel 源）", false, 21)]
        private static void Run()
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            if (user.sources.Count == 0)
            {
                if (EditorUtility.DisplayDialog("导表工具", "还没选过 Excel 源。先打开导表工具选一次？", "打开导表工具", "取消"))
                    RevExcelToolWindow.Open();
                return;
            }

            RevExcelReadResult read = RevExcelService.Read(user.sources, user.includeSubfolders, true);
            int bad = read.Tables.Count(t => !t.Ignored && !t.IsValid) + read.FileErrors.Count;

            if (read.Tables.Count == 0 || bad > 0)
            {
                // 有问题：交给窗口展示（哪张表、哪一行），比弹一大段文字好读
                RevExcelToolWindow.Open();
                return;
            }

            RevExcelReport report = RevExcelService.Export(read.Tables, false, RevExcelToolWindow.ConfirmRemoval);
            if (report.cancelled) return;

            if (!report.success || report.unmarked.Count > 0 || report.mapFailed)
            {
                RevExcelToolWindow.Open().ShowReport(report);        // 有需要处理的事：打开窗口看结果
                return;
            }

            string summary = report.written.Count == 0
                ? $"已是最新：{report.tableCount} 张表都没有变化"
                : $"导出完成：{report.tableCount} 张表 / {report.rowCount} 条数据，更新 {report.written.Count} 个文件";

            SceneView.lastActiveSceneView?.ShowNotification(new UnityEngine.GUIContent(summary));
            RevExcelLog.Info("[RevExcel] " + summary);
        }

        /// <summary>
        /// ★ 只生成数据文件（不生成代码）：策划只改了 Excel 里的数值、没动表结构时用 ——
        ///   不写任何代码文件，导完不会触发一次十几秒的全量脚本编译。
        /// </summary>
        [MenuItem("Revolution.Tools/配置表/快速导出（仅数据，不生成代码）", false, 22)]
        private static void RunDataOnly()
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            if (user.sources.Count == 0)
            {
                if (EditorUtility.DisplayDialog("导表工具", "还没选过 Excel 源。先打开导表工具选一次？", "打开导表工具", "取消"))
                    RevExcelToolWindow.Open();
                return;
            }

            RevExcelReadResult read = RevExcelService.Read(user.sources, user.includeSubfolders, true);
            int bad = read.Tables.Count(t => !t.Ignored && !t.IsValid) + read.FileErrors.Count;

            if (read.Tables.Count == 0 || bad > 0)
            {
                RevExcelToolWindow.Open();          // 有问题：交给窗口展示
                return;
            }

            // 仅数据模式不写代码：不存在"删掉表的代码"要确认的情况
            RevExcelReport report = RevExcelService.Export(read.Tables, false, null, ExcelExportMode.DataOnly);
            if (report.cancelled) return;

            if (!report.success || report.unmarked.Count > 0 || report.mapFailed)
            {
                RevExcelToolWindow.Open().ShowReport(report);
                return;
            }

            string summary = report.written.Count == 0
                ? $"已是最新：{report.tableCount} 张表的数据都没有变化"
                : $"仅数据导出完成：{report.tableCount} 张表 / {report.rowCount} 条数据，更新 {report.written.Count} 个数据文件（未生成代码）";

            SceneView.lastActiveSceneView?.ShowNotification(new UnityEngine.GUIContent(summary));
            RevExcelLog.Info("[RevExcel] " + summary);
        }
    }

    /// <summary>CI / 批处理入口（-executeMethod Revolution.Editor.ExcelTool.RevExcelCI.Export）</summary>
    public static class RevExcelCI
    {
        public static void Export()
        {
            int code = 1;
            try
            {
                code = Run() ? 0 : 1;
            }
            catch (Exception e)
            {
                RevExcelLog.Error("[RevExcel][CI] 导出异常：" + e);
            }

            if (UnityEngine.Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static bool Run()
        {
            List<string> sources = SourcesFromArgs();
            if (sources.Count == 0) sources = RevExcelUserSettings.instance.sources;

            if (sources.Count == 0)
            {
                RevExcelLog.Error("[RevExcel][CI] 没有 Excel 源：用 -excelSource <文件或文件夹> 指定");
                return false;
            }

            RevExcelReadResult read = RevExcelService.Read(sources, RevExcelUserSettings.instance.includeSubfolders, false);
            RevExcelLog.Info($"[RevExcel][CI] 读取 {read.Files.Count} 个文件，{read.Tables.Count} 张工作表");

            bool ok = read.Files.Count > 0;
            if (!ok) RevExcelLog.Error("[RevExcel][CI] 来源里没有找到任何 .xlsx：" + string.Join("; ", sources));

            foreach (KeyValuePair<string, string> error in read.FileErrors)
            {
                RevExcelLog.Error($"[RevExcel][CI] 读取失败 {error.Key}：{error.Value}");
                ok = false;
            }

            // CI 上严格：任何一张表有错都算失败（编辑器里是"跳过坏表"，流水线上必须让人看到）
            foreach (ExcelTable table in read.Tables.Where(t => !t.Ignored && !t.IsValid))
            {
                foreach (ExcelIssue issue in table.Errors)
                    RevExcelLog.Error($"[RevExcel][CI] [{table.Name}]（{table.SourceFile}）{issue.Message}");
                ok = false;
            }

            if (!ok) return false;

            RevExcelReport report = RevExcelService.Export(read.Tables, false, null);
            foreach (string w in report.warnings) RevExcelLog.Warn("[RevExcel][CI] " + w);
            foreach (string e in report.errors) RevExcelLog.Error("[RevExcel][CI] " + e);
            foreach (string f in report.unmarked) RevExcelLog.Warn("[RevExcel][CI] 数据文件没有 AB 标记（AB 模式读不到）：" + f);
            if (report.mapStatus.Length > 0) (report.mapFailed ? (Action<string>)RevExcelLog.Error : RevExcelLog.Info)("[RevExcel][CI] " + report.mapStatus);

            return report.success && !report.mapFailed;
        }

        /// <summary>读命令行里所有 -excelSource 参数（一个参数里也可以用 ';' 分隔多个）</summary>
        private static List<string> SourcesFromArgs()
        {
            var list = new List<string>();
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "-excelSource", StringComparison.OrdinalIgnoreCase)) continue;
                list.AddRange(args[i + 1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            }

            return list;
        }
    }
}

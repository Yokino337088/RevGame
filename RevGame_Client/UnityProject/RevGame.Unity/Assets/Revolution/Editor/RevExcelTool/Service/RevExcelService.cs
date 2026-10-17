// ============================================================
// RevExcelService.cs —— 导表的 Unity 侧编排：读 Excel → 校验输出位置 → 导出 → 导入 / 标记 / 生成映射
//
// 位置：Editor\RevExcelTool\Service\
//
// 【为什么单独一层】窗口、菜单"快速导出"、CI 三个入口走的是同一套流程，
//   只在"要不要弹框问一句"上不同 —— 流程写在这里，入口只管交互。
//
// 【编辑器版比 WPF 版多做的（都是"导完了却跑不起来"的常见原因）】
//   ① 代码目录体检：生成的代码会编进哪个程序集？那个程序集引用 Revolution.Runtime 了吗？
//      是不是编辑器专用（放 Editor 文件夹里 → 打包后运行时没有这些类）？—— 导出前就拦下；
//   ② 数据目录体检：运行时按逻辑路径 Data/<表名> 从「资源根目录」读，数据必须在 <资源根目录>/Data；
//      默认就跟随资源根目录（RevAB 打包工具里设置的那个），不用手填；
//   ③ AB 标记体检：数据文件没有 AB 标记 → 编辑器直读没问题，AB 模式 / 真机读不到（最难查的一类）；
//   ④ 新增 / 删除了表 → 自动生成资源映射（ResMap.txt），省掉"导完表还要去打包工具点一下"这一步；
//   ⑤ 只写内容真的变了的文件：代码没变就不触发脚本编译。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>一次读取的结果</summary>
    internal sealed class RevExcelReadResult
    {
        public readonly List<string> Files = new List<string>();
        public readonly List<ExcelTable> Tables = new List<ExcelTable>();

        /// <summary>读不出来的文件：路径 → 原因</summary>
        public readonly List<KeyValuePair<string, string>> FileErrors = new List<KeyValuePair<string, string>>();

        /// <summary>读取那一刻的文件指纹（用来发现"Excel 又保存过了"）</summary>
        public string Signature = "";
    }

    /// <summary>一次导出的结果（可序列化：导出后脚本重编译，窗口里的结果面板还在）</summary>
    [Serializable]
    internal sealed class RevExcelReport
    {
        public bool success;
        public bool cancelled;
        public string time = "";

        public int tableCount;
        public int rowCount;
        public int unchangedCount;
        public bool codeChanged;

        /// <summary>本次是"仅生成数据"模式（没碰代码文件；结果面板用它标注说明）</summary>
        public bool dataOnly;

        /// <summary>写了的文件（"新增 / 更新" + 路径）</summary>
        public List<string> written = new List<string>();

        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();

        /// <summary>没有 AB 标记的数据文件（AB 模式 / 真机读不到）</summary>
        public List<string> unmarked = new List<string>();

        /// <summary>数据目录里没有对应表的旧 txt</summary>
        public List<string> orphans = new List<string>();

        /// <summary>资源映射的处理结果（空 = 这次不需要生成）</summary>
        public string mapStatus = "";
        public bool mapFailed;
    }

    /// <summary>一条输出位置体检的结论</summary>
    internal struct RevExcelCheck
    {
        public MessageType Type;          // None = 没问题
        public string Message;

        public bool IsError => Type == MessageType.Error;

        public static RevExcelCheck Ok(string message = null) => new RevExcelCheck { Type = MessageType.None, Message = message };
        public static RevExcelCheck Info(string message) => new RevExcelCheck { Type = MessageType.Info, Message = message };
        public static RevExcelCheck Warn(string message) => new RevExcelCheck { Type = MessageType.Warning, Message = message };
        public static RevExcelCheck Fail(string message) => new RevExcelCheck { Type = MessageType.Error, Message = message };
    }

    internal static class RevExcelService
    {
        public const string DataFolderName = "Data";

        // ============================================================
        // 源
        // ============================================================

        /// <summary>把来源（文件 / 文件夹）展开成 .xlsx 列表（去重、排序；跳过 Excel 的锁文件 ~$xxx.xlsx）</summary>
        public static List<string> CollectFiles(IEnumerable<string> sources, bool includeSubfolders)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in sources ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path = Full(raw.Trim());

                if (File.Exists(path))
                {
                    if (IsExcel(path)) set.Add(path);
                    continue;
                }

                if (!Directory.Exists(path)) continue;

                SearchOption option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string file in Directory.GetFiles(path, "*.xlsx", option))
                    if (IsExcel(file)) set.Add(Full(file));
            }

            return set.ToList();
        }

        public static bool IsExcel(string path)
            => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
               && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

        /// <summary>文件指纹：路径 + 修改时间 + 大小（文件增删、Excel 保存都会让它变）</summary>
        public static string Signature(List<string> files)
        {
            var sb = new StringBuilder();
            foreach (string file in files)
            {
                var info = new FileInfo(file);
                sb.Append(file).Append('|');
                if (info.Exists) sb.Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>读全部文件；单个文件读不了不影响其它文件（原因记进 FileErrors）</summary>
        public static RevExcelReadResult Read(IEnumerable<string> sources, bool includeSubfolders, bool showProgress)
        {
            var result = new RevExcelReadResult();
            result.Files.AddRange(CollectFiles(sources, includeSubfolders));
            result.Signature = Signature(result.Files);

            bool progress = showProgress && result.Files.Count > 3;

            try
            {
                for (int i = 0; i < result.Files.Count; i++)
                {
                    string file = result.Files[i];
                    if (progress) EditorUtility.DisplayProgressBar("导表工具", "读取 " + Path.GetFileName(file), i / (float)result.Files.Count);

                    try
                    {
                        foreach (XlsxSheet sheet in XlsxReader.ReadWorkbook(file))
                            result.Tables.Add(ExcelTableBuilder.Build(sheet, file));
                    }
                    // ★ 任何异常都只算"这个文件读不了"：一个坏工作簿不该让整批读取中断、更不该把窗口画崩
                    catch (Exception e)
                    {
                        result.FileErrors.Add(new KeyValuePair<string, string>(file, e.Message));
                    }
                }
            }
            finally
            {
                if (progress) EditorUtility.ClearProgressBar();
            }

            return result;
        }

        // ============================================================
        // 输出位置
        // ============================================================

        /// <summary>资源根目录（RevAB 打包工具里设置的；没配置返回空串）。只读，不会创建配置资产</summary>
        public static string ResRoot()
        {
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            return cfg == null ? string.Empty : cfg.GetResRoot();
        }

        /// <summary>"跟随资源根目录"时数据该放哪（资源根目录没配置 → 空串）</summary>
        public static string ExpectedDataDir()
        {
            string root = ResRoot();
            return root.Length == 0 ? string.Empty : root + "/" + DataFolderName;
        }

        /// <summary>实际用的数据目录：设置里留空 = 跟随资源根目录</summary>
        public static string DataDir()
        {
            string custom = Clean(RevExcelProjectSettings.instance.dataDir);
            return custom.Length > 0 ? custom : ExpectedDataDir();
        }

        public static ExcelExportOptions Options()
        {
            RevExcelProjectSettings s = RevExcelProjectSettings.instance;
            return new ExcelExportOptions
            {
                StructDir = Clean(s.structDir),
                ContainerDir = Clean(s.containerDir),
                DataDir = DataDir(),
                StructFileName = string.IsNullOrWhiteSpace(s.structFileName) ? RevExcelProjectSettings.DefaultStructFileName : s.structFileName.Trim(),
                ContainerFileName = string.IsNullOrWhiteSpace(s.containerFileName) ? RevExcelProjectSettings.DefaultContainerFileName : s.containerFileName.Trim(),
            };
        }

        private static readonly Dictionary<string, RevExcelCheck> _codeCheckCache = new Dictionary<string, RevExcelCheck>();

        /// <summary>
        /// 代码目录体检：生成的 .cs 会编进哪个程序集、那个程序集能不能用。
        /// ★ 结果按目录缓存（要问 CompilationPipeline，不便宜）；脚本重编译后缓存随域重载自然清空。
        /// </summary>
        public static RevExcelCheck CheckCodeDir(string dir)
        {
            dir = Clean(dir);
            if (dir.Length == 0) return RevExcelCheck.Fail("还没设置代码目录");
            if (!IsUnderAssets(dir)) return RevExcelCheck.Fail("代码目录必须在工程的 Assets/ 下（Unity 才会编译它）");

            if (_codeCheckCache.TryGetValue(dir, out RevExcelCheck cached)) return cached;

            RevExcelCheck result = RevExcelCheck.Ok();
            string assemblyName = Path.GetFileNameWithoutExtension(
                CompilationPipeline.GetAssemblyNameFromScriptPath(dir + "/__RevExcelProbe.cs") ?? string.Empty);

            UnityEditor.Compilation.Assembly assembly = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .FirstOrDefault(a => a.name == assemblyName);

            if (assembly != null)
            {
                bool editorOnly = (assembly.flags & AssemblyFlags.EditorAssembly) != 0;
                bool seesRuntime = assembly.name == "Revolution.Runtime"
                                   || assembly.assemblyReferences.Any(r => r.name == "Revolution.Runtime");

                if (editorOnly)
                    result = RevExcelCheck.Fail($"这个目录属于编辑器专用程序集 {assemblyName}：打包后运行时没有这些表的类。" +
                                                "换一个不在 Editor 文件夹下的目录（推荐 " + RevExcelProjectSettings.DefaultCodeDir + "）");
                else if (!seesRuntime)
                    result = RevExcelCheck.Fail($"程序集 {assemblyName} 没有引用 Revolution.Runtime，生成的代码会编译不过" +
                                                "（在它的 .asmdef 里加上引用，或换到 " + RevExcelProjectSettings.DefaultCodeDir + "）");
                else
                    result = RevExcelCheck.Ok("编进程序集 " + assemblyName);
            }

            _codeCheckCache[dir] = result;
            return result;
        }

        /// <summary>数据目录体检：运行时能不能按 Data/&lt;表名&gt; 找到这些文件</summary>
        public static RevExcelCheck CheckDataDir()
        {
            string expected = ExpectedDataDir();
            string actual = DataDir();

            if (actual.Length == 0)
                return RevExcelCheck.Fail("还没设置「资源根目录」，数据目录没法跟随：到 RevAB 打包工具里设置资源根目录，或在这里手动指定数据目录");

            if (!IsUnderAssets(actual))
                return RevExcelCheck.Fail("数据目录必须在工程的 Assets/ 下（它是资源，要被导入 / 打包）");

            if (expected.Length == 0)
                return RevExcelCheck.Warn("还没设置「资源根目录」：运行时（编辑器直读 / AB）都找不到这些数据 —— 到 RevAB 打包工具里设置");

            if (!SamePath(actual, expected))
                return RevExcelCheck.Warn($"运行时按逻辑路径 Data/<表名> 从资源根目录读：数据要放在 {expected}，放在这里读不到。" +
                                          "（除非你给每张表的容器改了 resourceRoot）");

            return RevExcelCheck.Ok();
        }

        /// <summary>
        /// 数据目录的 AB 包情况（给设置面板显示）：
        /// 返回包名；没标记返回空串；按目录自动分包返回 null（打包时会自动标）。
        /// </summary>
        public static string DataDirBundle(out bool autoByFolder)
        {
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            autoByFolder = cfg != null && cfg.markMode == ABMarkMode.AutoByFolder;
            if (autoByFolder) return null;

            string dir = DataDir();
            if (dir.Length == 0 || !AssetDatabase.IsValidFolder(dir)) return string.Empty;
            return AssetDatabase.GetImplicitAssetBundleName(dir) ?? string.Empty;
        }

        /// <summary>给数据目录（文件夹）标 AB 包：里面现在和以后的 txt 都会继承这个包</summary>
        public static bool MarkDataDir(string bundle)
        {
            string dir = DataDir();
            if (dir.Length == 0) return false;

            if (!AssetDatabase.IsValidFolder(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            int added = ABBundleEditor.AddAssets(bundle, new[] { dir }, out _, out _);
            ABMarkerWatcher.RefreshNow("导表工具标记数据目录");
            return added > 0 || AssetDatabase.GetImplicitAssetBundleName(dir) == bundle;
        }

        // ============================================================
        // 导出
        // ============================================================

        /// <summary>计划（不写任何文件）：界面用它显示"有几处改动待导出"</summary>
        public static ExcelExportPlan Plan(IReadOnlyList<ExcelTable> tables) => ExcelExportPlanner.Plan(tables, Options());

        /// <summary>
        /// 导出。
        /// </summary>
        /// <param name="confirm">
        /// 危险情况（会删掉上次导出过的表的代码）时问一句；返回 false = 取消。null = 不问（CI）。
        /// </param>
        /// <param name="mode">
        /// <see cref="ExcelExportMode.DataOnly"/> = 只写数据 txt、完全不碰代码文件
        /// （只改了 Excel 数值、没动表结构时用：不生成代码 = 不触发全量脚本编译）。
        /// 此时跳过代码目录体检，也不会问"会删掉表的代码"（代码根本不参与）。
        /// </param>
        public static RevExcelReport Export(IReadOnlyList<ExcelTable> tables, bool force,
            Func<ExcelExportPlan, bool> confirm, ExcelExportMode mode = ExcelExportMode.Full)
        {
            var report = new RevExcelReport { time = DateTime.Now.ToString("HH:mm:ss"), dataOnly = mode == ExcelExportMode.DataOnly };
            ExcelExportOptions options = Options();

            // ---------- ① 输出位置：会让生成物编译不过 / 运行时读不到的，先拦下 ----------
            // 仅数据模式不写代码：代码目录体检（程序集 / 引用 / Editor 专用）对它没有意义，跳过
            if (mode == ExcelExportMode.Full)
            {
                foreach (string dir in new[] { options.StructDir, options.ContainerDir })
                {
                    RevExcelCheck code = CheckCodeDir(dir);
                    if (code.IsError) report.errors.Add($"代码目录 {dir}：{code.Message}");
                }
            }

            RevExcelCheck data = CheckDataDir();
            if (data.IsError) report.errors.Add("数据目录：" + data.Message);
            else if (data.Type == MessageType.Warning) report.warnings.Add("数据目录：" + data.Message);

            if (report.errors.Count > 0) return report;

            // ---------- ② 计划 ----------
            ExcelExportPlan plan = ExcelExportPlanner.Plan(tables, options);
            report.warnings.AddRange(plan.Warnings);
            report.orphans.AddRange(plan.OrphanDataFiles);

            if (!plan.CanExport)
            {
                report.errors.AddRange(plan.Errors);
                return report;
            }

            // 仅数据模式不写代码：不存在"删掉表的代码"这回事，不确认也不警告
            if (mode == ExcelExportMode.Full && plan.RemovedTables.Count > 0)
            {
                if (confirm != null && !confirm(plan))
                {
                    report.cancelled = true;
                    return report;
                }

                report.warnings.Add($"本次导出移除了 {plan.RemovedTables.Count} 张表的代码（不在当前 Excel 源里）：" +
                                    string.Join("、", plan.RemovedTables));
            }

            // ---------- ③ 写 + 导入 ----------
            List<ExcelPlannedFile> written = ExcelExportPlanner.Write(plan, force, mode);

            report.tableCount = plan.Tables.Count;
            report.rowCount = plan.RowCount;
            report.unchangedCount = plan.Files.Count - written.Count;

            bool dataSetChanged = false;
            foreach (ExcelPlannedFile f in written)
            {
                report.written.Add((f.Change == ExcelFileChange.New ? "新增  " : "更新  ") + f.Path);
                if (f.Kind != ExcelFileKind.Data) report.codeChanged = true;
                else if (f.Change == ExcelFileChange.New) dataSetChanged = true;
            }

            if (written.Count > 0) AssetDatabase.Refresh();      // 导入新文件；代码变了会在之后触发一次脚本编译

            // ---------- ④ AB 标记 + 资源映射 ----------
            CheckMarks(plan, report);

            bool mapMissing = !File.Exists(ABBuildSetting.MapAssetPath);
            if (RevExcelProjectSettings.instance.generateMapAfterExport && (dataSetChanged || mapMissing))
                GenerateMap(report, "导表后生成映射");

            report.success = true;
            RevExcelLog.Info($"[RevExcel] 导出完成{(report.dataOnly ? "（仅数据，未生成代码）" : "")}：" +
                             $"{report.tableCount} 张表 / {report.rowCount} 条数据，" +
                             $"写入 {written.Count} 个文件，{report.unchangedCount} 个未变" +
                             (report.codeChanged ? "（代码有变化，将重新编译）" : ""));
            return report;
        }

        /// <summary>数据文件有没有 AB 标记（按目录自动分包时打包会自动标，不用查）</summary>
        private static void CheckMarks(ExcelExportPlan plan, RevExcelReport report)
        {
            DataDirBundle(out bool autoByFolder);
            if (autoByFolder) return;

            foreach (ExcelPlannedFile f in plan.Files)
            {
                if (f.Kind != ExcelFileKind.Data) continue;
                if (string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(f.Path))) report.unmarked.Add(f.Path);
            }
        }

        /// <summary>生成资源映射（非交互：资源校验有错误就不写，只在结果里说明）</summary>
        public static void GenerateMap(RevExcelReport report, string note)
        {
            if (ResRoot().Length == 0)
            {
                report.mapStatus = "资源映射没有生成：还没设置资源根目录";
                report.mapFailed = true;
                return;
            }

            int count = ABMapGenerator.Generate(note, out ABValidateResult validate);
            if (count < 0)
            {
                report.mapStatus = $"资源映射没有更新：资源校验有 {validate.errors.Count} 个错误（去 RevAB 打包工具的「检查」页签看）";
                report.mapFailed = true;
                return;
            }

            report.mapStatus = $"资源映射已更新：{count} 条";
            report.mapFailed = false;
        }

        /// <summary>删掉孤儿数据文件（连同 .meta）</summary>
        public static int DeleteOrphans(IEnumerable<string> files)
        {
            int deleted = 0;
            foreach (string file in files)
            {
                string path = file.Replace('\\', '/');
                if (IsUnderAssets(path) ? AssetDatabase.DeleteAsset(path) : TryDelete(path)) deleted++;
            }

            if (deleted > 0) AssetDatabase.Refresh();
            return deleted;
        }

        private static bool TryDelete(string path)
        {
            try { File.Delete(path); return true; }
            catch (IOException) { return false; }
        }

        // ============================================================
        // 小工具
        // ============================================================

        public static string Clean(string path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        public static bool IsUnderAssets(string path)
            => path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal);

        private static bool SamePath(string a, string b)
            => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>相对工程根目录的路径 → 绝对路径（统一用 '/'）</summary>
        public static string Full(string path) => Path.GetFullPath(path).Replace('\\', '/');

        /// <summary>绝对路径 → 工程内的 "Assets/..."；不在工程里返回原样</summary>
        public static string ToProjectPath(string path)
        {
            string full = Full(path);
            string root = Full(Directory.GetCurrentDirectory()) + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        /// <summary>来源的显示名（在工程里就显示相对路径，短）</summary>
        public static string Display(string path) => ToProjectPath(path);
    }

    /// <summary>导表工具的日志出口（tag = "RevExcel"，统一进 RevLog；和 RevABLog 同一个惯例）</summary>
    internal static class RevExcelLog
    {
        internal const string Tag = "RevExcel";

        internal static void Info(string message) => RevLog.Info(message, Tag);
        internal static void Warn(string message) => RevLog.Warn(message, Tag);
        internal static void Error(string message) => RevLog.Error(message, Tag);
    }
}

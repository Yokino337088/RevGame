// ============================================================
// ExcelExportPlanner.cs —— 导出计划：先算清"会写哪些文件、哪些真的变了"，再落盘
//
// 位置：Editor\RevExcelTool\Core\
//
// 【为什么要先"计划"再写】
//   ① 只写真正变了的文件：Unity 里写一次 .cs 就是一次全量脚本编译（十几秒起步）。
//      Excel 只改了一个数值 → 只有那张表的 txt 变了，代码文件内容一字未动，就不该碰它。
//      比较时会忽略文件头里的"生成时间"那一行（否则每次导出都算"变了"）和换行符差异（git 的 autocrlf）。
//   ② 导出前就能告诉使用者"有几处改动待导出"：界面上直接看得见需不需要导。
//   ③ 危险操作先拦下来问一句：
//      · 本次 Excel 源里没有、但上次导出过的表 —— 这次导出会把它们的代码删掉（业务引用会编译不过）；
//      · 数据目录里的"孤儿 txt"（表删了，数据文件还在）。
//
// 【跨表校验（单张表看不出来、合在一起就编译不过的）】
//   · 同名表（两个工作簿里都有 Hero）→ 重复的类型名；
//   · 结构体名撞上别的表的容器名（表 Hero 与表 HeroTable）；
//   · 名字经过"非法字符换下划线"后撞在一起（Hero Skin 与 Hero_Skin）；
//   · 数据文件名只差大小写（Windows / macOS 的文件系统不区分大小写，会互相覆盖）；
//   · 表名占用了生成代码要用到的运行时类型名（RevDataTable、RevDataFieldParser、Serializable…）。
//
// 本文件只用 System.IO，不依赖 Unity：落盘之后的"导入 / 编译 / 标记"由编辑器层负责。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>导出选项（路径可以是相对工程根目录的，如 "Assets/Revolution/Generation"）</summary>
    public sealed class ExcelExportOptions
    {
        public string StructDir;
        public string ContainerDir;
        public string DataDir;
        public string StructFileName = "RevDataStructures.cs";
        public string ContainerFileName = "RevDataTables.cs";
    }

    public enum ExcelFileKind { Structures, Containers, Data }

    public enum ExcelFileChange { New, Changed, Unchanged }

    /// <summary>
    /// 导出模式：
    /// · <see cref="Full"/> —— 代码（数据结构类 + 容器类）+ 数据文件全都要（表结构有改动时用）；
    /// · <see cref="DataOnly"/> —— 只写数据 txt，完全不动代码文件
    ///   （只改了 Excel 里的数值、没动表结构时用：不生成代码 = 不触发一次十几秒的全量脚本编译）。
    /// </summary>
    public enum ExcelExportMode { Full, DataOnly }

    /// <summary>计划要写的一个文件</summary>
    public sealed class ExcelPlannedFile
    {
        public string Path;              // 用 '/' 分隔
        public string Content;
        public ExcelFileKind Kind;
        public ExcelTable Table;         // 数据文件对应的表（代码文件为 null）
        public ExcelFileChange Change;
    }

    /// <summary>一次导出的完整计划</summary>
    public sealed class ExcelExportPlan
    {
        /// <summary>会被导出的表（校验通过的）</summary>
        public readonly List<ExcelTable> Tables = new List<ExcelTable>();

        /// <summary>有错误、会被跳过的表</summary>
        public readonly List<ExcelTable> Skipped = new List<ExcelTable>();

        public readonly List<ExcelPlannedFile> Files = new List<ExcelPlannedFile>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>上次导出过、本次源里没有的表（容器类名）—— 这次导出会把它们的代码删掉</summary>
        public readonly List<string> RemovedTables = new List<string>();

        /// <summary>数据目录里本工具导出过、但已经没有对应表的 txt</summary>
        public readonly List<string> OrphanDataFiles = new List<string>();

        public int RowCount;

        public bool CanExport => Errors.Count == 0 && Tables.Count > 0;

        public int ChangedCount
        {
            get
            {
                int n = 0;
                foreach (ExcelPlannedFile f in Files) if (f.Change != ExcelFileChange.Unchanged) n++;
                return n;
            }
        }

        /// <summary>某张表的数据文件（没有返回 null）</summary>
        public ExcelPlannedFile DataFileOf(ExcelTable table)
        {
            foreach (ExcelPlannedFile f in Files)
                if (f.Kind == ExcelFileKind.Data && ReferenceEquals(f.Table, table)) return f;
            return null;
        }

        public ExcelPlannedFile FileOf(ExcelFileKind kind)
        {
            foreach (ExcelPlannedFile f in Files)
                if (f.Kind == kind) return f;
            return null;
        }
    }

    public static class ExcelExportPlanner
    {
        /// <summary>生成代码里会用到的运行时类型名：表不能叫这些（否则在 namespace Revolution 里把它们遮住，编译不过）</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "RevDataTable", "RevDataTableManager", "RevDataFieldParser", "RevDataTextFormat", "RevIDataTable",
            "RevDataTableLoadException", "Serializable", "SerializableAttribute", "System",
            "RevResManager", "RevTask", "RevResGroup",
        };

        // ★ (?:Rev)? 兼容过渡期：容器基类从 DataTable 改名为 RevDataTable 后，
        //   还没重新导出的旧容器文件里写的仍是 ": DataTable<" —— 两种都要能识别，
        //   否则"上次导出过的表这次没了"的删除提示会漏（FindRemovedTables 读的正是这个文件）。
        private static readonly Regex ContainerPattern =
            new Regex(@"public\s+sealed\s+class\s+(\S+)\s*:\s*(?:Rev)?DataTable<", RegexOptions.Compiled);

        private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ============================================================
        // 计划
        // ============================================================

        public static ExcelExportPlan Plan(IReadOnlyList<ExcelTable> allTables, ExcelExportOptions options)
        {
            var plan = new ExcelExportPlan();

            // ---------- ① 过滤：坏表跳过并点名，其余照常导出（与 WPF 版同一原则） ----------
            foreach (ExcelTable t in allTables)
            {
                if (t.Ignored) continue;
                if (t.IsValid) plan.Tables.Add(t);
                else
                {
                    plan.Skipped.Add(t);
                    plan.Warnings.Add($"表 [{t.Name}]（{t.SourceFile}）有 {t.Errors.Count} 个错误，已跳过导出");
                }
            }

            if (plan.Tables.Count == 0)
            {
                plan.Errors.Add(plan.Skipped.Count > 0
                    ? "所有表都有错误，没有任何可导出的表"
                    : "没有读取到任何表，请先选择 Excel 文件");
                return plan;
            }

            // ---------- ② 跨表校验 ----------
            CheckNames(plan);

            // ---------- ③ 输出目录 ----------
            if (string.IsNullOrWhiteSpace(options.StructDir)) plan.Errors.Add("未指定数据结构类的输出目录");
            if (string.IsNullOrWhiteSpace(options.ContainerDir)) plan.Errors.Add("未指定容器类的输出目录");
            if (string.IsNullOrWhiteSpace(options.DataDir)) plan.Errors.Add("未指定数据文件的输出目录");
            if (plan.Errors.Count > 0) return plan;

            // ---------- ④ 文件 ----------
            string structPath = Join(options.StructDir, options.StructFileName);
            string containerPath = Join(options.ContainerDir, options.ContainerFileName);

            if (string.Equals(structPath, containerPath, StringComparison.OrdinalIgnoreCase))
            {
                plan.Errors.Add("数据结构类与容器类的输出文件是同一个，请换个文件名");
                return plan;
            }

            Add(plan, structPath, ExcelFileKind.Structures, null, ExcelCodeGenerator.GenerateDataStructures(plan.Tables));
            Add(plan, containerPath, ExcelFileKind.Containers, null, ExcelCodeGenerator.GenerateContainers(plan.Tables));

            foreach (ExcelTable t in plan.Tables)
            {
                Add(plan, Join(options.DataDir, t.DataFileName()), ExcelFileKind.Data, t, ExcelDataWriter.Generate(t));
                plan.RowCount += t.Rows.Count;

                foreach (ExcelIssue w in t.Warnings)
                    plan.Warnings.Add($"[{t.Name}] {w.Message}");
            }

            // ---------- ⑤ 会被删掉的表 / 孤儿数据文件 ----------
            FindRemovedTables(plan, containerPath);
            FindOrphans(plan, options.DataDir);

            return plan;
        }

        private static void CheckNames(ExcelExportPlan plan)
        {
            var structNames = new Dictionary<string, ExcelTable>(StringComparer.Ordinal);
            var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);       // 生成的类型名 → 来源说明
            var dataFiles = new Dictionary<string, ExcelTable>(StringComparer.OrdinalIgnoreCase);

            foreach (ExcelTable t in plan.Tables)
            {
                string where = $"[{t.Name}]（{t.SourceFile}）";

                if (structNames.TryGetValue(t.StructName(), out ExcelTable same))
                {
                    plan.Errors.Add($"同名表 [{t.StructName()}]：{same.SourceFile} 与 {t.SourceFile} 里都有，会生成重复的类名");
                    continue;
                }
                structNames[t.StructName()] = t;

                string structId = ExcelCodeGenerator.Identifier(t.StructName());
                string containerId = ExcelCodeGenerator.Identifier(t.ContainerName());

                if (Reserved.Contains(structId))
                    plan.Errors.Add($"表名 {where} 与框架运行时的类型名 {structId} 相同，生成的代码会编译不过，请改工作表名");

                foreach (string id in new[] { structId, containerId })
                {
                    if (identifiers.TryGetValue(id, out string owner))
                        plan.Errors.Add($"表 {where} 生成的类型名 {id} 与 {owner} 冲突，请改工作表名");
                    else
                        identifiers[id] = $"表 {where} 的{(id == structId ? "结构体" : "容器类")}";
                }

                if (dataFiles.TryGetValue(t.DataFileName(), out ExcelTable clash))
                    plan.Errors.Add($"表 {where} 与 [{clash.Name}] 的数据文件名只差大小写（{t.DataFileName()}），" +
                                    "Windows / macOS 上会互相覆盖，请改工作表名");
                else
                    dataFiles[t.DataFileName()] = t;
            }
        }

        private static void Add(ExcelExportPlan plan, string path, ExcelFileKind kind, ExcelTable table, string content)
        {
            plan.Files.Add(new ExcelPlannedFile
            {
                Path = path,
                Content = content,
                Kind = kind,
                Table = table,
                Change = Compare(path, content, kind != ExcelFileKind.Data),
            });
        }

        private static ExcelFileChange Compare(string path, string content, bool isCode)
        {
            if (!File.Exists(path)) return ExcelFileChange.New;

            try
            {
                string old = File.ReadAllText(path);
                return Normalize(old, isCode) == Normalize(content, isCode) ? ExcelFileChange.Unchanged : ExcelFileChange.Changed;
            }
            catch (IOException)
            {
                return ExcelFileChange.Changed;          // 读不了就当变了：宁可多写一次
            }
        }

        /// <summary>比较口径：统一换行符；代码文件去掉"生成时间"那一行</summary>
        private static string Normalize(string text, bool isCode)
        {
            text = text.Replace("\r\n", "\n");
            if (!isCode) return text;

            var sb = new StringBuilder(text.Length);
            foreach (string line in text.Split('\n'))
                if (line.IndexOf(ExcelCodeGenerator.StampMarker, StringComparison.Ordinal) < 0)
                    sb.Append(line).Append('\n');
            return sb.ToString();
        }

        private static void FindRemovedTables(ExcelExportPlan plan, string containerPath)
        {
            if (!File.Exists(containerPath)) return;

            var now = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExcelTable t in plan.Tables) now.Add(ExcelCodeGenerator.Identifier(t.ContainerName()));

            // 有错被跳过的表，本次也会从代码里消失 —— 同样要算进来
            string old;
            try { old = File.ReadAllText(containerPath); }
            catch (IOException) { return; }

            foreach (Match m in ContainerPattern.Matches(old))
            {
                string name = m.Groups[1].Value;
                if (!now.Contains(name) && !plan.RemovedTables.Contains(name)) plan.RemovedTables.Add(name);
            }
        }

        private static void FindOrphans(ExcelExportPlan plan, string dataDir)
        {
            if (!Directory.Exists(dataDir)) return;

            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExcelPlannedFile f in plan.Files)
                if (f.Kind == ExcelFileKind.Data) planned.Add(Path.GetFileName(f.Path));

            foreach (string file in Directory.GetFiles(dataDir, "*.txt", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (planned.Contains(name)) continue;

                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }

                if (ExcelDataWriter.LooksGenerated(text, Path.GetFileNameWithoutExtension(file)))
                    plan.OrphanDataFiles.Add(file.Replace('\\', '/'));
            }

            plan.OrphanDataFiles.Sort(StringComparer.OrdinalIgnoreCase);
        }

        // ============================================================
        // 落盘
        // ============================================================

        /// <summary>
        /// 写文件：默认只写"新增 / 变了"的，force = true 时全部重写。返回实际写了的文件。
        /// <paramref name="mode"/> = <see cref="ExcelExportMode.DataOnly"/> 时只写数据 txt，
        /// 代码文件一个都不碰（表结构没改时用它：不碰代码 = 不触发全量脚本编译）。
        /// 编码与 WPF 版一致：代码 UTF-8 带 BOM（IDE 靠它认中文注释）；数据 UTF-8 不带 BOM
        /// （BOM 会让第一行的 "#" 不再是第一个字符，注释行失效）。
        /// </summary>
        public static List<ExcelPlannedFile> Write(ExcelExportPlan plan, bool force,
            ExcelExportMode mode = ExcelExportMode.Full)
        {
            var written = new List<ExcelPlannedFile>();
            if (!plan.CanExport) return written;

            foreach (ExcelPlannedFile f in plan.Files)
            {
                if (mode == ExcelExportMode.DataOnly && f.Kind != ExcelFileKind.Data) continue;
                if (!force && f.Change == ExcelFileChange.Unchanged) continue;

                string dir = Path.GetDirectoryName(f.Path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(f.Path, f.Content, f.Kind == ExcelFileKind.Data ? Utf8NoBom : Utf8Bom);
                written.Add(f);
            }

            return written;
        }

        private static string Join(string dir, string file)
            => (dir ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/" + file;
    }
}

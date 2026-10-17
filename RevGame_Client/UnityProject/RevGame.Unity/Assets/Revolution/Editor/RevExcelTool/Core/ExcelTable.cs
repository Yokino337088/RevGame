// ============================================================
// ExcelTable.cs —— Excel 表模型与配置规则校验
//
// 位置：Editor\RevExcelTool\Core\
//
// 【Excel 配置规则（与 WPF 版完全相同 —— 这是工具和策划之间的唯一契约）】
//   第 1 行：字段名
//   第 2 行：字段类型（int / float / string / bool）
//   第 3 行：字段描述（只生成注释，不参与数据）
//   第 4 行起：真正的数据
//   第一个字段是主键；列名 / 行首以 # 开头 = 备注，整列 / 整行忽略
//
// 【编辑器版多拦的几类问题（都是"生成出来编译不过"的）】
//   · 两个字段名大小写不同、却生成同一个 C# 名（hp / HP → 都是 Hp）；
//   · 字段名与表名相同（表 Item 里有字段 Item → 成员不能与类型同名，CS0542）；
//   · bool 列填了认不出的值（"x"、"对"）→ 运行时会静默当成 false，这里先提醒。
//   ★ 为什么编辑器版要格外在意"编译不过"：生成的代码在工程里，一旦编译失败，
//     整个工程的脚本都停在上一次成功的版本，连 Play 都进不去。
//
// 【编辑器版多忽略的两类工作表】
//   · 完全空白的工作表（新建工作簿自带的 Sheet2 / Sheet3）；
//   · 名字以 # 开头的工作表（和"# 开头的列 / 行是备注"同一个约定：放说明、草稿）。
//   WPF 版会把它们当成"有错的表"标红再跳过 —— 结果一样（都不导出），只是少了噪声。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>字段类型（框架只支持这四种，与 RevDataFieldParser 一一对应）</summary>
    public enum FieldType
    {
        Int,
        Float,
        String,
        Bool
    }

    /// <summary>一个字段（= Excel 的一列）</summary>
    public sealed class ExcelField
    {
        public int Column;          // 在 Excel 里的列号（0 基）
        public string Name;         // 第 1 行
        public FieldType Type;      // 第 2 行（解析后的）
        public string TypeText;     // 第 2 行（原始文本，报错时给人看）
        public string Desc;         // 第 3 行
        public bool IsKey;          // 是否主键

        /// <summary>Excel 里的列字母（"C"），报错 / 界面显示用</summary>
        public string ColumnName => XlsxReader.ColumnName(Column);

        /// <summary>生成 C# 字段名：首字母大写、"Id" 而不是 "ID"（跟 .NET 命名习惯保持一致）</summary>
        public string CSharpName()
        {
            string n = (Name ?? string.Empty).Trim();
            if (n.Length == 0) return "Field";

            // 全大写的短名（ID、HP）转成 Id、Hp，避免生成 ID 这种不符合约定的名字
            if (n.Length <= 3 && n == n.ToUpperInvariant())
                return char.ToUpperInvariant(n[0]) + n.Substring(1).ToLowerInvariant();

            return char.ToUpperInvariant(n[0]) + n.Substring(1);
        }

        /// <summary>生成用的 C# 类型名</summary>
        public string CSharpType()
        {
            switch (Type)
            {
                case FieldType.Int: return "int";
                case FieldType.Float: return "float";
                case FieldType.Bool: return "bool";
                default: return "string";
            }
        }

        /// <summary>生成的取值表达式（对应 RevDataFieldParser 的强类型方法）</summary>
        public string ParseExpression(string cellExpr)
        {
            switch (Type)
            {
                case FieldType.Int: return "RevDataFieldParser.ToInt(" + cellExpr + ")";
                case FieldType.Float: return "RevDataFieldParser.ToFloat(" + cellExpr + ")";
                case FieldType.Bool: return "RevDataFieldParser.ToBool(" + cellExpr + ")";
                default: return "RevDataFieldParser.ToStr(" + cellExpr + ")";
            }
        }

        /// <summary>
        /// 这个值在运行时能不能按本字段类型解析（空值 = 取默认值，算合法）。
        /// 口径与运行时 RevDataFieldParser 完全一致：界面预览标红、校验报错用的是同一个判断。
        /// </summary>
        public bool Accepts(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return true;

            switch (Type)
            {
                case FieldType.Int: return int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                case FieldType.Float: return float.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                case FieldType.Bool: return IsKnownBool(cell);
                default: return true;
            }
        }

        /// <summary>RevDataFieldParser.ToBool 认得的写法（其余值运行时一律当 false）</summary>
        public static bool IsKnownBool(string cell)
        {
            switch (cell.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "是": case "yes": case "y":
                case "false": case "0": case "否": case "no": case "n":
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>一条数据行</summary>
    public sealed class ExcelRow
    {
        public int ExcelRowNumber;      // 原始行号（1 基，报错时直接指给策划看）
        public string[] Cells;          // 与 Fields 一一对应的值

        public string Get(int fieldIndex)
            => (Cells != null && fieldIndex >= 0 && fieldIndex < Cells.Length) ? (Cells[fieldIndex] ?? string.Empty) : string.Empty;
    }

    /// <summary>一条问题（带 Excel 行号时，界面可以直接跳到那一行）</summary>
    public struct ExcelIssue
    {
        public string Message;

        /// <summary>Excel 行号（1 基）；0 = 不针对某一行</summary>
        public int Row;

        public override string ToString() => Message;
    }

    /// <summary>一张表（= 一个工作表）</summary>
    public sealed class ExcelTable
    {
        public string Name;
        public string SourceFile;          // 文件名（显示用）
        public string SourcePath;          // 完整路径（"在 Excel 中打开"用）

        public readonly List<ExcelField> Fields = new List<ExcelField>();
        public readonly List<ExcelRow> Rows = new List<ExcelRow>();
        public readonly List<ExcelIssue> Errors = new List<ExcelIssue>();
        public readonly List<ExcelIssue> Warnings = new List<ExcelIssue>();

        /// <summary>不参与导表的工作表（空白 / # 开头），不算错误；<see cref="IgnoreReason"/> 说明原因</summary>
        public bool Ignored;
        public string IgnoreReason;

        public bool IsValid => !Ignored && Errors.Count == 0;

        public ExcelField KeyField
        {
            get
            {
                foreach (ExcelField f in Fields)
                    if (f.IsKey) return f;
                return null;
            }
        }

        /// <summary>数据结构名：表名首字母大写（HeroSkin 表 → HeroSkin 结构体）</summary>
        public string StructName()
        {
            string n = (Name ?? "Table").Trim();
            if (n.Length == 0) n = "Table";
            return char.ToUpperInvariant(n[0]) + n.Substring(1);
        }

        /// <summary>容器类名：HeroSkin → HeroSkinTable</summary>
        public string ContainerName() => StructName() + "Table";

        /// <summary>TXT 数据文件名</summary>
        public string DataFileName() => StructName() + ".txt";

        internal void Error(string message, int row = 0) => Errors.Add(new ExcelIssue { Message = message, Row = row });
        internal void Warn(string message, int row = 0) => Warnings.Add(new ExcelIssue { Message = message, Row = row });
    }

    /// <summary>把工作表按配置规则解析成表模型，并完成全部校验</summary>
    public static class ExcelTableBuilder
    {
        private const int RowFieldName = 0;      // 第 1 行：字段名
        private const int RowFieldType = 1;      // 第 2 行：类型
        private const int RowFieldDesc = 2;      // 第 3 行：描述
        private const int RowDataStart = 3;      // 第 4 行起：数据

        /// <summary>同一类问题最多报几条（一整列填错时，报 500 条一模一样的错只会把真正的线索淹掉）</summary>
        private const int MaxIssuesPerField = 20;

        private static readonly Dictionary<string, FieldType> TypeMap = new Dictionary<string, FieldType>
        {
            { "int",    FieldType.Int    },
            { "float",  FieldType.Float  },
            { "string", FieldType.String },
            { "bool",   FieldType.Bool   }
        };

        public static ExcelTable Build(XlsxSheet sheet, string sourcePath)
        {
            var table = new ExcelTable
            {
                Name = sheet.Name?.Trim() ?? "Table",
                SourcePath = sourcePath,
                SourceFile = System.IO.Path.GetFileName(sourcePath)
            };

            // ---------- ⓪ 不参与导表的工作表 ----------
            if (table.Name.StartsWith("#", StringComparison.Ordinal))
            {
                table.Ignored = true;
                table.IgnoreReason = "工作表名以 # 开头（备注页），不导出";
                return table;
            }

            if (sheet.IsBlank)
            {
                table.Ignored = true;
                table.IgnoreReason = "空白工作表，不导出";
                return table;
            }

            if (sheet.Rows.Count <= RowFieldDesc)
            {
                table.Error($"表 [{table.Name}] 行数不足 3 行，至少要有字段名、类型、描述三行");
                return table;
            }

            // ---------- ① 字段（列）----------
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var usedCSharp = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int col = 0; col < sheet.ColumnCount; col++)
            {
                string name = sheet.Get(RowFieldName, col).Trim();
                if (name.Length == 0) continue;                  // 空列名的列直接忽略（Excel 里常有）
                if (name.StartsWith("#", StringComparison.Ordinal)) continue;   // 以 # 开头视为策划备注列

                string typeText = sheet.Get(RowFieldType, col).Trim();
                var field = new ExcelField
                {
                    Column = col,
                    Name = name,
                    TypeText = typeText,
                    Desc = sheet.Get(RowFieldDesc, col).Trim()
                };

                string where = $"{field.ColumnName} 列";

                if (!TypeMap.TryGetValue(typeText.ToLowerInvariant(), out FieldType type))
                    table.Error($"{where} 字段 [{name}] 的类型 \"{typeText}\" 不支持（只支持 int / float / string / bool）", RowFieldType + 1);
                else
                    field.Type = type;

                if (!usedNames.Add(name))
                    table.Error($"{where} 字段名 [{name}] 重复", RowFieldName + 1);

                // 字段名要能直接当 C# 字段名用（汉字是合法标识符，空格和标点不是）
                if (!IsValidIdentifier(name))
                    table.Error($"{where} 字段名 [{name}] 不能作为 C# 标识符（不能有空格/标点，不能以数字开头）", RowFieldName + 1);

                // 大小写不同、生成的 C# 名却一样（hp / HP → Hp）→ 两个同名成员，编译不过
                string csharp = field.CSharpName();
                if (usedCSharp.TryGetValue(csharp, out string owner) && owner != name)
                    table.Error($"{where} 字段 [{name}] 与 [{owner}] 生成的 C# 字段名都是 {csharp}，请改名", RowFieldName + 1);
                else
                    usedCSharp[csharp] = name;

                // 成员不能与所在类型同名（表 Item 里的字段 Item → CS0542）
                if (csharp == table.StructName())
                    table.Error($"{where} 字段 [{name}] 与表名 [{table.Name}] 相同：生成的结构体成员不能与类型同名，请改名", RowFieldName + 1);

                table.Fields.Add(field);
            }

            if (table.Fields.Count == 0)
            {
                table.Error($"表 [{table.Name}] 一个有效字段都没有（第 1 行是字段名，空列名的列会被忽略）", RowFieldName + 1);
                return table;
            }

            // ---------- ② 主键：默认第一个字段 ----------
            ExcelField keyField = table.Fields[0];
            keyField.IsKey = true;

            // 主键类型体检：int/string 是常规选择，float/bool 基本都配错了 —— 提前提醒，别等运行时查不到
            if (keyField.Type == FieldType.Float)
                table.Warn($"表 [{table.Name}] 用 float 做主键不推荐：浮点比较有精度风险，" +
                           "会出现「表里明明有、却查不到」的怪问题，建议改用 int 或 string");

            if (keyField.Type == FieldType.Bool)
                table.Warn($"表 [{table.Name}] 用 bool 做主键：bool 只有 true/false 两个取值，" +
                           "整张表最多两行数据，请确认是否配错");

            // ---------- ③ 数据行 ----------
            var keyValues = new HashSet<string>(StringComparer.Ordinal);
            var badCount = new int[table.Fields.Count];

            for (int r = RowDataStart; r < sheet.Rows.Count; r++)
            {
                if (sheet.IsRowEmpty(r)) continue;                                                   // 整行空
                if (sheet.Get(r, keyField.Column).Trim().StartsWith("#", StringComparison.Ordinal)) continue;  // 注释行

                var cells = new string[table.Fields.Count];
                for (int f = 0; f < table.Fields.Count; f++)
                    cells[f] = sheet.Get(r, table.Fields[f].Column).Trim();

                int excelRow = r + 1;                                  // 1 基，和 Excel 左侧行号一致

                if (cells[0].Length == 0)
                {
                    table.Error($"第 {excelRow} 行：主键 [{keyField.Name}] 为空", excelRow);
                    continue;
                }

                if (!keyValues.Add(cells[0]))
                {
                    table.Error($"第 {excelRow} 行：主键 [{keyField.Name}] = {cells[0]} 重复", excelRow);
                    continue;
                }

                for (int f = 0; f < table.Fields.Count; f++)
                {
                    ExcelField field = table.Fields[f];
                    string cell = cells[f];
                    if (field.Accepts(cell)) continue;

                    if (++badCount[f] > MaxIssuesPerField) continue;

                    string text = $"第 {excelRow} 行 [{field.Name}]（{field.ColumnName}{excelRow}）：\"{cell}\" ";

                    // 数值写错 = 运行时会取成 0，必须修；bool 认不出 = 运行时当 false，多半也是笔误 → 提醒
                    if (field.Type == FieldType.Bool)
                        table.Warn(text + "不是可识别的 bool（true/false、1/0、是/否、yes/no），运行时按 false 处理", excelRow);
                    else
                        table.Error(text + $"不是合法的 {field.TypeText.ToLowerInvariant()}", excelRow);
                }

                table.Rows.Add(new ExcelRow { ExcelRowNumber = excelRow, Cells = cells });
            }

            for (int f = 0; f < badCount.Length; f++)
                if (badCount[f] > MaxIssuesPerField)
                    table.Warn($"[{table.Fields[f].Name}] 列还有 {badCount[f] - MaxIssuesPerField} 处同类问题没有逐条列出");

            if (table.Rows.Count == 0)
                table.Warn($"表 [{table.Name}] 没有任何数据行");

            return table;
        }

        /// <summary>
        /// 是否是合法的 C# 标识符。
        /// 【注意】不能只允许 ASCII —— C# 允许 Unicode 字母，汉字字段名同样合法，
        /// 用 char.IsLetter 判断才能既不误杀汉字、又能拦住"英雄 id"这种带空格的写法。
        /// </summary>
        public static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (char.IsDigit(name[0])) return false;

            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;

            return true;
        }
    }
}

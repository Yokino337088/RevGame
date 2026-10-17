// ============================================================
// ExcelCodeGenerator.cs —— 生成 C# 代码（数据结构类 + 容器类）
//
// 位置：Editor\RevExcelTool\Core\
//
// 【生成什么】
//   ① 数据结构类：struct，一条数据一个实例（值类型 → 零 GC，见 RevDataTable 注释）
//   ② 容器类    ：继承 RevDataTable&lt;主键, 数据结构&gt;，按主键建字典索引
//
// 【★ 输出与 WPF 版逐字节一致（除生成时间外）】
//   同一个团队里可能有人用 WPF 版、有人用编辑器版 —— 两边生成的文件必须一模一样，
//   否则每换一个人导表，版本库里就多一份毫无意义的 diff。所以文件头也保持 "Revolution.ExcelTool"。
//   改这里的格式，请同步改 Revolution.ExcelTool/Core/CodeGenerator.cs。
//
// 【为什么生成的容器这么短？】
//   查询能力（FindByKey / Count / GetByIndex / 遍历…）全在运行时基类 RevDataTable 里，
//   每张表只需要提供两件事：主键怎么取（GetKey）、一行怎么解析（ParseRow）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution.Editor.ExcelTool
{
    public static class ExcelCodeGenerator
    {
        /// <summary>文件头里"生成时间"那一行的标记：比较"内容有没有变"时要跳过这一行</summary>
        public const string StampMarker = "生成时间：";

        /// <summary>生成数据结构类文件（所有表的结构体集中在一个文件）</summary>
        public static string GenerateDataStructures(IEnumerable<ExcelTable> tables)
        {
            var sb = new StringBuilder();
            WriteHeader(sb, "数据结构类");
            sb.Append("using System;\n\n");
            sb.Append("namespace Revolution\n{\n");

            bool first = true;
            foreach (ExcelTable table in tables)
            {
                if (!table.IsValid) continue;

                if (!first) sb.Append('\n');
                first = false;

                string structName = table.StructName();

                sb.Append("    /// <summary>").Append(Xml(structName))
                  .Append(" —— 配置表的一条数据（对应 Excel 一行；struct：值类型，零 GC）</summary>\n");
                sb.Append("    [Serializable]\n");
                sb.Append("    public struct ").Append(Identifier(structName)).Append("\n    {\n");

                foreach (ExcelField field in table.Fields)
                {
                    // 描述与"★ 主键"合成一条注释（否则会出现两条紧挨着的 <summary>，读起来很怪）
                    string desc = field.Desc.Length > 0 ? Xml(OneLine(field.Desc)) : string.Empty;
                    if (field.IsKey)
                        desc = desc.Length > 0 ? desc + "（★ 主键）" : "★ 主键";

                    if (desc.Length > 0)
                        sb.Append("        /// <summary>").Append(desc).Append("</summary>\n");

                    sb.Append("        public ").Append(field.CSharpType()).Append(' ')
                      .Append(Identifier(field.CSharpName())).Append(";\n\n");
                }

                // 去掉最后一个多余空行
                if (sb.Length > 1) sb.Length -= 1;
                sb.Append("    }\n");
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>生成容器类文件（所有表的容器集中在一个文件）</summary>
        public static string GenerateContainers(IEnumerable<ExcelTable> tables)
        {
            var sb = new StringBuilder();
            WriteHeader(sb, "容器类");
            sb.Append("namespace Revolution\n{\n");

            bool first = true;
            foreach (ExcelTable table in tables)
            {
                if (!table.IsValid) continue;

                ExcelField key = table.KeyField;
                if (key == null) continue;

                if (!first) sb.Append('\n');
                first = false;

                string structName = table.StructName();
                string containerName = table.ContainerName();

                sb.Append("    /// <summary>").Append(Xml(table.Name))
                  .Append(" 表的容器（主键：").Append(Xml(key.Name))
                  .Append("，").Append(table.Fields.Count).Append(" 个字段，")
                  .Append(table.Rows.Count).Append(" 条数据）</summary>\n");

                sb.Append("    public sealed class ").Append(Identifier(containerName))
                  .Append(" : RevDataTable<").Append(key.CSharpType()).Append(", ").Append(Identifier(structName)).Append(">\n    {\n");

                sb.Append("        /// <summary>便捷访问；未加载时为 null（先 RevDataTableManager.LoadAsync&lt;")
                  .Append(Identifier(containerName)).Append("&gt;()）</summary>\n");
                sb.Append("        public static ").Append(Identifier(containerName))
                  .Append(" Instance => RevDataTableManager.Get<").Append(Identifier(containerName)).Append(">();\n\n");

                sb.Append("        /// <summary>表名 = ").Append(Xml(table.Name))
                  .Append("；数据文件默认取逻辑路径 \"Data/").Append(Xml(structName)).Append("\"</summary>\n");
                sb.Append("        public ").Append(Identifier(containerName))
                  .Append("() : base(\"").Append(Escape(table.StructName())).Append("\") { }\n\n");

                sb.Append("        /// <summary>主键取值</summary>\n");
                sb.Append("        protected override ").Append(key.CSharpType())
                  .Append(" GetKey(in ").Append(Identifier(structName)).Append(" data) => data.")
                  .Append(Identifier(key.CSharpName())).Append(";\n\n");

                sb.Append("        /// <summary>一行文本 → 一条数据（生成代码，零反射、零装箱）</summary>\n");
                sb.Append("        protected override bool ParseRow(string[] cells, out ").Append(Identifier(structName)).Append(" data)\n");
                sb.Append("        {\n");
                sb.Append("            data = default;\n\n");
                sb.Append("            // 列数比结构少 → 数据文件与结构不匹配，按坏行处理\n");
                sb.Append("            if (cells.Length < ").Append(table.Fields.Count).Append(") return false;\n\n");

                for (int i = 0; i < table.Fields.Count; i++)
                {
                    ExcelField field = table.Fields[i];

                    // 先把语句拼完整再统一补空格 —— 按"整行"对齐，行尾注释才会竖着排齐
                    string statement = "data." + Identifier(field.CSharpName())
                                     + " = " + field.ParseExpression("cells[" + i + "]") + ";";

                    sb.Append("            ").Append(Pad(statement, 58));

                    if (field.Desc.Length > 0)
                        sb.Append(" // ").Append(OneLine(field.Desc));

                    sb.Append('\n');
                }

                sb.Append("\n            return true;\n");
                sb.Append("        }\n");
                sb.Append("    }\n");
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        // ==================== 小工具 ====================

        private static void WriteHeader(StringBuilder sb, string kind)
        {
            sb.Append("//------------------------------------------------------------------------------\n");
            sb.Append("// <auto-generated>\n");
            sb.Append("//     本文件由 Revolution.ExcelTool 自动生成（").Append(kind).Append("），请勿手动修改。\n");
            sb.Append("//     重新导表会整体覆盖。").Append(StampMarker).Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("// </auto-generated>\n");
            sb.Append("//------------------------------------------------------------------------------\n");
        }

        /// <summary>补空格对齐（只为了让生成出来的代码易读）</summary>
        private static string Pad(string text, int width)
            => text.Length >= width ? text : text + new string(' ', width - text.Length);

        /// <summary>描述文本压成一行（XML 注释里不能出现换行）</summary>
        private static string OneLine(string text)
            => text == null ? string.Empty : text.Replace("\r", " ").Replace("\n", " ").Trim();

        /// <summary>转义 XML 注释里的特殊字符，避免生成出非法注释</summary>
        private static string Xml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Escape(string text)
            => text == null ? string.Empty : text.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>把不能直接当标识符的字符换成下划线（校验已经拦过一次，这里是最后兜底）</summary>
        public static string Identifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}

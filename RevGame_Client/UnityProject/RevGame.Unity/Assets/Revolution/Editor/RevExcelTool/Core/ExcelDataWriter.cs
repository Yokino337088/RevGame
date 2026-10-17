// ============================================================
// ExcelDataWriter.cs —— 生成 TXT 数据文件（与 WPF 版逐字节一致）
//
// 位置：Editor\RevExcelTool\Core\
//
// 【文件长什么样】
//   #HeroSkin                      ← 表名（注释行）
//   #ID	HeroName	Quality        ← 字段名（注释行，方便肉眼核对 / git diff）
//   #英雄ID	英雄名	品质          ← 字段描述（注释行）
//   1001	亚瑟	3                  ← 数据（一行一条）
//
// 【拼接与转义交给 RevDataTextFormat】
//   它就是运行时读数据用的那一份实现（Runtime\RevDataLoad\Core\RevDataTextFormat.cs）——
//   编辑器程序集直接引用运行时程序集，"写出来的东西运行时一定读得懂"。
// ============================================================
using System.Collections.Generic;
using System.Text;

namespace Revolution.Editor.ExcelTool
{
    public static class ExcelDataWriter
    {
        public static string Generate(ExcelTable table)
        {
            var sb = new StringBuilder();

            // ① 表名
            sb.Append(RevDataTextFormat.Comment(table.Name)).Append(RevDataTextFormat.LineSeparator);

            // ② 字段名 / 描述（原始名，方便和 Excel 对照）
            var names = new List<string>(table.Fields.Count);
            var descs = new List<string>(table.Fields.Count);

            foreach (ExcelField field in table.Fields)
            {
                names.Add(field.Name);
                descs.Add(field.Desc);
            }

            string separator = RevDataTextFormat.FieldSeparator.ToString();
            sb.Append(RevDataTextFormat.Comment(string.Join(separator, names))).Append(RevDataTextFormat.LineSeparator);
            sb.Append(RevDataTextFormat.Comment(string.Join(separator, descs))).Append(RevDataTextFormat.LineSeparator);

            // ③ 数据行（顺序 = Fields 顺序，与生成的 ParseRow 一一对应）
            foreach (ExcelRow row in table.Rows)
                sb.Append(RevDataTextFormat.JoinFields(row.Cells)).Append(RevDataTextFormat.LineSeparator);

            return sb.ToString();
        }

        /// <summary>
        /// 这个 txt 看起来是不是本工具导出的（前三行都是注释、第一行是表名）。
        /// 用来找"孤儿数据文件"：表从 Excel 里删掉了，数据文件还留在工程里。
        /// </summary>
        public static bool LooksGenerated(string text, string fileNameWithoutExtension)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string[] lines = text.Split(RevDataTextFormat.LineSeparator);
            if (lines.Length < 3) return false;

            for (int i = 0; i < 3; i++)
                if (!RevDataTextFormat.TrimLineEnd(lines[i]).StartsWith(RevDataTextFormat.CommentPrefix.ToString())) return false;

            // 文件名 = 表名首字母大写，所以只比较"忽略大小写"
            string title = RevDataTextFormat.TrimLineEnd(lines[0]).Substring(1).Trim();
            return string.Equals(title, fileNameWithoutExtension, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}

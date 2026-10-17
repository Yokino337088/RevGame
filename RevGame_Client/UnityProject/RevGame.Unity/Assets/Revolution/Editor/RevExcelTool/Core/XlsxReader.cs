// ============================================================
// XlsxReader.cs —— 极简 xlsx 读取器（零第三方依赖；与 WPF 版读取口径一致）
//
// 位置：Editor\RevExcelTool\Core\
//
// 【xlsx 的结构（只关心这四样）】
//   xl/workbook.xml              工作表清单（名字 + r:id）
//   xl/_rels/workbook.xml.rels   r:id → 工作表 XML 的真实路径
//   xl/sharedStrings.xml         字符串池（单元格里的文本存这里，单元格只存下标）
//   xl/worksheets/sheetN.xml     单元格数据
//
// 【和 WPF 版（Revolution.ExcelTool/Core/XlsxReader.cs）的区别】
//   · 用 XmlReader 流式读，不把整张表的 XML 树建出来：几万行的表内存也很平；
//   · ★ 行号按 <row r="N"> 定位：Excel 会把"完全空、也没格式"的行直接省略不写，
//     只按顺序数行的话，一旦第 3 行（描述）整行没填，后面每一行都会错位一行 ——
//     这里缺的行补成空行，行号永远和 Excel 左侧的行号一致；
//   · 富文本的注音（<rPh>，日文假名标注）不拼进正文；
//   · 打开文件用共享读写（见 ExcelZipReader）：Excel 开着也能读。
//
// 【不支持什么】.xls 老格式、加密工作簿；样式、图表、批注。
//   公式取它的"缓存值"（Excel 保存时会把计算结果写进 <v>，够导表用）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>一张工作表：解析后的文本矩阵（行 0 = Excel 第 1 行）</summary>
    public sealed class XlsxSheet
    {
        public string Name;

        /// <summary>每一行的单元格（行内按列号补齐空串）；Excel 省略掉的空行也补成了空数组</summary>
        public readonly List<string[]> Rows = new List<string[]>();

        /// <summary>整张表的最大列数</summary>
        public int ColumnCount;

        /// <summary>取单元格；越界返回空串（导表时"少配了列"是常态，不该抛异常）</summary>
        public string Get(int row, int col)
        {
            if (row < 0 || row >= Rows.Count) return string.Empty;
            string[] line = Rows[row];
            return (col < 0 || col >= line.Length) ? string.Empty : (line[col] ?? string.Empty);
        }

        /// <summary>某一行是否整行为空</summary>
        public bool IsRowEmpty(int row)
        {
            if (row < 0 || row >= Rows.Count) return true;
            foreach (string cell in Rows[row])
                if (!string.IsNullOrWhiteSpace(cell)) return false;
            return true;
        }

        /// <summary>整张表一个非空单元格都没有（新建工作簿里多出来的 Sheet2 / Sheet3）</summary>
        public bool IsBlank
        {
            get
            {
                for (int r = 0; r < Rows.Count; r++)
                    if (!IsRowEmpty(r)) return false;
                return true;
            }
        }
    }

    /// <summary>xlsx 读取器</summary>
    public static class XlsxReader
    {
        private static readonly XmlReaderSettings Settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,       // xlsx 里不会有 DTD；禁掉顺带防 XXE
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,                    // ★ 不能忽略：<t xml:space="preserve"> </t> 里的空格是真数据
            CloseInput = true,
        };

        /// <summary>读出一个工作簿里的全部工作表（顺序 = Excel 里工作表标签的顺序）</summary>
        public static List<XlsxSheet> ReadWorkbook(string filePath)
        {
            var sheets = new List<XlsxSheet>();

            using (ExcelZipReader zip = ExcelZipReader.Open(filePath))
            {
                if (!zip.Contains("xl/workbook.xml"))
                    throw new InvalidDataException("找不到 xl/workbook.xml：不是 Excel 工作簿（可能是 Word / PowerPoint 文件改了后缀）");

                List<string> sharedStrings = ReadSharedStrings(zip);

                foreach (KeyValuePair<string, string> sheet in ReadSheetRefs(zip))
                {
                    using (Stream stream = zip.OpenEntry(sheet.Value))
                    {
                        if (stream == null) continue;              // 图表页（chartsheet）等没有单元格的部件
                        sheets.Add(ParseSheet(stream, sheet.Key, sharedStrings));
                    }
                }
            }

            return sheets;
        }

        // ==================== 字符串池 ====================

        private static List<string> ReadSharedStrings(ExcelZipReader zip)
        {
            var list = new List<string>();

            using (Stream stream = zip.OpenEntry("xl/sharedStrings.xml"))
            {
                if (stream == null) return list;                   // 全数字的表不会有这个文件

                using (XmlReader xml = XmlReader.Create(stream, Settings))
                {
                    while (xml.Read())
                        if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "si")
                            list.Add(ReadRichText(xml));
                }
            }

            return list;
        }

        /// <summary>
        /// 读 &lt;si&gt; / &lt;is&gt; 下的文本：普通文本是一个 &lt;t&gt;，富文本是多个 &lt;r&gt;&lt;t&gt; —— 要拼起来。
        /// 调用时 reader 停在起始元素上；返回时停在它的结束元素上。
        /// </summary>
        private static string ReadRichText(XmlReader xml)
        {
            if (xml.IsEmptyElement) return string.Empty;

            var sb = new StringBuilder();
            int depth = xml.Depth;
            int phoneticDepth = -1;       // 在 <rPh>（注音）里面时不收文字
            bool inText = false;

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth) break;

                switch (xml.NodeType)
                {
                    case XmlNodeType.Element:
                        if (xml.LocalName == "rPh" && !xml.IsEmptyElement) phoneticDepth = xml.Depth;
                        else if (xml.LocalName == "t" && !xml.IsEmptyElement) inText = phoneticDepth < 0;
                        break;

                    case XmlNodeType.EndElement:
                        if (xml.LocalName == "t") inText = false;
                        else if (xml.Depth == phoneticDepth) phoneticDepth = -1;
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (inText) sb.Append(xml.Value);
                        break;
                }
            }

            return sb.ToString();
        }

        // ==================== 工作表清单 ====================

        /// <summary>工作表名 → 工作表 XML 在 zip 里的路径（保持工作簿里的顺序）</summary>
        private static List<KeyValuePair<string, string>> ReadSheetRefs(ExcelZipReader zip)
        {
            Dictionary<string, string> rels = ReadRels(zip);
            var list = new List<KeyValuePair<string, string>>();

            using (Stream stream = zip.OpenEntry("xl/workbook.xml"))
            using (XmlReader xml = XmlReader.Create(stream, Settings))
            {
                int index = 0;
                while (xml.Read())
                {
                    if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "sheet") continue;
                    index++;

                    string name = xml.GetAttribute("name") ?? ("Sheet" + index);
                    string rid = RelationshipId(xml);

                    // rels 里没有就按 Excel 的默认命名猜一个，尽量别整张表丢掉
                    string target = rid != null && rels.TryGetValue(rid, out string t)
                        ? t
                        : "xl/worksheets/sheet" + index + ".xml";

                    list.Add(new KeyValuePair<string, string>(name, target));
                }
            }

            return list;
        }

        /// <summary>取 r:id（按"本地名 id + 命名空间是 relationships"认，Strict / Transitional 两种命名空间都能认）</summary>
        private static string RelationshipId(XmlReader xml)
        {
            if (!xml.MoveToFirstAttribute()) return null;

            string id = null;
            do
            {
                if (xml.LocalName == "id" && xml.NamespaceURI.EndsWith("relationships", StringComparison.Ordinal))
                {
                    id = xml.Value;
                    break;
                }
            } while (xml.MoveToNextAttribute());

            xml.MoveToElement();
            return id;
        }

        private static Dictionary<string, string> ReadRels(ExcelZipReader zip)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            using (Stream stream = zip.OpenEntry("xl/_rels/workbook.xml.rels"))
            {
                if (stream == null) return map;

                using (XmlReader xml = XmlReader.Create(stream, Settings))
                {
                    while (xml.Read())
                    {
                        if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship") continue;

                        string id = xml.GetAttribute("Id");
                        string target = xml.GetAttribute("Target");
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;

                        map[id] = NormalizeTarget(target);
                    }
                }
            }

            return map;
        }

        /// <summary>把 rels 里的 Target 规范成 zip 内的路径</summary>
        private static string NormalizeTarget(string target)
        {
            target = target.Replace('\\', '/');

            if (target.StartsWith("/", StringComparison.Ordinal)) return target.Substring(1);    // /xl/worksheets/sheet1.xml
            if (target.StartsWith("xl/", StringComparison.Ordinal)) return target;              // 已经是完整路径
            if (target.StartsWith("../", StringComparison.Ordinal)) return target.Substring(3); // 相对 xl/ 的上一级
            return "xl/" + target;                                                             // worksheets/sheet1.xml
        }

        // ==================== 单元格 ====================

        private static XlsxSheet ParseSheet(Stream stream, string name, List<string> sharedStrings)
        {
            var sheet = new XlsxSheet { Name = name };

            using (XmlReader xml = XmlReader.Create(stream, Settings))
            {
                var cells = new List<KeyValuePair<int, string>>();

                while (xml.Read())
                {
                    if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row") continue;

                    // ① 行号：<row r="5">（1 基）；没有 r 就接在上一行后面
                    int rowIndex = ParsePositive(xml.GetAttribute("r")) - 1;
                    if (rowIndex < 0) rowIndex = sheet.Rows.Count;

                    // Excel 省略掉的空行补齐 —— 行号才能和 Excel 左侧对上
                    while (sheet.Rows.Count < rowIndex) sheet.Rows.Add(Array.Empty<string>());

                    // ② 单元格
                    cells.Clear();
                    int maxCol = -1;

                    if (!xml.IsEmptyElement)
                    {
                        int rowDepth = xml.Depth;
                        while (xml.Read())
                        {
                            if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == rowDepth) break;
                            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "c") continue;

                            // 单元格的 r 属性（如 "C7"）才是列号的依据 —— 空单元格会被省略，按顺序数会整体错列
                            int col = ColumnFromRef(xml.GetAttribute("r"));
                            if (col < 0) col = maxCol + 1;

                            string type = xml.GetAttribute("t");
                            cells.Add(new KeyValuePair<int, string>(col, ReadCell(xml, type, sharedStrings)));
                            if (col > maxCol) maxCol = col;
                        }
                    }

                    var line = new string[maxCol + 1];
                    for (int i = 0; i < line.Length; i++) line[i] = string.Empty;
                    foreach (KeyValuePair<int, string> cell in cells) line[cell.Key] = cell.Value;

                    // 重复的行号（不规范的生成器才会出现）→ 以后写的为准
                    if (rowIndex < sheet.Rows.Count) sheet.Rows[rowIndex] = line;
                    else sheet.Rows.Add(line);

                    if (line.Length > sheet.ColumnCount) sheet.ColumnCount = line.Length;
                }
            }

            return sheet;
        }

        /// <summary>读一个 &lt;c&gt; 的值；调用时停在 &lt;c&gt; 起始元素上，返回时停在它的结束元素上</summary>
        private static string ReadCell(XmlReader xml, string type, List<string> sharedStrings)
        {
            if (xml.IsEmptyElement) return string.Empty;

            string value = null;
            string inline = null;
            int depth = xml.Depth;

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth) break;
                if (xml.NodeType != XmlNodeType.Element) continue;

                if (xml.LocalName == "v") value = xml.ReadElementContentAsString();       // 读完停在 </v> 之后
                else if (xml.LocalName == "is") inline = ReadRichText(xml);

                // ReadElementContentAsString 已经前进到下一个节点：它可能正好是 </c>
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth) break;
            }

            switch (type)
            {
                case "s":           // 共享字符串：值是字符串池的下标
                    return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                           && idx >= 0 && idx < sharedStrings.Count
                        ? sharedStrings[idx]
                        : string.Empty;

                case "inlineStr":   // 内联字符串
                    return inline ?? string.Empty;

                case "b":           // 布尔：1 / 0
                    return (value ?? "").Trim() == "1" ? "TRUE" : "FALSE";

                case "e":           // 错误值（#N/A、#REF!）→ 当空处理，让校验去报
                    return string.Empty;

                default:            // 数字 / 公式缓存值（t="str" / "n" / 不写）
                    return value ?? string.Empty;
            }
        }

        /// <summary>"C7" → 2（0 基列号）；取不到返回 -1</summary>
        private static int ColumnFromRef(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;

            int col = 0;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
                else break;                                   // 碰到数字就结束（A→1、Z→26、AA→27）
            }
            return col - 1;
        }

        private static int ParsePositive(string text)
            => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;

        /// <summary>0 基列号 → Excel 列字母（2 → "C"），报错 / 界面显示用</summary>
        public static string ColumnName(int col)
        {
            var sb = new StringBuilder();
            for (int n = col + 1; n > 0; n = (n - 1) / 26)
                sb.Insert(0, (char)('A' + (n - 1) % 26));
            return sb.ToString();
        }
    }
}

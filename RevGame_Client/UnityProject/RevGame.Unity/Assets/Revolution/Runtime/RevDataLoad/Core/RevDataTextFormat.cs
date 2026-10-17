// ============================================================
// RevDataTextFormat.cs —— 数据文本格式（运行时与导表工具共用的唯一实现）
//
// 位置：Runtime\RevDataLoad\Core\
//
// 【为什么单独抽成这一个文件？】
//   导表工具（WPF）负责"写"，运行时（Unity）负责"读"，两端必须严格对称。
//   这里把格式规则收进一个不依赖 UnityEngine 的纯 C# 文件，
//   工具工程用 <Compile Include="..." Link="..."> 链接同一份源码 ——
//   改一处两端同时生效，从根上消除"写读不一致"这类最难查的 bug。
//
// 【格式规则】
//   1. 一行一条记录；字段之间用 \t（制表符）分隔
//   2. 转义：文字里的 \ 制表 回车 换行 会被写成两个字符 \\ \t \r \n
//   3. "#" 开头的整行是注释（表名、字段名、描述、版本号都放这里）→ 解析时跳过
//   4. 空行跳过
//
//   文件示例：
//     #HeroSkin
//     #ID	HeroName	Quality	Desc
//     1001	亚瑟	3	战士
//     1002	妲己	3	法师
//
// 【为什么不用 JSON / CSV？】
//   JSON：框架要零依赖，不能引第三方库；且文本膨胀、解析慢。
//   CSV ：分隔符是逗号，字符串里出现的逗号/引号要成对转义，规则比这里复杂得多。
//   本格式只有一条"制表符分列 + 四字符转义"的规则，手写解析器二十行搞定。
// ============================================================
namespace Revolution
{
    /// <summary>数据文本格式：分列、转义、拼接（读写两端共用）</summary>
    public static class RevDataTextFormat
    {
        /// <summary>字段分隔符（制表符：Excel 里复制出来天然就是这个）</summary>
        public const char FieldSeparator = '\t';

        /// <summary>注释行前缀</summary>
        public const char CommentPrefix = '#';

        /// <summary>行分隔符（写文件统一用 \n；读的时候兼容 \r\n）</summary>
        public const char LineSeparator = '\n';

        // ==================== 读：拆分一行 ====================

        /// <summary>
        /// 把一行拆成单元格，并把每个单元格的转义还原。
        /// 【注意】必须先按 \t 拆，再逐个反转义 —— 顺序反了，字符串里被转义成 "\t" 两个字符的内容会被误拆成两列。
        /// </summary>
        public static string[] SplitFields(string line)
        {
            string[] raw = line.Split(FieldSeparator);
            for (int i = 0; i < raw.Length; i++)
                raw[i] = Unescape(raw[i]);
            return raw;
        }

        /// <summary>反转义单个单元格</summary>
        public static string Unescape(string cell)
        {
            if (string.IsNullOrEmpty(cell) || cell.IndexOf('\\') < 0) return cell;

            var sb = new System.Text.StringBuilder(cell.Length);
            for (int i = 0; i < cell.Length; i++)
            {
                char c = cell[i];
                if (c != '\\' || i == cell.Length - 1) { sb.Append(c); continue; }

                char n = cell[++i];
                switch (n)
                {
                    case '\\': sb.Append('\\'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    default: sb.Append('\\').Append(n); break;   // 未知转义：原样保留
                }
            }
            return sb.ToString();
        }

        // ==================== 写：拼接一行 ====================

        /// <summary>把一行单元格拼成一行文本（写侧调用）</summary>
        public static string JoinFields(string[] cells)
        {
            if (cells == null || cells.Length == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(FieldSeparator);
                sb.Append(Escape(cells[i]));
            }
            return sb.ToString();
        }

        /// <summary>转义单个值：把会影响行/列结构的字符写成两个字符</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf('\\') < 0 && value.IndexOf('\t') < 0 &&
                value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0) return value;

            var sb = new System.Text.StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ==================== 通用判断 ====================

        /// <summary>该行是否要整行跳过（空行 / 注释行）</summary>
        public static bool IsSkippable(string line)
        {
            if (string.IsNullOrEmpty(line)) return true;
            return line[0] == CommentPrefix;
        }

        /// <summary>去掉行尾的 \r（兼容 Windows 换行）</summary>
        public static string TrimLineEnd(string line)
        {
            if (!string.IsNullOrEmpty(line) && line[line.Length - 1] == '\r')
                return line.Substring(0, line.Length - 1);
            return line;
        }

        /// <summary>生成注释行（写侧用：表名、字段名都走这里）</summary>
        public static string Comment(string text) => CommentPrefix + (text ?? string.Empty);
    }
}

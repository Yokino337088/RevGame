// RevGMParser.cs —— 把一行文本拆成"命令名 + 参数"
//
// 【为什么单独一个文件】这是最容易踩坑的一步（王者文档 `04:78-100` 专门讲过）：
//   `设置名字 "王者 荣耀"` 用 Split(' ') 会拆坏 —— 引号里的空格不是分隔符。
//   这里手写扫描，不用正则：引号内的空格保留、引号本身不留在参数里、中文全角空格和 Tab 也算分隔符。
//
// 【零分配取向】只有确实需要切分时才建 List；单参数/无参数是最常见情况。

using System.Collections.Generic;
using System.Text;

namespace Revolution
{
    internal static class RevGMParser
    {
        private static readonly string[] NoArgs = new string[0];

        /// <summary>把一行文本拆成 token（第 0 个是命令名）。</summary>
        internal static string[] Tokenize(string line)
        {
            if (string.IsNullOrEmpty(line)) return NoArgs;

            List<string> tokens = null;
            StringBuilder current = null;
            bool inQuotes = false;
            bool hasCurrent = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"') inQuotes = false;         // 收尾引号：不留在参数里
                    else current.Append(c);
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    hasCurrent = true;                       // "" → 一个空参数
                    current ??= new StringBuilder(16);
                    continue;
                }

                if (c == ' ' || c == '\t' || c == '\u3000')  // 半角空格 / Tab / 中文全角空格
                {
                    if (!hasCurrent) continue;
                    tokens ??= new List<string>(4);
                    tokens.Add(current?.ToString() ?? string.Empty);
                    current?.Clear();
                    hasCurrent = false;
                    continue;
                }

                current ??= new StringBuilder(16);
                current.Append(c);
                hasCurrent = true;
            }

            if (hasCurrent)
            {
                tokens ??= new List<string>(4);
                tokens.Add(current?.ToString() ?? string.Empty);
            }

            return tokens == null ? NoArgs : tokens.ToArray();
        }

        /// <summary>拆出命令名与参数（命令名 = 第一个 token）。</summary>
        internal static void Split(string line, out string name, out string[] args)
        {
            string[] tokens = Tokenize(line);

            if (tokens.Length == 0) { name = string.Empty; args = NoArgs; return; }

            name = tokens[0];

            if (tokens.Length == 1) { args = NoArgs; return; }

            args = new string[tokens.Length - 1];
            System.Array.Copy(tokens, 1, args, 0, args.Length);
        }
    }
}

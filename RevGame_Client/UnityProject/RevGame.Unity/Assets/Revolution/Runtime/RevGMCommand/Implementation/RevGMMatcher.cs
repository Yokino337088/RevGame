// RevGMMatcher.cs —— 联想的核心：模糊匹配 + 打分（框架内部，面板与 RevGM.Suggest 都用它）
//
// 【为什么要模糊匹配】王者那套必须"输入完整命令名"才能执行（测试指南原话），实际用起来就是"记不住、抄错一个字符就没反应"。
//   这里支持三种命中方式，并且越"像"排得越前：
//     ① 前缀命中   打 "加金" → 「加金币」                      （最准，排最前）
//     ② 连续子串   打 "金币" → 「经济/货币/加金币」
//     ③ 子序列     打 "jbi"  → 「减/兵…」这类缩写也能找到         （最松，排最后）
//   分两轮打分：先看"末段名"（用户记得的通常就是末段），再看"完整名（含分组）"。
//
// 【命中位置】面板拿它把命中的字符高亮出来，所以 TryMatch 会返回每个命中字符的下标。

using System;

namespace Revolution
{
    /// <summary>GM 命令的模糊匹配与打分。</summary>
    public static class RevGMMatcher
    {
        /// <summary>
        /// 子序列匹配（忽略大小写）：<paramref name="query"/> 的每个字符都要按顺序出现在 <paramref name="text"/> 里。
        /// </summary>
        /// <param name="positions">命中字符在 text 里的下标（面板据此高亮）</param>
        public static bool TryMatch(string text, string query, out int[] positions)
        {
            positions = null;

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
            if (query.Length > text.Length) return false;

            int[] hits = new int[query.Length];
            int qi = 0;

            for (int i = 0; i < text.Length && qi < query.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[qi]))
                    hits[qi++] = i;
            }

            if (qi < query.Length) return false;

            positions = hits;
            return true;
        }

        /// <summary>
        /// 打分（0 = 不匹配，越大越该排在前面）。
        /// <para>末段名命中 1000/800/600，完整名命中 500/450/350（对应 前缀/连续子串/子序列）。</para>
        /// </summary>
        public static int Score(RevGMCommand command, string query)
        {
            if (command == null) return 0;

            string q = query?.Trim();
            if (string.IsNullOrEmpty(q)) return 0;

            int byBase = ScoreText(command.BaseName, q, 1000, 800, 600);
            int byFull = ScoreText(command.Name, q, 500, 450, 350);

            return byBase > byFull ? byBase : byFull;
        }

        private static int ScoreText(string text, string query, int prefixScore, int containsScore, int subsequenceScore)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return prefixScore;
            if (text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return containsScore;

            return TryMatch(text, query, out _) ? subsequenceScore : 0;
        }
    }
}

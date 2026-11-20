// ============================================================
// RevHotVersion.cs —— 版本号的比较规则（纯 C#，可工程外断言）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【为什么要专门一个文件】
//   版本号比较是"差量更新"的地基：1.2.0.10 必须大于 1.2.0.9（字符串比较会得出相反结论）。
//   规则很简单：按 '.' 切段、逐段按数字比；某一边缺段当 0；某段不是数字才退回字符串比较。
//
// 【大版本锚定提醒】
//   这里的比较只用于"同一大版本内的资源版本"排序与"远端清单 vs 本地清单"的一致性判断；
//   "跨大版本"不参与比较 —— 那是 AppVersion 不匹配，直接走强更（见 RevHotUpdate.CheckAsync）。
// ============================================================
using System;
using System.Globalization;

namespace Revolution.HotUpdate
{
    /// <summary>版本号工具（形如 1.2.0.37；段数不限，缺的段按 0 处理）。</summary>
    public static class RevHotVersion
    {
        /// <summary>
        /// 比较 a 与 b：返回 正数(a 大) / 0(相等) / 负数(b 大)。
        /// null / 空串按"最小"处理。
        /// </summary>
        public static int Compare(string a, string b)
        {
            bool emptyA = string.IsNullOrEmpty(a);
            bool emptyB = string.IsNullOrEmpty(b);
            if (emptyA && emptyB) return 0;
            if (emptyA) return -1;
            if (emptyB) return 1;

            string[] sa = a.Split('.');
            string[] sb = b.Split('.');
            int count = Math.Max(sa.Length, sb.Length);

            for (int i = 0; i < count; i++)
            {
                string da = i < sa.Length ? sa[i] : "0";
                string db = i < sb.Length ? sb[i] : "0";

                int na;
                int nb;
                bool numA = int.TryParse(da, NumberStyles.Integer, CultureInfo.InvariantCulture, out na);
                bool numB = int.TryParse(db, NumberStyles.Integer, CultureInfo.InvariantCulture, out nb);

                // 两边都是数字 → 按数字比（这是版本号的常规情况）
                if (numA && numB)
                {
                    if (na != nb) return na.CompareTo(nb);
                    continue;
                }

                // 有一边不是数字 → 只能按字符串比（例如 "1.2.0-rc1"）
                int byString = string.Compare(da, db, StringComparison.OrdinalIgnoreCase);
                if (byString != 0) return byString;
            }

            return 0;
        }

        /// <summary>两个版本是否完全一致（大版本锚定下，"远端资源版本 == 本地资源版本"就是"没有更新"）。</summary>
        public static bool AreEqual(string a, string b)
        {
            return Compare(a, b) == 0;
        }
    }
}

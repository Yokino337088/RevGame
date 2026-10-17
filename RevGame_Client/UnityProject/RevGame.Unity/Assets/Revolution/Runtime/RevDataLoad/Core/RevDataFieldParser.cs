// ============================================================
// RevDataFieldParser.cs —— 单元格取值（生成代码专用）
//
// 位置：Runtime\RevDataLoad\Core\
//
// 【为什么要有这一层，而不是直接 int.Parse ？】
//   ① 容错：策划表里 "  "（空格）、"-"、空单元格都很常见，
//      直接 Parse 会抛异常，一个空格就能把整张表炸掉。这里统一"解析失败取默认值"。
//   ② 区域性：float.Parse 默认受系统语言影响（中文/欧洲系统的小数点是逗号），
//      用 InvariantCulture 才能保证同一份数据在任何机器上结果一致。
//   ③ 零装箱：全部是静态方法 + struct 返回值，生成的 ParseRow 里不产生任何装箱。
//
// 【为什么不做成泛型 Convert<T> ？】
//   泛型要传 Type 或装箱 object，反而更慢、更难读。
//   生成器知道每个字段的确切类型，直接生成对应的强类型调用最快。
// ============================================================
using System.Globalization;

namespace Revolution
{
    /// <summary>字段取值工具（生成的 ParseRow 调用它）</summary>
    public static class RevDataFieldParser
    {
        public static int ToInt(string cell, int defaultValue = 0)
            => int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : defaultValue;

        public static long ToLong(string cell, long defaultValue = 0L)
            => long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : defaultValue;

        public static float ToFloat(string cell, float defaultValue = 0f)
            => float.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : defaultValue;

        /// <summary>
        /// 取布尔值。约定：true / 1 / 是 / yes / Y （不区分大小写）为真，其余为假。
        /// 【为什么这么宽？】策划表里这几种写法都会出现，与其让策划改表，不如读取端宽容一点。
        /// </summary>
        public static bool ToBool(string cell, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(cell)) return defaultValue;

            switch (cell.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "是":
                case "yes":
                case "y":
                    return true;

                case "false":
                case "0":
                case "否":
                case "no":
                case "n":
                    return false;

                default:
                    return defaultValue;
            }
        }

        /// <summary>取字符串（null 统一成空串，省得业务到处判空）</summary>
        public static string ToStr(string cell) => cell ?? string.Empty;
    }
}

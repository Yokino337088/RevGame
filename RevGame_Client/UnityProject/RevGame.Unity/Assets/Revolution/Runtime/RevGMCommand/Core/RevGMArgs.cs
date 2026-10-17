// RevGMArgs.cs —— 取参数（带默认值、带人话错误）
//
// 【为什么要有它】王者那套是业务自己写 `SmartConvert<int>(args[0])`：解析失败抛的是 TypeConverter 的英文异常，
//   而且"缺参数"和"类型不对"要等到业务代码里才暴露。这里把取参数收成一行，并且**错误信息直接给人看**：
//     var count = args.Int(0, 1000);   // 没传就是 1000；传了 "abc" → 抛「参数「数量」应该是整数，实际收到 "abc"」
//
// 【框架怎么用它】框架会在调用 handler 之前，按注册时给的参数说明先校验一遍
//   （数量够不够、类型对不对、枚举值在不在候选里）→ 参数错误在业务执行前就被拦下并回显。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Revolution
{
    /// <summary>一次执行里"用户敲进来的参数"，以及按类型取值的方法。</summary>
    public readonly struct RevGMArgs
    {
        private static readonly string[] Empty = new string[0];

        private readonly string[] _items;
        private readonly RevGMArg[] _declared;      // 注册时声明的参数说明（用于错误信息里的参数名）

        internal RevGMArgs(string[] items, RevGMArg[] declared)
        {
            _items = items ?? Empty;
            _declared = declared;
        }

        /// <summary>参数个数</summary>
        public int Count => _items.Length;

        /// <summary>没有参数</summary>
        public bool IsEmpty => _items.Length == 0;

        /// <summary>第 <paramref name="index"/> 个参数是否存在</summary>
        public bool Has(int index) => index >= 0 && index < _items.Length;

        /// <summary>第 <paramref name="index"/> 个参数的原始文本（不存在返回 null）</summary>
        public string Raw(int index) => Has(index) ? _items[index] : null;

        /// <summary>参数整体（原样拼回一行，用于日志 / 提示）</summary>
        public string Text => _items.Length == 0 ? string.Empty : string.Join(" ", _items);

        /// <summary>复制一份参数数组</summary>
        public string[] ToArray() => (string[])_items.Clone();

        // ============================================================
        // 取值（解析失败抛 RevGMUsageException，消息会原样显示在面板上）
        // ============================================================

        /// <summary>取文本；没传就用 <paramref name="fallback"/></summary>
        public string Str(int index, string fallback = null) => Has(index) ? _items[index] : fallback;

        /// <summary>取整数；没传就用 <paramref name="fallback"/></summary>
        public int Int(int index, int fallback = 0)
        {
            if (!Has(index)) return fallback;
            if (int.TryParse(_items[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
            throw TypeError(index, "整数");
        }

        /// <summary>取小数（小数点是 . 或 , 都认）；没传就用 <paramref name="fallback"/></summary>
        public float Float(int index, float fallback = 0f)
        {
            if (!Has(index)) return fallback;
            if (TryParseFloat(_items[index], out float value)) return value;
            throw TypeError(index, "小数");
        }

        /// <summary>取开关（true/false、1/0、on/off、yes/no、是/否、开/关 都认）；没传就用 <paramref name="fallback"/></summary>
        public bool Bool(int index, bool fallback = false)
        {
            if (!Has(index)) return fallback;
            if (TryParseBool(_items[index], out bool value)) return value;
            throw TypeError(index, "开关（true/false）");
        }

        /// <summary>取枚举；没传就用 <paramref name="fallback"/>（枚举候选值请用 RevGMArg.Enum 声明，面板会列出来）</summary>
        public T Enum<T>(int index, T fallback = default) where T : struct, Enum
        {
            if (!Has(index)) return fallback;
            if (System.Enum.TryParse(_items[index], true, out T value) && System.Enum.IsDefined(typeof(T), value)) return value;
            throw TypeError(index, typeof(T).Name + "（候选：" + string.Join("/", System.Enum.GetNames(typeof(T))) + "）");
        }

        // ============================================================
        // 内部：校验 + 错误信息
        // ============================================================

        /// <summary>执行前按参数说明预校验（返回 null 表示通过）</summary>
        internal static string Validate(string[] items, RevGMArg[] declared)
        {
            if (declared == null || declared.Length == 0) return null;      // 没写参数说明 → 不做校验

            if (items.Length > declared.Length)
                return $"参数多了 {items.Length - declared.Length} 个（本命令最多接受 {declared.Length} 个参数；多个词请用引号括起来，例如 \"王者 荣耀\"）";

            for (int i = 0; i < declared.Length; i++)
            {
                RevGMArg arg = declared[i];

                if (i >= items.Length)                                  // 位置不够
                {
                    if (arg.IsRequired) return $"缺少必填参数「{arg.Name}」（{arg.Describe()}）";
                    continue;                                          // 有默认值 → 交给取参数时的 fallback
                }

                if (!CanConvert(items[i], arg, out string reason)) return $"参数「{arg.Name}」{reason}";
            }

            return null;
        }

        private static bool CanConvert(string raw, RevGMArg arg, out string reason)
        {
            switch (arg.Type)
            {
                case RevGMArgType.Str:
                    reason = null;
                    return true;

                case RevGMArgType.Int:
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) { reason = null; return true; }
                    reason = $"应该是整数，实际收到 \"{raw}\"";
                    return false;

                case RevGMArgType.Float:
                    if (TryParseFloat(raw, out _)) { reason = null; return true; }
                    reason = $"应该是小数，实际收到 \"{raw}\"";
                    return false;

                case RevGMArgType.Bool:
                    if (TryParseBool(raw, out _)) { reason = null; return true; }
                    reason = $"应该是开关（true/false），实际收到 \"{raw}\"";
                    return false;

                case RevGMArgType.Enum:
                    if (arg.Candidates != null && arg.Candidates.Length > 0)
                    {
                        for (int i = 0; i < arg.Candidates.Length; i++)
                            if (string.Equals(arg.Candidates[i], raw, StringComparison.OrdinalIgnoreCase)) { reason = null; return true; }

                        reason = $"应该是 {string.Join(" / ", arg.Candidates)} 之一，实际收到 \"{raw}\"";
                        return false;
                    }

                    reason = null;
                    return true;

                default:
                    reason = null;
                    return true;
            }
        }

        /// <summary>
        /// 小数解析：先按不变文化（"1.5" 永远认），再退回当前文化（"1,5" 这种逗号小数点也认）。
        /// <para>★ 取值（<see cref="Float"/>）与执行前的预校验必须共用这一个函数 ——
        /// 曾经出现两边各写一套：取值器能解析的 "1,5" 被预校验判成"应该是小数"，
        /// 于是"参数没错却被拦下"（在逗号小数点的系统区域下必现）。</para>
        /// </summary>
        private static bool TryParseFloat(string raw, out float value)
        {
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": case "是": case "开": case "启用":
                    value = true; return true;
                case "false": case "0": case "off": case "no": case "否": case "关": case "禁用":
                    value = false; return true;
                default:
                    value = false; return false;
            }
        }

        private string Name(int index)
            => _declared != null && index < _declared.Length && !string.IsNullOrEmpty(_declared[index].Name)
                ? _declared[index].Name
                : (index + 1).ToString(CultureInfo.InvariantCulture);

        private RevGMUsageException TypeError(int index, string expected)
            => new RevGMUsageException($"参数「{Name(index)}」应该是{expected}，实际收到 \"{Raw(index)}\"");

        /// <summary>调试显示：例如 <c>1000 heroId=7</c></summary>
        public override string ToString() => Text;
    }
}

// ============================================================
// ConfigText.cs —— 配置里的“说明文字模板”渲染。
//
// 为什么要有模板：
// 增益说明原来是写死的整句中文（“本轮全体攻击加成+25%”），而真正的 0.25 写在另一处代码里。
// 两处各改各的，迟早出现“说明写 25%、实际是 30%”。现在说明文字里的数字用占位符引用同一行的数值，
// 策划只改一个数，文字自动同步。
//
// 占位符写法：{名字} 取数字本身（3 → 3，0.25 → 0.25）；{名字%} 按百分数显示（0.25 → 25%）。
// 未知占位符、没有闭合的花括号都会记成问题，装配阶段就报错，而不是显示成奇怪的原文。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevGame.Data
{
    internal static class ConfigText
    {
        private static readonly string[] StarCnNames = { "", "一", "二", "三" };

        /// <summary>数字原样显示，最多三位小数（3 → "3"，0.25 → "0.25"）。</summary>
        public static string Number(float value)
        {
            return Math.Round((double)value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>按百分数显示（0.25 → "25%"）。先放大再取整到三位小数，避免 0.35f 显示成 34.99999%。</summary>
        public static string Percent(float value)
        {
            return Math.Round((double)value * 100.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }

        public static string StarCn(int star) => StarCnNames[star];

        /// <summary>
        /// 渲染模板。resolver 返回 null 表示“这个占位符不存在或此处不可用”，会记成问题并保留原文。
        /// </summary>
        public static string Render(string template, string where, List<string> problems, Func<string, string> resolver)
        {
            if (string.IsNullOrEmpty(template)) { return string.Empty; }
            var sb = new StringBuilder(template.Length + 16);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c != '{') { sb.Append(c); i++; continue; }
                int close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    problems.Add(where + " 的模板有未闭合的“{”：" + template);
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                string token = template.Substring(i + 1, close - i - 1);
                string value = resolver(token);
                if (value == null)
                {
                    problems.Add(where + " 的占位符 {" + token + "} 不可用（模板：" + template + "）");
                    sb.Append('{').Append(token).Append('}');
                }
                else { sb.Append(value); }
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>给运行时再次渲染（如奖励里的棋子名）用：占位符缺失直接抛出，不再返回问题清单。</summary>
        public static string RenderOrThrow(string template, string where, Func<string, string> resolver)
        {
            var problems = new List<string>();
            string text = Render(template, where, problems, resolver);
            if (problems.Count > 0) { throw new InvalidOperationException(string.Join("；", problems)); }
            return text;
        }
    }
}

// ============================================================
// RevAutomationPayload —— 在 JsonUtility 映射 DTO 之前校验原始 JSON。
// 【为什么】JsonUtility 会忽略拼错字段、截断不合类型的输入，导致“参数没生效”。
// 先严格解析/检查白名单和基础类型，再让具体动作检查业务范围与对象身份。
// ============================================================
// JsonUtility 会静默忽略未知字段；先严格解析，再检查当前动作的字段和 C# 类型。
using System;
using System.Linq;
namespace Revolution.Editor
{
    internal static class RevAutomationPayload
    {
        internal static void Validate(RevAutomationRequest request, string[] allowed)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            if (allowed != null && values.Keys.Except(allowed, StringComparer.Ordinal).Any())
            { throw new ArgumentException(request.action + " 未知参数：" + string.Join(", ", values.Keys.Except(allowed, StringComparer.Ordinal))); }
            foreach (var pair in values)
            {
                var field = typeof(RevAutomationEditorOptions).GetField(pair.Key) ?? typeof(RevAutomationOptions).GetField(pair.Key)
                    ?? typeof(RevAutomationScenarioSpec).GetField(pair.Key);
                if (field == null) { continue; } // 项目扩展负责自己的参数契约。
                Type type = field.FieldType; object value = pair.Value;
                bool valid = type == typeof(string) ? value is string : type == typeof(bool) ? value is bool : type.IsArray ? value is System.Collections.Generic.List<object> :
                    type.IsPrimitive ? value is double : value is System.Collections.Generic.Dictionary<string, object>;
                if (!valid) { throw new ArgumentException(pair.Key + " 类型应为 " + type.Name); }
                if (type == typeof(int) && ((double)value < int.MinValue || (double)value > int.MaxValue || Math.Truncate((double)value) != (double)value))
                { throw new ArgumentException(pair.Key + " 需要 Int32 整数，不能截断小数或溢出"); }
                if (type == typeof(long) && ((double)value < long.MinValue || (double)value >= 9223372036854775808d || Math.Truncate((double)value) != (double)value))
                { throw new ArgumentException(pair.Key + " 需要 Int64 整数"); }
            }
        }
    }
}

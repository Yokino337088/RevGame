using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Revolution.Editor
{
    /// <summary>同帧批处理的参数与结果引用。只解析 JSON 数据，不执行脚本。</summary>
    internal sealed class RevAutomationBatchPlan
    {
        internal sealed class Step
        {
            internal string Id, Action, PayloadJson;
            internal bool Confirm;
            internal Dictionary<string, object> Payload;
            internal List<object> Expect;

            internal string Arguments(Dictionary<string, object> results)
            {
                return Payload == null ? (string.IsNullOrEmpty(PayloadJson) ? "{}" : PayloadJson)
                    : RevAutomationJson.Write(Visit(Payload, null, results));
            }
        }

        internal Step[] Steps;
        internal bool StopOnError, ValidateOnly, Structured;

        internal static RevAutomationBatchPlan Read(string json)
        {
            var map = RevAutomationJson.Object(RevAutomationJson.Parse(json));
            if (!map.TryGetValue("steps", out var raw) || raw is not List<object> items || items.Count < 1 || items.Count > 64)
            {
                throw new ArgumentException("batch.call 的 steps 需要 1～64 步");
            }

            var plan = new RevAutomationBatchPlan
            {
                Steps = new Step[items.Count],
                StopOnError = ReadBool(map, "stopOnError", true),
                ValidateOnly = ReadBool(map, "validateOnly", false)
            };
            plan.Structured = plan.ValidateOnly || items.OfType<Dictionary<string, object>>()
                .Any(step => step.ContainsKey("id") || step.ContainsKey("payload") || step.ContainsKey("expect"));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < items.Count; index++)
            {
                if (items[index] == null && !plan.Structured)
                {
                    continue;
                }
                try
                {
                    plan.Steps[index] = ReadStep(RevAutomationJson.Object(items[index]), plan.Structured, ids);
                }
                catch (ArgumentException error)
                {
                    throw new ArgumentException("batch.call 第 " + (index + 1) + " 步：" + error.Message, error);
                }
            }
            return plan;
        }

        private static Step ReadStep(Dictionary<string, object> map, bool structured, HashSet<string> ids)
        {
            var step = new Step
            {
                Action = ReadString(map, "action"), Id = ReadString(map, "id"),
                PayloadJson = ReadString(map, "payloadJson"), Confirm = ReadBool(map, "confirm", false)
            };
            if (!structured)
            {
                return step;
            }
            if (map.Keys.Any(key => key is not ("id" or "action" or "payload" or "payloadJson" or "confirm" or "expect")))
            {
                throw new ArgumentException("含未知步骤字段");
            }
            if (string.IsNullOrWhiteSpace(step.Action))
            {
                throw new ArgumentException("缺少 action");
            }
            if (map.TryGetValue("payload", out var arguments))
            {
                if (map.ContainsKey("payloadJson"))
                {
                    throw new ArgumentException("同一步只能使用 payload 或 payloadJson");
                }
                step.Payload = RevAutomationJson.Object(arguments);
                Visit(step.Payload, ids, null);
            }
            else if (!string.IsNullOrEmpty(step.PayloadJson))
            {
                RevAutomationJson.Object(RevAutomationJson.Parse(step.PayloadJson));
            }
            if (map.TryGetValue("expect", out var expected))
            {
                step.Expect = expected as List<object> ?? throw new ArgumentException("expect 必须是数组");
                foreach (var item in step.Expect)
                {
                    var condition = RevAutomationJson.Object(item);
                    string path = ReadString(condition, "path");
                    bool hasEquals = condition.ContainsKey("equals");
                    bool hasExists = condition.ContainsKey("exists");
                    if (condition.Count != 2 || hasEquals == hasExists || path == null || !ValidPointer(path)
                        || (hasExists && condition["exists"] is not bool))
                    {
                        throw new ArgumentException("断言需使用 JSON Pointer 和 equals 或 exists");
                    }
                }
            }
            if (step.Id != null && (string.IsNullOrWhiteSpace(step.Id) || step.Id.Contains("#") || !ids.Add(step.Id)))
            {
                throw new ArgumentException("id 必须非空、唯一且不能含 #");
            }
            return step;
        }

        private static string ReadString(Dictionary<string, object> map, string key)
        {
            return !map.TryGetValue(key, out var value) || value == null ? null :
                (value as string ?? throw new ArgumentException(key + " 必须是字符串"));
        }

        private static bool ReadBool(Dictionary<string, object> map, string key, bool fallback)
        {
            return !map.TryGetValue(key, out var value) ? fallback :
                (value is bool flag ? flag : throw new ArgumentException(key + " 必须是布尔值"));
        }

        private static bool ValidPointer(string path)
        {
            if (path.Length == 0)
            {
                return true;
            }
            if (path[0] != '/')
            {
                return false;
            }
            for (int index = 0; index < path.Length; index++)
            {
                if (path[index] == '~' && (index + 1 == path.Length || path[++index] is not ('0' or '1')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>预检时确认引用只指向前序步骤。执行期从前序成功结果取值并保留 JSON 类型。</summary>
        private static object Visit(object value, HashSet<string> ids, Dictionary<string, object> results)
        {
            if (value is Dictionary<string, object> map)
            {
                if (!map.TryGetValue("$ref", out var raw))
                {
                    if (results != null)
                    {
                        return map.ToDictionary(pair => pair.Key, pair => Visit(pair.Value, null, results), StringComparer.Ordinal);
                    }
                    foreach (object child in map.Values)
                    {
                        Visit(child, ids, null);
                    }
                    return null;
                }
                if (map.Count != 1 || raw is not string reference)
                {
                    throw new ArgumentException("引用必须是单独的 {\"$ref\":\"步骤ID#/data/字段\"} 对象");
                }
                int split = reference.IndexOf('#');
                if (split < 1 || !ValidPointer(reference.Substring(split + 1)))
                {
                    throw new ArgumentException("引用需使用 步骤ID#/JSON/Pointer：" + reference);
                }
                string id = reference.Substring(0, split);
                if (results == null)
                {
                    if (!ids.Contains(id))
                    {
                        throw new ArgumentException("只能引用前序步骤：" + id);
                    }
                    return null;
                }
                if (!results.TryGetValue(id, out var previous) || !TryPointer(previous, reference.Substring(split + 1), out var found))
                {
                    throw new ArgumentException("前序步骤失败或结果不存在：" + reference);
                }
                return found;
            }
            if (value is List<object> array)
            {
                if (results != null)
                {
                    return array.Select(item => Visit(item, null, results)).ToArray();
                }
                foreach (object child in array)
                {
                    Visit(child, ids, null);
                }
            }
            return value;
        }

        private static bool TryPointer(object source, string path, out object value)
        {
            value = source;
            if (path.Length == 0)
            {
                return true;
            }
            foreach (string part in path.Substring(1).Split('/'))
            {
                string key = part.Replace("~1", "/").Replace("~0", "~");
                if (value is Dictionary<string, object> map && map.TryGetValue(key, out var entry))
                {
                    value = entry;
                }
                else if (value is List<object> array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index >= 0 && index < array.Count && key == index.ToString(CultureInfo.InvariantCulture))
                {
                    value = array[index];
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        internal static void Assert(List<object> conditions, Dictionary<string, object> result)
        {
            if (conditions == null)
            {
                return;
            }
            foreach (var entry in conditions)
            {
                var condition = RevAutomationJson.Object(entry);
                string path = (string)condition["path"];
                bool exists = TryPointer(result, path, out var actual);
                bool matched = condition.TryGetValue("exists", out var expected)
                    ? exists == (bool)expected
                    : exists && Equal(actual, condition["equals"]);
                if (!matched)
                {
                    throw new InvalidOperationException("断言未满足：" + path + "，条件 " + RevAutomationJson.Write(condition) +
                        "，实际 " + (exists ? RevAutomationJson.Write(actual) : "不存在"));
                }
            }
        }

        private static bool Equal(object first, object second)
        {
            if (first is Dictionary<string, object> left && second is Dictionary<string, object> right)
            {
                return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && Equal(pair.Value, value));
            }
            if (first is List<object> a && second is List<object> b)
            {
                return a.Count == b.Count && a.Zip(b, Equal).All(equal => equal);
            }
            return Equals(first, second);
        }
    }
}

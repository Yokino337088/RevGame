using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>按 SO 字段校验 JSON，CLI 可读写枚举名称。无需第三方运行时序列化器。</summary>
    internal static class RevGalJson
    {
        public static RevGalDefinition Import(string json)
        {
            var root = RevAutomationJson.Object(RevAutomationJson.Parse(json));
            if (!root.ContainsKey("Schema") || !root.ContainsKey("Id") || !root.ContainsKey("Start") || !root.ContainsKey("Nodes"))
            {
                throw new ArgumentException("剧情 JSON 需要 Schema、Id、Start 和 Nodes");
            }
            var normalized = Normalize(root, typeof(RevGalDefinition), "$");
            return JsonUtility.FromJson<RevGalDefinition>(RevAutomationJson.Write(normalized));
        }

        public static string Export(RevGalDefinition story)
        {
            return RevAutomationJson.Write(ToDocument(story, typeof(RevGalDefinition)));
        }

        private static object Normalize(object value, Type type, string location)
        {
            if (value == null && !type.IsValueType)
            {
                return null;
            }
            if (type == typeof(string) && value is string || type == typeof(bool) && value is bool)
            {
                return value;
            }
            if (type == typeof(int) && IsInt(value))
            {
                return value;
            }
            if (type == typeof(float) && value is double real && Math.Abs(real) <= float.MaxValue)
            {
                return value;
            }
            if (type.IsEnum)
            {
                object enumValue = value is string name && Enum.TryParse(type, name, true, out var parsed)
                    ? parsed : IsInt(value) ? Enum.ToObject(type, (int)(double)value) : null;
                if (enumValue == null || !Enum.IsDefined(type, enumValue))
                {
                    throw new ArgumentException(location + " 需要合法的 " + type.Name);
                }
                return Convert.ToInt32(enumValue);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && value is List<object> sequence)
            {
                var itemType = type.GetGenericArguments()[0];
                return sequence.Select((item, index) => Normalize(item, itemType, location + "[" + index + "]")).ToList();
            }
            if (value is Dictionary<string, object> document && !type.IsPrimitive && type != typeof(string)
                && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var fields = Fields(type).ToDictionary(field => field.Name, StringComparer.Ordinal);
                var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in document)
                {
                    if (!fields.TryGetValue(pair.Key, out var field))
                    {
                        throw new ArgumentException(location + " 未知字段：" + pair.Key);
                    }
                    normalized.Add(pair.Key, Normalize(pair.Value, field.FieldType, location + "." + pair.Key));
                }
                return normalized;
            }
            throw new ArgumentException(location + " 数据类型错误，应为 " + type.Name);
        }

        private static object ToDocument(object value, Type type)
        {
            if (value == null)
            {
                return null;
            }
            if (type.IsEnum)
            {
                return value.ToString();
            }
            if (type.IsPrimitive || type == typeof(string))
            {
                return value;
            }
            if (value is System.Collections.IEnumerable items)
            {
                Type itemType = type.GetGenericArguments()[0];
                return items.Cast<object>().Select(item => ToDocument(item, itemType)).ToArray();
            }
            return Fields(type).ToDictionary(field => field.Name, field => ToDocument(field.GetValue(value), field.FieldType));
        }

        private static FieldInfo[] Fields(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        }

        private static bool IsInt(object value)
        {
            return value is double number && number >= int.MinValue && number <= int.MaxValue && number == Math.Truncate(number);
        }
    }
}

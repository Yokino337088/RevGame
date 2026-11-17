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

        /// <summary>节点文档与整篇导入共用同一套字段、枚举和类型规则。</summary>
        public static RevGalNode ReadNode(object document)
        {
            return JsonUtility.FromJson<RevGalNode>(RevAutomationJson.Write(Normalize(document, typeof(RevGalNode), "$.node")));
        }

        /// <summary>批量配置章节 / 小节时，同样检查嵌套字段和资源路径的数据类型。</summary>
        public static List<RevGalStagePreset> ReadStagePresets(object document)
        {
            var normalized = Normalize(document, typeof(List<RevGalStagePreset>), "$.stagePresets");
            return JsonUtility.FromJson<RevGalDefinition>("{\"StagePresets\":" + RevAutomationJson.Write(normalized) + "}").StagePresets;
        }

        public static object NodeDocument(RevGalNode node)
        {
            return ToDocument(node, typeof(RevGalNode));
        }

        /// <summary>对象逐字段合并，数组按顺序整体替换，为局部修改保留未指定的值。</summary>
        public static RevGalNode PatchNode(RevGalNode current, Dictionary<string, object> changes)
        {
            var edited = ReadNode(changes);
            PreserveFields(current, edited, typeof(RevGalNode), changes);
            return edited;
        }

        private static void PreserveFields(object current, object edited, Type type, Dictionary<string, object> changes)
        {
            foreach (var field in Fields(type))
            {
                object original = field.GetValue(current);
                if (!changes.TryGetValue(field.Name, out var value))
                {
                    field.SetValue(edited, original);
                }
                else if (value == null)
                {
                    field.SetValue(edited, null);
                }
                else if (value is Dictionary<string, object> nested && original != null)
                {
                    object updated = field.GetValue(edited) ?? Activator.CreateInstance(field.FieldType);
                    PreserveFields(original, updated, field.FieldType, nested);
                    // Vector2 等值类型通过反射读取后是装箱对象，需要写回字段。
                    field.SetValue(edited, updated);
                }
            }
        }

        /// <summary>复制剧情模型的数据图，保留空值，避免预检或改线触碰编辑器正在持有的原定义。</summary>
        public static RevGalDefinition CopyDefinition(RevGalDefinition definition)
        {
            return (RevGalDefinition)CopyValue(definition, typeof(RevGalDefinition));
        }

        private static object CopyValue(object value, Type type)
        {
            if (value == null || type.IsValueType || type == typeof(string))
            {
                return value;
            }
            object copy = Activator.CreateInstance(type);
            if (value is System.Collections.IList items && copy is System.Collections.IList list)
            {
                Type itemType = type.GetGenericArguments()[0];
                foreach (object item in items)
                {
                    list.Add(CopyValue(item, itemType));
                }
                return list;
            }
            foreach (var field in Fields(type))
            {
                field.SetValue(copy, CopyValue(field.GetValue(value), field.FieldType));
            }
            return copy;
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

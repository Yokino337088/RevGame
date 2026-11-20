// RevAutomationContract.cs — 用同一份 Unity DTO 生成工具描述并校验真实调用。
// 只接收可被 JsonUtility 序列化的简单字段。未知类型在登记时失败，避免工具猜测参数。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Revolution
{
    /// <summary>标记可以省略的 DTO 字段，省略时沿用序列化类型的默认值。</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RevAutomationOptionalAttribute : Attribute
    {
    }

    /// <summary>运行期注册与 Editor 导出共用的严格 Schema 生成器。</summary>
    public static class RevAutomationContract
    {
        /// <summary>在登记时生成 Schema；根必须是可序列化 DTO。返回值可供目录和工具发现共用。</summary>
        public static Dictionary<string, object> Describe(Type type)
        {
            Dictionary<string, object> schema = DescribeField(type, new HashSet<Type>());
            if (!Equals(schema["type"], "object"))
            {
                throw new ArgumentException("能力契约根必须是可序列化对象。", nameof(type));
            }
            RevAutomationJson.CheckSchema(schema);
            return schema;
        }

        /// <summary>递归生成 Unity 序列化支持的类型，拒绝循环结构和隐含精度损失。</summary>
        private static Dictionary<string, object> DescribeField(Type type, HashSet<Type> pending)
        {
            if (type == typeof(string))
            {
                return new Dictionary<string, object> { ["type"] = "string" };
            }
            if (type == typeof(bool))
            {
                return new Dictionary<string, object> { ["type"] = "boolean" };
            }
            if (type == typeof(int))
            {
                return new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = (double)int.MinValue, ["maximum"] = (double)int.MaxValue };
            }
            if (type == typeof(float) || type == typeof(double))
            {
                return new Dictionary<string, object> { ["type"] = "number" };
            }
            if (type.IsArray && type.GetArrayRank() == 1 && !type.GetElementType().IsArray)
            {
                return new Dictionary<string, object> { ["type"] = "array", ["items"] = DescribeField(type.GetElementType(), pending) };
            }
            if (!type.IsSerializable || type.IsEnum || type.IsPrimitive || type.IsGenericType
                || type.Namespace == "System" || (type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true)
                || typeof(UnityEngine.Object).IsAssignableFrom(type) || !pending.Add(type))
            {
                throw new ArgumentException("能力契约类型不受支持或存在循环引用：" + type);
            }
            try
            {
                var properties = new Dictionary<string, object>(StringComparer.Ordinal);
                // 与已经解析的 JSON 使用相同的容器类型，使注册和在线校验真正走同一契约。
                var required = new List<object>();
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(row => row.Name, StringComparer.Ordinal))
                {
                    if (field.IsInitOnly || field.IsNotSerialized)
                    {
                        continue;
                    }
                    properties.Add(field.Name, DescribeField(field.FieldType, pending));
                    if (!field.IsDefined(typeof(RevAutomationOptionalAttribute), false))
                    {
                        required.Add(field.Name);
                    }
                }
                return new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false
                };
            }
            finally
            {
                pending.Remove(type);
            }
        }
    }
}
#endif

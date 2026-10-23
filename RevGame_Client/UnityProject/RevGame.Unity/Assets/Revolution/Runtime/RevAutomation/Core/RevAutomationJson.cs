// ============================================================
// RevAutomationJson —— Editor 与 Player 共用的严格 JSON 和 Schema 子集。
// 【为什么】网络/Agent 参数不能依赖 JsonUtility 的静默容错；拼错、溢出应明确失败。
// 【怎么用】Parse 得纯数据，Validate 校验契约，Write 把 DTO/字典写成响应。
// 数字采用 double；超长整数标识请传字符串。Schema 未支持的关键字直接拒绝。
// ============================================================
// 小型、严格的 JSON/参数契约实现，让运行时入口也能拒绝拼错字段及错误类型；不依赖编辑器或第三方包。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Revolution
{
    public static class RevAutomationJson
    {
        public static object Parse(string json)
        {
            if (json == null || json.Length > 4 * 1024 * 1024) { throw new ArgumentException("JSON 缺失或超过 4 MiB"); }
            var reader = new Reader(json); object value = reader.Value(0); reader.White();
            if (reader.At != json.Length) { throw new ArgumentException("JSON 尾部存在多余内容"); }
            return value;
        }

        public static string Write(object value)
        {
            if (value == null) { return "null"; }
            if (value is string text) { return Quote(text); }
            if (value is bool flag) { return flag ? "true" : "false"; }
            if (value is IDictionary map)
            {
                var entries = new List<string>();
                foreach (DictionaryEntry entry in map) { entries.Add(Quote((string)entry.Key) + ":" + Write(entry.Value)); }
                return "{" + string.Join(",", entries) + "}";
            }
            if (value is IEnumerable sequence)
            {
                var entries = new List<string>(); foreach (object item in sequence) { entries.Add(Write(item)); }
                return "[" + string.Join(",", entries) + "]";
            }
            if (value.GetType().IsPrimitive || value is decimal)
            {
                string number = Convert.ToString(value, CultureInfo.InvariantCulture);
                Parse(number); return number;
            }
            string json = JsonUtility.ToJson(value); Parse(json); return json;
        }

        public static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>
            ?? throw new ArgumentException("需要 JSON 对象");

        // 有意支持可移植的 JSON Schema 子集；不识别的约束在注册时拒绝，避免假装已验证。
        public static void CheckSchema(Dictionary<string, object> schema, int depth = 0)
        {
            if (depth > 32) { throw new ArgumentException("Schema 嵌套过深"); }
            string[] supported = { "type", "properties", "required", "additionalProperties", "items", "enum", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "description", "title", "default" };
            if (schema.Keys.Except(supported).Any()) { throw new ArgumentException("不支持的 Schema 关键字：" + string.Join(",", schema.Keys.Except(supported))); }
            if (!schema.TryGetValue("type", out object type) || !(type is string) || !new[] { "object", "array", "string", "integer", "number", "boolean", "null" }.Contains((string)type))
            { throw new ArgumentException("Schema 必须声明单一 type"); }
            if (schema.TryGetValue("additionalProperties", out object extra) && !(extra is bool)) { throw new ArgumentException("additionalProperties 必须为 bool"); }
            var properties = schema.TryGetValue("properties", out object props) ? Object(props) : new Dictionary<string, object>();
            foreach (object child in properties.Values) { CheckSchema(Object(child), depth + 1); }
            if (schema.TryGetValue("items", out object items)) { CheckSchema(Object(items), depth + 1); }
            if (schema.TryGetValue("required", out object required) && (!(required is List<object> names) || names.Any(n => !(n is string) || !properties.ContainsKey((string)n))))
            { throw new ArgumentException("required 必须列出 properties 中的名字"); }
            foreach (string key in new[] { "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems" })
            { if (schema.TryGetValue(key, out object bound) && !(bound is double)) { throw new ArgumentException(key + " 必须为数值"); } }
        }

        public static void Validate(object value, Dictionary<string, object> schema, string path = "$")
        {
            string type = (string)schema["type"];
            bool valid = type == "object" ? value is Dictionary<string, object> : type == "array" ? value is List<object> :
                type == "string" ? value is string : type == "boolean" ? value is bool : type == "null" ? value == null :
                value is double number && (type != "integer" || Math.Truncate(number) == number);
            if (!valid) { throw new ArgumentException(path + " 类型应为 " + type); }
            if (schema.TryGetValue("enum", out object choices) && (!(choices is List<object> list) || !list.Any(c => Write(c) == Write(value))))
            { throw new ArgumentException(path + " 不在 enum 候选值中"); }
            if (value is Dictionary<string, object> obj)
            {
                var props = schema.TryGetValue("properties", out object p) ? Object(p) : new Dictionary<string, object>();
                if (schema.TryGetValue("required", out object required))
                { foreach (string key in ((List<object>)required).Cast<string>()) { if (!obj.ContainsKey(key)) { throw new ArgumentException(path + " 缺少 " + key); } } }
                foreach (var pair in obj)
                {
                    if (props.TryGetValue(pair.Key, out object child)) { Validate(pair.Value, Object(child), path + "." + pair.Key); }
                    else if (schema.TryGetValue("additionalProperties", out object allow) && !(bool)allow) { throw new ArgumentException(path + " 未知字段 " + pair.Key); }
                }
            }
            if (value is List<object> array && schema.TryGetValue("items", out object itemSchema))
            { for (int i = 0; i < array.Count; i++) { Validate(array[i], Object(itemSchema), path + "[" + i + "]"); } }
            foreach (var rule in new[] { ("minimum", false), ("maximum", true), ("minLength", false), ("maxLength", true), ("minItems", false), ("maxItems", true) })
            {
                if (!schema.TryGetValue(rule.Item1, out object bound)) { continue; }
                double size = value is string s ? s.Length : value is List<object> a ? a.Count : value is double n ? n : double.NaN;
                if (double.IsNaN(size) || (rule.Item2 ? size > (double)bound : size < (double)bound)) { throw new ArgumentException(path + " 超出 " + rule.Item1); }
            }
        }

        private static string Quote(string text)
        {
            var b = new StringBuilder("\"");
            foreach (char c in text)
            {
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c < 32) { b.Append("\\u").Append(((int)c).ToString("x4")); }
                else { b.Append(c); }
            }
            return b.Append('"').ToString();
        }

        private sealed class Reader
        {
            private readonly string _text;
            public int At;
            private static readonly Regex Number = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            public Reader(string text) { _text = text; }
            public void White() { while (At < _text.Length && " \n\r\t".IndexOf(_text[At]) >= 0) { At++; } }
            private bool Take(char c) { White(); if (At >= _text.Length || _text[At] != c) { return false; } At++; return true; }
            private void Need(char c) { if (!Take(c)) { throw new ArgumentException("JSON 位置 " + At + " 需要 " + c); } }
            private string String()
            {
                Need('"'); var b = new StringBuilder();
                while (At < _text.Length)
                {
                    char c = _text[At++]; if (c == '"') { return b.ToString(); }
                    if (c < 32) { throw new ArgumentException("JSON 字符串含控制字符"); }
                    if (c != '\\') { b.Append(c); continue; }
                    if (At >= _text.Length) { break; }
                    c = _text[At++];
                    if (c == '"' || c == '\\' || c == '/') { b.Append(c); }
                    else if ("bfnrt".IndexOf(c) >= 0) { b.Append("\b\f\n\r\t"["bfnrt".IndexOf(c)]); }
                    else if (c == 'u' && At + 4 <= _text.Length && ushort.TryParse(_text.Substring(At, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                    { b.Append((char)code); At += 4; }
                    else { throw new ArgumentException("JSON 转义无效"); }
                }
                throw new ArgumentException("JSON 字符串未闭合");
            }
            public object Value(int depth)
            {
                if (depth > 64) { throw new ArgumentException("JSON 嵌套超过 64 层"); }
                White(); if (At >= _text.Length) { throw new ArgumentException("JSON 缺少值"); }
                if (_text[At] == '"') { return String(); }
                if (Take('{'))
                {
                    var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) { return obj; }
                    do { string key = String(); Need(':'); if (obj.ContainsKey(key)) { throw new ArgumentException("JSON 重复字段：" + key); } obj.Add(key, Value(depth + 1)); } while (Take(','));
                    Need('}'); return obj;
                }
                if (Take('['))
                {
                    var list = new List<object>(); if (Take(']')) { return list; }
                    do { list.Add(Value(depth + 1)); } while (Take(',')); Need(']'); return list;
                }
                foreach (string token in new[] { "true", "false", "null" })
                { if (_text.IndexOf(token, At, StringComparison.Ordinal) == At) { At += token.Length; return token == "null" ? null : (object)(token == "true"); } }
                var match = Number.Match(_text, At);
                if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsInfinity(number))
                { throw new ArgumentException("JSON 数值无效，位置 " + At); }
                At += match.Length; return number;
            }
        }
    }
}

#endif

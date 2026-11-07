// RevAutomationEvidence —— 图片以文件保存一次，JSON 只保留路径/类型/大小，避免 base64 把回执撑大。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;

namespace Revolution
{
    public static class RevAutomationEvidence
    {
        public static string WithoutImages(string json)
        {
            object value = RevAutomationJson.Parse(json);
            Trim(value);
            return RevAutomationJson.Write(value);
        }
        private static void Trim(object value)
        {
            if (value is Dictionary<string, object> map)
            {
                map.Remove("base64");
                // JsonUtility 将空的可选字符串写成空串；这类回执没有内嵌 JSON。
                if (map.TryGetValue("dataJson", out object json) && json is string nested && !string.IsNullOrWhiteSpace(nested))
                { map["dataJson"] = WithoutImages(nested); }
                foreach (object child in map.Values) { Trim(child); }
            }
            else if (value is List<object> list) { foreach (object child in list) { Trim(child); } }
        }
    }
}
#endif

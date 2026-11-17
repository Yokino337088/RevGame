using System;
using System.Collections.Generic;
using System.Linq;

namespace Revolution.Gal.Editor
{
    /// <summary>离屏回归读取一个 JSON 用例集，对每条路线单独建立 Gal 会话并收集断言。</summary>
    internal static class RevGalRouteSuite
    {
        /// <summary>工作台和 CLI 都按这份格式验证输入。未知字段属于配置错误，避免拼错期待值时静默通过。</summary>
        private const string Schema = @"{
            ""type"":""object"", ""required"":[""cases""], ""additionalProperties"":false,
            ""properties"":{""cases"":{
                ""type"":""array"", ""minItems"":1, ""maxItems"":64,
                ""items"":{
                    ""type"":""object"", ""required"":[""name"",""operations""], ""additionalProperties"":false,
                    ""properties"":{
                        ""name"":{""type"":""string"",""minLength"":1},
                        ""entry"":{""type"":""string""},
                        ""operations"":{""type"":""array"",""items"":{""type"":""string""}},
                        ""expect"":{""type"":""object""},
                        ""expectRejectedStep"":{""type"":""string"",""minLength"":1}
                    }
                }
            }}
        }";

        /// <summary>先检查所有用例，再执行整批。某条路线失败依然保留其余路线的结果。</summary>
        public static Dictionary<string, object> Run(RevGalStory story, string document)
        {
            var root = RevAutomationJson.Object(RevAutomationJson.Parse(document));
            RevAutomationJson.Validate(root, RevAutomationJson.Object(RevAutomationJson.Parse(Schema)));
            var cases = ((List<object>)root["cases"]).Select(RevAutomationJson.Object).ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in cases)
            {
                string name = (string)item["name"];
                var operations = (List<object>)item["operations"];
                var expected = Get<Dictionary<string, object>>(item, "expect");
                string reject = Get<string>(item, "expectRejectedStep");
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                {
                    throw new ArgumentException("用例名称需要非空而且唯一：" + name);
                }
                if (operations.Count > RevGalTrial.MaximumOperations)
                {
                    throw new ArgumentException(name + " 超过单条路线的步骤上限");
                }
                if ((expected == null || expected.Count == 0) && reject == null)
                {
                    throw new ArgumentException(name + " 需要填写预期状态或被拒的操作");
                }
                if (reject != null && !Equals(operations.LastOrDefault(), reject))
                {
                    throw new ArgumentException(name + " 的预期拒绝操作应放在步骤末尾");
                }
            }

            var results = cases.Select(item => RunCase(story, item)).ToArray();
            int passed = results.Count(row => (bool)row["passed"]);
            return new Dictionary<string, object>
            {
                ["storyId"] = story.Definition.Id, ["passed"] = passed,
                ["failed"] = results.Length - passed, ["cases"] = results
            };
        }

        /// <summary>对一条用例记录失败原因与最终快照。预期的业务拒绝需要在指定步骤发生。</summary>
        private static Dictionary<string, object> RunCase(RevGalStory story, Dictionary<string, object> item)
        {
            var row = new Dictionary<string, object> { ["name"] = item["name"], ["passed"] = false };
            try
            {
                var operations = ((List<object>)item["operations"]).Cast<string>().ToArray();
                string reject = Get<string>(item, "expectRejectedStep");
                var result = RevGalTrial.Run(story, Get<string>(item, "entry"), operations);
                row["state"] = result.Snapshot;
                row["rejectedStep"] = result.RejectedStep;
                int traceCount = ((List<Dictionary<string, object>>)result.Snapshot["trace"]).Count;
                if (result.RejectedStep != reject || reject != null && traceCount != operations.Length)
                {
                    row["message"] = result.Rejection ?? "未发生预期的操作拒绝：" + reject;
                    return row;
                }
                var expected = Get<Dictionary<string, object>>(item, "expect");
                if (expected != null)
                {
                    // 用实际 CLI 使用的 JSON 形状作比较，数值与 DTO 序列化保持一致。
                    var actual = RevAutomationJson.Parse(RevAutomationJson.Write(result.Snapshot));
                    string difference = FirstDifference(expected, actual, "state");
                    if (difference != null)
                    {
                        row["message"] = difference;
                        return row;
                    }
                }
                row["passed"] = true;
                row["message"] = "通过";
            }
            catch (Exception error)
            {
                // 运行异常和正常的拒绝分开，配置 expectRejectedStep 也不会吞掉异常。
                row["message"] = error.GetType().Name + "：" + error.Message;
            }
            return row;
        }

        /// <summary>对象比较预期内的字段，数组比较顺序和长度，返回首个不同的字段路径。</summary>
        private static string FirstDifference(object expected, object actual, string path)
        {
            if (expected is Dictionary<string, object> fields && actual is Dictionary<string, object> values)
            {
                foreach (var field in fields)
                {
                    string child = path + "." + field.Key;
                    if (!values.TryGetValue(field.Key, out var value))
                    {
                        return "实际结果缺少字段：" + child;
                    }
                    string mismatch = FirstDifference(field.Value, value, child);
                    if (mismatch != null)
                    {
                        return mismatch;
                    }
                }
                return null;
            }
            if (expected is List<object> list && actual is List<object> actualList && list.Count == actualList.Count)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    string mismatch = FirstDifference(list[index], actualList[index], $"{path}[{index}]");
                    if (mismatch != null)
                    {
                        return mismatch;
                    }
                }
                return null;
            }
            string wanted = RevAutomationJson.Write(expected);
            string got = RevAutomationJson.Write(actual);
            return wanted == got ? null : $"{path} 期望 {wanted}，实际 {got}";
        }

        /// <summary>从已经验证过的用例读取可选字段，省略时返回空。</summary>
        private static T Get<T>(Dictionary<string, object> item, string key) where T : class
        {
            return item.TryGetValue(key, out var value) ? (T)value : null;
        }
    }
}

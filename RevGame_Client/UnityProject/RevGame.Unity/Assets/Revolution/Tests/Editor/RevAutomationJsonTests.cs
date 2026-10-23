// 使用真实运行时 JSON 内核验证入口边界；这些测试不会碰用户场景或游戏存档。
using System;
using NUnit.Framework;

namespace Revolution.Editor.Tests
{
    public sealed class RevAutomationJsonTests
    {
        [TestCase("{\"id\":1,\"id\":2}")]
        [TestCase("{\"id\":NaN}")]
        [TestCase("{\"id\":1} trailing")]
        [TestCase("{\"id\":01}")]
        public void InvalidJsonIsRejected(string json) => Assert.Throws<ArgumentException>(() => RevAutomationJson.Parse(json));

        [TestCase("{\"limit\":1.5}")]
        [TestCase("{\"limit\":2147483648}")]
        [TestCase("{\"limti\":1}")]
        [TestCase("{\"limit\":true}")]
        public void EditorRejectsTypeAndSpellingErrors(string json)
        {
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(new RevAutomationRequest { action = "gameobject.find", payloadJson = json }));
        }
        [Test] public void JsonSchemaGuardsRequiredBoundsAndUnknownFields()
        {
            var schema = RevAutomationJson.Object(RevAutomationJson.Parse("{\"type\":\"object\",\"properties\":{\"amount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}},\"required\":[\"amount\"],\"additionalProperties\":false}"));
            RevAutomationJson.CheckSchema(schema);
            RevAutomationJson.Validate(RevAutomationJson.Parse("{\"amount\":2}"), schema);
            foreach (string bad in new[] { "{}", "{\"amount\":2.5}", "{\"amount\":0}", "{\"amount\":2,\"typo\":0}" })
            { Assert.Throws<ArgumentException>(() => RevAutomationJson.Validate(RevAutomationJson.Parse(bad), schema)); }
        }
        [Test] public void JsonPreservesUnicodeEscapesAndEmptyValues()
        {
            object value = RevAutomationJson.Parse("{\"中文\":[true,null,\"\\u4f60\\u597d\",\"\"]}");
            string json = RevAutomationJson.Write(value);
            StringAssert.Contains("你好", json);
            Assert.That(RevAutomationJson.Write(RevAutomationJson.Parse(json)), Is.EqualTo(json));
        }
    }
}

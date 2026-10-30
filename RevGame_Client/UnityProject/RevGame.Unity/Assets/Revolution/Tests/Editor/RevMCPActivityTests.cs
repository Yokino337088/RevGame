using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Revolution.Editor;
using UnityEngine;

namespace Revolution.Tests
{
    /// <summary>使用真实 JSONL 文件验证实时窗口读取，临时文件均放 Library，结束恢复桥的活动目录。</summary>
    public sealed class RevMCPActivityTests
    {
        private string _folder;
        [SetUp]
        public void Setup()
        {
            _folder = Path.GetFullPath("Library/RevMCPActivityTests/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            RevAutomationActivity.Configure(_folder);
        }
        [TearDown]
        public void Cleanup()
        {
            RevAutomationActivity.Configure(Path.Combine(RevAutomationBridge.Root, "activity"));
            Directory.Delete(_folder, true);
        }

        [Test]
        public void StartAppearsImmediatelyAndCompletionMergesIntoSameRow()
        {
            var reader = new RevMCPActivityReader(_folder);
            var ticket = RevAutomationActivity.Begin("Unity GM", "gm", "gm.exec", "{\"command\":\"测试/中文 3\"}");
            Assert.That(reader.Poll(), Is.True);
            Assert.That(reader.Entries.Single().state, Is.EqualTo("Running"));
            RevAutomationActivity.Finish(ticket, true, "已加 3 个", new { count = 3 });
            reader.Poll();
            Assert.That(reader.Entries.Count, Is.EqualTo(1));
            Assert.That(reader.Entries.Single().state, Is.EqualTo("Succeeded"));
            Assert.That(reader.Entries.Single().argumentsJson, Does.Contain("测试/中文"));
        }

        [Test]
        public void HalfWrittenUnicodeLineIsIgnoredUntilNewlineArrives()
        {
            string file = Path.Combine(_folder, "agent-fixture.jsonl");
            var entry = new RevAutomationActivityEntry { id = "fixture", action = "测试/查询", state = "Running", utc = DateTime.UtcNow.ToString("O") };
            byte[] line = Encoding.UTF8.GetBytes(JsonUtility.ToJson(entry) + "\n");
            File.WriteAllBytes(file, line.Take(line.Length - 2).ToArray());
            var reader = new RevMCPActivityReader(_folder);
            reader.Poll(); Assert.That(reader.Entries, Is.Empty);
            using (var stream = new FileStream(file, FileMode.Append)) { stream.Write(line, line.Length - 2, 2); }
            reader.Poll(); Assert.That(reader.Entries.Single().action, Is.EqualTo("测试/查询"));
        }

        [Test]
        public void ClearViewDoesNotDeleteFilesAndOnlyShowsNewEvents()
        {
            var reader = new RevMCPActivityReader(_folder);
            RevAutomationActivity.Finish(RevAutomationActivity.Begin("Unity", "editor", "first", "{}"), true, "完成");
            reader.Poll(); reader.ClearView(); reader.Poll();
            Assert.That(reader.Entries, Is.Empty);
            Assert.That(Directory.GetFiles(_folder), Is.Not.Empty);
            RevAutomationActivity.Finish(RevAutomationActivity.Begin("Unity", "editor", "second", "{}"), true, "完成");
            reader.Poll(); Assert.That(reader.Entries.Single().action, Is.EqualTo("second"));
        }

        [Test]
        public void NestedCredentialsAndImagesAreRedactedFromDisk()
        {
            var ticket = RevAutomationActivity.Begin("Unity", "game", "login", "{\"argumentsJson\":\"{\\\"token\\\":\\\"secret-value\\\"}\"}");
            RevAutomationActivity.Finish(ticket, true, "Bearer message-secret", new { base64 = "image-secret", password = "password-secret" });
            string all = string.Join("\n", Directory.GetFiles(_folder).Select(File.ReadAllText));
            foreach (string secret in new[] { "secret-value", "message-secret", "image-secret", "password-secret" })
            { Assert.That(all, Does.Not.Contain(secret)); }
        }

        [Test]
        public void CompletedRecordDoesNotRegressAndReaderSurvivesBadLines()
        {
            string final = JsonUtility.ToJson(new RevAutomationActivityEntry { id = "same", action = "query", state = "Succeeded", utc = DateTime.UtcNow.ToString("O") });
            string start = JsonUtility.ToJson(new RevAutomationActivityEntry { id = "same", action = "query", state = "Running", utc = DateTime.UtcNow.ToString("O") });
            File.WriteAllText(Path.Combine(_folder, "stream.jsonl"), final + "\ninvalid\n" + start + "\n");
            var reader = new RevMCPActivityReader(_folder); reader.Poll();
            Assert.That(reader.Entries.Single().state, Is.EqualTo("Succeeded"));
            Assert.That(reader.InvalidLines, Is.EqualTo(1));
        }

        [Test]
        public void WriterFailureCannotReplaceBusinessReturnValue()
        {
            string blocker = Path.Combine(_folder, "file"); File.WriteAllText(blocker, "fixture");
            RevAutomationActivity.Configure(blocker);
            object result = RevAutomationActivity.TraceRuntime("query", "{}", () => 42);
            Assert.That(result, Is.EqualTo(42));
            Assert.That(RevAutomationActivity.LastError, Is.Not.Empty);
        }

        [Test]
        public void HumanExplanationDescribesOperationWithoutInventingIntent()
        {
            var entry = new RevAutomationActivityEntry { action = "editor.refresh", target = "editor" };
            Assert.That(RevMCPActivityDescription.Title(entry), Is.EqualTo("重新导入修改后的资源"));
            Assert.That(RevMCPActivityDescription.Purpose(entry), Does.Contain("磁盘"));
            Assert.That(entry.intent, Is.Null);
            entry = new RevAutomationActivityEntry { action = "gm.exec", target = "gm", argumentsJson = "{\"command\":\"测试/加金币 3\"}", description = "验证金币真实增加" };
            Assert.That(RevMCPActivityDescription.Title(entry), Does.Contain("测试/加金币 3"));
            Assert.That(RevMCPActivityDescription.Purpose(entry), Does.Contain("金币真实增加"));
        }

        [Test]
        public void OperationDisposeFailureIsLoggedAsFailureAndRethrown()
        {
            var operation = (RevRuntimeOperation)RevAutomationActivity.TraceRuntime("test.wait", "{}", () => new FailingDispose());
            Assert.That(operation.Tick(), Is.True);
            Assert.Throws<InvalidOperationException>(() => operation.Dispose());
            var reader = new RevMCPActivityReader(_folder); reader.Poll();
            Assert.That(reader.Entries.Single().state, Is.EqualTo("Failed"));
            Assert.That(reader.Entries.Single().error, Is.EqualTo("DisposeFailed"));
        }
        private sealed class FailingDispose : RevRuntimeOperation
        {
            public override bool Tick() { Result = "完成"; return true; }
            public override void Dispose() { throw new InvalidOperationException("清理失败"); }
        }

        [TestCase("editor", "gm.exec", "{\"command\":\"测试/加金币 3\"}", "测试/加金币 3")]
        [TestCase("game", "runtime.call", "{\"runtimeAction\":\"gm.exec\",\"argumentsJson\":\"{\\\"command\\\":\\\"测试/设置名字 \\\\\\\"中文 空格\\\\\\\"\\\"}\"}", "测试/设置名字 \"中文 空格\"")]
        [TestCase("mcp", "rev_call", "{\"target\":\"gm\",\"action\":\"测试/加金币\",\"arguments\":{\"arg0\":3}}", "测试/加金币")]
        [TestCase("gm", "测试/加金币", "{\"arg0\":3}", "测试/加金币")]
        public void RuntimeGMShowsExplicitEnvironmentAndActualCommand(string target, string action, string json, string expected)
        {
            var entry = new RevAutomationActivityEntry { target = target, action = action, argumentsJson = json };
            Assert.That(RevMCPActivityDescription.TryGetRuntimeGM(entry, out string command), Is.True);
            Assert.That(command, Does.Contain(expected));
            Assert.That(RevMCPActivityDescription.Title(entry), Does.Contain("游戏运行时"));
            Assert.That(RevMCPActivityDescription.Title(entry), Does.Contain(expected));
            if (target == "gm" || action == "rev_call") { Assert.That(command, Does.Contain("\"arg0\":3")); }
        }

        [TestCase("runtime.capabilities")]
        [TestCase("gm.list")]
        public void DiscoveringGMCommandsIsNotMislabelledAsExecutingGM(string action)
        {
            var entry = new RevAutomationActivityEntry { target = "editor", action = action, argumentsJson = "{}" };
            Assert.That(RevMCPActivityDescription.TryGetRuntimeGM(entry, out _), Is.False);
        }
    }
}

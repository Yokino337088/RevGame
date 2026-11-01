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

        private static System.Collections.Generic.Dictionary<string, object> DiagnosticSnapshot(string directory, string payload = "{}")
        {
            var method = typeof(RevMCPDiagnostics).GetMethod("ReadActivity", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return (System.Collections.Generic.Dictionary<string, object>)method.Invoke(null, new object[] { new RevAutomationRequest { payloadJson = payload }, directory });
        }

        [Test]
        public void DiagnosticsReportsPendingAndContinuesOriginalReadProgress()
        {
            DateTime utc = DateTime.UtcNow.AddHours(-1);
            for (int stream = 0; stream < 16; stream++)
            {
                var contents = new StringBuilder();
                for (int row = 0; row < 100; row++)
                {
                    var entry = new RevAutomationActivityEntry { id = "entry-" + (stream * 100 + row), action = "fixture", target = "editor",
                        state = "Succeeded", utc = utc.AddSeconds(stream * 100 + row).ToString("O") };
                    contents.AppendLine(JsonUtility.ToJson(entry));
                }
                File.WriteAllText(Path.Combine(_folder, "stream-" + stream + ".jsonl"), contents.ToString());
            }
            var result = DiagnosticSnapshot(_folder, "{\"limit\":500}");
            Assert.That(result["pending"], Is.True);
            Assert.That(result["totalIsFinal"], Is.False);
            Assert.That((int)result["polls"], Is.InRange(1, 8));
            for (int retry = 0; retry < 32 && (bool)result["pending"]; retry++)
            { result = DiagnosticSnapshot(_folder, "{\"limit\":500}"); }
            Assert.That(result["pending"], Is.False);
            Assert.That(result["totalIsFinal"], Is.True);
            Assert.That(result["total"], Is.EqualTo(500));
            var entries = (RevAutomationActivityEntry[])result["entries"];
            Assert.That(entries[0].id, Is.EqualTo("entry-1599"));
            Assert.That(entries.Last().id, Is.EqualTo("entry-1100"));
        }

        [Test]
        public void DiagnosticsFiltersAndPagesOnlyTheRetainedView()
        {
            var ticket = RevAutomationActivity.Begin("Unity GM", "gm", "测试/分页", "{}");
            RevAutomationActivity.Finish(ticket, true, "完成");
            RevAutomationActivity.Finish(RevAutomationActivity.Begin("Unity", "editor", "other", "{}"), true, "完成");
            var result = DiagnosticSnapshot(_folder, "{\"target\":\"gm\",\"state\":\"Succeeded\",\"query\":\"分页\",\"limit\":1}");
            Assert.That(result["totalIsFinal"], Is.True);
            Assert.That(result["total"], Is.EqualTo(1));
            Assert.That(((RevAutomationActivityEntry[])result["entries"]).Single().id, Is.EqualTo(ticket.Id));
            result = DiagnosticSnapshot(_folder, "{\"target\":\"gm\",\"offset\":1,\"limit\":1}");
            Assert.That(result["total"], Is.EqualTo(1));
            Assert.That((Array)result["entries"], Is.Empty);
            Assert.That((Array)result["summaries"], Is.Empty);
            Assert.That((string)result["scope"], Does.Contain("不代表全部历史"));
        }

        [Test]
        public void DiagnosticsExcludesHalfAndInvalidLinesFromObservedTotal()
        {
            string file = Path.Combine(_folder, "diagnostic.jsonl");
            var entry = new RevAutomationActivityEntry { id = "fixture", action = "fixture", state = "Succeeded", utc = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(file, "bad-json\n" + JsonUtility.ToJson(entry));
            var result = DiagnosticSnapshot(_folder);
            Assert.That(result["total"], Is.Zero);
            Assert.That(result["invalidLines"], Is.EqualTo(1));
            Assert.That(result["pending"], Is.False);
            File.AppendAllText(file, "\n");
            result = DiagnosticSnapshot(_folder);
            Assert.That(result["total"], Is.EqualTo(1));
            Assert.That(result["totalIsFinal"], Is.True);
        }

        [Test]
        public void DiagnosticsDoesNotReuseRowsAcrossMissingOrDifferentDirectories()
        {
            RevAutomationActivity.Finish(RevAutomationActivity.Begin("Unity", "editor", "first", "{}"), true, "完成");
            Assert.That(DiagnosticSnapshot(_folder)["total"], Is.EqualTo(1));
            string other = Path.Combine(_folder, "other");
            Directory.CreateDirectory(other);
            Assert.That(DiagnosticSnapshot(other)["total"], Is.Zero);
            Assert.That(DiagnosticSnapshot(_folder)["total"], Is.EqualTo(1));
            Directory.Delete(_folder, true);
            try { Assert.That(DiagnosticSnapshot(_folder)["total"], Is.Zero); }
            finally { Directory.CreateDirectory(_folder); }
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
        public void ClearViewKeepsTailForStreamOutsideRecentSixtyFour()
        {
            DateTime baseline = DateTime.UtcNow.AddHours(-1);
            for (int i = 0; i < 65; i++)
            {
                string path = Path.Combine(_folder, "stream-" + i + ".jsonl");
                var entry = new RevAutomationActivityEntry { id = "old-" + i, action = "old", state = "Succeeded", utc = baseline.ToString("O") };
                File.WriteAllText(path, JsonUtility.ToJson(entry) + "\n");
                File.SetLastWriteTimeUtc(path, baseline.AddSeconds(i));
            }
            var reader = new RevMCPActivityReader(_folder);
            reader.ClearView(); reader.Poll();
            Assert.That(reader.Entries, Is.Empty);
            string dormant = Path.Combine(_folder, "stream-0.jsonl");
            var fresh = new RevAutomationActivityEntry { id = "new", action = "new", state = "Succeeded", utc = DateTime.UtcNow.ToString("O") };
            File.AppendAllText(dormant, JsonUtility.ToJson(fresh) + "\n");
            reader.Poll();
            Assert.That(reader.Entries.Count, Is.EqualTo(1));
            Assert.That(reader.Entries.Single().id, Is.EqualTo("new"));
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

        [Test]
        public void IdlePollDoesNotReadBytesOrInvalidateView()
        {
            RevAutomationActivity.Finish(RevAutomationActivity.Begin("Unity", "editor", "query", "{}"), true, "完成");
            var reader = new RevMCPActivityReader(_folder);
            Assert.That(reader.Poll(), Is.True);
            int version = reader.Version;
            Assert.That(reader.Poll(), Is.False);
            Assert.That(reader.LastPollBytes, Is.Zero);
            Assert.That(reader.LastPollLines, Is.Zero);
            Assert.That(reader.Version, Is.EqualTo(version));
            Assert.That(reader.HasPendingData, Is.False);
        }

        private static string FixtureLine(string id, DateTime utc, string message = "")
        {
            return JsonUtility.ToJson(new RevAutomationActivityEntry { id = id, action = "query", state = "Succeeded", utc = utc.ToString("O"), message = message }) + "\n";
        }

        [Test]
        public void PollBudgetIsSharedAcrossStreamsAndRotatesToOtherAgents()
        {
            DateTime now = DateTime.UtcNow;
            for (int stream = 0; stream < 3; stream++)
            {
                string path = Path.Combine(_folder, "stream-" + stream + ".jsonl");
                File.WriteAllText(path, string.Concat(Enumerable.Range(0, 200).Select(i => FixtureLine(stream + "-" + i, now.AddTicks(i)))));
                File.SetLastWriteTimeUtc(path, now.AddSeconds(stream));
            }
            var reader = new RevMCPActivityReader(_folder);
            Assert.That(reader.Poll(), Is.True);
            Assert.That(reader.LastPollLines, Is.EqualTo(RevMCPActivityReader.PollLineBudget));
            Assert.That(reader.LastPollBytes, Is.LessThanOrEqualTo(RevMCPActivityReader.PollByteBudget));
            Assert.That(reader.Entries.All(e => e.id.StartsWith("2-")), Is.True);
            Assert.That(reader.HasPendingData, Is.True);
            reader.Poll();
            Assert.That(reader.Entries.Any(e => e.id.StartsWith("1-")), Is.True);
            Assert.That(reader.LastPollLines, Is.LessThanOrEqualTo(RevMCPActivityReader.PollLineBudget));
        }

        [Test]
        public void MultiPollBacklogRetainsNewestFiveHundredInTimeOrder()
        {
            DateTime now = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(_folder, "history.jsonl"), string.Concat(Enumerable.Range(0, 700).Reverse().Select(i => FixtureLine(i.ToString(), now.AddTicks(i)))));
            var reader = new RevMCPActivityReader(_folder);
            for (int pass = 0; pass < 10; pass++)
            {
                reader.Poll();
                Assert.That(reader.Entries.Count, Is.LessThanOrEqualTo(RevMCPActivityReader.Capacity));
                Assert.That(reader.LastPollBytes, Is.LessThanOrEqualTo(RevMCPActivityReader.PollByteBudget));
                Assert.That(reader.LastPollLines, Is.LessThanOrEqualTo(RevMCPActivityReader.PollLineBudget));
            }
            Assert.That(reader.Entries.Count, Is.EqualTo(500));
            Assert.That(reader.Entries.First().id, Is.EqualTo("200"));
            Assert.That(reader.Entries.Last().id, Is.EqualTo("699"));
            Assert.That(reader.HasPendingData, Is.False);
        }

        [Test]
        public void TruncatedStreamIsReadAgainButOlderCompletionCannotRegress()
        {
            DateTime now = DateTime.UtcNow;
            string path = Path.Combine(_folder, "truncate.jsonl");
            File.WriteAllText(path, FixtureLine("same", now, new string('x', 1000)));
            var reader = new RevMCPActivityReader(_folder); reader.Poll();
            File.WriteAllText(path, FixtureLine("same", now.AddSeconds(-1), "旧结果") + FixtureLine("new", now.AddSeconds(1)));
            reader.Poll();
            Assert.That(reader.Entries.Single(e => e.id == "same").message, Has.Length.EqualTo(1000));
            Assert.That(reader.Entries.Any(e => e.id == "new"), Is.True);
        }

        [Test]
        public void OversizedMalformedLineDoesNotStarveFollowingValidEvent()
        {
            string path = Path.Combine(_folder, "oversized.jsonl");
            File.WriteAllText(path, new string('x', RevMCPActivityReader.PollByteBudget * 2));
            var reader = new RevMCPActivityReader(_folder); reader.Poll();
            File.AppendAllText(path, "\n" + FixtureLine("valid", DateTime.UtcNow));
            for (int pass = 0; pass < 4; pass++) { reader.Poll(); }
            Assert.That(reader.Entries.Single().id, Is.EqualTo("valid"));
        }

        private static object WindowField(RevMCPWindow window, string name)
        {
            return typeof(RevMCPWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);
        }

        private static void WindowInvoke(RevMCPWindow window, string name)
        {
            typeof(RevMCPWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, null);
        }

        [Test]
        public void WindowCacheReusesIdleRowsAndRefreshesGMCompletionAndFilters()
        {
            var reader = new RevMCPActivityReader(_folder);
            var ticket = RevAutomationActivity.Begin("Unity", "editor", "gm.exec", "{\"command\":\"测试/加金币 3\"}");
            reader.Poll();
            var window = ScriptableObject.CreateInstance<RevMCPWindow>();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(RevMCPWindow).GetField("_reader", flags).SetValue(window, reader);
                WindowInvoke(window, "RefreshActivityRows");
                var rows = (System.Collections.IDictionary)WindowField(window, "_rows");
                object running = rows[ticket.Id];
                WindowInvoke(window, "RefreshActivityRows");
                Assert.That(rows[ticket.Id], Is.SameAs(running));
                Assert.That(running.GetType().GetField("RuntimeGM").GetValue(running), Is.EqualTo(true));
                Assert.That(running.GetType().GetField("Title").GetValue(running), Does.Contain("测试/加金币 3"));
                RevAutomationActivity.Finish(ticket, true, "完成"); reader.Poll();
                WindowInvoke(window, "RefreshActivityRows");
                Assert.That(rows[ticket.Id], Is.Not.SameAs(running));
                typeof(RevMCPWindow).GetField("_targetFilter", flags).SetValue(window, 4);
                typeof(RevMCPWindow).GetField("_query", flags).SetValue(window, "加金币");
                WindowInvoke(window, "RefreshActivityFilter");
                Assert.That(((System.Collections.IList)WindowField(window, "_filteredRows")).Count, Is.EqualTo(1));
                typeof(RevMCPWindow).GetField("_selectedId", flags).SetValue(window, ticket.Id);
                typeof(RevMCPWindow).GetField("_stateFilter", flags).SetValue(window, 1);
                WindowInvoke(window, "RefreshActivityFilter");
                Assert.That(((System.Collections.IList)WindowField(window, "_filteredRows")).Count, Is.Zero);
                Assert.That(WindowField(window, "_selectedId"), Is.Null);
                reader.ClearView(); WindowInvoke(window, "RefreshActivityRows");
                Assert.That(rows.Count, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [TestCase(-1f, 0)]
        [TestCase(0f, 0)]
        [TestCase(75f, 0)]
        [TestCase(76f, 1)]
        [TestCase(200f, 2)]
        [TestCase(300f, 3)]
        public void VirtualRowsRespectVariableHeightViewportBoundaries(float scroll, int expected)
        {
            var method = typeof(RevMCPWindow).GetMethod("FirstVisibleRow", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method.Invoke(null, new object[] { new float[] { 76, 200, 300 }, scroll }), Is.EqualTo(expected));
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

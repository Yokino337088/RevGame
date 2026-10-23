// ============================================================
// RevAutomationTests —— 把 Unity Test Runner 接入可查询、有结果证据的工单。
// 【为什么】启动测试不等于测试通过，必须等待 RunFinished 并核对真实用例数量。
// 【重载】tests-started 标记防止重复 Execute；TestJobKey 在引擎完成前拦住变更。
// 【取消】旧 Test Framework 只能取消等待，不能承诺强制停止正在执行的测试。
// ============================================================
// Test Framework 是可选依赖；缺包时本程序集不编译，能力目录中也没有 test.*。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Revolution.Editor.Testing
{
    [InitializeOnLoad]
    public static class RevAutomationTests
    {
        private static readonly TestRunnerApi Api;
        [Serializable] private sealed class TestInfo { public string name; public bool suite; }
        [Serializable] private sealed class TestList { public TestInfo[] tests; }
        [Serializable] private sealed class TestRow { public string name, state, message, stack; public double seconds; }
        [Serializable] private sealed class TestResult
        {
            public bool success, finished;
            public string id, state, message, resultPath;
            public int passed, failed, skipped, inconclusive;
            public double seconds;
            public TestRow[] tests;
        }

        static RevAutomationTests()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.hideFlags = HideFlags.HideAndDontSave;
            Api.RegisterCallbacks(new Callbacks());
        }

        private static TestMode Mode(string mode)
        {
            if (mode == "EditMode") { return TestMode.EditMode; }
            if (mode == "PlayMode") { return TestMode.PlayMode; }
            throw new ArgumentException("mode 为 EditMode 或 PlayMode");
        }

        [RevAutomationAction("test.list", Environment = "live", Keys = new[] { "mode" }, Description = "发现 Unity Test Runner 测试树")]
        public static object List(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var operation = new ListOperation();
            Api.RetrieveTestList(Mode(p.mode == "Single" ? "EditMode" : p.mode), root => operation.Finish(root));
            return operation;
        }

        private sealed class ListOperation : RevAutomationOperation
        {
            private bool _done;
            public void Finish(ITestAdaptor root)
            {
                var rows = new List<TestInfo>();
                void Visit(ITestAdaptor node) { rows.Add(new TestInfo { name = node.FullName, suite = node.IsSuite }); if (node.HasChildren) { foreach (var child in node.Children) { Visit(child); } } }
                Visit(root); Result = new TestList { tests = rows.ToArray() }; _done = true;
            }
            public override bool Tick() => _done;
        }

        [RevAutomationAction("test.run", Environment = "live", Keys = new[] { "mode", "testNames", "categories", "assemblies" }, ResumeAfterReload = true, Description = "运行 EditMode/PlayMode 测试并等待完整结果；零测试判失败")]
        public static object Run(RevAutomationRequest request)
        {
            string work = RevAutomationBridge.JobDirectory(request.id);
            string marker = Path.Combine(work, "tests-started");
            if (!File.Exists(marker))
            {
                RevAutomationActions.RequireEditMode();
                if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
                { throw new InvalidOperationException("运行测试前请先 scene.save；Test Runner 会切换场景"); }
                var active = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic);
                if (active != null && (bool)active.Invoke(null, null)) { throw new InvalidOperationException("已有 Test Runner 测试在执行"); }
                var p = RevAutomationEditorOptions.Read(request);
                var filter = new Filter { testMode = Mode(p.mode == "Single" ? "EditMode" : p.mode), testNames = p.testNames,
                    categoryNames = p.categories, assemblyNames = p.assemblies };
                SessionState.SetString(RevAutomationBridge.TestJobKey, request.id);
                File.WriteAllText(marker, request.id);
                try { Api.Execute(new ExecutionSettings(filter)); }
                catch { SessionState.EraseString(RevAutomationBridge.TestJobKey); throw; }
            }
            return new RunOperation(request.id);
        }

        [RevAutomationAction("test.result", Environment = "either", Keys = new[] { "id" }, Description = "查询测试工单；取消等待后测试引擎可能仍在运行")]
        public static object Result(RevAutomationRequest request)
        {
            string id = RevAutomationEditorOptions.Read(request).id;
            string path = Path.Combine(RevAutomationBridge.JobDirectory(id), "tests-result.json");
            if (File.Exists(path)) { return JsonUtility.FromJson<TestResult>(File.ReadAllText(path)); }
            if (!File.Exists(Path.Combine(RevAutomationBridge.JobDirectory(id), "tests-started"))) { throw new ArgumentException("这不是一个已启动的测试工单"); }
            return new TestResult { id = id, finished = false, state = SessionState.GetString(RevAutomationBridge.TestJobKey, "") == id ? "Running" : "Interrupted", resultPath = path };
        }

        private sealed class RunOperation : RevAutomationOperation
        {
            private readonly string _id;
            public RunOperation(string id) { _id = id; }
            public override bool Tick()
            {
                string path = Path.Combine(RevAutomationBridge.JobDirectory(_id), "tests-result.json");
                if (!File.Exists(path)) { return false; }
                var result = JsonUtility.FromJson<TestResult>(File.ReadAllText(path));
                Result = result; Success = result.success;
                Message = Success ? "测试通过" : "测试失败、无匹配测试或存在未完成用例；详见 tests-result.json";
                return true;
            }
            // 1.1 的公开 API 无法强制中断；桥接仍用 TestJobKey 阻止后续变更，直到测试结束。
            public override void Cancel() { base.Cancel(); Result = new TestResult { id = _id, finished = false, state = "CancellationRequested" }; }
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void OnError(string message) => Finish(new TestResult { state = "Error", message = message });
            public void RunFinished(ITestResultAdaptor result)
            {
                var rows = new List<TestRow>();
                void Visit(ITestResultAdaptor node)
                {
                    if (!node.Test.IsSuite) { rows.Add(new TestRow { name = node.FullName, state = node.ResultState, message = node.Message, stack = node.StackTrace, seconds = node.Duration }); }
                    if (node.HasChildren) { foreach (var child in node.Children) { Visit(child); } }
                }
                Visit(result);
                Finish(new TestResult { success = result.ResultState == "Passed" && result.PassCount > 0 && result.FailCount == 0 && result.InconclusiveCount == 0 && result.SkipCount == 0,
                    state = result.ResultState, passed = result.PassCount, failed = result.FailCount, skipped = result.SkipCount, inconclusive = result.InconclusiveCount,
                    seconds = result.Duration, tests = rows.ToArray(), message = result.Message });
            }
            private static void Finish(TestResult result)
            {
                string id = SessionState.GetString(RevAutomationBridge.TestJobKey, "");
                if (id.Length == 0) { return; }
                result.id = id; result.finished = true; result.resultPath = Path.Combine(RevAutomationBridge.JobDirectory(id), "tests-result.json");
                RevAutomationBridge.WriteJson(result.resultPath, result);
                SessionState.EraseString(RevAutomationBridge.TestJobKey);
            }
        }
    }
}

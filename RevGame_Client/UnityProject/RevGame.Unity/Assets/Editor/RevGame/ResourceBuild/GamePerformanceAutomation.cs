// ============================================================
// GamePerformanceAutomation.cs —— 只接固定项目测试方法，不开放任意反射执行。
// begin只接受采样范围，state用于确认完整段数/错误/清理；started不能当成性能达标。
// ============================================================
using System;
using Revolution.Editor;
using UnityEngine;
namespace RevGame.Editor
{
    /// <summary>固定项目测量入口，只接受采样范围。异步作业状态和清理必须另行等待，不把started当通过。</summary>
    public static class GamePerformanceAutomation
    {
        // 参数只控制测试工作量；Begin的真实夹具再检查章节、时长、次数和隔离进度前置条件。
        [Serializable]
        private sealed class Options
        {
            public string label = "repeat";
            public int first = 1, last = 6, seconds = 20, repeats = 1;
        }
        [Serializable] private sealed class Result { public string text; }
        [RevAutomationAction("game.perf.begin",Environment="play",Keys=new[]{"label","first","last","seconds","repeats"},Description="固定48棋预分配采样帧/GPU/GC摘要，无Profiler导出并正式回首页")]
        public static object Begin(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<Options>(request.payloadJson) ?? new Options();
            return Invoke("Begin", new object[] { options.label, options.first, options.last, options.seconds, options.repeats });
        }
        [RevAutomationAction("game.perf.state",Environment="play",Keys=new string[0],Description="只读唯一runId的测量摘要、完成状态与清理")]
        public static object State(RevAutomationRequest request) { return Invoke("State",null); }
        [RevAutomationAction("game.perf.cancel",Environment="play",Keys=new string[0],Description="取消当前测量并执行正式回首页收尾")]
        public static object Cancel(RevAutomationRequest request) { return Invoke("Cancel",null); }
        [RevAutomationAction("game.perf.feedback-begin",Environment="play",Keys=new string[0],Description="发起密集反馈回归，按阶段轮询完成状态，不阻塞MCP超时")]
        public static object FeedbackBegin(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            type.GetMethod("Begin").Invoke(null, null);
            return new Result { text = (string)type.GetMethod("State").Invoke(null, null) };
        }
        [RevAutomationAction("game.perf.feedback-state",Environment="play",Keys=new string[0],Description="只读密集反馈阶段、结果与已执行断言数")]
        public static object FeedbackState(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("State").Invoke(null, null) };
        }
        private static object Invoke(string method, object[] args)
        {
            try
            {
                var type = Type.GetType("RevGame.Editor.GameBattleMeasuredChecks, Assembly-CSharp-Editor", true);
                return new Result { text = (string)type.GetMethod(method).Invoke(null, args) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                // 反射包装不能掩盖业务拒绝原因，直接向自动化返回真实失败。
                throw error.InnerException ?? error;
            }
        }
    }
}

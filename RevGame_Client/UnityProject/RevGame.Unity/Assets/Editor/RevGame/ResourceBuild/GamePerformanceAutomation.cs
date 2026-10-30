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
        // RevMCP JSON参数的容器，默认值只控制测试范围，不更改正式玩法数值。真实Begin会再校验边界。
        private sealed class Options
        {
            // 人类可读标签；唯一作业身份由夹具生成runId，不能拿label相同就覆盖解释旧报告。
            public string label = "repeat";
            // 起止章1～6，每段10～600秒，重复1～10次；这些上限防误启动无界作业。
            public int first = 1, last = 6, seconds = 20, repeats = 1;
        }
        // 桥接层只包装text，里面的JSON仍需解析并核对status/errors/cleaned；工具成功不等于战斗通过。
        [Serializable] private sealed class Result { public string text; }
        [RevAutomationAction("game.perf.begin",Environment="play",Keys=new[]{"label","first","last","seconds","repeats"},Description="固定48棋预分配采样帧/GPU/GC摘要，无Profiler导出并正式回首页")]
        // 启动固定类型的异步作业并返回“运行中”；调用方后续查State，不阻塞MCP等十分钟而超时。
        public static object Begin(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<Options>(request.payloadJson) ?? new Options();
            return Invoke("Begin", new object[] { options.label, options.first, options.last, options.seconds, options.repeats });
        }
        [RevAutomationAction("game.perf.state",Environment="play",Keys=new string[0],Description="只读唯一runId的测量摘要、完成状态与清理")]
        // 只读当前唯一作业结果；动作不重新开始压测。
        public static object State(RevAutomationRequest request) { return Invoke("State",null); }
        [RevAutomationAction("game.perf.cancel",Environment="play",Keys=new string[0],Description="取消当前测量并执行正式回首页收尾")]
        // 转交取消请求，让夹具finally正常回首页；不能把工具返回“已请求”解释为“已清理”。
        public static object Cancel(RevAutomationRequest request) { return Invoke("Cancel",null); }
        [RevAutomationAction("game.perf.feedback-begin",Environment="play",Keys=new string[0],Description="发起密集反馈回归，按阶段轮询完成状态，不阻塞MCP超时")]
        // 启动真实AB表现夹具；其Stage/Result供后续查询，内部finally负责回首页和只还自己的积分租约。
        public static object FeedbackBegin(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            type.GetMethod("Begin").Invoke(null, null);
            return new Result { text = (string)type.GetMethod("State").Invoke(null, null) };
        }
        [RevAutomationAction("game.perf.feedback-state",Environment="play",Keys=new string[0],Description="只读密集反馈阶段、结果与已执行断言数")]
        // 只查当前夹具进度/断言，不重复注入反馈，因此安全等待较长视觉检查。
        public static object FeedbackState(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GameReadabilityChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("State").Invoke(null, null) };
        }
        // Unity程序集接线只反射固定类型及内部固定方法名，不接受用户提供任意类型或代码。
        // 反射异常拆出真实InnerException，避免把玩法拒绝原因藏在“调用失败”包装里。
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

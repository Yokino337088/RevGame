using System;
using System.Reflection;
using Revolution.Editor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>本机性能采样的固定MCP接线；只桥接项目夹具，不提供任意反射目标。开始立即返回，状态查询避免长工单超时。</summary>
    public static class GameDeviceProfileAutomation
    {
        [Serializable] private sealed class Options { public string label = "baseline"; public int chapter; public int seconds = 20; }
        [Serializable] private sealed class Result { public string status, progress; }
        private static Type Checks { get { return Type.GetType("RevGame.Editor.GameBattleDeviceProfileChecks, Assembly-CSharp-Editor", true); } }

        [RevAutomationAction("game.device-profile.begin", Environment = "play", Keys = new[] { "label", "chapter", "seconds" }, Description = "关闭常规日志采集CPU/GPU/内存；label=baseline/repeat/background-off/hud-off/original-sync，chapter=0六章或1~6，seconds=10~90")]
        public static object Begin(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<Options>(request.payloadJson) ?? new Options();
            try { Checks.GetMethod("Begin").Invoke(null, new object[] { options.label, options.chapter, options.seconds }); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            return State(request);
        }

        [RevAutomationAction("game.device-profile.state", Environment = "play", Keys = new string[0], Description = "只读本次采样进度、断言和清理结果")]
        public static object State(RevAutomationRequest request)
        {
            return new Result { status = (string)Checks.GetProperty("LastResult").GetValue(null), progress = (string)Checks.GetProperty("Progress").GetValue(null) };
        }
    }
}

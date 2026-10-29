using System;
using Revolution.Editor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>编辑器桥只暴露固定Boss表现检查，不接受任意源码/类型/方法输入；当前框架没有ReadOnly特性字段。</summary>
    public static class GameBossImpactAutomation
    {
        [Serializable] private sealed class Options { public int first = 1, last = 6, fromSkill = 0, toSkill = 17; public bool extended = true; }
        [Serializable] private sealed class Result { public string text; }

        [RevAutomationAction("game.boss-impact.begin", Environment = "play", Keys = new[] { "first", "last", "extended", "fromSkill", "toSkill" }, Description = "高生命隔离靶場逐招真实Boss技能/音频/镜头/棋格Play验收")]
        public static object Begin(RevAutomationRequest request)
        {
            Options o = JsonUtility.FromJson<Options>(request.payloadJson) ?? new Options();
            return Invoke("Begin", new object[] { o.first, o.last, o.extended, o.fromSkill, o.toSkill });
        }

        [RevAutomationAction("game.boss-impact.state", Environment = "play", Keys = new string[0], Description = "只读本次Boss视觉Play案例、断言、状态与证据目录")]
        public static object State(RevAutomationRequest request) { return Invoke("State", null); }

        [RevAutomationAction("game.boss-impact.cancel", Environment = "play", Keys = new string[0], Description = "取消Boss视觉Play并执行正式返回首页清理")]
        public static object Cancel(RevAutomationRequest request) { return Invoke("Cancel", null); }

        private static object Invoke(string method, object[] args)
        {
            try
            {
                Type type = Type.GetType("RevGame.Editor.GameBossImpactPlayChecks, Assembly-CSharp-Editor", true);
                return new Result { text = (string)type.GetMethod(method).Invoke(null, args) };
            }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }
}

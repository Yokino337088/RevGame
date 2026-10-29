// 固定动作白名单；ResourceBuild独立程序集通过现有Editor桥调用主编辑器程序集，不执行输入代码。
using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    public static class GameBossFeedbackAutomation
    {
        [Serializable] private sealed class Result { public string text; }

        [RevAutomationAction("game.boss-feedback.audit", Environment = "edit", Keys = new string[0], Description = "Boss三十招声音映射、材质参数、真实命中震动筛选审计")]
        public static object Audit(RevAutomationRequest request)
        {
            return Invoke("Audit");
        }

        [RevAutomationAction("game.boss-feedback.state", Environment = "play", Keys = new string[0], Description = "只读Boss特效组数、动态棋格、命中震动和声音请求计数")]
        public static object State(RevAutomationRequest request)
        {
            return Invoke("State");
        }

        private static object Invoke(string method)
        {
            try
            {
                var type = Type.GetType("RevGame.Editor.GameBossFeedbackChecks, Assembly-CSharp-Editor", true);
                return new Result { text = (string)type.GetMethod(method).Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }
}

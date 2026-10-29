using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>
    /// 资源工具程序集不能直接引用Assembly-CSharp-Editor，因此沿项目惯例跨程序集调用唯一固定检查类。
    /// 动作不接收类型/方法参数，外部MCP只能触发这项已登记的规则检查，不能执行任意反射代码。
    /// </summary>
    public static class GameBossSupportAutomation
    {
        /// <summary>桥接数据遵循已有text响应约定；内容是检查器的JSON，调用方须进一步断言passed与检查数。</summary>
        [Serializable]
        private sealed class Result
        {
            public string text;
        }

        [RevAutomationAction("game.boss-support.rules", Environment = "edit", Keys = new string[0], Description = "正式配置下检查最后单棋保护与六章Boss分组轮换、独立冷却、撤销清理；不运行180回合")]
        public static object Rules(RevAutomationRequest request)
        {
            try
            {
                Type checks = Type.GetType("RevGame.Editor.GameBossSupportChecks, Assembly-CSharp-Editor", true);
                return new Result { text = (string)checks.GetMethod("Run").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                // 透出原始中文断言，工具失败能直接说明哪条业务契约未满足。
                throw error.InnerException ?? error;
            }
        }
    }
}

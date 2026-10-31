using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>自动化注册必须位于已有ResourceBuild编辑器程序集；固定反射调用业务编辑器检查，避免反向程序集依赖。</summary>
    public static class GameEnemyRangedAutomation
    {
        [RevAutomationAction("game.enemy-ranged.rules", Environment = "edit", Keys = new string[0], Description = "敌方远程攻击与攻速+50%的真实伤害/FSM/边界回归")]
        public static object Run(RevAutomationRequest request)
        {
            try
            {
                Type type = Type.GetType("RevGame.Editor.GameEnemyRangedChecks, Assembly-CSharp-Editor", true);
                return type.GetMethod("Run").Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }
}

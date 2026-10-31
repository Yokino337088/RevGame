using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>沿用项目固定反射接线跨编辑器程序集；只暴露已审查的攻击断言入口，不接受任意类型或方法。</summary>
    public static class GamePieceAttackAutomation
    {
        [RevAutomationAction("game.piece-attacks.rules", Environment = "edit", Keys = new string[0], Description = "九棋种攻击范围、目标数量、真实伤害、边界及清理断言")]
        public static object Run(RevAutomationRequest request)
        {
            try
            {
                Type type = Type.GetType("RevGame.Editor.GamePieceAttackChecks, Assembly-CSharp-Editor", true);
                return type.GetMethod("Run").Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }
}

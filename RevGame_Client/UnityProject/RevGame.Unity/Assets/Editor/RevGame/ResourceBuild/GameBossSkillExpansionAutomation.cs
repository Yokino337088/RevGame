using System;
using Revolution.Editor;
namespace RevGame.Editor
{
    /// <summary>显式白名单动作，不接收任意类型/方法字符串；发现后才由RevMCP调用。</summary>
    public static class GameBossSkillExpansionAutomation
    {
        [Serializable] private sealed class Result { public string text; }
        [RevAutomationAction("game.boss-skills.rules", Environment="edit", Keys=new string[0], Description="六章自然轮换、移动、十二专属技能与控制清理断言")]
        public static object Rules(RevAutomationRequest request)
        {
            try { return new Result { text = (string)Type.GetType("RevGame.Editor.GameBossSkillExpansionChecks, Assembly-CSharp-Editor", true).GetMethod("Run").Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }
}

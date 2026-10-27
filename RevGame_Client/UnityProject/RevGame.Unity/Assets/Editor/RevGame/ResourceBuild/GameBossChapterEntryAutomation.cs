using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    public static class GameBossChapterEntryAutomation
    {
        [RevAutomationAction("game.boss-chapters.entry-check", Environment = "edit", Keys = new string[0], Description = "六章真实Boss准备回合和GM目录离线断言，不切用户场景")]
        public static object Check(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameBossChapterEntryChecks, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("Run").Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}

using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    public static class GameBossKeyframeAutomation
    {
        [RevAutomationAction("game.boss-keyframes.configure", Environment = "edit", Keys = new string[0], Description = "装配六章Boss各8张关键帧，不修改玩法/用户场景")]
        public static object Configure(RevAutomationRequest request) { return Call("Configure"); }
        [RevAutomationAction("game.boss-keyframes.check", Environment = "edit", Keys = new string[0], Description = "验证48帧、章节映射及独立分包")]
        public static object Check(RevAutomationRequest request) { return Call("Check"); }
        [RevAutomationAction("game.boss-keyframes.preview", Environment = "edit", Keys = new string[0], Description = "隔离真实AB绘制六章Boss换帧并检查回池清理")]
        public static object Preview(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameBossKeyframePreview, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("Run").Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static object Call(string method)
        {
            Type type = Type.GetType("RevGame.Editor.GameBossKeyframeBuild, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod(method).Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}

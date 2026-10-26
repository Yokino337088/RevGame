using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>只暴露固定棋盘资产配置与验收入口；不允许外部指定任意反射目标。</summary>
    public static class GameBoardMagicAutomation
    {
        [RevAutomationAction("game.board-magic.configure", Environment = "edit", Keys = new string[0], Description = "装配六章棋盘材质、绿色落点及眼睛序列帧并配置AB")]
        public static object Configure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameBoardMagicBuild, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [RevAutomationAction("game.board-magic.preview", Environment = "edit", Keys = new string[0], Description = "独立预览场景绘制六章真实棋格与绿色落点，不切换用户场景")]
        public static object Preview(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameBoardMagicPreview, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("Capture").Invoke(null, null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}

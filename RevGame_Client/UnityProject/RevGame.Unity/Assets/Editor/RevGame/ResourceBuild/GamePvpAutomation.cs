using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>仅开放固定PvP配方，不暴露任意脚本或反射入口。</summary>
    public static class GamePvpAutomation
    {
        [RevAutomationAction("game.pvp.build", Environment = "edit", Keys = new string[0], Description = "制作九路围棋场景、棋盘Shader材质和联机面板")]
        public static object Build(RevAutomationRequest request)
        {
            try
            {
                return new Result { text = (string)Type.GetType("RevGame.Editor.GamePvpBuild, Assembly-CSharp-Editor", true).GetMethod("Build").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.ToString() ?? error.ToString());
            }
        }
        [Serializable]
        private sealed class Result
        {
            public string text;
        }
    }
}

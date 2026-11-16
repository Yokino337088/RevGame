using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>从活动编辑器调用项目书固定装配配方，资产写回由 Unity 序列化。</summary>
    public static class GameProjectOpenSourceBuildAction
    {
        [RevAutomationAction("game.project-book.configure", Environment = "edit", Keys = new string[0], Description = "装配项目开源书本和大厅入口，保留作者配置")]
        public static object Configure(RevAutomationRequest request)
        {
            Type build = Type.GetType("RevGame.Editor.GameProjectOpenSourceBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)build.GetMethod("Configure").Invoke(null, null) };
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }

        [Serializable]
        private sealed class Result
        {
            public string text;
        }
    }
}

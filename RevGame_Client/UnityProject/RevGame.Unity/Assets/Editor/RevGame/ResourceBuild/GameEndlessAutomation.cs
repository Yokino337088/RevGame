using System;
using Revolution.Editor;
namespace RevGame.Editor
{
    /// <summary>只公开固定无尽资产制作配方，不提供任意反射执行入口。</summary>
    public static class GameEndlessAutomation
    {
        [RevAutomationAction("game.endless.publish", Environment = "edit", Keys = new string[0], Description = "通过项目已有发布入口复制已构建PC资源到StreamingAssets")]
        public static object Publish(RevAutomationRequest request)
        {
            return new Result
            {
                text = (string)Type.GetType("RevGame.Editor.GameEndlessBuild, Assembly-CSharp-Editor", true).GetMethod("Publish").Invoke(null, null)
            };
        }
        [Serializable] private sealed class Result
        {
            public string text;
        }
        [RevAutomationAction("game.endless.build", Environment = "edit", Keys = new string[0], Description = "制作无尽独立场景、HUD、入口、棋盘和共享Shader材质")]
        public static object Build(RevAutomationRequest request)
        {
            try
            {
                return new Result
                {
                    text = (string)Type.GetType("RevGame.Editor.GameEndlessBuild, Assembly-CSharp-Editor", true).GetMethod("Build").Invoke(null, null)
                };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.ToString() ?? error.ToString());
            }
        }
    }
}

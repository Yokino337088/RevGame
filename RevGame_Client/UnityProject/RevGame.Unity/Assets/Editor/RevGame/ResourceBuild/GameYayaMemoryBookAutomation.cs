using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>供活动编辑器调用固定制作配方和隔离存档检查。</summary>
    public static class GameYayaMemoryBookAutomation
    {
        [RevAutomationAction("game.memories.configure", Environment = "edit", Keys = new string[0], Description = "装配鸦鸦收藏书、六章原画及大厅入口")]
        public static object Configure(RevAutomationRequest request)
        {
            return Call("Configure");
        }

        [RevAutomationAction("game.memories.persistence", Environment = "edit", Keys = new string[0], Description = "校验临时存档键的通关、已阅和重新读取")]
        public static object Persistence(RevAutomationRequest request)
        {
            return Call("CheckProgress");
        }

        [RevAutomationAction("game.memories.restart-arm", Environment = "edit", Keys = new string[0], Description = "给多次隔离Play分配临时磁盘收藏存档")]
        public static object ArmRestart(RevAutomationRequest request)
        {
            return Call("ArmRestartCheck");
        }

        [RevAutomationAction("game.memories.restart-disarm", Environment = "edit", Keys = new string[0], Description = "清理重启验收使用的临时收藏存档")]
        public static object DisarmRestart(RevAutomationRequest request)
        {
            return Call("DisarmRestartCheck");
        }

        private static object Call(string method)
        {
            Type type = Type.GetType("RevGame.Editor.GameYayaMemoryBookBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod(method).Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
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

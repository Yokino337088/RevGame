using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>显式MCP装配入口。程序集只能固定调用项目故事工具，不提供任意反射执行。</summary>
    public static class GameStoryAutomation
    {
        [RevAutomationAction("game.story.progress-check", Environment = "edit", Keys = new string[0], Description = "临时存档键验证首次开场/教学持久化及测试隔离")]
        public static object CheckProgress(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameStoryBuild, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("CheckProgress", BindingFlags.Public | BindingFlags.Static).Invoke(null, null) };
        }
        [Serializable] private sealed class TestOptions { public int flags; }
        [RevAutomationAction("game.story.test-arm", Environment = "edit", Keys = new[] { "flags" }, Description = "下次Play使用内存剧情进度；flags=0首次/1仅开场完成/3全完成")]
        public static object Arm(RevAutomationRequest request)
        {
            var options = UnityEngine.JsonUtility.FromJson<TestOptions>(request.payloadJson);
            int flags = options == null ? 0 : options.flags;
            if (flags != 0 && flags != 1 && flags != 3) { throw new ArgumentException("flags只支持0、1、3"); }
            UnityEditor.SessionState.SetBool("RevGame.Story.IsolatedTest", true);
            UnityEditor.SessionState.SetInt("RevGame.Story.TestFlags", flags);
            return new Result { text = "isolated" };
        }
        [RevAutomationAction("game.story.test-disarm", Environment = "edit", Keys = new string[0], Description = "退出测试后恢复正常启动存档选择")]
        public static object Disarm(RevAutomationRequest request)
        {
            UnityEditor.SessionState.EraseBool("RevGame.Story.IsolatedTest");
            UnityEditor.SessionState.EraseInt("RevGame.Story.TestFlags");
            return new Result { text = "normal" };
        }
        [RevAutomationAction("game.story.configure", Environment = "edit", Keys = new string[0], Description = "创建剧情SO、下方对话框和真实棋盘引导并配置资源")]
        public static object Configure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameStoryBuild, Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static).Invoke(null, null) }; }
            catch (TargetInvocationException error)
            {
                // 保留真实资产门禁错误，MCP才知道哪个资源不合规，而不只看到“反射调用失败”。
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}

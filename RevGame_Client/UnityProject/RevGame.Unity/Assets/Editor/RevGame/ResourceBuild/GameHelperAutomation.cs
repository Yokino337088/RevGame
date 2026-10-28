using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>编译成功后调用固定新增资源配方，编辑器自动化不支持任意反射方法。</summary>
    public static class GameHelperAutomation
    {
        [Serializable] private sealed class Result { public string text; }
        [RevAutomationAction("game.helper.build", Environment="edit", Keys=new string[0], Description="制作帮手四帧预制体、台词框、移除按钮、六章弹幕与蓝束材质")]
        public static object Build(RevAutomationRequest request)
        {
            return new Result { text = (string)Type.GetType("RevGame.Editor.GameHelperBuild, Assembly-CSharp-Editor", true).GetMethod("Build").Invoke(null, null) };
        }
        [RevAutomationAction("game.helper.rules", Environment="edit", Keys=new string[0], Description="检查移除、45吞噬、30远程、帮手积分与敌人跳跃的真实规则")]
        public static object Rules(RevAutomationRequest request)
        {
            return new Result { text = (string)Type.GetType("RevGame.Editor.GameHelperChecks, Assembly-CSharp-Editor", true).GetMethod("Run").Invoke(null, null) };
        }
        [RevAutomationAction("game.helper.play", Environment="play", Keys=new string[0], Description="隔离Play六章四帧、移除UI、弹幕与蓝束真实视觉验证")]
        public static object Play(RevAutomationRequest request)
        {
            return new Result { text = (string)Type.GetType("RevGame.Editor.GameHelperPlayChecks, Assembly-CSharp-Editor", true).GetMethod("Begin").Invoke(null, null) };
        }
        [RevAutomationAction("game.helper.state", Environment="play", Keys=new string[0], Description="读取帮手Play验收断言、错误和状态")]
        public static object State(RevAutomationRequest request)
        {
            return new Result { text = (string)Type.GetType("RevGame.Editor.GameHelperPlayChecks, Assembly-CSharp-Editor", true).GetMethod("State").Invoke(null, null) };
        }
        [RevAutomationAction("game.helper.atlas-list", Environment="edit", Keys=new string[0], Description="只读当前实际程序集中的图集生成清单")]
        public static object AtlasList(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Generated.GameSpriteAtlases, Assembly-CSharp", true);
            return new Result { text = string.Join(",", (string[])type.GetField("Names").GetValue(null)) };
        }
        [RevAutomationAction("game.helper.recompile", Environment="edit", Keys=new string[0], Description="图集生成脚本修改后显式请求脚本编译，不修改场景")]
        public static object Recompile(RevAutomationRequest request)
        {
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            return new Result { text = "requested" };
        }
    }
}

// 资源工具程序集只调用固定的项目制作入口，沿用现有RevMCP桥，不开放任意反射或任意代码执行。
using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>将固定项目制作/检查入口接到既有MCP注册表，不承载运行时业务或第二套资源加载逻辑。</summary>
    public static class GameElementalEffectAutomation
    {
        [RevAutomationAction("game.elemental-fx.configure", Environment = "edit", Keys = new string[0], Description = "制作并绑定火焰、水浪、圣光三份共享Shader材质")]
        public static object Configure(RevAutomationRequest request)
        {
            return Invoke("Configure");
        }

        [RevAutomationAction("game.elemental-fx.validate", Environment = "edit", Keys = new string[0], Description = "检查元素共享材质、离散帧参数和FullRect载体")]
        public static object Validate(RevAutomationRequest request)
        {
            return Invoke("Validate");
        }

        /// <summary>只允许本类传入的固定方法名，拆出真实异常，让编译或引用失败的首因直接显示给调用者。</summary>
        private static object Invoke(string method)
        {
            try
            {
                Type type = Type.GetType("RevGame.Editor.GameElementalEffectBuild, Assembly-CSharp-Editor", true);
                return new Result { text = (string)type.GetMethod(method).Invoke(null, null) };
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }

        [Serializable]
        private sealed class Result
        {
            /// <summary>项目制作入口的真实结果文字，异常由桥报告失败，不以默认成功替代。</summary>
            public string text;
        }
    }
}

// 固定项目制作/检查的跨程序集适配器，CLI只调用已知方法；没有任意反射执行接口。
using System;
using System.Reflection;
using Revolution.Editor;
namespace RevGame.Editor
{
    /// <summary>局内互动制作走RevCLI，业务验收走实际GM/RevRuntimeAutomation。</summary>
    public static class GameYayaReactionAutomation
    {
        /// <summary>固定规则检查入口，异常解除反射包装保留真实原因。</summary>
        [RevAutomationAction("game.yaya-reaction.rules", Environment = "edit", Keys = new string[0], Description = "校验Excel台词和8项表现分类边界")]
        public static object Rules(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameYayaReactionChecks, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("Rules").Invoke(null, null) };
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
        /// <summary>自动化协议字段DTO，避免匿名属性被Unity序列化丢失。</summary>
        [Serializable]
        private sealed class Result
        {
            /// <summary>检查或制作的结果摘要，不代表实际玩法验收通过。</summary>
            public string text;
        }
        /// <summary>本次局部更新只改积分显示与六章场景参数，避免重做全部角色资源。</summary>
        [RevAutomationAction("game.yaya-reaction.readability", Environment = "edit", Keys = new string[0], Description = "放大六章鸦鸦立绘并统一积分弹幕背景与大字布局")]
        public static object Readability(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameYayaReactionBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("ConfigureReadability").Invoke(null, null) };
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
        /// <summary>只调用已知项目配方，资源/Prefab/场景由Unity API保存并保留GUID。</summary>
        [RevAutomationAction("game.yaya-reaction.configure", Environment = "edit", Keys = new string[0], Description = "制作六章/无尽场景鸦鸦互动、统一差分和世界台词框")]
        public static object Configure(RevAutomationRequest request)
        {
            try
            {
                Type type = Type.GetType("RevGame.Editor.GameYayaReactionBuild, Assembly-CSharp-Editor", true);
                return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
    }
}

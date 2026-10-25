// ============================================================
// GameEyeAutomation.cs —— 眼位改造的固定自动化入口。
// 资源程序集只能引用固定编辑器夹具，不能接受任意反射目标；业务验收仍调用真实服务。
// ============================================================
using System;
using System.Reflection;
using Revolution.Editor;

namespace RevGame.Editor
{
    public static class GameEyeAutomation
    {
        [Serializable] private sealed class Response { public string text; }
        private static Type Checks => Type.GetType("RevGame.Editor.GameEyeChecks, Assembly-CSharp-Editor", true);

        [RevAutomationAction("game.eye.configure", Environment = "edit", Keys = new string[0], Description = "替换卡通双眼图片且保留棋盘其余配置")]
        public static object Configure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameEyeBuilder, Assembly-CSharp-Editor", true);
            return new Response { text = (string)type.GetMethod("Configure").Invoke(null, null) };
        }

        [RevAutomationAction("game.eye.rules", Environment = "edit", Keys = new string[0], Description = "眼位增益、破眼门槛、驻眼增援与玩家重量级AI规则断言")]
        public static object Rules(RevAutomationRequest request)
        {
            try { return new Response { text = (string)Checks.GetMethod("RunRules").Invoke(null, null) }; }
            catch (TargetInvocationException error)
            {
                // 固定程序集桥只解包异常以暴露真正失败的业务断言，不能把反射包装错误当成测试结果。
                throw new InvalidOperationException("眼位规则检查失败：" + (error.InnerException ?? error), error);
            }
        }

        [RevAutomationAction("game.eye.check", Environment = "play", Keys = new string[0], Description = "真实AB眼图标、驻眼Shader动态、增益与退场清理验收")]
        public static object Check(RevAutomationRequest request)
        {
            PropertyInfo result = Checks.GetProperty("LastResult");
            if ((string)result.GetValue(null) == "运行中") { throw new InvalidOperationException("眼位验收正在运行"); }
            Checks.GetMethod("Begin").Invoke(null, null);
            return new Operation(result);
        }

        private sealed class Operation : RevAutomationOperation
        {
            private readonly PropertyInfo _result;
            public Operation(PropertyInfo result) { _result = result; }
            public override bool Tick()
            {
                string text = (string)_result.GetValue(null);
                if (text == "运行中") { return false; }
                Success = text.StartsWith("通过", StringComparison.Ordinal);
                Message = text;
                Result = new Response { text = text };
                return true;
            }
        }
    }
}

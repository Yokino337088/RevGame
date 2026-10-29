using System;
using Revolution.Editor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>跨程序集只调用固定检查类，不接受任意反射方法或C#代码。</summary>
    public static class GameHelperSupportAutomation
    {
        [Serializable] private sealed class Options { public int chapter = 1; }
        [Serializable] private sealed class Result { public string text; }

        [RevAutomationAction("game.helper.support.begin", Environment = "play", Keys = new[] { "chapter" }, Description = "隔离Play：最后一棋治疗/撤销、导弹水母爆炸、声音播放与生命周期检查")]
        public static object Begin(RevAutomationRequest request)
        {
            Options options = JsonUtility.FromJson<Options>(request.payloadJson) ?? new Options();
            return Invoke("GameHelperSupportPlayChecks", "Begin", new object[] { options.chapter });
        }

        // 当前安装的RevAutomationActionAttribute没有ReadOnly字段。此方法本身纯查询，调用端必须有界轮询，
        // 不能因为描述写了“只读”就把它冒充成工具注册表认可的readOnly动作放进until。
        [RevAutomationAction("game.helper.support.state", Environment = "play", Keys = new string[0], Description = "只读：帮手保护Play检查的运行状态、断言与证据目录")]
        public static object State(RevAutomationRequest request)
        {
            return Invoke("GameHelperSupportPlayChecks", "State", null);
        }

        [RevAutomationAction("game.helper.vfx-assets", Environment = "edit", Keys = new string[0], Description = "读取已保存帮手Prefab，检查首次接触爆炸、治疗引用、回池清理和七段音效")]
        public static object Assets(RevAutomationRequest request)
        {
            return Invoke("GameHelperVfxChecks", "Run", null);
        }

        private static object Invoke(string type, string method, object[] arguments)
        {
            try
            {
                Type target = Type.GetType("RevGame.Editor." + type + ", Assembly-CSharp-Editor", true);
                return new Result { text = (string)target.GetMethod(method).Invoke(null, arguments) };
            }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }
}

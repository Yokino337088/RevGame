using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>编辑器自动化桥接层，调用既定的战斗 UI 资产装配入口。</summary>
    public static class GameCombatUiAutomation
    {
        /// <summary>通过编辑态工具应用插画，向调用者返回实际保存结果。</summary>
        [RevAutomationAction("game.combat.ui-art", Environment = "edit", Keys = new string[0], Description = "装配对局按钮、战况牌和无尽菜单")]
        public static object Configure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameCombatUiBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }

        /// <summary>一次制作结果，按 Unity 可序列化字段返回给编辑器工具。</summary>
        [Serializable]
        private sealed class Result
        {
            /// <summary>实际装配的范围。</summary>
            public string text;
        }
    }
}

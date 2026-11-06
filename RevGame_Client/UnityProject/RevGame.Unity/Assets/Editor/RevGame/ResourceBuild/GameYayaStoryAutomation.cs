// 固定制作入口让RevCLI可重复搭建本次场景/UI资产；不提供任意反射代码执行能力。
using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>固定鸦鸦资源制作动作，跨Editor程序集只调用已知类型，不开放任意代码执行。</summary>
    public static class GameYayaStoryAutomation
    {
        /// <summary>模拟美术配置、保存和重制作，实际配方必须保留选择；检查器finally还原并保存。</summary>
        [RevAutomationAction("game.story.preservation-check", Environment = "edit", Keys = new string[0], Description = "实际保存立绘/场景位移并重制作，验证配置不被覆盖，finally恢复")]
        public static object CheckPreservation(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameLobbyConfigurationChecks, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("CheckPreservation").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
        /// <summary>固定配置验收入口，临时数据由项目检查器finally清理。</summary>
        [RevAutomationAction("game.story.portrait-check", Environment = "edit", Keys = new string[0], Description = "验证可配置立绘默认/表情/篇章/逐句优先级，不改正式剧情")]
        public static object CheckPortraits(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameLobbyConfigurationChecks, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("CheckPortraits").Invoke(null, null) };
        }
        /// <summary>只更新大厅场景节点接线，已有人工位移和Inspector参数保持。</summary>
        [RevAutomationAction("game.lobby.configure", Environment = "edit", Keys = new string[0], Description = "添加可配置看板娘位移节点，保留已有大厅构图")]
        public static object ConfigureLobby(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameLobbySceneBuild, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
        }
        /// <summary>只调用固定项目制作配方；资源门禁失败向外返回原始原因，不假装制作成功。</summary>
        [RevAutomationAction("game.yaya-story.configure", Environment = "edit", Keys = new string[0], Description = "制作鸦鸦大厅原画遮罩、待机与六表情全屏剧情，保留Prefab GUID")]
        public static object Configure(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameYayaStoryBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
        /// <summary>跨自动化协议的字段DTO，避免匿名属性被Unity序列化为空对象。</summary>
        [Serializable]
        private sealed class Result
        {
            /// <summary>制作结果摘要，后续仍需图集、字体及实际AB验收。</summary>
            public string text;
        }
    }
}

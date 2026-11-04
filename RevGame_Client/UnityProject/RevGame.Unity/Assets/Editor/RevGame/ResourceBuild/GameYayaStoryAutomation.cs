// 固定制作入口让RevCLI可重复搭建本次场景/UI资产；不提供任意反射代码执行能力。
using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>固定鸦鸦资源制作动作，跨Editor程序集只调用已知类型，不开放任意代码执行。</summary>
    public static class GameYayaStoryAutomation
    {
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

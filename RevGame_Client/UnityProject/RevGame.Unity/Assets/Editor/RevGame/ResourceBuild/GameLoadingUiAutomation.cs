using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>编辑态局部更新加载面板，转发到场景装配配方并返回真实保存回执。</summary>
    public static class GameLoadingUiAutomation
    {
        /// <summary>装配新资源并写回 LoadingPanel。缺少素材或保存失败时返回配方的错误。</summary>
        [RevAutomationAction("game.loading.progress-art", Environment = "edit", Keys = new string[0], Description = "更新正式加载面板进度条")]
        public static object UpdateProgressArt(RevAutomationRequest request)
        {
            return RunRecipe("UpdateLoadingProgress");
        }

        /// <summary>装配和保存新版背景，保留原漩涡和进度 UI。</summary>
        [RevAutomationAction("game.loading.backdrop-art", Environment = "edit", Keys = new string[0], Description = "更新正式加载面板背景")]
        public static object UpdateBackdropArt(RevAutomationRequest request)
        {
            return RunRecipe("UpdateLoadingBackdrop");
        }

        /// <summary>调用本类固定登记的场景配方并转交异常，回执只在保存成功后返回。</summary>
        private static Result RunRecipe(string method)
        {
            Type type = Type.GetType("RevGame.Editor.GameSceneBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod(method).Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }

        /// <summary>一次实际保存的结果。</summary>
        [Serializable]
        private sealed class Result
        {
            /// <summary>本次更新的加载面板配置。</summary>
            public string text;
        }
    }
}

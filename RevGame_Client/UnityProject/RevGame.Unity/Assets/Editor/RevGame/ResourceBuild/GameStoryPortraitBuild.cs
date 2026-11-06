// ResourceBuild拥有独立Editor程序集，业务SO在Assembly-CSharp；跨程序集只调用固定立绘派生配置入口。
using System;
using System.Reflection;

namespace RevGame.Editor
{
    /// <summary>资源总入口的固定适配器，不开放任意类型或方法，保留制作失败的实际原因。</summary>
    public static class GameStoryPortraitBuild
    {
        /// <summary>图集生成后同步剧情图集列表；运行期仍只读配置，不在加载时扫描AssetDatabase。</summary>
        public static void Configure()
        {
            Type type = Type.GetType("RevGame.Editor.GameStoryPortraitAssets, Assembly-CSharp-Editor", true);
            try
            {
                type.GetMethod("Configure").Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.Message ?? error.Message, error.InnerException ?? error);
            }
        }
    }
}

// ============================================================
// RevSceneDefine.cs —— 场景加载的公共词汇
//
// 位置：Runtime\RevScene\Core\（**纯 C#**，不引用 UnityEngine）
//
// 只有一样东西：加载状态。
//   业务拿它决定"加载界面显示什么"（加载中？成功？失败？），
//   而不用去问 Unity 的 AsyncOperation（那个东西在激活前后语义会变）。
// ============================================================
namespace Revolution
{
    /// <summary>场景加载状态（业务用它驱动加载界面 / 遮罩）。</summary>
    public enum RevSceneLoadState
    {
        /// <summary>空闲：当前没有正在进行的切换。</summary>
        Idle = 0,

        /// <summary>加载中：进度条阶段（新场景还没激活，当前场景还在）。</summary>
        Loading,

        /// <summary>已完成：新场景已激活，<c>RevScene.CurrentName</c> 就是目标场景。</summary>
        Done,

        /// <summary>失败：场景名不存在 / 没加进 Build Settings（原因见 <c>RevScene.OnLoadFailed</c>）。</summary>
        Failed,
    }
}

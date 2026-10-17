// ============================================================
// RevSceneLog.cs —— 模块内统一日志出口
//
// 位置：Runtime\RevScene\Support\
//
// 框架里每个模块都有自己的日志出口（RevUILog / RevPoolLog / RevInputLog……），
// 好处是：① 统一带 tag，② 业务想接管日志只改一处。
// 这里同样：错误 / 告警默认走框架日志系统 RevLog（tag = Scene），诊断日志默认关闭。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>模块内日志出口（业务不直接调它；诊断开关是 <c>RevScene.VerboseLog</c>）。</summary>
    internal static class RevSceneLog
    {
        /// <summary>错误出口（切换失败、场景名写错等，必须修的问题）。</summary>
        public static Action<string> Error = msg => RevLog.Error(msg, "Scene");

        /// <summary>告警出口（重复请求、能自动兜住的异常）。</summary>
        public static Action<string> Warning = msg => RevLog.Warn(msg, "Scene");

        /// <summary>是否输出诊断（业务用 <c>RevScene.VerboseLog = true</c> 打开）。</summary>
        public static bool Verbose;

        /// <summary>打一条诊断（关闭时一行代码就返回，参数求值也省了）。</summary>
        public static void Info(string message)
        {
            if (Verbose) RevLog.Info(message, "Scene");
        }
    }
}

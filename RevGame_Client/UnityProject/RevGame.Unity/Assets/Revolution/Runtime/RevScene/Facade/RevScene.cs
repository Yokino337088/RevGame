// ============================================================
// RevScene.cs —— 场景加载门面（业务唯一入口）
//
// 位置：Runtime\RevScene\Facade\
//
// 【三行就够】
//   await RevScene.LoadAsync("Battle");                         // 异步切换（推荐）
//   RevScene.Load("Login");                                     // 同步切换（小场景 / 必须立刻切）
//   await RevScene.LoadAsync("Battle", p => bar.value = p);      // 带进度
//
// 【不想 await？挂事件就够了】（挂一次，全局生效）
//   RevScene.OnLoadStart  += name => loading.Open(name);        // 要开始切了：开加载界面
//   RevScene.OnProgress   += p    => bar.value = p;             // 进度（0~1，单调不减）
//   RevScene.OnLoaded     += name => loading.Close();           // 已进入新场景：关加载界面
//   RevScene.OnLoadFailed += why  => RevLog.Error(why);         // 失败原因（人话）
//
// 【切场景时想顺手收自己的摊子】（例如 UI / 音效 / 资源分组）
//   RevScene.OnLoadStart += _ =>
//   {
//       RevUI.ShutdownAll();                                       // 关掉所有界面
//       RevSound.StopAll();                                        // 停掉音效
//       RevResBootstrap.Instance.Shutdown(RevResGroup.Battle);     // 按业务域卸资源
//   };
//   注：对象池默认**已经**帮你清了（RevScene.AutoClearPool = true），不用重复写。
//
// 【它和"场景里放什么"无关】本模块只管"怎么切"，不碰场景内容；
//   设计取舍与验证见《场景系统 · 架构解析》。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>场景加载门面：一行切换 · 进度可接 · 切之前按约定清理。</summary>
    public static class RevScene
    {
        // ==================== 配置（默认值就够用）====================

        /// <summary>
        /// 切场景前是否自动清空<b>对象池</b>（默认 true）。
        /// 旧场景池化过的实例被带进新场景，最常见的表现是"莫名还在的画面 / 内存不降"。
        /// </summary>
        public static bool AutoClearPool = true;

        /// <summary>
        /// 进度条的默认最短展示时长（秒）：小场景加载太快时"闪一下"不好看，设 0.5 之类即可。
        /// 单次调用也可用 <c>minSeconds</c> 参数覆盖它。
        /// </summary>
        public static float DefaultMinSeconds = 0f;

        /// <summary>诊断日志开关（默认关；打开后走 RevLog，tag = Scene）。</summary>
        public static bool VerboseLog
        {
            get => RevSceneLog.Verbose;
            set => RevSceneLog.Verbose = value;
        }

        // ==================== 事件（不 await 也能接）====================

        /// <summary>要开始切了（参数：目标场景名或 buildIndex 描述）。在这里收自己的摊子。</summary>
        public static Action<string> OnLoadStart;

        /// <summary>进度变化（参数：0~1，单调不减；已经处理过 Unity 的 90% 平台期）。</summary>
        public static Action<float> OnProgress;

        /// <summary>已经进入新场景（参数：新场景名）。</summary>
        public static Action<string> OnLoaded;

        /// <summary>切换失败（参数：场景名 + 人话原因）。</summary>
        public static Action<string> OnLoadFailed;

        // ==================== 查询 ====================

        /// <summary>当前（活动）场景名。</summary>
        public static string CurrentName => RevSceneLoader.CurrentName;

        /// <summary>当前（活动）场景的 buildIndex。</summary>
        public static int CurrentIndex => RevSceneLoader.CurrentIndex;

        /// <summary>当前是否正在切换。</summary>
        public static bool IsLoading => RevSceneLoader.IsLoading;

        /// <summary>当前进度（0~1；可直接喂进度条）。</summary>
        public static float Progress => RevSceneLoader.Progress;

        /// <summary>当前状态（Idle / Loading / Done / Failed，业务用它驱动加载界面）。</summary>
        public static RevSceneLoadState State => RevSceneLoader.State;

        // ==================== 用（三行就够）====================

        /// <summary>
        /// 异步切换场景（推荐）：<c>await RevScene.LoadAsync("Battle");</c>
        /// </summary>
        /// <param name="sceneName">场景名（必须已加进 Build Settings 的 Scenes In Build）</param>
        /// <param name="onProgress">进度回调（0~1，可选；与订阅 <see cref="OnProgress"/> 等价）</param>
        /// <param name="minSeconds">进度条最短展示时长（秒）；&lt; 0 表示用 <see cref="DefaultMinSeconds"/></param>
        public static RevTask LoadAsync(string sceneName, Action<float> onProgress = null, float minSeconds = -1f)
            => RevSceneLoader.LoadAsync(sceneName, onProgress, ResolveMinSeconds(minSeconds));

        /// <summary>按 buildIndex 异步切换场景（场景名其实更稳，改名不会失效）。</summary>
        /// <param name="buildIndex">Build Settings 里的序号</param>
        /// <param name="onProgress">进度回调（0~1，可选）</param>
        /// <param name="minSeconds">进度条最短展示时长（秒）；&lt; 0 表示用 <see cref="DefaultMinSeconds"/></param>
        public static RevTask LoadAsync(int buildIndex, Action<float> onProgress = null, float minSeconds = -1f)
            => RevSceneLoader.LoadAsync(buildIndex, onProgress, ResolveMinSeconds(minSeconds));

        /// <summary>同步切换场景（<b>会卡帧</b>：只在"必须立刻切"的场合用，大场景一律 LoadAsync）。</summary>
        public static void Load(string sceneName) => RevSceneLoader.Load(sceneName);

        /// <summary>重新加载当前场景（最常见的用法：死亡重开、断线重连回战斗）。</summary>
        public static RevTask ReloadAsync(Action<float> onProgress = null, float minSeconds = -1f)
            => RevSceneLoader.LoadAsync(CurrentName, onProgress, ResolveMinSeconds(minSeconds));

        // ==================== 内部 ====================

        /// <summary>把"未指定"（&lt; 0）换算成默认最短展示时长。</summary>
        internal static float ResolveMinSeconds(float minSeconds)
            => minSeconds < 0f ? DefaultMinSeconds : minSeconds;

        internal static void RaiseStart(string label) => Guard(OnLoadStart, label, "OnLoadStart");
        internal static void RaiseProgress(float value) => Guard(OnProgress, value, "OnProgress");
        internal static void RaiseLoaded(string sceneName) => Guard(OnLoaded, sceneName, "OnLoaded");
        internal static void RaiseFailed(string reason) => Guard(OnLoadFailed, reason, "OnLoadFailed");

        /// <summary>广播事件：某个订阅者抛异常，绝不许影响加载流程（和事件系统的隔离原则一致）。</summary>
        private static void Guard<T>(Action<T> handler, T arg, string what)
        {
            if (handler == null) return;
            // 同一个场景事件可能有多个界面/业务模块订阅。若整体 Invoke，前一个订阅者抛异常会跳过后面所有订阅者。
            // 分别保护每个回调，让某个界面代码出错时，其他模块仍能收到开始、进度、成功或失败通知。
            foreach (Action<T> subscriber in handler.GetInvocationList())
            {
                try { subscriber(arg); }
                catch (Exception e)
                {
                    RevSceneLog.Error("[RevScene] " + what + " 的订阅者抛异常（已隔离）：" + e);
                }
            }
        }
    }
}

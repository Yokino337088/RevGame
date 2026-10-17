// ============================================================
// RevSceneLoader.cs —— 真正干活的那个（Unity 侧）
//
// 位置：Runtime\RevScene\Support\
//
// 【一次切换的四步，顺序固定】
//   ① 拒绝并发：正在切场景时，新请求直接忽略并告警（两个加载互相踩 = 黑屏 / 回不去）
//   ② 切之前清理：默认清空对象池（旧场景的池化实例不该跟着新场景走）
//   ③ 加载并盯进度：allowSceneActivation = false —— 先让进度条走完，再真正激活
//   ④ 收尾：进度置 1、状态置 Done、广播已进入的场景名
//
// 【为什么关掉自动激活（allowSceneActivation = false）】
//   Unity 在激活前只把 progress 报到 0.9；直接等 isDone，进度条会卡在 90% 再瞬间跳满。
//   这里改成：进度由 RevSceneProgressTracker 换算成 0~1，
//   到 100%（且满足最短展示时长）才放行激活 —— 进度条连续且不会跳。
//
// 【怎么等一帧】await RevTaskScheduler.NextFrame()：零分配，**不用新起 MonoBehaviour 宿主**。
//   这也是本模块"轻量"的关键：没有隐藏的 GameObject，没有 Update 轮询。
//
// 【场景名写错怎么办】LoadSceneAsync 会返回 null（名字不在 Build Settings 里），
//   这里当场判掉并给出人话原因，绝不让你对着黑屏猜。
// ============================================================
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Revolution
{
    /// <summary>场景加载的实际执行者（门面 <see cref="RevScene"/> 调它，业务不直接碰）。</summary>
    internal static class RevSceneLoader
    {
        /// <summary>是否正在切换（门面用它 / 业务查 RevScene.IsLoading）。</summary>
        internal static bool IsLoading { get; private set; }

        /// <summary>当前状态。</summary>
        internal static RevSceneLoadState State { get; private set; } = RevSceneLoadState.Idle;

        /// <summary>当前显示进度（0~1；不在加载中时保持上一次的值）。</summary>
        internal static float Progress { get; private set; }

        /// <summary>当前（活动）场景名。</summary>
        internal static string CurrentName => SceneManager.GetActiveScene().name;

        /// <summary>当前（活动）场景的 buildIndex。</summary>
        internal static int CurrentIndex => SceneManager.GetActiveScene().buildIndex;

        private static RevSceneProgressTracker _tracker;

        // ==================== 异步（推荐）====================

        /// <summary>按场景名异步切换。</summary>
        internal static RevTask LoadAsync(string sceneName, Action<float> onProgress, float minSeconds)
            => RunAsync(sceneName, -1, onProgress, minSeconds);

        /// <summary>按 buildIndex 异步切换。</summary>
        internal static RevTask LoadAsync(int buildIndex, Action<float> onProgress, float minSeconds)
            => RunAsync(null, buildIndex, onProgress, minSeconds);

        private static async RevTask RunAsync(string sceneName, int buildIndex, Action<float> onProgress, float minSeconds)
        {
            bool useIndex = sceneName == null;
            string label = useIndex ? ("buildIndex " + buildIndex) : sceneName;

            // ① 拒绝并发
            if (IsLoading)
            {
                RevSceneLog.Warning("[RevScene] 正在切换中，忽略本次请求：" + label + "（等这次完成后重调即可）");
                return;
            }

            // ② 切之前清理 + 广播"要开始了"
            Prepare(label, minSeconds);

            // ★ Bug 修复（2026-09-30）：发起/推进/收尾必须整体兜异常，否则模块会永久死锁。
            //   Prepare 已把 IsLoading 置 true，而"发起加载"是会抛异常的 ——
            //   典型：buildIndex 越界时 LoadSceneAsync 直接抛 ArgumentOutOfRangeException
            //   （不像"场景名不存在"那样返回 null，那条路有处理、这条路原来没有）。
            //   异常一冒，IsLoading 永久卡在 true，之后所有加载请求都被①的"拒绝并发"拦下，
            //   表现为"换场景从此永远没反应"。这里转成统一失败收尾（State=Failed + OnLoadFailed），
            //   与"场景不存在"走同一条路；不 rethrow —— 失败可由 State / OnLoadFailed 感知。
            try
            {
                // ③ 发起异步加载（名字不在 Build Settings 时这里就是 null）
                AsyncOperation op = useIndex
                    ? SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Single)
                    : SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);

                if (op == null)
                {
                    Fail(label, "场景不存在，或没有加进 Build Settings（菜单 File / Build Settings 的 Scenes In Build）");
                    return;
                }

                op.allowSceneActivation = false;      // 先让进度条走完，再激活
                RevSceneLog.Info("[RevScene] 开始异步切换 → " + label);

                // ④ 每帧推进：换算进度 → 广播 → 到 100% 才放行激活
                while (!op.isDone)
                {
                    float value = _tracker.Tick(op.progress, Time.unscaledDeltaTime);
                    Report(value, onProgress);
                    if (_tracker.CanActivate) op.allowSceneActivation = true;
                    await RevTaskScheduler.NextFrame();
                }

                _tracker.Complete();
                Report(1f, onProgress);

                IsLoading = false;
                State = RevSceneLoadState.Done;
                string loaded = CurrentName;
                RevSceneLog.Info("[RevScene] 已进入场景：" + loaded);
                RevScene.RaiseLoaded(loaded);
            }
            catch (Exception e)
            {
                // ★ 同上（Bug 修复 2026-09-30）：推进阶段抛的异常也走失败收尾，绝不留 IsLoading=true 的残局
                Fail(label, "加载过程抛异常：" + e.Message);
            }
        }

        // ==================== 同步（会卡帧）====================

        /// <summary>同步切换：调用这一帧就换成新场景（大场景会明显卡顿）。</summary>
        internal static void Load(string sceneName)
        {
            if (IsLoading)
            {
                RevSceneLog.Warning("[RevScene] 正在切换中，忽略本次同步请求：" + sceneName);
                return;
            }

            if (string.IsNullOrEmpty(sceneName))
            {
                Fail("(空场景名)", "场景名不能为空");
                return;
            }

            Prepare(sceneName);

            // Unity 的同步 LoadScene 会在返回前完成场景加载，并触发 sceneLoaded；但在 LoadScene 调用刚返回的那一刻，
            // 不能自行假设激活场景名已经更新。OnLoaded 的含义是“目标场景现在已经可用”，因此先订阅 Unity 的加载完成通知，
            // 只有收到匹配目标场景的通知后才把状态标成 Done 并广播。这样订阅者在 OnLoaded 里读取 CurrentName/查找场景对象时，
            // 不会误读旧场景。若 LoadScene 抛异常，下面 catch 会移除临时订阅并按统一失败流程收尾。
            string expectedSceneName = sceneName.Replace('\\', '/');
            int lastSeparator = expectedSceneName.LastIndexOf('/');
            if (lastSeparator >= 0) expectedSceneName = expectedSceneName.Substring(lastSeparator + 1);
            if (expectedSceneName.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                expectedSceneName = expectedSceneName.Substring(0, expectedSceneName.Length - ".unity".Length);

            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onSceneLoaded = null;
            onSceneLoaded = (scene, mode) =>
            {
                // API 允许传 Build Settings 场景名，也允许传 Assets 下的完整路径；scene.name 只有文件名，先把输入路径折成同一种名字再比较。
                if (!string.Equals(scene.name, expectedSceneName, StringComparison.Ordinal)) return;
                SceneManager.sceneLoaded -= onSceneLoaded;
                Progress = 1f;
                // 和异步路径一致，先在 IsLoading=true 时通知最后一次进度，避免 OnProgress 订阅者重入发起另一场切换。
                RevScene.RaiseProgress(1f);
                IsLoading = false;
                State = RevSceneLoadState.Done;
                RevSceneLog.Info("[RevScene] 已同步切换 → " + scene.name);
                RevScene.RaiseLoaded(scene.name);
            };
            SceneManager.sceneLoaded += onSceneLoaded;

            try
            {
                SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
            }
            catch (Exception e)
            {
                SceneManager.sceneLoaded -= onSceneLoaded;
                // 场景名无效等问题可能让 Unity 在加载过程中抛异常；撤掉临时监听并恢复 IsLoading，
                // 否则状态会永远停在 Loading，后续所有切场景请求都会被并发保护拒绝。
                Fail(sceneName, "发起同步切换抛异常：" + e.Message);
            }
        }

        // ==================== 公共步骤 ====================

        /// <summary>切之前：清理 + 状态就位 + 广播开始。</summary>
        /// <param name="label">场景名或 buildIndex 描述（只用于日志 / 事件）</param>
        /// <param name="minSeconds">进度条最短展示时长（秒）；同步切换用不到，默认 0</param>
        private static void Prepare(string label, float minSeconds = 0f)
        {
            if (RevScene.AutoClearPool)
            {
                int cleared = RevPool.ClearAll();       // 旧场景的池化实例不该跟着走
                if (cleared > 0) RevSceneLog.Info("[RevScene] 已清理对象池：" + cleared + " 个实例");
            }

            _tracker = new RevSceneProgressTracker(minSeconds);
            IsLoading = true;
            State = RevSceneLoadState.Loading;
            Progress = 0f;

            RevSceneLog.Info("[RevScene] 准备切换 → " + label);
            RevScene.RaiseStart(label);
            RevScene.RaiseProgress(0f);
        }

        /// <summary>进度广播（同时喂给门面事件与本次调用的回调；回调抛异常只报错，不中断加载）。</summary>
        private static void Report(float value, Action<float> onProgress)
        {
            Progress = value;
            RevScene.RaiseProgress(value);

            if (onProgress == null) return;
            try
            {
                onProgress(value);
            }
            catch (Exception e)
            {
                RevSceneLog.Error("[RevScene] 进度回调抛异常（已隔离，加载继续）：" + e);
            }
        }

        /// <summary>失败收尾：状态置 Failed + 广播原因（人话，不用你猜）。</summary>
        private static void Fail(string label, string reason)
        {
            IsLoading = false;
            State = RevSceneLoadState.Failed;
            RevSceneLog.Error("[RevScene] 切换失败：" + label + " —— " + reason);
            RevScene.RaiseFailed(label + "：" + reason);
        }

        // ★ Bug 修复（2026-09-30）：进 Play 复位 —— Domain Reload 关闭（项目常态）时
        //   static 运行状态会跨局存活：退出 Play 那一刻若正在异步加载，
        //   IsLoading=true / State=Loading 就残留到下一次运行，之后所有加载请求
        //   被"拒绝并发"拦下（表现为"换场景永远没反应"）且一条日志都没有 ——
        //   连 Bug① 那样的失败日志都不会有，因为根本没走到发起那一步。
        //   与 RevMonoUnityHooks 的"进 Play 复位"是同一款防线：进 Play 前把运行状态清零。
        //   （AutoClearPool / DefaultMinSeconds / VerboseLog 是业务配置不是运行状态，故意保留。）
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            IsLoading = false;
            State = RevSceneLoadState.Idle;
            Progress = 0f;
            _tracker = null;
        }
    }
}

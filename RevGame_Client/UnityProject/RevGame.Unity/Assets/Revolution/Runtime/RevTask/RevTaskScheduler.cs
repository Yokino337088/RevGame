// ============================================================
// RevTaskScheduler.cs —— Update 驱动的调度器（替代协程）
//
// 位置：Runtime\RevTask\
//
// 【它替代了什么】
//   协程的"每帧推进"能力。区别：
//     · 协程：每个协程每帧一次 MoveNext（虚调用 + 状态机），yield 还有分配；
//     · 这里：一个 Action 出队 + 调用；配合零分配的 NextFrame awaiter，几乎没有开销。
//
// 【为什么不注入 PlayerLoop？】
//   Update 驱动的开销与 PlayerLoop 同量级，但代码量少一个数量级。
//   真到瓶颈再升级 —— 对外接口不变，只换内部实现。
// ============================================================
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Revolution
{
    /// <summary>RevTask 调度器：全局唯一，DontDestroyOnLoad</summary>
    public sealed class RevTaskScheduler : MonoBehaviour
    {
        // 帧内要执行的续体（Yield / 延时到期 / 异步操作完成）
        private static readonly Queue<Action> _frameQueue = new Queue<Action>(256);

        // 延时队列（每帧扫一遍到期项；节点数量通常很小）
        private static readonly List<DelayNode> _delays = new List<DelayNode>(64);

        /// <summary>每帧最多执行多少个续体，防止某一帧被大量回调卡住</summary>
        private static int _maxContinuationsPerFrame = 128;
        public static int MaxContinuationsPerFrame
        {
            get { return _maxContinuationsPerFrame; }
            set
            {
                if (value <= 0) { throw new ArgumentOutOfRangeException(nameof(value), "续体预算必须大于 0"); }
                _maxContinuationsPerFrame = value;
            }
        }
        private static bool _quitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            _quitting = false;
            _frameQueue.Clear();
            _delays.Clear();
            _maxContinuationsPerFrame = 128;
        }

        private static RevTaskScheduler _instance;

        private struct DelayNode
        {
            public float DueTime;            // 到期时刻（Time.realtimeSinceStartup）
            public Action Continuation;
        }

        // ============================================================
        // 对外：把续体排到下一帧
        // ============================================================
        public static void Post(Action continuation)
        {
            if (continuation == null) return;
            EnsureInstance();
            _frameQueue.Enqueue(continuation);
        }

        /// <summary>
        /// 零分配的"等一帧"：await RevTaskScheduler.NextFrame();
        /// 为什么零分配？直接把状态机的续体交给队列，不创建任何 Promise。
        /// </summary>
        public static RevNextFrameAwaiter NextFrame() => default;

        /// <summary>等一帧（返回 RevTask 的便捷版，有一次小分配；热路径请用 NextFrame()）</summary>
        public static RevTask Yield()
        {
            var source = new RevTaskCompletionSource();
            Post(source.SetResult);
            return source.Task;
        }

        /// <summary>延时（毫秒）</summary>
        public static RevTask Delay(int milliseconds)
        {
            var source = new RevTaskCompletionSource();

            if (milliseconds <= 0)
            {
                Post(source.SetResult);
                return source.Task;
            }

            EnsureInstance();
            _delays.Add(new DelayNode
            {
                DueTime = Time.realtimeSinceStartup + milliseconds * 0.001f,
                Continuation = source.SetResult
            });
            return source.Task;
        }

        /// <summary>
        /// 等所有任务都结束后再完成；只要其中一个任务失败，await WhenAll 也会把该异常交给调用方。
        /// 空数组或 null 按“没有任务需要等待”处理，立即成功完成。
        /// </summary>
        public static RevTask WhenAll(params RevTask[] tasks)
        {
            var source = new RevTaskCompletionSource();

            if (tasks == null || tasks.Length == 0)
            {
                source.SetResult();
                return source.Task;
            }

            // 原实现只数“完成了几个”，最后无论子任务成功还是失败都 SetResult；调用方 await WhenAll 会误以为整批成功，
            // 例如下载其中一个文件失败，后续流程却继续把不完整版本标记成完成。每个子任务结束时读取它的结果并保存第一个异常，
            // 仍等整批任务都结束（让其他任务有机会清理），最后再让 WhenAll 成功或以该异常失败。
            int remain = tasks.Length;
            Exception firstException = null;
            foreach (RevTask t in tasks)
            {
                RevTask task = t;
                task.GetAwaiter().OnCompleted(() =>
                {
                    try { task.GetAwaiter().GetResult(); }
                    catch (Exception e) { if (firstException == null) firstException = e; }

                    if (--remain != 0) return;
                    if (firstException != null) source.SetException(firstException);
                    else source.SetResult();
                });
            }

            return source.Task;
        }

        // ============================================================
        // 驱动
        // ============================================================
        private void Update()
        {
            // ① 延时到期的先入帧队列
            float now = Time.realtimeSinceStartup;
            for (int i = _delays.Count - 1; i >= 0; i--)
            {
                if (now < _delays[i].DueTime) continue;

                _frameQueue.Enqueue(_delays[i].Continuation);
                _delays.RemoveAt(i);
            }

            // ② 限量执行帧队列（本帧新入队的留到下一帧，避免卡帧）
            int budget = _frameQueue.Count < MaxContinuationsPerFrame
                ? _frameQueue.Count
                : MaxContinuationsPerFrame;

            while (budget-- > 0 && _frameQueue.Count > 0)
            {
                RevTaskContinuations.InvokeOne(_frameQueue.Dequeue());
            }
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
        }

        private void OnDestroy()
        {
            if (!ReferenceEquals(_instance, this)) { return; }
            _instance = null;
            if (_quitting || !Application.isPlaying)
            {
                _frameQueue.Clear();
                _delays.Clear();
                return;
            }
            // 运行中意外销毁宿主时保留队列并重建，Yield/Delay/零分配 NextFrame 的续体都不能遗失。
            EnsureInstance();
        }

        private static void EnsureInstance()
        {
            if (_instance != null || _quitting) { return; }

            var go = new GameObject("[RevTaskScheduler]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<RevTaskScheduler>();
            go.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    /// <summary>
    /// 零分配的"等一帧" awaiter：
    /// IsCompleted 恒为 false，所以状态机一定会在 OnCompleted 里把续体 Post 到下一帧。
    /// </summary>
    public readonly struct RevNextFrameAwaiter : ICriticalNotifyCompletion
    {
        public bool IsCompleted => false;

        /// <summary>
        /// 让 `await RevTaskScheduler.NextFrame();` 直接可用。
        /// ★ 少了这一行编译不过：await 找的是"表达式的 GetAwaiter()"，
        ///   而这个类型自己就是 awaiter，所以标准写法就是"返回自己"（readonly struct，零分配）。
        /// </summary>
        public RevNextFrameAwaiter GetAwaiter() => this;

        public void GetResult() { }

        public void OnCompleted(Action continuation) => RevTaskScheduler.Post(continuation);

        public void UnsafeOnCompleted(Action continuation) => RevTaskScheduler.Post(continuation);
    }
}

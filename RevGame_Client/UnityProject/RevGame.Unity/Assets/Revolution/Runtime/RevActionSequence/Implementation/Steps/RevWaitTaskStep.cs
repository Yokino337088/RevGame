// RevWaitTaskStep.cs —— 等异步任务（RevTask / RevTask<T>）完成：两套时序之间的桥
// 【关键】收的是工厂 ctx => 任务，任务在运行时创建 → 清单可缓存、并发互不干扰。
// 【实现】任务存状态槽（按运行实例隔离）；每帧问 GetAwaiter().IsCompleted；
//         任务失败 → 打日志（带序列名与步骤）并按 failAsCancel 决定：放行 / 取消整条序列。

using System;

namespace Revolution
{
    /// <summary>
    /// 等异步任务步骤：运行时调用工厂拿到任务，等它完成后才放行（对应"等资源加载完 / 等网络回包"）。
    /// </summary>
    internal sealed class RevWaitTaskStep : RevStepBase
    {
        private sealed class State
        {
            public RevTask Task;
            public bool Started;
        }

        private readonly Func<RevSequenceContext, RevTask> _taskFactory;
        private readonly bool _cancelOnFailure;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => true;

        internal RevWaitTaskStep(string name, Func<RevSequenceContext, RevTask> taskFactory, bool cancelOnFailure)
        {
            Name = name;
            _taskFactory = taskFactory;
            _cancelOnFailure = cancelOnFailure;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.Task = _taskFactory(context);
            state.Started = true;
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state == null || !state.Started) return true;      // 没启动（不该发生）→ 不卡住

            RevTaskAwaiter awaiter = state.Task.GetAwaiter();
            if (!awaiter.IsCompleted) return false;                 // 还没完成：继续挂起

            try
            {
                awaiter.GetResult();
            }
            catch (Exception e)
            {
                RevWaitTaskFailure.Report(run, e, _cancelOnFailure);
            }

            state.Task = default;                                   // 不留引用：运行实例是池化的
            return true;
        }
    }

    /// <summary>
    /// 等带返回值的异步任务：完成后把结果交给 <c>onResult</c>（例如"加载完资源，拿到它再用"）。
    /// </summary>
    internal sealed class RevWaitTaskStep<T> : RevStepBase
    {
        private sealed class State
        {
            public RevTask<T> Task;
            public bool Started;
        }

        private readonly Func<RevSequenceContext, RevTask<T>> _taskFactory;
        private readonly Action<RevSequenceContext, T> _onResult;
        private readonly bool _cancelOnFailure;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => true;

        internal RevWaitTaskStep(string name, Func<RevSequenceContext, RevTask<T>> taskFactory,
                                 Action<RevSequenceContext, T> onResult, bool cancelOnFailure)
        {
            Name = name;
            _taskFactory = taskFactory;
            _onResult = onResult;
            _cancelOnFailure = cancelOnFailure;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.Task = _taskFactory(context);
            state.Started = true;
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state == null || !state.Started) return true;

            RevTaskAwaiter<T> awaiter = state.Task.GetAwaiter();
            if (!awaiter.IsCompleted) return false;

            state.Task = default;

            T result;
            try
            {
                result = awaiter.GetResult();
            }
            catch (Exception e)
            {
                RevWaitTaskFailure.Report(run, e, _cancelOnFailure);
                return true;
            }

            _onResult?.Invoke(context, result);     // 这里抛异常 = 步骤抛异常：引擎会记日志并按取消收尾
            return true;
        }
    }

    internal static class RevWaitTaskFailure
    {
        internal static void Report(RevSequenceRun run, Exception e, bool cancel)
        {
            RevLog.Exception(e, $"[动作序列] {run.Where} 等待的异步任务失败了 → " +
                                (cancel ? "取消整条序列（会执行收尾步骤）" : "继续执行后面的步骤"), RevSequenceRunner.LogTag);

            if (cancel) run.IsCancellationRequested = true;
        }
    }
}

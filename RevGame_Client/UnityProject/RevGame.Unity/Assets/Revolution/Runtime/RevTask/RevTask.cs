// ============================================================
// RevTask.cs —— 轻量异步任务（资源加载专用，替代协程 / UniTask）
//
// 位置：Runtime\RevTask\
//
// 【它是什么】
//   一个"可以 await、可以传播异常、可以被取消"的任务对象。
//
// 【核心组成】
//   Promise（完成源 / 引用类型，状态共享）
//     + Awaiter（把 Promise 接到 C# 的 await 机制）
//     + MethodBuilder（让 async 方法能返回 RevTask）
//
// 【为什么 Promise 是 class？】
//   它要被"异步状态机"和"await 方"同时持有，必须引用类型才能共享状态；
//   而 RevTask 本身是 struct，只是 Promise 的轻量外壳（避免额外分配）。
//
// 【环境要求】.NET Standard 2.1（Unity 2021.2+），因为用到 AsyncMethodBuilderAttribute。
// ============================================================
using System;
using System.Runtime.CompilerServices;

// ------------------------------------------------------------
// 兼容性补丁：.NET Standard 2.0 环境下没有 AsyncMethodBuilderAttribute，
// 这里补一个。.NET Standard 2.1 / Unity 2021.2+ 已内置，不会重复定义。
// ------------------------------------------------------------
#if !(UNITY_2021_2_OR_NEWER || NETSTANDARD2_1 || NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER)
namespace System.Runtime.CompilerServices
{
    using System;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
        Inherited = false, AllowMultiple = false)]
    internal sealed class AsyncMethodBuilderAttribute : Attribute
    {
        public AsyncMethodBuilderAttribute(Type builderType) => BuilderType = builderType;
        public Type BuilderType { get; }
    }
}
#endif

namespace Revolution
{
    /// <summary>无返回值的异步任务</summary>
    [AsyncMethodBuilder(typeof(RevTaskMethodBuilder))]
    public readonly struct RevTask
    {
        internal readonly RevTaskPromise Promise;

        internal RevTask(RevTaskPromise promise) { Promise = promise; }

        /// <summary>await 支持：await someRevTask;</summary>
        public RevTaskAwaiter GetAwaiter() => new RevTaskAwaiter(Promise);

        /// <summary>已完成的任务（Promise 为 null 即视为已完成）</summary>
        public static RevTask Completed => default;

        /// <summary>延时（毫秒），由 RevTaskScheduler 驱动，不用协程</summary>
        public static RevTask Delay(int milliseconds) => RevTaskScheduler.Delay(milliseconds);

        /// <summary>等一帧（便捷版；热路径请用零分配的 RevTaskScheduler.NextFrame()）</summary>
        public static RevTask Yield() => RevTaskScheduler.Yield();

        /// <summary>等一批任务全部完成</summary>
        public static RevTask WhenAll(params RevTask[] tasks) => RevTaskScheduler.WhenAll(tasks);

        /// <summary>创建"稍后手动完成"的源（用于包装回调式 API）</summary>
        public static RevTaskCompletionSource CreateSource() => new RevTaskCompletionSource();

        /// <summary>构造一个已失败的任务</summary>
        public static RevTask FromException(Exception e)
        {
            var promise = new RevTaskPromise();
            promise.SetException(e);
            return new RevTask(promise);
        }

        /// <summary>不等待、显式丢弃（替代 async void，让意图明确）</summary>
        public void Forget() { }
    }

    /// <summary>带返回值的异步任务</summary>
    [AsyncMethodBuilder(typeof(RevTaskMethodBuilder<>))]
    public readonly struct RevTask<T>
    {
        internal readonly RevTaskPromise<T> Promise;

        internal RevTask(RevTaskPromise<T> promise) { Promise = promise; }

        public RevTaskAwaiter<T> GetAwaiter() => new RevTaskAwaiter<T>(Promise);

        public static RevTaskCompletionSource<T> CreateSource() => new RevTaskCompletionSource<T>();

        public static RevTask<T> FromResult(T value)
        {
            var promise = new RevTaskPromise<T>();
            promise.SetResult(value);
            return new RevTask<T>(promise);
        }

        public void Forget() { }
    }

    // ============================================================
    // Promise：真正的状态容器（续体 + 异常 + 结果）
    // ============================================================
    internal static class RevTaskContinuations
    {
        internal static void InvokeOne(Action continuation)
        {
            if (continuation == null) { return; }
            try { continuation(); }
            catch (Exception e) { RevLog.Exception(e, "RevTask 手工续体异常", "Task"); }
        }

        internal static void Invoke(Action continuations)
        {
            if (continuations == null) { return; }
            foreach (Action continuation in continuations.GetInvocationList())
            {
                InvokeOne(continuation);
            }
        }
    }

    internal sealed class RevTaskPromise
    {
        private Action _continuation;
        private int _continuationCount;
        private Exception _exception;

        public bool IsCompleted { get; private set; }

        /// <summary>注册续体；若已完成则立即执行（★ 保证回调绝不丢失）</summary>
        public void OnCompleted(Action continuation)
        {
            if (continuation == null) { return; }
            // 手工一次传入的 Action 也可能是多播委托，必须拆开登记，避免首个抛错吞掉后面的通知。
            foreach (Action item in continuation.GetInvocationList()) { OnCompletedSingle(item); }
        }

        internal void OnCompletedSingle(Action continuation)
        {
            if (continuation == null) { return; }
            if (IsCompleted) { RevTaskContinuations.InvokeOne(continuation); return; }
            _continuation = (Action)Delegate.Combine(_continuation, continuation);
            _continuationCount++;
        }

        public void SetResult()
        {
            if (IsCompleted) return;          // 防重复完成（某些 Unity 回调可能重复触发）
            IsCompleted = true;

            Action c = _continuation;
            int count = _continuationCount;
            _continuation = null;
            _continuationCount = 0;
            if (count <= 1) { RevTaskContinuations.InvokeOne(c); }
            else { RevTaskContinuations.Invoke(c); }
        }

        public void SetException(Exception e)
        {
            // CompletionSource 可能被多个回调同时/重复完成。任务一旦成功唤醒 await 方，结果就已经定了；
            // 后到的错误回调不能再把它改成失败，否则同一个 await 会先看似成功、稍后再次等待却突然抛异常。
            if (IsCompleted) return;
            _exception = e;
            SetResult();
        }

        /// <summary>把异常重新抛给 await 方 —— 这是"异常不丢"的关键</summary>
        public void ThrowIfFaulted()
        {
            if (_exception != null) throw _exception;
        }
    }

    internal sealed class RevTaskPromise<T>
    {
        private Action _continuation;
        private int _continuationCount;
        private Exception _exception;
        private T _result;

        public bool IsCompleted { get; private set; }

        public void OnCompleted(Action continuation)
        {
            if (continuation == null) { return; }
            // 手工一次传入的 Action 也可能是多播委托，必须拆开登记，避免首个抛错吞掉后面的通知。
            foreach (Action item in continuation.GetInvocationList()) { OnCompletedSingle(item); }
        }

        internal void OnCompletedSingle(Action continuation)
        {
            if (continuation == null) { return; }
            if (IsCompleted) { RevTaskContinuations.InvokeOne(continuation); return; }
            _continuation = (Action)Delegate.Combine(_continuation, continuation);
            _continuationCount++;
        }

        public void SetResult(T result)
        {
            if (IsCompleted) return;
            _result = result;
            IsCompleted = true;

            Action c = _continuation;
            int count = _continuationCount;
            _continuation = null;
            _continuationCount = 0;
            if (count <= 1) { RevTaskContinuations.InvokeOne(c); }
            else { RevTaskContinuations.Invoke(c); }
        }

        public void SetException(Exception e)
        {
            // 带返回值的任务也遵守“第一次完成生效”：成功值一旦交给 await 方，重复到达的异常不能覆盖它，反过来也不能覆盖先到的失败。
            if (IsCompleted) return;
            _exception = e;
            SetResult(default);
        }

        public T GetResult()
        {
            if (_exception != null) throw _exception;
            return _result;
        }
    }

    // ============================================================
    // Awaiter：把 Promise 接到 C# 的 await 机制上
    // ============================================================
    public struct RevTaskAwaiter : ICriticalNotifyCompletion
    {
        private readonly RevTaskPromise _promise;

        internal RevTaskAwaiter(RevTaskPromise promise) { _promise = promise; }

        public bool IsCompleted => _promise == null || _promise.IsCompleted;

        public void GetResult() => _promise?.ThrowIfFaulted();

        public void OnCompleted(Action continuation)
        {
            if (_promise == null || _promise.IsCompleted) { RevTaskContinuations.Invoke(continuation); return; }
            _promise.OnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            // 编译器生成的 await 续体是单个 MoveNext；保留此热路径，避免每次 await 为拆委托分配数组。
            if (_promise == null || _promise.IsCompleted) { RevTaskContinuations.InvokeOne(continuation); return; }
            _promise.OnCompletedSingle(continuation);
        }
    }

    public struct RevTaskAwaiter<T> : ICriticalNotifyCompletion
    {
        private readonly RevTaskPromise<T> _promise;

        internal RevTaskAwaiter(RevTaskPromise<T> promise) { _promise = promise; }

        public bool IsCompleted => _promise == null || _promise.IsCompleted;

        public T GetResult() => _promise == null ? default : _promise.GetResult();

        public void OnCompleted(Action continuation)
        {
            if (_promise == null || _promise.IsCompleted) { RevTaskContinuations.Invoke(continuation); return; }
            _promise.OnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            // 编译器生成的 await 续体是单个 MoveNext；保留此热路径，避免每次 await 为拆委托分配数组。
            if (_promise == null || _promise.IsCompleted) { RevTaskContinuations.InvokeOne(continuation); return; }
            _promise.OnCompletedSingle(continuation);
        }
    }

    // ============================================================
    // 完成源：给"回调式 API"用的桥
    // ============================================================
    public sealed class RevTaskCompletionSource
    {
        private readonly RevTaskPromise _promise = new RevTaskPromise();

        public RevTask Task => new RevTask(_promise);

        public void SetResult() => _promise.SetResult();

        public void SetException(Exception e) => _promise.SetException(e);
    }

    public sealed class RevTaskCompletionSource<T>
    {
        private readonly RevTaskPromise<T> _promise = new RevTaskPromise<T>();

        public RevTask<T> Task => new RevTask<T>(_promise);

        public void SetResult(T value) => _promise.SetResult(value);

        public void SetException(Exception e) => _promise.SetException(e);
    }

    // ============================================================
    // MethodBuilder：让 async 方法能返回 RevTask
    //
    // 【实现技巧】把"状态机驱动"全部委托给 BCL 的 AsyncTaskMethodBuilder，
    //   我们只接管"任务表示"（RevTask）与完成通知。
    //   这样 60 行就能拥有 async/await 支持。
    // ============================================================
    public struct RevTaskMethodBuilder
    {
        private RevTaskPromise _promise;
        private AsyncTaskMethodBuilder _core;

        public static RevTaskMethodBuilder Create()
            => new RevTaskMethodBuilder
            {
                _promise = new RevTaskPromise(),
                _core = AsyncTaskMethodBuilder.Create()
            };

        public RevTask Task => new RevTask(_promise);

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
            => _core.Start(ref stateMachine);

        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => _core.SetStateMachine(stateMachine);

        public void SetResult()
        {
            _promise.SetResult();     // 先唤醒 await 方
            _core.SetResult();        // 再结束 BCL 内部状态机（避免悬挂）
        }

        public void SetException(Exception e)
        {
            // ① 先把异常交给我们的 Promise：await 方会在这里拿到它
            _promise.SetException(e);

            // ② 再让 BCL 状态机收尾。
            //    ★ _core.SetException 会把 BCL 内部那个"没人 await 的 Task"置为 faulted。
            //      Unity 默认忽略未观察的 Task 异常，不会崩；
            //      若真机日志出现 UnobservedTaskException 警告，
            //      把这一行换成 _core.SetResult() 即可（异常已由上面的 Promise 传播）。
            _core.SetException(e);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => _core.AwaitOnCompleted(ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => _core.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }

    public struct RevTaskMethodBuilder<T>
    {
        private RevTaskPromise<T> _promise;
        private AsyncTaskMethodBuilder<T> _core;

        public static RevTaskMethodBuilder<T> Create()
            => new RevTaskMethodBuilder<T>
            {
                _promise = new RevTaskPromise<T>(),
                _core = AsyncTaskMethodBuilder<T>.Create()
            };

        public RevTask<T> Task => new RevTask<T>(_promise);

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
            => _core.Start(ref stateMachine);

        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => _core.SetStateMachine(stateMachine);

        public void SetResult(T result)
        {
            _promise.SetResult(result);
            _core.SetResult(result);
        }

        public void SetException(Exception e)
        {
            // 同无返回值版本：先交给 Promise 传播，再让 BCL 收尾。
            // 若真机出现 UnobservedTaskException 警告，把 _core.SetException 换成 _core.SetResult()。
            _promise.SetException(e);
            _core.SetException(e);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => _core.AwaitOnCompleted(ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => _core.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }
}

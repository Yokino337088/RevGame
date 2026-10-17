using System;

namespace Revolution
{
    /// <summary>
    /// 资源加载器接口
    /// 它在四层里的位置最底层，只管"怎么读"
    /// 不关心缓存、引用计数、分组 —— 那些都是上层（策略 / 门面）的职责
    /// </summary>
    public interface IRevResLoader
    {
        /// <summary>
        /// 同步加载。
        /// 实现要点：失败时不要抛异常（异常会打断整条加载链路），
        ///          而是返回 null，并通过 err 说明失败原因。
        /// </summary>
        object Load(RevResHandle handle, out RevResLoadErrorReason err);

        /// <summary>
        /// 异步加载。实现必须保证两件事：
        ///   ① 完成后调用 handle.SetContent(...) 写入结果；
        ///   ② 无论成功 / 失败 / 取消，都要调用 onFinished(handle) 通知回调。
        /// 否则异步泵会一直等待这一个任务，把整个加载队列卡住。
        ///
        /// token：切场景时的取消令牌（由 RevAsyncLoadPump 传入），在关键节点检查它即可中断；
        ///        但即使不检查，也必须在 finally 里回调（契约优先于取消）。
        /// </summary>
        void LoadAsync(RevResHandle handle, Action<RevResHandle> onFinished, RevCancellationToken token = null);
    }
}
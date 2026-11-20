// RevScopedOperations.cs — 将请求交付、超时和迟到结果的回收绑定到业务域实例。
// 同一个域内可显式共享同键查询。关闭后等待方立即结案，底层不可取消的操作仍能收尾。
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>主线程请求组。默认归当前域，界面也可以在关窗时提前释放该组。</summary>
    public sealed class RevScopedOperations<T> : IDisposable
    {
        /// <summary>一次接单的具体代次。旧回包通过这个实例识别，不依赖可重复的业务键。</summary>
        private sealed class Entry
        {
            /// <summary>用于区分同一所有者中的请求。</summary>
            internal readonly string Key;
            /// <summary>接单时确定的共享权限。</summary>
            internal readonly bool Share;
            /// <summary>传给实际工作，超时或关域后转为取消。</summary>
            internal readonly RevCancellationTokenSource Cancellation = new RevCancellationTokenSource();
            /// <summary>所有观察者等待同一终态。</summary>
            internal readonly RevTaskCompletionSource<T> Completion = RevTask<T>.CreateSource();
            /// <summary>选用非缩放时间，结束后归还计时器槽。</summary>
            internal RevTimerHandle Deadline;

            /// <summary>记录去重键与当前操作的共享规则。</summary>
            internal Entry(string key, bool share)
            {
                Key = key;
                Share = share;
            }
        }

        /// <summary>创建组时捕获的实际业务域。常驻组允许为空。</summary>
        private readonly RevScopeHandle _owner;
        /// <summary>本组唯一在途请求。终态在唤醒等待者前先移除。</summary>
        private readonly Dictionary<string, Entry> _active = new Dictionary<string, Entry>(StringComparer.Ordinal);
        /// <summary>组一经关闭即拒绝新动作。</summary>
        private bool _closed;

        /// <summary>创建并登记到指定或当前域。常驻宿主可关闭默认归域并自行释放。</summary>
        public RevScopedOperations(RevScopeHandle owner = null, bool inheritScope = true)
        {
            _owner = owner ?? (inheritScope ? RevScope.DefaultNode : null);
            _owner?.Own(this);
        }

        /// <summary>当前请求数，用于生命周期验收。</summary>
        public int PendingCount => _active.Count;

        /// <summary>
        /// 在 Unity 主线程开始工作。相同键且双方允许共享时复用任务。零秒表示无超时。
        /// 版本校验发生在交付前。所有失效后才产出的值交给 discard，业务写入应在显式提交点再次复核。
        /// </summary>
        public RevTask<T> Run(string key, Func<RevCancellationToken, RevTask<T>> work, bool share = false,
            float timeoutSeconds = 0, Func<bool> canDeliver = null, Action<T> discard = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("请求键不能为空。", nameof(key));
            }
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }
            if (timeoutSeconds < 0 || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            }
            if (_closed || (_owner != null && !_owner.IsOpen))
            {
                throw new RevOperationCanceledException();
            }
            if (_active.TryGetValue(key, out Entry existing))
            {
                if (share && existing.Share)
                {
                    return existing.Completion.Task;
                }
                throw new InvalidOperationException("已有同键操作在途：" + key);
            }

            var entry = new Entry(key, share);
            _active.Add(key, entry);
            if (timeoutSeconds > 0)
            {
                entry.Deadline = RevTimer.After(timeoutSeconds, () => Cancel(entry, new TimeoutException("操作超时：" + key)),
                    RevTimeDomain.Unscaled, this);
                if (entry.Deadline.IsEmpty)
                {
                    Cancel(entry, new InvalidOperationException("无法为操作登记超时：" + key));
                    return entry.Completion.Task;
                }
            }
            ExecuteAsync(entry, work, canDeliver, discard).Forget();
            return entry.Completion.Task;
        }

        /// <summary>观察真实工作直至完成。底层的取消能力由工作入口决定，框架始终核对代次与归属。</summary>
        private async RevTask ExecuteAsync(Entry entry, Func<RevCancellationToken, RevTask<T>> work, Func<bool> canDeliver, Action<T> discard)
        {
            T value;
            try
            {
                RevTask<T> task = default;
                if (_owner != null)
                {
                    _owner.StartOwned(() => task = work(entry.Cancellation.Token));
                }
                else
                {
                    task = work(entry.Cancellation.Token);
                }
                value = await task;
            }
            catch (Exception error)
            {
                if (Take(entry))
                {
                    entry.Completion.SetException(error);
                }
                return;
            }

            // 已撤销的实例只需要清理值。版本检查可能重入并关闭本组，之后仍要核对代次。
            if (!_active.TryGetValue(entry.Key, out Entry current) || !ReferenceEquals(current, entry))
            {
                Discard(discard, value);
                return;
            }
            Exception rejection = null;
            try
            {
                if (_closed || (_owner != null && !_owner.IsOpen) || (canDeliver != null && !canDeliver()))
                {
                    rejection = new RevOperationCanceledException();
                }
            }
            catch (Exception error)
            {
                rejection = error;
            }
            if (!Take(entry))
            {
                Discard(discard, value);
            }
            else if (rejection != null)
            {
                Discard(discard, value);
                entry.Completion.SetException(rejection);
            }
            else
            {
                entry.Completion.SetResult(value);
            }
        }

        /// <summary>只承认原来的在途实例，先释放名额及计时器；被撤销的旧回包不能影响重试。</summary>
        private bool Take(Entry entry)
        {
            if (!_active.TryGetValue(entry.Key, out Entry current) || !ReferenceEquals(current, entry))
            {
                return false;
            }
            _active.Remove(entry.Key);
            entry.Deadline.Stop();
            return true;
        }

        /// <summary>先移除代次，再通知底层中止和唤醒观察者，避免同步回包抢占终态。</summary>
        private void Cancel(Entry entry, Exception error)
        {
            if (Take(entry))
            {
                entry.Cancellation.Cancel();
                entry.Completion.SetException(error);
            }
        }

        /// <summary>归还无法交付的结果。单个回收错误保留诊断并继续完成请求。</summary>
        private static void Discard(Action<T> discard, T value)
        {
            try
            {
                discard?.Invoke(value);
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "迟到请求结果归还失败", "RevScope");
            }
        }

        /// <summary>关闭并撤销全部请求。在域关闭与界面主动关闭间可重复调用。</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            if (_owner?.IsOpen == true)
            {
                _owner.Disown(this);
            }
            foreach (Entry entry in new List<Entry>(_active.Values))
            {
                Cancel(entry, new RevOperationCanceledException());
            }
        }
    }
}

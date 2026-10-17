// RevSequenceEventBus.cs —— 强类型事件总线 + 订阅句柄（RevSequenceSubscription）
// 【用法】Publish(new MyEvent(...)) ／ Subscribe<T>(handler) → Dispose 反注册；BindEvents 一行接序列。
// 【要点】键是「事件类型」本身（编译期可查），替代原体系的字符串事件名。
// 【边界】本总线只服务动作序列；要和 UI 等其它系统通信请用框架的 RevEventSystem（RevEvent）。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 强类型事件总线：按<b>事件类型</b>订阅与广播（替代原体系的字符串事件名 + 通用参数槽）。
    /// <para>用法见文件头注释；订阅返回的句柄 Dispose 即反注册。</para>
    /// </summary>
    public sealed class RevSequenceEventBus
    {
        private readonly Dictionary<Type, Delegate> _handlers = new Dictionary<Type, Delegate>();

        /// <summary>当前订阅组数量（框架内部 / 调试用）</summary>
        internal int HandlerGroupCount => _handlers.Count;

        /// <summary>
        /// 订阅某类事件。
        /// <para>返回值 Dispose 即反注册（推荐 <c>using</c> 或与宿主生命周期成对调用）。</para>
        /// </summary>
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Type key = typeof(TEvent);
            _handlers.TryGetValue(key, out Delegate existing);
            _handlers[key] = Delegate.Combine(existing, handler);

            return new RevSequenceSubscription(
                () =>
                {
                    if (!_handlers.TryGetValue(key, out Delegate current)) return;

                    Delegate left = Delegate.Remove(current, handler);
                    if (left == null) _handlers.Remove(key);
                    else _handlers[key] = left;
                },
                () => _handlers.TryGetValue(key, out Delegate current) && current != null
                      && Array.IndexOf(current.GetInvocationList(), handler) >= 0);
        }

        /// <summary>
        /// 广播事件（同步调用所有订阅者）。
        /// <para>按本次订阅快照逐个隔离异常；失败只记录日志，继续通知其余监听者。</para>
        /// </summary>
        public void Publish<TEvent>(TEvent evt)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out Delegate handler)) { return; }
            foreach (Action<TEvent> listener in handler.GetInvocationList())
            {
                try { listener(evt); }
                catch (Exception e) { RevLog.Exception(e, $"序列事件监听异常：{typeof(TEvent).Name}", "ActionSequence"); }
            }
        }

        /// <summary>清空所有订阅（框架内部：宿主销毁时调用，例如 RevSequencePlayer.OnDestroy）</summary>
        internal void Clear() => _handlers.Clear();
    }

    /// <summary>
    /// 一次订阅的句柄：<see cref="Dispose"/> 即反注册（幂等：重复 Dispose 安全）。
    /// </summary>
    public sealed class RevSequenceSubscription : IDisposable
    {
        private Action _unsubscribe;        // 反注册动作（Dispose 后置空，保证幂等）
        private readonly Func<bool> _isAlive;

        internal RevSequenceSubscription(Action unsubscribe, Func<bool> isAlive)
        {
            _unsubscribe = unsubscribe;
            _isAlive = isAlive;
        }

        /// <summary>
        /// 这个订阅还生效吗（框架内部：用于"订阅失效就自动解绑"的场景）。
        /// <para>注意成本：事件总线的实现会走一次 <c>GetInvocationList()</c>（会分配），别放在每帧路径里。</para>
        /// </summary>
        internal bool IsAlive => _unsubscribe != null && (_isAlive == null || _isAlive());

        /// <summary>反注册（幂等）</summary>
        public void Dispose()
        {
            Action action = _unsubscribe;
            _unsubscribe = null;            // 先置空再执行：防止反注册过程中再次 Dispose 造成递归
            action?.Invoke();
        }
    }
}

// RevServiceRegistry.cs —— 实例表（框架内部，只做存取，不做决策）
// 【为什么单独一张表】它是"作用域隔离"的落点：根容器一张表存 Singleton，每个作用域各一张表存 Scoped
//   （参考实现把服务表做成实例成员而非全局静态，解决了同一服务类型多场景多实例的问题，这里是它的泛化版）。
// 【三个列表各干什么】实例表（按类型取）、创建顺序（逆序释放）、Tick 列表（能力探测自动挂载）。
// 【逆序释放】后创建的往往依赖先创建的 → 释放必须倒着来（参考实现"全量卸载忽略摘钩子"的教训之一）。

using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution
{
    /// <summary>一个容器自己持有的实例集合（Singleton 表 / 某个作用域的 Scoped 表）。</summary>
    internal sealed class RevServiceRegistry
    {
        private readonly Dictionary<Type, object> _instances;
        private readonly List<object> _creationOrder;          // 创建顺序（释放时倒序走）
        private RevITickable[] _tickSnapshot = Array.Empty<RevITickable>();
        private bool _tickDirty = true;
        private bool _ticking;
        private readonly List<RevITickable> _tickables;        // 实现了 RevITickable 的实例（创建时探测，避免每帧 cast）

        internal RevServiceRegistry(int capacity = 8)
        {
            _instances = new Dictionary<Type, object>(capacity);
            _creationOrder = new List<object>(capacity);
            _tickables = new List<RevITickable>(4);
        }

        /// <summary>本容器已持有的实例数（调试用）</summary>
        internal int Count => _instances.Count;

        /// <summary>按类型取已持有的实例</summary>
        internal bool TryGet(Type type, out object instance) => _instances.TryGetValue(type, out instance);

        /// <summary>放入表里（只在"创建成功 + OnInit 成功"之后调用）</summary>
        internal void Add(Type type, object instance, bool ownsInstance)
        {
            _instances[type] = instance;

            // ★ Bug 修复（2026-09-30）：同一实例注册到多个服务类型时（例如
            //   AddSingleton<IAudio>(svc) 之后又 AddSingleton<IAudioEx>(svc)，都是合法用法），
            //   创建序表与 Tick 列表只能挂一次 —— 原实现会重复追加，导致：
            //   ① DisposeAll 对它 Dispose 两遍（双重释放）；② 每帧 OnTick 被驱动两遍，
            //   直接违反 RevITickable 写明的承诺"同一个服务不会被驱动两次（避免一帧跑两次）"。
            //   （Contains 是 O(n)，但 Add 只发生在创建期、每个服务类型一次，n = 服务数，可忽略。）
            if (_creationOrder.Contains(instance))
            {
                if (!ownsInstance) _ownedFlags[instance] = false;   // 任一次声明"业务自己的"，容器就永不释放它
                return;
            }

            _creationOrder.Add(instance);

            // 能力探测：实现了 RevITickable 就自动进 Tick 列表
            // （对应王者"注册后按接口自动挂到帧更新通道"的精华，但挂钩子的范围收敛在"这个容器"里）
            if (instance is RevITickable tickable) { _tickables.Add(tickable); _tickDirty = true; }

            if (!ownsInstance) _ownedFlags[instance] = false;
        }

        // 只对"容器自己创建的"实例做释放；业务传入的实例不动它（谁创建谁负责）
        private readonly Dictionary<object, bool> _ownedFlags = new Dictionary<object, bool>(8);

        /// <summary>回滚：创建或 OnInit 失败时把这个实例摘掉（不留半初始化的坏实例）</summary>
        internal void Remove(Type type, object instance)
        {
            _instances.Remove(type);
            _creationOrder.Remove(instance);
            _ownedFlags.Remove(instance);

            if (instance is RevITickable tickable) { _tickables.Remove(tickable); _tickDirty = true; }
        }

        /// <summary>驱动本容器的可 Tick 实例（身份快照：Tick 里新创建的服务从下一帧开始被驱动）</summary>
        internal void Tick(float deltaTime)
        {
            if (_ticking) { return; }
            if (_tickDirty)
            {
                _tickSnapshot = _tickables.ToArray();
                _tickDirty = false;
            }
            RevITickable[] snapshot = _tickSnapshot;
            _ticking = true;
            try
            {
                foreach (RevITickable service in snapshot)
                {
                    if (!_tickables.Contains(service)) { continue; }
                    try { service.OnTick(deltaTime); }
                    catch (Exception e) { RevLog.Exception(e, "作用域服务 Tick 异常", "Services"); }
                }
            }
            finally { _ticking = false; }
        }

        /// <summary>释放本容器的全部实例（逆创建序；只释放容器自己创建的）</summary>
        internal void DisposeAll()
        {
            List<Exception> exceptions = null;
            try
            {
                for (int i = _creationOrder.Count - 1; i >= 0; i--)
                {
                    object instance = _creationOrder[i];

                    if (_ownedFlags.TryGetValue(instance, out bool owned) && !owned) continue;   // 业务自己的实例：不动
                    if (!(instance is IDisposable disposable)) continue;

                    try { disposable.Dispose(); }
                    catch (Exception e)
                    {
                        // IDisposable 是业务代码，可能因为文件已关闭、网络断开等原因抛异常。若这里直接让异常冒出循环，
                        // 后面创建的服务就永远得不到 Dispose，持有的资源会泄漏；所以记下错误、继续倒序清理全部实例，结束后再统一报告。
                        if (exceptions == null) exceptions = new List<Exception>();
                        exceptions.Add(e);
                    }
                }
            }
            finally
            {
                // 即使有 Dispose 抛异常，也要清空容器记录，防止下一次释放时重复访问已经处理过的实例。
                _instances.Clear();
                _creationOrder.Clear();
                _tickables.Clear();
                _tickSnapshot = Array.Empty<RevITickable>();
                _tickDirty = true;
                _ownedFlags.Clear();
            }

            if (exceptions != null) throw new AggregateException("释放服务时有一个或多个 IDisposable 抛出异常。", exceptions);
        }

        /// <summary>把已持有的服务类型列出来（写进"取不到服务"的错误信息，让排查一眼到位）</summary>
        internal string DescribeHeldTypes()
        {
            if (_instances.Count == 0) return "（本容器还没有创建过任何服务）";

            StringBuilder builder = new StringBuilder();
            foreach (Type type in _instances.Keys)
            {
                if (builder.Length > 0) builder.Append("、");
                builder.Append(type.Name);
            }

            return builder.ToString();
        }
    }
}

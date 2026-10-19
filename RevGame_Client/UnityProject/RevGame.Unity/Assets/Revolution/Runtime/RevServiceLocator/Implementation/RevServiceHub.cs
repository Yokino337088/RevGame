// ============================================================
// RevServiceHub.cs —— 动态服务表的内核（框架内部；业务只用门面 RevServices）
//
// 位置：Runtime\RevServiceLocator\Implementation\
//
// 【它和 RevServiceLocator（Builder 那套）的区别】
//   · RevServiceLocator：先 Create().Add…().Build() 冻结，装配只读，适合"作用域 / 多实例 / 单测隔离"；
//   · RevServiceHub：注册表本身是活的 —— 随时注册、随时注销，适合"一行注册、用完就卸、别占内存"。
//   两者共用同一套语义：懒创建、OnInit 钩子、循环依赖报错、谁创建谁释放、逆序释放。
//
// 【为什么要有"注销"】只能注册不能注销的容器，服务一旦注册就会一直占着内存（连同它引用的所有东西）。
//   注销 = 把它从表里摘掉 + 如果是容器创建的就 Dispose + 不再被每帧驱动。
//
// 【注销的三条规矩（写进代码，不靠自觉）】
//   ① 容器创建的服务 → 注销时 Dispose（逆创建序）；你自己传进来的实例 → 注销只是"摘表"，不动它（谁创建谁负责）；
//   ② 注销之后别人手里已经拿到的引用依然有效（容器不会替你把它们变成 null）—— 所以注销前想清楚还有谁在用；
//   ③ 注销后再取 → 明确报错，并在错误信息里告诉你"它曾被注销过"，不让你对着"没注册过"的提示瞎找。
//
// 【纯 C#】不引用 UnityEngine：可以链接进普通 .NET 工程跑断言。进 Play 复位 / 每帧驱动由 Support 层补。
// 【线程】只允许主线程（和 Unity 一致）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution
{
    /// <summary>动态服务表：随时注册 / 注销，懒创建，注销时按"谁创建谁释放"回收。</summary>
    internal sealed class RevServiceHub : RevIServiceLocator
    {
        /// <summary>一条注册记录 + 它创建出来的实例（实例为 null 表示还没被取用过）。</summary>
        private sealed class Entry
        {
            internal readonly Type ServiceType;
            internal readonly Func<RevIServiceLocator, object> Factory;
            internal readonly bool OwnsInstance;      // 容器创建的为 true；业务传进来的实例为 false
            internal readonly object Owner;           // 谁注册的（用于 UnregisterAllOf；可为 null = 全局）
            internal readonly string Origin;          // 这条注册是怎么来的（写进错误信息，便于定位）
            internal object Instance;

            internal Entry(Type serviceType, Func<RevIServiceLocator, object> factory, bool ownsInstance, object owner, string origin)
            {
                ServiceType = serviceType;
                Factory = factory;
                OwnsInstance = ownsInstance;
                Owner = owner;
                Origin = origin;
            }
        }

        private readonly Dictionary<Type, Entry> _entries = new Dictionary<Type, Entry>(16);
        private readonly List<Entry> _creationOrder = new List<Entry>(16);        // 已创建的实例（释放时倒序走）
        private readonly List<RevITickable> _tickables = new List<RevITickable>(4);
        private readonly List<Type> _creating = new List<Type>(4);                // 正在创建 / 初始化（循环依赖检测）
        private readonly HashSet<Type> _unregistered = new HashSet<Type>();       // 曾被注销过的类型（只用来让报错更友好）

        private bool _ticking;
        private bool _tickDirty;

        /// <summary>第一次出现可 Tick 的服务时回调（由门面接到 Support 层，去挂每帧驱动）。内核只"喊一声"，不知道宿主是什么。</summary>
        internal Action TickableCreated;

        /// <summary>已注册的服务个数（调试 / 断言用）。</summary>
        internal int Count => _entries.Count;

        /// <summary>当前是否存在"已创建且可 Tick"的服务（Support 层据此决定要不要挂每帧驱动；空列表就一帧都不多跑）。</summary>
        internal bool HasTickable => _tickables.Count > 0;

        // ============================================================
        // 注册 / 注销
        // ============================================================

        /// <summary>注册一条服务。默认懒创建；eager 在登记后走同一条 Resolve 链立刻创建，失败仅撤销本次登记。</summary>
        internal void Register(Type serviceType, Func<RevIServiceLocator, object> factory, bool ownsInstance, object owner, string origin, bool eager = false)
        {
            if (_entries.TryGetValue(serviceType, out Entry existing))
            {
                throw new InvalidOperationException(
                    $"服务 {serviceType.Name} 已经注册过了（第一次来自 {existing.Origin}，这次来自 {origin}）。" +
                    $"要换实现：先 RevServices.Unregister<{serviceType.Name}>() 再注册；" +
                    "要同时存在多份（比如两局战斗各一份）：改用 RevServiceLocator.Create() 的作用域（见《服务定位器使用说明》进阶一节）。");
            }

            var entry = new Entry(serviceType, factory, ownsInstance, owner, origin);
            _entries[serviceType] = entry;
            _unregistered.Remove(serviceType);
            if (!eager) { return; }

            try
            {
                // 与首次 GetRequired 共用 Factory / OnInit / Tick / 循环依赖检查；不为立即创建写第二套生命周期。
                Resolve(serviceType, required: true);
            }
            catch
            {
                // ★ 懒创建失败会保留注册供下次取用重试；立即创建失败发生在 Register 内，调用者认为注册尚未成功。
                // 只撤销自己的 Entry：工厂重入时若已注销并注册了新 Entry，不可误删后来者。
                if (_entries.TryGetValue(serviceType, out Entry current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(serviceType);
                    _unregistered.Remove(serviceType);
                    try { Release(entry); }
                    catch { /* 清理异常不能掩盖构造 / OnInit 的原始失败原因 */ }
                }
                throw;
            }
        }

        /// <summary>
        /// 注销一个服务：摘表 → 不再被驱动 → 如果是容器创建的就 Dispose。没注册过返回 false。
        /// <para>★ Dispose 抛异常时，表已经摘干净了（状态一致），异常再继续抛给调用方。</para>
        /// </summary>
        internal bool Unregister(Type serviceType)
        {
            if (!_entries.TryGetValue(serviceType, out Entry entry))
            {
                return false;
            }

            _entries.Remove(serviceType);
            _unregistered.Add(serviceType);
            Release(entry);
            return true;
        }

        /// <summary>注销某个 owner 注册的全部服务（对象销毁时一行清干净）。逆创建序释放；个别 Dispose 抛错不影响其余，最后统一抛出。</summary>
        internal int UnregisterAllOf(object owner)
        {
            if (owner == null)
            {
                return 0;
            }

            List<Entry> matched = new List<Entry>(4);

            // 先走"已创建"的（倒序：后创建的先放，因为后创建的可能依赖先创建的）
            for (int i = _creationOrder.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_creationOrder[i].Owner, owner))
                {
                    matched.Add(_creationOrder[i]);
                }
            }

            // 再补上"登记了但还没创建"的（它们没有实例，摘表即可）
            foreach (KeyValuePair<Type, Entry> pair in _entries)
            {
                if (ReferenceEquals(pair.Value.Owner, owner) && pair.Value.Instance == null)
                {
                    matched.Add(pair.Value);
                }
            }

            return RemoveEntries(matched);
        }

        /// <summary>注销全部服务（逆创建序释放）。返回注销个数。</summary>
        internal int UnregisterAll()
        {
            List<Entry> all = new List<Entry>(_entries.Count);

            for (int i = _creationOrder.Count - 1; i >= 0; i--)
            {
                all.Add(_creationOrder[i]);
            }

            foreach (KeyValuePair<Type, Entry> pair in _entries)
            {
                if (pair.Value.Instance == null)
                {
                    all.Add(pair.Value);
                }
            }

            return RemoveEntries(all);
        }

        /// <summary>
        /// 进 Play / 换工程时彻底清空（关闭 Domain Reload 时静态数据会跨 Play 残留）。
        /// 上一局留下的实例可能已经引用了被销毁的对象，Dispose 抛错没有意义，所以这里吞掉异常、保证一定清干净。
        /// </summary>
        internal void ResetForNewSession()
        {
            try { UnregisterAll(); }
            catch { /* 上一局的 Dispose 失败不影响新一局启动 */ }

            _entries.Clear();
            _creationOrder.Clear();
            _tickables.Clear();
            _creating.Clear();
            _unregistered.Clear();
            _ticking = false;
            _tickDirty = false;
        }

        /// <summary>逐个注销一批记录；任何一个 Dispose 抛错都先记下，全部处理完再统一抛出。</summary>
        private int RemoveEntries(List<Entry> entries)
        {
            List<Exception> exceptions = null;
            int removed = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];

                // 先从表里摘掉（以免 Dispose 回调里又取到它）；表里的那条必须还是这一条，防止重复处理
                if (!_entries.TryGetValue(entry.ServiceType, out Entry current) || !ReferenceEquals(current, entry))
                {
                    continue;
                }

                _entries.Remove(entry.ServiceType);
                _unregistered.Add(entry.ServiceType);
                removed++;

                try
                {
                    Release(entry);
                }
                catch (Exception e)
                {
                    if (exceptions == null) { exceptions = new List<Exception>(); }
                    exceptions.Add(e);
                }
            }

            if (exceptions != null)
            {
                throw new AggregateException("注销服务时有一个或多个 Dispose 抛出异常（其余服务已正常注销）。", exceptions);
            }

            return removed;
        }

        /// <summary>释放一条记录名下的实例：摘创建序 → 摘 Tick 列表 → 需要时 Dispose。没创建过则什么也不做。</summary>
        private void Release(Entry entry)
        {
            object instance = entry.Instance;
            if (instance == null)
            {
                return;
            }

            entry.Instance = null;
            _creationOrder.Remove(entry);

            // 同一个实例可能被注册成多个服务类型（例如 IAudio 和 IAudioEx 指向同一个对象）：还有别的类型在用它时，既不能停驱动也不能 Dispose
            if (IsHeldByAnother(instance))
            {
                return;
            }

            RemoveTickable(instance);

            if (entry.OwnsInstance && instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private bool IsHeldByAnother(object instance)
        {
            for (int i = 0; i < _creationOrder.Count; i++)
            {
                if (ReferenceEquals(_creationOrder[i].Instance, instance))
                {
                    return true;
                }
            }

            return false;
        }

        // ============================================================
        // 取服务（RevIServiceLocator）
        // ============================================================

        /// <summary>取服务（必须有）：没注册就抛异常，错误信息里列出当前已注册的服务。</summary>
        public T GetRequired<T>() where T : class
        {
            return (T)Resolve(typeof(T), required: true);
        }

        /// <summary>取服务（可能有）：没注册返回 null。</summary>
        public T Get<T>() where T : class
        {
            return Resolve(typeof(T), required: false) as T;
        }

        /// <summary>尝试取服务：拿到返回 true，否则 false 且 service 为 null。</summary>
        public bool TryGet<T>(out T service) where T : class
        {
            service = Resolve(typeof(T), required: false) as T;
            return service != null;
        }

        /// <summary>这个类型注册过吗（不会创建它）。</summary>
        internal bool IsRegistered(Type serviceType) => _entries.ContainsKey(serviceType);

        // ============================================================
        // 解析 / 创建
        // ============================================================

        private object Resolve(Type type, bool required)
        {
            // ⓪ 循环依赖：这个类型正在创建 / 初始化中，却又被别人取用 → 报错并打印依赖链
            if (_creating.Count > 0 && _creating.Contains(type))
            {
                throw new InvalidOperationException(
                    $"检测到循环依赖：{BuildDependencyChain(type)} —— 服务之间不能互相等对方先创建。" +
                    "做法：构造函数里只初始化自己（不要取别的服务），需要依赖就在 OnInit 里取，并且不要互相取用。");
            }

            if (!_entries.TryGetValue(type, out Entry entry))
            {
                if (required)
                {
                    throw new InvalidOperationException(BuildNotFoundMessage(type));
                }

                return null;
            }

            // 热路径：已创建过就是一次字典查找 + 一次判空
            if (entry.Instance != null)
            {
                return entry.Instance;
            }

            return Create(entry);
        }

        /// <summary>
        /// 创建并登记一个实例：先入表、再 OnInit；创建或 OnInit 失败就回滚，绝不把半初始化的实例留在表里。
        /// 回滚后这条注册依然在，下次取用会重新尝试创建。
        /// </summary>
        private object Create(Entry entry)
        {
            Type type = entry.ServiceType;
            _creating.Add(type);

            try
            {
                object instance = entry.Factory(this);

                if (instance == null)
                {
                    throw new InvalidOperationException($"创建 {type.Name} 时工厂返回了 null（{entry.Origin}）");
                }

                // 工厂执行期间这条注册可能被注销了（工厂里调了 Unregister）：这时不能再把实例挂进表里
                if (!_entries.TryGetValue(type, out Entry current) || !ReferenceEquals(current, entry))
                {
                    DisposeQuietly(entry, instance);
                    throw new InvalidOperationException($"服务 {type.Name} 在创建过程中被注销了，本次取用作废（请不要在工厂里注销自己）。");
                }

                entry.Instance = instance;
                _creationOrder.Add(entry);
                try
                {
                    // 宿主首次挂 Tick 驱动也可能抛异常；和 OnInit 一样必须回滚实例，不能留半成品。
                    AddTickable(instance);
                    if (instance is RevIServiceInit init)
                    {
                        init.OnInit(this);
                    }
                }
                catch
                {
                    Rollback(entry, instance);
                    throw;
                }

                // OnInit 里把自己注销了：此时实例已被释放，不能再当成功结果交出去
                if (entry.Instance == null)
                {
                    throw new InvalidOperationException($"服务 {type.Name} 在 OnInit 过程中被注销了，本次取用作废（请不要在 OnInit 里注销自己）。");
                }

                return instance;
            }
            finally
            {
                _creating.Remove(type);
            }
        }

        /// <summary>OnInit 失败的回滚：摘掉实例（不留坏对象），容器创建的还要 Dispose；Dispose 的异常要吞掉，不能盖住 OnInit 的原始异常。</summary>
        private void Rollback(Entry entry, object instance)
        {
            entry.Instance = null;
            _creationOrder.Remove(entry);

            if (!IsHeldByAnother(instance))
            {
                RemoveTickable(instance);
                DisposeQuietly(entry, instance);
            }
        }

        private static void DisposeQuietly(Entry entry, object instance)
        {
            if (entry.OwnsInstance && instance is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch { /* 回滚路径上的清理失败不能盖住原始异常 */ }
            }
        }

        // ============================================================
        // 每帧驱动
        // ============================================================

        private void AddTickable(object instance)
        {
            if (instance is RevITickable tickable && !_tickables.Contains(tickable))
            {
                _tickables.Add(tickable);
                TickableCreated?.Invoke();
            }
        }

        private void RemoveTickable(object instance)
        {
            if (!(instance is RevITickable tickable))
            {
                return;
            }

            if (_ticking)
            {
                // 正在遍历 Tick 列表时（OnTick 里注销了某个服务）：只打空标记，遍历结束后再统一压缩，避免下标错位漏掉别的服务
                int index = _tickables.IndexOf(tickable);
                if (index >= 0)
                {
                    _tickables[index] = null;
                    _tickDirty = true;
                }

                return;
            }

            _tickables.Remove(tickable);
        }

        /// <summary>
        /// 每帧驱动已创建的、实现了 RevITickable 的服务。
        /// <para>onError 不为 null 时：某个服务的 OnTick 抛异常只上报它、不影响其余服务继续被驱动（Unity 下接到日志）；
        /// 为 null 时异常直接抛给调用方（单测里想让问题当场炸出来）。</para>
        /// <para>本帧新创建的服务从下一帧开始被驱动。</para>
        /// </summary>
        internal void Tick(float deltaTime, Action<Exception> onError)
        {
            int count = _tickables.Count;
            if (count == 0)
            {
                return;
            }

            bool outer = _ticking;
            _ticking = true;

            try
            {
                for (int i = 0; i < count && i < _tickables.Count; i++)
                {
                    RevITickable tickable = _tickables[i];
                    if (tickable == null)
                    {
                        continue;
                    }

                    if (onError == null)
                    {
                        tickable.OnTick(deltaTime);
                        continue;
                    }

                    try { tickable.OnTick(deltaTime); }
                    catch (Exception e) { onError(e); }
                }
            }
            finally
            {
                if (!outer)
                {
                    _ticking = false;

                    if (_tickDirty)
                    {
                        _tickables.RemoveAll(IsNull);
                        _tickDirty = false;
                    }
                }
            }
        }

        private static bool IsNull(RevITickable tickable) => tickable == null;

        // ============================================================
        // 错误信息 / 调试显示
        // ============================================================

        private string BuildNotFoundMessage(Type type)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("取不到服务 ").Append(type.Name).Append("（当前没有注册）。");
            builder.Append("\n· 已注册的服务：").Append(DescribeRegistered());

            if (_unregistered.Contains(type))
            {
                builder.Append("\n· ★ 它曾经被 Unregister 注销过 —— 注销后不能再取；如果还要用，请重新 RevServices.Register。");
            }

            if (!type.IsInterface)
            {
                builder.Append("\n· 提醒：注册时通常写的是<接口>（Register<IXxx, Xxx>()），取的时候也要用接口（GetRequired<IXxx>()）");
            }

            builder.Append("\n· 其它常见原因：① 还没执行 Register（注册要在取用之前，通常放在启动入口或模块的 Awake / OnEnable）；② 注册它的对象已经销毁并连带注销了它（owner 清理）");
            return builder.ToString();
        }

        private string DescribeRegistered()
        {
            if (_entries.Count == 0)
            {
                return "（一个都没有 —— 是不是忘了 Register？）";
            }

            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<Type, Entry> pair in _entries)
            {
                if (builder.Length > 0) { builder.Append("、"); }
                builder.Append(pair.Key.Name);
            }

            return builder.ToString();
        }

        private string BuildDependencyChain(Type repeated)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < _creating.Count; i++)
            {
                if (builder.Length > 0) { builder.Append(" → "); }
                builder.Append(_creating[i].Name);
            }

            builder.Append(" → ").Append(repeated.Name);
            return builder.ToString();
        }

        /// <summary>把当前注册情况打成一段文字（排查"谁注册了什么、谁还占着"用）。</summary>
        internal string Describe()
        {
            if (_entries.Count == 0)
            {
                return "RevServices：当前没有注册任何服务";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("RevServices：已注册 ").Append(_entries.Count).Append(" 个，已创建 ").Append(_creationOrder.Count).Append(" 个");

            foreach (KeyValuePair<Type, Entry> pair in _entries)
            {
                Entry entry = pair.Value;
                builder.Append("\n  · ").Append(entry.ServiceType.Name)
                       .Append(entry.Instance == null ? "（未创建）" : "（已创建）")
                       .Append(entry.OwnsInstance ? "" : "（实例归业务释放）")
                       .Append(entry.Owner == null ? "" : "，owner=" + entry.Owner.GetType().Name)
                       .Append("，来自 ").Append(entry.Origin);
            }

            return builder.ToString();
        }
    }
}

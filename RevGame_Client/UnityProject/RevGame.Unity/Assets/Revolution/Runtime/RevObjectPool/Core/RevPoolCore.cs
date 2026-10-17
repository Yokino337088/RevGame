// ============================================================
// RevPoolCore.cs —— 池的引擎（纯 C#，GameObject 与 C# 对象共用）
//
// 位置：Runtime\ObjectPool\Core\
//
// 【为什么一套核心能同时服务两类对象】
//   两类对象的差别只有四件事：怎么造、取出时做什么、归还时做什么、怎么销毁
//   （GameObject → Instantiate/Destroy；C# 对象 → new / 交给 GC）。
//   把这四件事做成注入的回调后，"池的行为"（LIFO、容量、统计、延迟回收、重复归还拦截）
//   就完全共用 —— 而且因为它不引用 UnityEngine，可以在工程外直接跑断言。
//
// 【LIFO：从尾巴上取】（王者 CGameObjectPool 的做法）
//   最后放回去的对象"最新鲜"（缓存还热、内存局部性好），优先复用它。
//   实现上 _idle 用 List 当栈：归还 Add、取出 RemoveAt(Count-1)，都是 O(1)，且零分配。
//
// 【延迟回收】
//   Return(item, delayFrames)：立刻失活（业务马上看不到它），但要等 N 帧才真正进空闲列表。
//   王者用它避免"刚回收、同一帧又被取出来"的抖动。默认 0 = 立即回收。
//
// 【重复归还拦截：修掉旧框架的一个真 bug】
//   旧 PushObj 直接 queue.Enqueue / stack.Push，同一个对象归还两次就会在池里出现两份，
//   之后两次取出拿到的是**同一个实例**，两个使用者互相踩。
//   这里用一个"引用相等"的集合记录空闲对象，重复归还当场拒绝并报错。
//   ★ 必须引用相等：业务类型一旦重写了 Equals（值语义），默认比较器会把
//     "两个内容相同但不同的实例"误判成重复，静默丢对象。
// ============================================================
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Revolution
{
    /// <summary>
    /// 池的核心实现。业务不直接用它 —— 走 <see cref="RevRefPool"/>（C# 引用对象）或
    /// <see cref="RevPool"/>（GameObject / 组件）。
    /// </summary>
    public sealed class RevPoolCore<T> : IRevPool where T : class
    {
        /// <summary>延迟回收队列的一项（struct：进出队列不产生 GC）</summary>
        private struct DelayEntry
        {
            public T Item;
            public int FramesLeft;
        }

        // ===== 注入的四件事 =====
        private readonly Func<T> _create;
        private readonly Func<T, bool> _isAlive;
        private readonly Predicate<T> _isDead;
        private int _sweepFrame;
        private readonly Action<T> _onTake;
        private readonly Action<T> _onPut;
        private readonly Action<T> _onDestroy;

        // ===== 数据 =====
        // _idle 当栈用：尾部 = 最后归还的（LIFO 先取它）
        private readonly List<T> _idle = new List<T>();
        // 空闲对象的"引用相等"集合：重复归还拦截（见文件头）
        private readonly HashSet<T> _idleSet = new HashSet<T>(ReferenceComparer.Instance);
        // 延迟回收队列 + 对应的引用相等集合：延迟期间重复归还也要拦（否则同一实例会同时躺在延迟队列和空闲列表）
        private readonly List<DelayEntry> _delaying = new List<DelayEntry>();
        private readonly HashSet<T> _delayingSet = new HashSet<T>(ReferenceComparer.Instance);
        private readonly HashSet<T> _activeSet = new HashSet<T>(ReferenceComparer.Instance);
        private readonly HashSet<T> _returningSet = new HashSet<T>(ReferenceComparer.Instance);

        // ===== 统计 =====
        // 生命周期统计（不清零；在用数量由 _activeSet 精确跟踪）
        private int _created;
        private int _destroyed;
        private int _lost;
        // 流量类（ResetStats 清它）
        private int _get;
        private int _hit;
        private int _miss;
        private int _return;

        /// <param name="name">池名（日志/统计用）</param>
        /// <param name="capacity">空闲上限（0 = 不限）</param>
        /// <param name="create">怎么造一个新对象</param>
        /// <param name="isAlive">对象是否还在（GameObject 被外部 Destroy 时返回 false；C# 对象恒为 true）</param>
        /// <param name="onTake">取出时（GameObject → 激活；C# 对象 → OnPoolGet）</param>
        /// <param name="onPut">归还时（GameObject → 失活 + 挂回池节点；C# 对象 → OnPoolReturn）</param>
        /// <param name="onDestroy">真正销毁时（GameObject → Object.Destroy；C# 对象 → null，交给 GC）</param>
        public RevPoolCore(string name, int capacity, Func<T> create, Func<T, bool> isAlive,
            Action<T> onTake = null, Action<T> onPut = null, Action<T> onDestroy = null)
        {
            Name = name ?? typeof(T).Name;
            Capacity = capacity;
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _isAlive = isAlive ?? (item => item != null);
            _isDead = item => !_isAlive(item);
            _onTake = onTake;
            _onPut = onPut;
            _onDestroy = onDestroy;
        }

        public string Name { get; }

        public int Capacity { get; set; }

        public int IdleCount => _idle.Count;

        /// <summary>当前已取出且未归还的对象数（不包含延迟回收队列）。</summary>
        public int ActiveCount => _activeSet.Count;

        public int RecyclingCount => _delaying.Count;

        // ==================== 取 / 还 ====================

        /// <summary>
        /// 取一个对象：池里有活的就复用（LIFO），没有就创建。
        /// 创建失败（例如 prefab 已经没了）时返回 null 并报错 —— 不抛异常，游戏逻辑里可以直接判空。
        /// </summary>
        public T Get()
        {
            _get++;

            while (_idle.Count > 0)
            {
                int last = _idle.Count - 1;
                T item = _idle[last];
                _idle.RemoveAt(last);
                _idleSet.Remove(item);

                // 空壳（被外部销毁 / 场景卸载带走的）→ 丢掉它继续找下一个
                if (!_isAlive(item)) { _lost++; continue; }

                _activeSet.Add(item);
                _hit++;
                _onTake?.Invoke(item);
                return item;
            }

            _miss++;
            T created = _create();

            if (created == null)
            {
                RevPoolLog.Error($"池 \"{Name}\" 创建对象失败，本次取出返回 null。" +
                                 $"常见原因：prefab 被卸载 / 被销毁 / 资源加载失败。");
                return null;
            }

            _created++;
            _activeSet.Add(created);
            _onTake?.Invoke(created);
            return created;
        }

        /// <summary>
        /// 归还对象。返回是否真的收下了（false = 被拒绝，日志里会说明原因）。
        ///
        /// <paramref name="delayFrames"/> &gt; 0 时进入延迟回收：对象立刻失活，
        /// 但要等 <see cref="Tick"/> 推进够帧数才真正回到空闲列表。
        /// </summary>
        public bool Return(T item, int delayFrames = 0)
        {
            if (ReferenceEquals(item, null))
            {
                RevPoolLog.Warning($"池 \"{Name}\"：归还了 null，已忽略。");
                return false;
            }

            _return++;

            // ★ 先判活：已销毁的对象不能再交给回调去碰（会直接抛异常）
            if (!_isAlive(item))
            {
                _activeSet.Remove(item);
                _lost++;
                RevPoolLog.Warning($"池 \"{Name}\"：归还的对象已经被销毁（多半是场景切换或被人 Destroy 了），已丢弃。");
                return false;
            }

            // ★ 先判重（空闲 + 延迟回收中），再碰回调：
            //   延迟归还后又立即归还，会让同一实例同时躺在延迟队列和空闲列表 ——
            //   两次 Get 拿到同一个对象（双重所有权）；判重放在 _onPut 之前，
            //   也不会把一个正在被使用的对象失活 / 挪回池节点。
            if (_idleSet.Contains(item) || _delayingSet.Contains(item) || _returningSet.Contains(item))
            {
                RevPoolLog.Error($"池 \"{Name}\"：这个对象已经在池里 / 延迟回收中或正在归还，重复归还已忽略。" +
                                 $"检查一下是不是取一次还了两次。");
                return false;
            }
            if (!_activeSet.Contains(item))
            {
                RevPoolLog.Warning($"池 \"{Name}\"：对象不属于当前在用实例，已拒绝归还。");
                return false;
            }

            // 归还时框架会调用业务的 OnPoolReturn 来清状态。这个业务回调也可能再次调用 Return（例如清理函数间接触发了回收）。
            // 此时对象还在“借出中”集合里，也还没进入空闲或延迟队列；如果不额外标记“正在归还”，第二次 Return 就会再清一次、再入池一次，
            // 最后同一个对象可能被 Get 两次发给不同业务方。先加入 _returningSet，直到回调结束后再移除，专门挡住这类递归归还。
            _returningSet.Add(item);
            try { _onPut?.Invoke(item); }        // 业务清理 / 失活 / 挂回池节点
            finally { _returningSet.Remove(item); }

            // 回调里可能把它销毁了（比如 OnPoolReturn 里 Destroy）→ 不能再入池
            if (!_isAlive(item))
            {
                _activeSet.Remove(item);
                _lost++;
                return false;
            }

            _activeSet.Remove(item);
            if (delayFrames > 0)
            {
                _delayingSet.Add(item);
                _delaying.Add(new DelayEntry { Item = item, FramesLeft = delayFrames });
                return true;
            }

            return Push(item);
        }

        /// <summary>推进延迟回收（由驱动每帧调用；工程外测试可以手动调）。</summary>
        public void Tick()
        {
            // 场景销毁 / PlayOn 目标销毁不会经过 Return；定期摘除失效包装引用，不能随池一直累积。
            if (++_sweepFrame >= 60)
            {
                _sweepFrame = 0;
                _lost += _activeSet.RemoveWhere(_isDead);
            }
            if (_delaying.Count == 0) { return; }

            // 倒序：删除元素不影响前面的下标
            for (int i = _delaying.Count - 1; i >= 0; i--)
            {
                DelayEntry entry = _delaying[i];
                entry.FramesLeft--;

                if (entry.FramesLeft > 0)
                {
                    _delaying[i] = entry;
                    continue;
                }

                _delaying.RemoveAt(i);
                _delayingSet.Remove(entry.Item);
                if (_isAlive(entry.Item)) Push(entry.Item);
                else _lost++;
            }
        }

        // ==================== 清理 ====================

        /// <summary>
        /// 清空池：销毁所有空闲对象与延迟回收中的对象，返回销毁数量。
        /// 池本身保留（对象池注册表里那条记录不动），之后仍能继续取出 / 归还。
        /// </summary>
        public int ClearIdle()
        {
            int removed = Trim(0);

            for (int i = _delaying.Count - 1; i >= 0; i--)
            {
                T item = _delaying[i].Item;
                if (_isAlive(item))
                {
                    DestroyItem(item, "Clear");
                    removed++;
                }
                else
                {
                    _lost++;
                }
            }

            _delaying.Clear();
            _delayingSet.Clear();
            return removed;
        }

        void IRevPool.Clear() => ClearIdle();

        /// <summary>强制销毁所有当前借出的实例（池重绑 / 销毁时使用）。</summary>
        public int DestroyActive()
        {
            if (_activeSet.Count == 0) return 0;

            var active = new List<T>(_activeSet);
            _activeSet.Clear();
            int removed = 0;
            for (int i = 0; i < active.Count; i++)
            {
                T item = active[i];
                if (_isAlive(item))
                {
                    DestroyItem(item, "DestroyActive");
                    removed++;
                }
                else _lost++;
            }
            return removed;
        }

        /// <summary>
        /// 只保留 keepCount 个空闲对象，其余销毁。返回销毁数量。
        /// （从"最后归还的"那一端开始丢：Trim 是内存维护动作，不额外维护热度排序。）
        /// </summary>
        public int Trim(int keepCount)
        {
            if (keepCount < 0) keepCount = 0;

            int removed = 0;
            while (_idle.Count > keepCount)
            {
                int last = _idle.Count - 1;
                T item = _idle[last];
                _idle.RemoveAt(last);
                _idleSet.Remove(item);

                DestroyItem(item, "Trim");
                removed++;
            }

            return removed;
        }

        public RevPoolStats GetStats() => new RevPoolStats
        {
            Created = _created,
            Destroyed = _destroyed,
            Lost = _lost,
            GetCount = _get,
            Hit = _hit,
            Miss = _miss,
            ReturnCount = _return,
            IdleCount = _idle.Count,
            ActiveCount = ActiveCount,
            Recycling = _delaying.Count,
            Capacity = Capacity,
        };

        /// <summary>
        /// 清空"请求类"计数（命中/未命中/请求/归还）。
        /// ★ 创建 / 销毁 / 丢失不清 —— 它们是账目，ActiveCount 就靠它们推导。
        /// </summary>
        public void ResetStats()
        {
            _get = 0;
            _hit = 0;
            _miss = 0;
            _return = 0;
        }

        public override string ToString() => $"{Name}：{GetStats()}";

        // ==================== 内部 ====================

        /// <summary>真正入池：先拦重复归还，再看容量上限。</summary>
        private bool Push(T item)
        {
            // ★ 重复归还：同一个对象已经在池里了
            if (!_idleSet.Add(item))
            {
                RevPoolLog.Error($"池 \"{Name}\"：这个对象已经在池里了，重复归还已忽略。" +
                                 $"检查一下是不是取一次还了两次（旧框架这里会静默压两份，之后两次取出拿到同一个实例）。");
                return false;
            }

            if (Capacity > 0 && _idle.Count >= Capacity)
            {
                _idleSet.Remove(item);
                DestroyItem(item, "超出空闲上限");
                return false;
            }

            _idle.Add(item);
            return true;
        }

        private void DestroyItem(T item, string reason)
        {
            _destroyed++;
            RevPoolLog.Debug($"池 \"{Name}\" 销毁一个空闲对象（{reason}）。");
            _onDestroy?.Invoke(item);
        }

        /// <summary>
        /// 引用相等的比较器：池只关心"是不是同一个实例"，
        /// 不关心业务类型怎么重写 Equals（重写过 Equals 的类型用默认比较器会误判重复）。
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<T>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}

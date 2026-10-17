// ============================================================
// RevRefPools.cs —— C# 引用对象池（纯 C#）
//
// 位置：Runtime\ObjectPool\Core\
//
// 【业务就认 Facade\RevRefPool.cs 那三个方法】
//     var msg = RevRefPool.Get<ChatMessage>();     // 一行取出
//     RevRefPool.Return(msg);                      // 一行归还
//     RevRefPool.SetCapacity<ChatMessage>(128);    // 想调上限再调
//   这个类是它背后的注册表：一个 (类型 + 变体) 一个池。
//
// 【为什么键是"类型 + 变体"而不是字符串】
//   旧框架的 key 是 nameSpace + "_" + typeof(T).Name 拼出来的字符串 ——
//   每次取/还都要拼一次字符串（分配 + 比较）。这里用值类型键：
//   Type 的哈希由运行时缓存，变体一般传常量字符串，查找过程零分配。
//   ★ 但变体是"后门"：同一语义的两条池，更推荐派生一个子类（类型即语义），
//     免得半年后没人记得 variant = "battle" 是干什么的。
//
// 【取不到池就去还，会怎样】
//   报错并拒绝（不会凭空建一个池把对象收下）—— 因为"取用同一个 key"
//   是成对出现的，还的时候找不到池，说明调用方自己搞错了（多半是 variant 不一致）。
//   旧框架这里是 Warning 后直接 return，连错在哪都看不出来。
//
// 【默认上限】DefaultCapacity（默认 64，0 = 不限）。
//   想单独调某个类型：SetCapacity<T>()；想统一调：ApplyCapacityToAll()。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>C# 引用对象池的注册表（框架内部）。业务请用 <see cref="RevRefPool"/>。</summary>
    internal static class RevRefPools
    {
        /// <summary>新建池时的默认空闲上限（0 = 不限）。</summary>
        public static int DefaultCapacity = 64;

        // (类型 + 变体) → 池
        private static readonly Dictionary<RevPoolKey, IRevPool> _pools = new Dictionary<RevPoolKey, IRevPool>();

        /// <summary>当前池数量。</summary>
        public static int PoolCount => _pools.Count;

        // ==================== 取 / 还 ====================

        /// <summary>取出一个对象（池里有就复用，没有就 new）。</summary>
        public static T Get<T>(string variant = null) where T : class, IRevPoolable, new()
            => GetOrCreatePool<T>(variant).Get();

        /// <summary>归还一个对象。返回是否真的收下了。</summary>
        public static bool Return<T>(T item, string variant = null) where T : class, IRevPoolable
        {
            if (item == null)
            {
                RevPoolLog.Warning($"归还了 null（{typeof(T).Name}），已忽略。");
                return false;
            }

            RevPoolKey key = new RevPoolKey(typeof(T), variant);

            if (!_pools.TryGetValue(key, out IRevPool pool) || !(pool is RevPoolCore<T> core))
            {
                RevPoolLog.Error($"归还的对象在池注册表里找不到对应的池：{key}。" +
                                 $"取的时候必须用同一个类型和同一个 variant（取用成对，别一边带 variant 一边不带）。");
                return false;
            }

            return core.Return(item);
        }

        // ==================== 容量 ====================

        /// <summary>设置某个池的空闲上限（会立即对池内已超出的空闲对象做一次 Trim）。</summary>
        public static void SetCapacity<T>(int capacity, string variant = null) where T : class, IRevPoolable, new()
        {
            RevPoolCore<T> pool = GetOrCreatePool<T>(variant);
            pool.Capacity = capacity;
            if (capacity > 0) pool.Trim(capacity);           // 0 = 不限，不能按 0 裁剪（会把空闲清空）
        }

        /// <summary>统一设置所有池（含以后新建的池）的空闲上限。</summary>
        public static void ApplyCapacityToAll(int capacity)
        {
            DefaultCapacity = capacity;

            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools)
            {
                pair.Value.Capacity = capacity;
                if (capacity > 0) pair.Value.Trim(capacity); // 0 = 不限，不能按 0 裁剪（会把空闲清空）
            }
        }

        // ==================== 清理 ====================

        /// <summary>清空某个池的空闲对象（池保留）。</summary>
        public static bool Clear<T>(string variant = null) where T : class, IRevPoolable
        {
            RevPoolKey key = new RevPoolKey(typeof(T), variant);
            if (!_pools.TryGetValue(key, out IRevPool pool)) return false;

            pool.Clear();
            return true;
        }

        /// <summary>清空所有池的空闲对象（池保留）。切账号 / 回登录时用。</summary>
        public static void ClearAll()
        {
            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools) pair.Value.Clear();
        }

        /// <summary>连池一起销毁（清空 + 从注册表移除；引用池没有别的资源要释放）。</summary>
        public static void DestroyAll()
        {
            ClearAll();
            _pools.Clear();
        }

        /// <summary>只保留 keep 个空闲对象，其余销毁。返回销毁总数。</summary>
        public static int TrimAll(int keep)
        {
            int removed = 0;
            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools) removed += pair.Value.Trim(keep);

            return removed;
        }

        // ==================== 驱动 / 统计 ====================

        /// <summary>推进所有池的延迟回收。由 <see cref="RevPoolDriver"/> 每帧调用。</summary>
        public static int Tick()
        {
            int count = 0;
            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools)
            {
                if (pair.Value.RecyclingCount == 0) continue;
                pair.Value.Tick();
                count++;
            }

            return count;
        }

        /// <summary>某个池的统计快照（池不存在时返回空快照）。</summary>
        public static RevPoolStats GetStats<T>(string variant = null) where T : class, IRevPoolable
            => _pools.TryGetValue(new RevPoolKey(typeof(T), variant), out IRevPool pool)
                ? pool.GetStats()
                : default;

        /// <summary>把某个池的"请求类"计数清零（想看某一段时间的命中率时用；创建/销毁账目不动）。</summary>
        public static bool ResetStats<T>(string variant = null) where T : class, IRevPoolable
        {
            RevPoolKey key = new RevPoolKey(typeof(T), variant);
            if (!_pools.TryGetValue(key, out IRevPool pool)) return false;

            pool.ResetStats();
            return true;
        }

        /// <summary>所有池的请求计数清零。</summary>
        public static void ResetStatsAll()
        {
            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools) pair.Value.ResetStats();
        }

        /// <summary>所有池的合计统计（把内存占用与命中率加总，用来看整体）。</summary>
        public static RevPoolStats GetGlobalStats()
        {
            var total = new RevPoolStats();

            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools)
            {
                RevPoolStats one = pair.Value.GetStats();
                total.Created += one.Created;
                total.Destroyed += one.Destroyed;
                total.Lost += one.Lost;
                total.GetCount += one.GetCount;
                total.Hit += one.Hit;
                total.Miss += one.Miss;
                total.ReturnCount += one.ReturnCount;
                total.IdleCount += one.IdleCount;
                total.ActiveCount += one.ActiveCount;
                total.Recycling += one.Recycling;
            }

            return total;
        }

        /// <summary>所有池名（调试 / 找泄漏用）。会分配 List，别放热路径。</summary>
        public static List<string> GetPoolNames()
        {
            var names = new List<string>(_pools.Count);
            foreach (KeyValuePair<RevPoolKey, IRevPool> pair in _pools) names.Add(pair.Value.Name);

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // ==================== 内部 ====================

        private static RevPoolCore<T> GetOrCreatePool<T>(string variant) where T : class, IRevPoolable, new()
        {
            RevPoolKey key = new RevPoolKey(typeof(T), variant);

            if (_pools.TryGetValue(key, out IRevPool found) && found is RevPoolCore<T> typed) return typed;

            RevPoolCore<T> pool = new RevPoolCore<T>(
                name: key.ToString(),
                capacity: DefaultCapacity,
                create: () => new T(),
                isAlive: null,                                   // C# 对象不会被外部销毁，用默认的 item != null
                onTake: item => item.OnPoolGet(),
                onPut: item => item.OnPoolReturn());

            _pools[key] = pool;
            RevPoolLog.Debug($"新建引用对象池：{key}（上限 {DefaultCapacity}）");
            return pool;
        }
    }
}

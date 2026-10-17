// ============================================================
// RevRefPool.cs —— C# 引用对象池门面（业务唯一入口 · 纯 C# 对象）
//
// 位置：Runtime\ObjectPool\Facade\
//
// 【一行取出，一行归还】
//     var msg = RevRefPool.Get<ChatMessage>();     // 池里有就复用，没有就 new
//     msg.Init(sender, text);
//     ...
//     RevRefPool.Return(msg);                      // ★ msg 的 OnPoolReturn 里必须把字段清干净
//
// 【池化对象要做什么】
//     1) 实现 IRevPoolable（或继承 RevPoolableBase），共两个方法：
//          OnPoolGet()    取出时唤醒（重新赋值 / 开始计时）
//          OnPoolReturn() 归还时清理（★ 字段清空、事件解绑、引用置 null）
//     2) 必须有公开无参构造函数（泛型约束 new()）。
//
// 【最经典的坑：忘了清理】
//     对象池的对象**不会被销毁**，所以字段也不会被重置。
//     "上次的数据还在"是池化最常出的事故 —— 框架把 OnPoolReturn 做成接口必填项，
//     就是为了让你写这个类的时候躲不开这个问题。
//
// 【和 GameObject 池的分工】
//     本类只管 C# 引用对象（配置 DTO、消息体、计算中间结果……）。
//     GameObject / 组件请用 RevPool。
// ============================================================
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>C# 引用对象池门面：一行取出、一行归还。</summary>
    public static class RevRefPool
    {
        /// <summary>
        /// 默认空闲上限（0 = 不限）。改它会立即对所有已有池生效，
        /// 并影响以后新建的池；想单独调某个类型用 <see cref="SetCapacity{T}"/>。
        /// </summary>
        public static int DefaultCapacity
        {
            get => RevRefPools.DefaultCapacity;
            set => RevRefPools.ApplyCapacityToAll(value);
        }

        /// <summary>当前有多少条引用池。</summary>
        public static int PoolCount => RevRefPools.PoolCount;

        // ==================== 取 / 还 ====================

        /// <summary>
        /// 取出一个对象：池里有就复用，没有就 <c>new</c>。
        /// <paramref name="variant"/> 用于"同一个类要两条互不干扰的池"（不传最好）。
        /// </summary>
        public static T Get<T>(string variant = null) where T : class, IRevPoolable, new()
            => RevRefPools.Get<T>(variant);

        /// <summary>归还对象。<b>返回是否真的收下了</b>（取用必须成对，且类型 / variant 要一致）。</summary>
        public static bool Return<T>(T item, string variant = null) where T : class, IRevPoolable
            => RevRefPools.Return(item, variant);

        // ==================== 容量 ====================

        /// <summary>设置某个类型（某条池）的空闲上限；超出部分会被立即销毁。</summary>
        public static void SetCapacity<T>(int capacity, string variant = null) where T : class, IRevPoolable, new()
            => RevRefPools.SetCapacity<T>(capacity, variant);

        /// <summary>统一设置所有引用池的上限。</summary>
        public static void ApplyCapacityToAll(int capacity) => RevRefPools.ApplyCapacityToAll(capacity);

        // ==================== 清理 ====================

        /// <summary>清空某个类型的空闲对象（池保留）。</summary>
        public static bool Clear<T>(string variant = null) where T : class, IRevPoolable
            => RevRefPools.Clear<T>(variant);

        /// <summary>清空所有引用池的空闲对象（池保留）。</summary>
        public static void ClearAll() => RevRefPools.ClearAll();

        /// <summary>销毁所有引用池（连池记录一起清掉）。</summary>
        public static void DestroyAll() => RevRefPools.DestroyAll();

        /// <summary>每条池最多留 keep 个空闲对象，其余销毁。返回销毁总数。</summary>
        public static int TrimAll(int keep) => RevRefPools.TrimAll(keep);

        // ==================== 统计 ====================

        /// <summary>某个类型的统计快照。</summary>
        public static RevPoolStats GetStats<T>(string variant = null) where T : class, IRevPoolable
            => RevRefPools.GetStats<T>(variant);

        /// <summary>把某个池的请求计数清零（想看某一段时间的命中率时用）。</summary>
        public static bool ResetStats<T>(string variant = null) where T : class, IRevPoolable
            => RevRefPools.ResetStats<T>(variant);

        /// <summary>所有引用池的请求计数清零。</summary>
        public static void ResetStatsAll() => RevRefPools.ResetStatsAll();

        /// <summary>所有引用池的合计统计。</summary>
        public static RevPoolStats GetGlobalStats() => RevRefPools.GetGlobalStats();

        /// <summary>所有引用池名（调试用，会分配）。</summary>
        public static List<string> GetPoolNames() => RevRefPools.GetPoolNames();
    }
}

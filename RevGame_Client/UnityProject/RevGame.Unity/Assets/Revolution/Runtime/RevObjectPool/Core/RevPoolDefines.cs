// ============================================================
// RevPoolDefines.cs —— 对象池的公共契约与数据
//
// 位置：Runtime\ObjectPool\Core\
//
// 【这一层是纯 C#】不引用 UnityEngine，所以能在工程外跑断言（核心逻辑不靠 Unity 也能验）。
//   Unity 相关的部分在 Core\RevGameObjectPool*.cs、Facade\、Support\ 里。
//
// 【IRevPoolable：池化对象的两个回调】
//   取出时 OnPoolGet、归还时 OnPoolReturn。
//   ★ 最要紧的一条：**字段必须在 OnPoolReturn 里清干净**。
//   对象池最经典的 bug 就是"上次的数据还在"—— 对象没被销毁，字段自然不会被重置；
//   这不是池的错，是归还时没清理。所以框架把"归还回调"做成接口的必填项
//   （而不是可选事件），逼你写这个类的时候就把"哪些字段要清"想一遍。
//
// 【RevPoolStats：一个池的运行画像】
//   HitRate（命中率）= 从池里复用 ÷ 总请求数，它是判断"池开得合不合适"的唯一指标：
//     · 接近 100% → 池大小合适（甚至偏小，可以调大上限）
//     · 很低      → 池白占内存（调小上限，或者这个对象根本不该池化）
//   王者把这类统计上报到灯塔；这里做成一份随时能打印的快照。
//
// 【ActiveCount 与 Recycling 分开统计】
//   ActiveCount 由引用相等的活动实例集合跟踪，只包含当前借出的对象；归还后立刻移出，
//   即使进入延迟回收也单独计入 Recycling，不会被错误地报成仍在使用。
//   该集合同时让池 Rebind / Dispose 能销毁所有旧代借出对象，防止归还到新 prefab 池。
//
// 【Lost 是什么】
//   池里的对象被外部销毁了（最典型：场景切换把它连带销毁）。
//   框架不做每帧扫描，而是在"下次取到它"时发现并丢掉（lazy sweep），计入 Lost。
// ============================================================
using System;
using System.Diagnostics;

namespace Revolution
{
    /// <summary>池化对象的生命周期回调。想被池管理的类实现它，或继承 <see cref="RevPoolableBase"/>。</summary>
    public interface IRevPoolable
    {
        /// <summary>从池里取出来时调用：把对象"唤醒"（初始化本次要用的状态）。</summary>
        void OnPoolGet();

        /// <summary>
        /// 归还进池时调用。★ 必须在这里把字段清干净 —— 否则下次取出来会带着上一次的数据。
        /// </summary>
        void OnPoolReturn();
    }

    /// <summary>不想手写两个空实现时继承它（默认什么都不做）。</summary>
    public abstract class RevPoolableBase : IRevPoolable
    {
        public virtual void OnPoolGet() { }

        public virtual void OnPoolReturn() { }
    }

    /// <summary>
    /// 池的管理接口（非泛型）：统计、清理、容量、延迟回收驱动。
    /// 有了它，注册表才能用同一个字典装不同 T 的池。
    /// </summary>
    public interface IRevPool
    {
        /// <summary>池名（日志与统计里用，例如 "RecycleBin" 或 "GameObject:Bullet"）。</summary>
        string Name { get; }

        /// <summary>空闲对象数量上限（0 = 不限）。超出上限的归还对象会被销毁。</summary>
        int Capacity { get; set; }

        /// <summary>当前空闲对象数。</summary>
        int IdleCount { get; }

        /// <summary>当前正在使用中的对象数（推导值，见文件头）。</summary>
        int ActiveCount { get; }

        /// <summary>延迟回收队列里的对象数。</summary>
        int RecyclingCount { get; }

        /// <summary>取一份统计快照。</summary>
        RevPoolStats GetStats();

        /// <summary>推进延迟回收（由 <see cref="RevPoolDriver"/> 每帧驱动，也可手动调）。</summary>
        void Tick();

        /// <summary>清空池：销毁所有空闲对象（池本身保留，之后仍可继续用）。</summary>
        void Clear();

        /// <summary>只保留 <paramref name="keepCount"/> 个空闲对象，其余销毁。返回销毁数量。</summary>
        int Trim(int keepCount);

        /// <summary>
        /// 清空"请求类"计数（请求 / 命中 / 未命中 / 归还），想看"某一段时间的表现"时用。
        /// 创建 / 销毁 / 丢失属于账目（在用数靠它们推导），不清。
        /// </summary>
        void ResetStats();
    }

    /// <summary>
    /// C# 引用对象池的键 = 类型 + 可选变体。
    ///
    /// 【为什么要"变体"】同一个类可能要用在两处互不干扰的场景（旧框架那个 nameSpace 参数
    ///   就是干这个的）。这里用值类型键替代"拼字符串再查字典"，查找过程零分配；
    ///   ★ 但更推荐的做法是**派生一个子类**（类型即语义），变体只是给"懒得建类"留的后门。
    /// </summary>
    public readonly struct RevPoolKey : IEquatable<RevPoolKey>
    {
        public readonly Type Type;

        public readonly string Variant;

        public RevPoolKey(Type type, string variant)
        {
            Type = type;
            Variant = string.IsNullOrEmpty(variant) ? null : variant;
        }

        public bool Equals(RevPoolKey other)
            => Type == other.Type && string.Equals(Variant, other.Variant, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is RevPoolKey other && Equals(other);

        public override int GetHashCode()
        {
            int typeHash = Type != null ? Type.GetHashCode() : 0;
            return Variant == null ? typeHash : (typeHash * 397) ^ Variant.GetHashCode();
        }

        public override string ToString()
            => Variant == null ? Type?.Name : Type?.Name + ":" + Variant;
    }

    /// <summary>统计快照（值类型：拿到的是"那一刻"的画像，不受后续操作影响）。</summary>
    public struct RevPoolStats
    {
        /// <summary>累计创建过的对象数（= 本该有多少次 Instantiate/new）。</summary>
        public int Created;

        /// <summary>累计销毁数（超上限、Trim、Clear 造成的）。</summary>
        public int Destroyed;

        /// <summary>累计"丢失"数（空闲对象被外部销毁，取用时才发现）。</summary>
        public int Lost;

        /// <summary>请求总数。</summary>
        public int GetCount;

        /// <summary>其中从池里复用到的次数（命中）。</summary>
        public int Hit;

        /// <summary>其中池里没有、新建的次数（未命中）。</summary>
        public int Miss;

        /// <summary>归还总数。</summary>
        public int ReturnCount;

        /// <summary>当前空闲数。</summary>
        public int IdleCount;

        /// <summary>当前在用数。</summary>
        public int ActiveCount;

        /// <summary>正在延迟回收中的数量。</summary>
        public int Recycling;

        /// <summary>空闲对象上限（0 = 不限）。</summary>
        public int Capacity;

        /// <summary>命中率（0~1）。池化有没有赚，看它。</summary>
        public float HitRate => GetCount > 0 ? (float)Hit / GetCount : 0f;

        public override string ToString()
            => $"创建 {Created}、销毁 {Destroyed}、丢失 {Lost}；空闲 {IdleCount}、在用 {ActiveCount}、回收中 {Recycling}（上限 {Capacity}）；" +
               $"请求 {GetCount} 次（命中 {Hit}、新建 {Miss}，命中率 {HitRate:P0}）；归还 {ReturnCount} 次";
    }

    /// <summary>
    /// 日志出口（全框架共用一处，方便业务接管）。
    /// Unity 下由 <c>Support\RevPoolUnityHooks.cs</c> 自动接到 Debug.LogWarning；
    /// 没有出口时退到标准错误 —— 绝不静默。
    /// </summary>
    public static class RevPoolLog
    {
        /// <summary>日志出口（业务可替换成自己的日志系统）。</summary>
        public static Action<string> Sink;

        /// <summary>告警（池用错了、对象丢了、容量到了……）。</summary>
        public static void Warning(string message) => Write("[RevPool] " + message);

        /// <summary>错误（调用方式不对，必须修）。</summary>
        public static void Error(string message) => Write("[RevPool][错误] " + message);

        /// <summary>
        /// 调试日志：<c>[Conditional("UNITY_EDITOR")]</c> 让它在正式包里被编译器**完全移除**
        /// （连参数求值都不发生）。这是王者"调试代码零开销"的做法。
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        public static void Debug(string message) => Write("[RevPool][调试] " + message);

        private static void Write(string message)
        {
            Action<string> sink = Sink;
            if (sink != null)
            {
                sink(message);
                return;
            }

            RevLog.Warn(message, "Pool");
        }
    }
}

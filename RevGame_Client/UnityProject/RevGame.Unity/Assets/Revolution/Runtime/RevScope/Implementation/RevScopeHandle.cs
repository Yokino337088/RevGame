// ============================================================
// RevScopeHandle.cs —— 一个"域"的实体（同时也是它的凭据）
//
// 位置：Runtime\RevScope\Implementation\
//
// 【它是什么】RevScope.Enter(...) 返回的就是它。你可以：
//   · 用 using 包一小段代码：出块自动 Close；
//   · 不理它：以后用 RevScope.Exit("域名") 关；
//   · 拿着它调 Attach / SetTickEnabled / Close。
//
// 【★ 本类最关键的一个设计：它自己就是"owner"】
//   计时器 / 事件监听 / 每帧回调的清理，都靠"owner 对象"这把钥匙：
//       RevTimer.CancelAllOf(owner)  /  RevEvent.RemoveAllByOwner(owner)  /  RevMono.RemoveAllOf(owner)
//   域里"没写 owner"的注册，框架就把**这个域的 RevScopeHandle 本身**当成 owner 填进去 ——
//   于是域关闭时只要对它自己调一遍上面三个方法，就把这个域里所有没主人的注册一网打尽。
//   好处：每次注册**不需要额外记账**（不往任何清单里加东西），所以注册路径零 GC、零额外开销。
//
// 【空句柄】Enter 失败（空域名 / 重复进入 / 正在关闭）时返回 RevScopeHandle.Empty：
//   它是一个"已关闭"的哑句柄，所有方法都是安全空操作，using 包着也不会炸 ——
//   这是框架里"空句柄"的一贯做法（和 RevTimerHandle.Empty 一样），避免调用方到处判 null。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一个域。由 <see cref="RevScope.Enter(string, RevResGroup)"/> 返回；<c>using</c> 退出即关闭。</summary>
    public sealed class RevScopeHandle : IDisposable
    {
        private static readonly RevScopeHandle EmptyInstance = new RevScopeHandle();

        /// <summary>空句柄：Enter 失败时返回它。已关闭、所有操作都是安全空操作。</summary>
        public static RevScopeHandle Empty => EmptyInstance;

        private readonly RevScopeCore _core;

        /// <summary>
        /// 本域的每帧开关（给 RevMono 热路径直接读的字段，故意不用属性）。
        /// ★ 只影响"归本域兜底"的每帧回调与 RevLogic.OnTick；不影响计时器（计时器是"时间"，不是"帧回调"）。
        /// </summary>
        internal bool TickOn = true;

        /// <summary>挂在本域上的归属对象（界面 / 手动 Attach 的对象）。★ 引用比较，不看 Equals 重载。</summary>
        internal readonly HashSet<object> Owners = new HashSet<object>(RevReferenceComparer.Instance);

        /// <summary>本域里运行中的业务单元（按启动顺序；关闭时逆序停）。</summary>
        internal readonly List<RevLogicBase> Logics = new List<RevLogicBase>(4);

        /// <summary>本域里重写了 OnTick 的业务单元（只有它们才会被每帧驱动）。</summary>
        internal readonly List<RevLogicBase> Tickers = new List<RevLogicBase>(2);

        internal readonly bool TakeOverOwner;
        internal readonly long EnteredTicks;

        /// <summary>域名。</summary>
        public string Name { get; }

        /// <summary>关闭时要整组回收的资源分组（Unknown = 不按组回收）。</summary>
        public RevResGroup Reclaim { get; }

        /// <summary>当前状态。</summary>
        public RevScopeState State { get; internal set; }

        /// <summary>排查备注（会出现在 Dump 里）。</summary>
        public string DebugNote { get; set; }

        /// <summary>是否开着（正在关闭 / 已关闭都算 false）。</summary>
        public bool IsOpen => State == RevScopeState.Open;

        /// <summary>挂在本域上的归属对象个数（不含"没写 owner 的计时器 / 事件"，那些不逐个记账）。</summary>
        public int OwnerCount => Owners.Count;

        /// <summary>本域的每帧开关（等价于 <see cref="SetTickEnabled"/>）。</summary>
        public bool TickEnabled
        {
            get => TickOn;
            set => TickOn = value;
        }

        // 空句柄专用
        private RevScopeHandle()
        {
            Name = "(空)";
            State = RevScopeState.Closed;
        }

        internal RevScopeHandle(RevScopeCore core, string name, RevResGroup reclaim, bool takeOverOwner, string note, long enteredTicks)
        {
            _core = core;
            Name = name;
            Reclaim = reclaim;
            TakeOverOwner = takeOverOwner;
            DebugNote = note;
            EnteredTicks = enteredTicks;
            State = RevScopeState.Open;
        }

        // ==================== 归属 ====================

        /// <summary>
        /// 把一个对象登记为本域的成员。域关闭时，框架会对它执行"一行清理"：
        /// 摘它的事件监听、停它的计时器、摘它的每帧回调、注销它注册的服务；界面（RevUIPanel）还会被关闭。
        /// <para>什么时候用：对象的注册写了显式 <c>owner: this</c>（所以不会被"没写 owner"的兜底管到），
        /// 但你又想让它跟着这个域一起结束。</para>
        /// <para>★ 往已关闭的域登记会被明确拒绝（并说清原因）—— 那说明这段代码在域结束之后才跑。</para>
        /// </summary>
        public void Attach(object owner)
        {
            if (owner == null || _core == null) { return; }

            // 域不是"开着"的时候（含已关闭的空句柄），内核会明确报错并拒绝，不会悄悄记到一个永远不会被清理的地方
            _core.AttachTo(this, owner);
        }

        /// <summary>链式版：先登记归属、再把对象原样返回。<c>var logic = scope.Add(new Foo());</c></summary>
        public T Add<T>(T owner) where T : class
        {
            Attach(owner);
            return owner;
        }

        /// <summary>暂停 / 恢复本域的每帧回调（对象、状态、监听都还在；这不是回收）。</summary>
        public void SetTickEnabled(bool enabled)
        {
            TickOn = enabled;
        }

        // ==================== 关闭 ====================

        /// <summary>关闭本域，返回回收的数量。重复调用安全（第二次返回 0）。</summary>
        public int Close() => _core == null ? 0 : _core.Exit(this);

        /// <summary>关闭本域并直接拿到"体检报告"（域本来就没开着时返回 null）。</summary>
        public RevScopeCloseReport CloseWithReport()
        {
            if (_core == null || State != RevScopeState.Open) { return null; }

            _core.Exit(this);
            return _core.LastCloseReport;
        }

        /// <summary>退出 using 块 = 关闭本域。</summary>
        public void Dispose() => Close();

        public override string ToString() => "域「" + Name + "」(" + State + ")";

        /// <summary>归属对象的快照（遍历期间可能有对象被摘 / 搬家，所以先拷一份再用）。</summary>
        internal object[] SnapshotOwners()
        {
            object[] snapshot = new object[Owners.Count];
            Owners.CopyTo(snapshot);
            return snapshot;
        }
    }

    /// <summary>
    /// 按"引用"比较的比较器。
    /// ★ 为什么不用默认比较器：Unity 的 Object 重载了 ==（销毁后和 null 相等）、也可能重载 Equals，
    ///   用它们做集合的键会出现"已销毁对象找不到自己"的怪事。归属对象只关心"是不是同一个对象"，所以只认引用。
    /// </summary>
    internal sealed class RevReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly RevReferenceComparer Instance = new RevReferenceComparer();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

// ============================================================
// RevEventListener.cs —— 单个"监听者"节点（事件系统的最小单元）
//
// 位置：Runtime\RevEventSystem\Core\
//
// 【它是什么】
//   一条注册记录：谁在听（Handler）、属于谁（Owner）、多急（Priority）。
//
// 【为什么 Handler 用 Delegate 存】
//   因为委托的"参数个数"信息已经写在类型里了，节点只需要存"一个委托"，
//   0~4 个参数就能共用同一个节点类型。
//   旧框架为 0~16 个参数各写了一个 EventInfo<...> 类（16 个类、360 行），
//   换来的只是"泛型参数写在类名上"——节点根本不需要知道参数个数。
//
// 【Owner 是什么、为什么要有】
//   监听者归谁所有。UI 界面注册了 5 个事件、销毁时忘了逐个反注册，事件系统
//   还攥着它的引用 → 对象永远不会被回收（事件系统最典型的泄漏）。
//   有了 Owner（可显式传，也可由 Handler.Target 自动推断），销毁时一句
//   RevEvent.RemoveAllByOwner(this) 就能一次性摘干净。
//
// 【IsAbandoned：删除为什么是"只标记"】
//   派发时是按索引遍历监听者列表的，回调里如果直接把节点从列表里摘掉，
//   下标会整体错位（漏调用 / 越界）。所以派发过程中只把节点标记为"已弃用"，
//   等派发深度归零后再统一清理。
//   这样整个派发过程零复制、零分配 —— 这是王者 CEventHandler.m_isAbandon 的做法。
//
// 【对象池】
//   AddEventListener / RemoveEventListener 的调用频率可能很高（UI 反复开关会走 OnEnable/OnDisable），
//   节点走对象池复用，避免频繁 GC。
//
// 【注意】节点只由 RevEventHandlerGroup 内部创建/回收，业务不要直接 new。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 一条监听记录：谁在听（<see cref="Handler"/>）、属于谁（<see cref="Owner"/>）、多急（<see cref="Priority"/>）。
    /// 由事件系统内部创建与回收（对象池），业务不要直接实例化。
    /// </summary>
    public sealed class RevEventListener
    {
        // 池上限：避免极端情况（同时挂着上万条监听）下池无限膨胀
        private const int PoolCapacity = 512;

        private static readonly Queue<RevEventListener> Pool = new Queue<RevEventListener>(64);

        /// <summary>监听者委托。实际类型是 <c>Action</c> / <c>Action&lt;T1&gt;</c> / … / <c>Action&lt;T1,T2,T3,T4&gt;</c> 之一。</summary>
        public Delegate Handler;

        /// <summary>归属对象：<c>RemoveAllByOwner</c> 的匹配依据；未显式指定时为 <c>Handler.Target</c>。</summary>
        public object Owner;

        /// <summary>
        /// ★ 作用域标记：注册时"没写 owner"且当时开着一个域，这里记下那个域（域关闭时按它整批摘除）。
        /// 为什么不直接把域塞进 <see cref="Owner"/>：没写 owner 时 Owner 本来是 <c>Handler.Target</c>，
        /// 业务（或界面基类）靠 <c>RemoveAllByOwner(this)</c> 摘自己的监听 —— 偷换成域就会让这条老路失效。
        /// 所以两个维度分开记：Owner 管"对象销毁时摘"，Scope 管"域关闭时摘"，谁先到谁先摘，互不干扰。
        /// 显式写了 owner 的监听 Scope 为 null（显式永远优先，域不插手）。
        /// </summary>
        internal object Scope;

        /// <summary>优先级：数值越大越先执行（同优先级按注册顺序）。</summary>
        public int Priority;

        /// <summary>注册序号：同优先级排序时保证"先注册先执行"（<c>List.Sort</c> 本身不是稳定排序）。</summary>
        public int Seq;

        /// <summary>是否已在派发过程中被"标记删除"（等派发结束再真正摘除）。</summary>
        public bool IsAbandoned;

        // 只能由池创建：构造函数私有
        private RevEventListener() { }

        /// <summary>从对象池取一个节点（池空则新建）。</summary>
        internal static RevEventListener Rent()
        {
            return Pool.Count > 0 ? Pool.Dequeue() : new RevEventListener();
        }

        /// <summary>用完归还对象池。</summary>
        internal static void Return(RevEventListener node)
        {
            if (node == null) return;

            node.Reset();

            // 注意先 Reset 再判断容量：池满时节点会被丢弃，但引用已经清干净了
            if (Pool.Count < PoolCapacity) Pool.Enqueue(node);
        }

        /// <summary>派发中标记删除：断开委托引用，但不摘列表（摘了会破坏遍历）。</summary>
        internal void Abandon()
        {
            Handler = null;          // ★ 立即断开引用：被删的监听者不该再被事件系统持有
            IsAbandoned = true;
        }

        /// <summary>重置到"刚创建"的状态。务必清空引用，否则池会替已销毁的对象"续命"（池自己变成泄漏源）。</summary>
        private void Reset()
        {
            Handler = null;
            Owner = null;
            Scope = null;
            Priority = 0;
            Seq = 0;
            IsAbandoned = false;
        }

        /// <summary>清空对象池（进 Play / 域重载时调用）。</summary>
        internal static void ClearPool()
        {
            Pool.Clear();
        }

        /// <summary>调试描述：出错时用于"点名"是哪个监听者出的问题。</summary>
        public override string ToString()
        {
            string handler = Handler == null
                ? "<已弃用>"
                : (Handler.Method.DeclaringType != null
                    ? Handler.Method.DeclaringType.Name + "." + Handler.Method.Name
                    : Handler.Method.Name);

            string owner = Owner == null ? "null" : Owner.GetType().Name;

            return $"{handler}(owner={owner}, priority={Priority})";
        }
    }
}

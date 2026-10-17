// ============================================================
// RevEventHandlerGroup.cs —— 一个事件名对应的"监听者集合"
//
// 位置：Runtime\RevEventSystem\Core\
//
// 【它负责四件事】
//   ① 装住这个事件的所有监听者（List，容量预留 1）；
//   ② 派发中增删保护（标记删除 + 派发结束统一清理）；
//   ③ 优先级排序（只有真用到优先级才付排序的代价）；
//   ④ 节点回收（还回对象池，不产生 GC）。
//
// 【容量为什么是 1 而不是默认的 4】
//   业务里绝大多数事件只有 1 个监听者。若按默认容量 4 分配，
//   每个事件白白浪费 3 个槽位；事件一多就是约 75% 的浪费。
//   （王者 CEventHandlerGroup 的结论，属于"了解业务特征后做针对性优化"。）
//
// 【派发中增删为什么这么麻烦】
//   派发是按索引遍历列表的：
//     · 回调里删除监听者 → 列表缩短 → 下标错位 → 漏调用或越界崩溃；
//     · 回调里新增监听者 → 列表变长 → 本次不该执行的监听者被执行。
//   所以规则定成：
//     · 派发中删除 = 只打标记（IsAbandoned），派发结束再统一清理；
//     · 派发中新增 = 本轮不执行，下一轮才生效（循环边界在派发开始时锁定）。
//   两条规则合起来的效果：派发过程零复制、零分配，且行为可预测。
//
// 【嵌套派发】
//   _dispatchDepth 记录派发深度，保证"A 事件的回调里又派发 A"时，
//   清理动作只在最外层结束时做一次（否则内层就会把还在遍历的列表改掉）。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 一个事件名对应的监听者集合。派发中增删保护、优先级排序、节点回收都在这里。
    /// </summary>
    public sealed class RevEventHandlerGroup
    {
        // ★ 容量预留 1：绝大多数事件只有 1 个监听者（见文件头说明）
        private readonly List<RevEventListener> _listeners = new List<RevEventListener>(1);

        private int _dispatchDepth;        // 派发深度：>0 表示"正在派发"（支持嵌套派发）
        private int _dispatchLimit;        // 最外层派发开始时锁定；嵌套派发也不执行本轮新增监听者
        private bool _changedInDispatch;   // 派发过程中列表是否被改动过（决定收尾要不要清理）
        private bool _hasPriorities;       // 此事件曾注册过非零优先级，默认优先级新增也必须重新排序
        private bool _needSort;            // 优先级顺序可能变化 → 下次最外层派发前重排

        // ★ 活跃监听者数（不含"派发中已标记删除、等收尾清理"的节点）。
        //   为什么不能直接用 _listeners.Count：派发过程中删掉的节点只是打标记、还留在列表里，
        //   用物理长度会让 Count / GetListenerCount / HasListener 虚报"还有人在听" ——
        //   排查泄漏时会看到一个早就反注册干净的监听者，判断"这个事件还该不该发"也会判断错。
        private int _liveCount;

        /// <summary>活跃监听者数量（已反注册、只等收尾清理的节点不计入）。</summary>
        public int Count => _liveCount;

        /// <summary>当前是否正在派发。</summary>
        public bool IsDispatching => _dispatchDepth > 0;

        /// <summary>
        /// 派发引擎按索引直接遍历它 —— 不复制列表，这是"派发零分配"的前提。
        /// 只给同程序集的派发引擎用。
        /// </summary>
        internal List<RevEventListener> Listeners => _listeners;

        /// <summary>本轮有效监听范围。嵌套派发复用最外层开始时的边界。</summary>
        internal int DispatchLimit => _dispatchLimit;

        // ==================== 注册 / 反注册 ====================

        /// <summary>加入一个监听者。<paramref name="seq"/> 由派发引擎发号，用于同优先级时保持注册顺序。</summary>
        internal void Add(Delegate handler, object owner, int priority, int seq, object scope)
        {
            RevEventListener node = RevEventListener.Rent();
            node.Handler = handler;
            // 不传 owner 时用委托的目标对象：注册实例方法就能被 RemoveAllByOwner 批量移除
            node.Owner = owner ?? handler.Target;
            node.Scope = scope;                  // 作用域标记（没有域 / 显式写了 owner 时为 null）
            node.Priority = priority;
            node.Seq = seq;
            _listeners.Add(node);
            _liveCount++;                        // ★ 新节点一定是"活跃"的，与 Abandon 严格成对

            // 全部为默认优先级时不排序；已有优先级节点时，新增默认优先级也可能改变顺序。
            if (priority != 0) _hasPriorities = true;
            if (_hasPriorities) _needSort = true;

            if (_dispatchDepth > 0) _changedInDispatch = true;
        }

        /// <summary>移除该事件上这个委托的全部注册（同一个委托注册了两次就移除两次）。</summary>
        internal int Remove(Delegate handler)
        {
            int removed = 0;

            // 倒序遍历：边遍历边摘除时，不会影响尚未处理的元素下标
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                RevEventListener node = _listeners[i];
                if (node.IsAbandoned) continue;

                // ★ 用委托相等性判断，而不是引用相等：
                //   RevEvent.RemoveEventListener("X", OnX) 传入的方法组会新建一个委托实例，
                //   但 Delegate 的 == 比较的是"目标对象 + 方法"，所以照样能匹配上。
                if (node.Handler != handler) continue;

                RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>移除归属该对象的全部监听者。</summary>
        internal int RemoveByOwner(object owner)
        {
            if (owner == null) return 0;

            int removed = 0;
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                RevEventListener node = _listeners[i];
                if (node.IsAbandoned) continue;

                // ★ 两把钥匙任一匹配都摘：Owner（对象销毁时摘）或 Scope（域关闭时摘，传进来的 owner 是域本身）
                if (!ReferenceEquals(node.Owner, owner) && !ReferenceEquals(node.Scope, owner)) continue;

                RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>清空本事件的全部监听者。</summary>
        internal void Clear()
        {
            if (_dispatchDepth > 0)
            {
                // 派发中：只能标记（列表正在被遍历）
                for (int i = 0; i < _listeners.Count; i++)
                {
                    RevEventListener node = _listeners[i];

                    // ★ 已经被标记过的节点不要再扣一次计数（否则多次 Clear 会把 _liveCount 扣成负数）
                    if (node.IsAbandoned) continue;

                    node.Abandon();
                    _liveCount--;
                }

                _changedInDispatch = true;
                return;
            }

            ReturnAll();
        }

        // ==================== 派发生命周期 ====================

        /// <summary>派发开始：外层锁定监听范围并按优先级排序，嵌套派发沿用该范围。</summary>
        internal void BeginDispatch()
        {
            if (_dispatchDepth == 0)
            {
                // 只在最外层排序：派发中途重排会打乱正在进行的索引遍历。
                if (_needSort) SortByPriority();
                _dispatchLimit = _listeners.Count;
            }

            _dispatchDepth++;
        }

        /// <summary>派发结束：深度归零且期间有过增删时，统一清理被标记删除的节点。</summary>
        internal void EndDispatch()
        {
            if (_dispatchDepth > 0) _dispatchDepth--;

            if (_dispatchDepth > 0) return;          // 嵌套派发没结束，先别动列表
            if (!_changedInDispatch) return;          // 期间没增删 → 无事可做（绝大多数情况走这里）

            _changedInDispatch = false;
            CleanupAbandoned();
        }

        // ==================== 内部实现 ====================

        /// <summary>摘掉第 index 个监听者；派发中只能标记。</summary>
        private void RemoveAt(int index)
        {
            if (_dispatchDepth > 0)
            {
                // ★ 派发中：只标记不摘除，否则会破坏正在进行的索引遍历
                _listeners[index].Abandon();
                _liveCount--;                    // 活跃数立刻减少：它不会收到任何后续派发
                _changedInDispatch = true;
                return;
            }

            RevEventListener.Return(_listeners[index]);
            _listeners.RemoveAt(index);
            _liveCount--;
        }

        /// <summary>清理被标记删除的节点（派发结束后调用）。</summary>
        private void CleanupAbandoned()
        {
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                if (!_listeners[i].IsAbandoned) continue;

                // 计数已经在 Abandon 的那一刻扣掉了，这里只做"真正摘除 + 还池"，不能再扣一次
                RevEventListener.Return(_listeners[i]);
                _listeners.RemoveAt(i);
            }
        }

        /// <summary>
        /// 按优先级排序：数值大的先执行，同优先级保持注册顺序。
        /// Seq 兜底是必须的 —— <c>List.Sort</c> 不是稳定排序，只比 Priority 会让同优先级的监听者顺序随机。
        /// </summary>
        private void SortByPriority()
        {
            _needSort = false;

            // static lambda（C# 9）：编译器把它缓存成静态字段，调用时零分配
            _listeners.Sort(static (a, b) =>
            {
                int byPriority = b.Priority.CompareTo(a.Priority);
                return byPriority != 0 ? byPriority : a.Seq.CompareTo(b.Seq);
            });
        }

        /// <summary>把列表里的节点全部还回对象池。</summary>
        private void ReturnAll()
        {
            for (int i = 0; i < _listeners.Count; i++) RevEventListener.Return(_listeners[i]);

            _listeners.Clear();
            _liveCount = 0;
            _needSort = false;
            _changedInDispatch = false;

            // ★ 连同 _hasPriorities 一起复位：
            //   清空后这个事件上已经没有带优先级的监听者了，若不复位，之后每次 Add 都会标脏 _needSort，
            //   于是"没用优先级"的事件也要在每次派发前排一次序（白付排序代价）。
            _hasPriorities = false;
        }

        /// <summary>调试描述：日志里打印"这个事件上挂了哪些监听者"。</summary>
        public override string ToString()
        {
            // 报活跃数：派发中打印时，已标记删除的节点不该让排查的人以为还挂着监听者
            return $"监听者 {_liveCount} 个{(IsDispatching ? "（派发中）" : string.Empty)}";
        }
    }
}

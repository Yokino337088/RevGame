// ============================================================
// RevMonoScope.cs —— 作用域（using 一块，退出把这块加的监听全摘掉、协程全停）
//
// 位置：Runtime\RevPublicMono\Support\
//
// 【它解决什么】"忘记移除监听"是这类模块唯一的泄漏方式（旧实现连去重都没有，
//   同一个方法加两次就是每帧跑两次，而你看不出来）。作用域把"收尾"变成结构性的：
//
//   <code>
//   using (var scope = RevMono.OpenScope())
//   {
//       scope.AddUpdate(OnTick);              // 这块里加的监听者都归 scope 管
//       scope.StartCoroutine(RunSequence());  // 这块里起的协程也归 scope 管
//   }   // ← 出块：监听全摘、协程全停；不用记委托、不用逐个 Remove
//   </code>
//
// 【什么时候用它】进入一个界面/一段玩法/一个关卡时 —— 凡是"这段时间内才有意义"的每帧逻辑。
//
// 【和 RemoveAllOf 的关系】作用域内部就是用自己当 owner：
//   scope.AddUpdate(f) ≡ RevMono.AddUpdate(f, owner: scope)，
//   scope.StartCoroutine(r) 的协程也登记到作用域；RemoveAllOf(scope) 会同时停止这批协程。
// ============================================================
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>公共 Mono 模块的作用域：`using` 一块，退出时摘掉这块加的监听者、停掉这块起的协程。</summary>
    public sealed class RevMonoScope : IDisposable
    {
        private readonly List<Coroutine> _routines = new List<Coroutine>(4);
        private bool _disposed;
        private int _version;

        private sealed class RoutineTicket
        {
            internal Coroutine Handle;
            internal bool Completed;
        }

        internal RevMonoScope()
        {
        }

        /// <summary>这块里加一个每帧回调。</summary>
        public bool AddUpdate(Action action) => !_disposed && RevMono.AddUpdate(action, this);

        /// <summary>这块里加一个每帧最后的回调。</summary>
        public bool AddLateUpdate(Action action) => !_disposed && RevMono.AddLateUpdate(action, this);

        /// <summary>这块里加一个物理帧回调。</summary>
        public bool AddFixedUpdate(Action action) => !_disposed && RevMono.AddFixedUpdate(action, this);

        /// <summary>这块里起一条协程（退出作用域时会自动停掉它）。</summary>
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            if (_disposed || routine == null) return null;

            int version = _version;
            var ticket = new RoutineTicket();
            Coroutine handle = RevMono.StartCoroutine(TrackRoutine(routine, ticket, version));
            ticket.Handle = handle;
            if (handle == null || ticket.Completed) return handle;

            // Unity 启动协程时会先执行它，直到遇到第一个 yield 才把 Coroutine 句柄交还给这里。
            // 协程第一次运行的代码可能又关闭/释放当前 scope；那一刻句柄还没放进 _routines，Close 找不到这条协程，无法停止它。
            // 因此启动返回后检查 scope 的关闭状态和版本号；若启动期间发生过关闭，就马上停止协程，防止它在界面/玩法结束后继续跑。
            if (_disposed || version != _version)
            {
                RevMono.StopCoroutine(handle);
                return null;
            }

            _routines.Add(handle);
            return handle;
        }

        private IEnumerator TrackRoutine(IEnumerator routine, RoutineTicket ticket, int version)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (!_disposed && version == _version && stack.Count > 0)
                {
                    IEnumerator current = stack.Peek();
                    if (!current.MoveNext())
                    {
                        stack.Pop();
                        (current as IDisposable)?.Dispose();
                        continue;
                    }

                    object yielded = current.Current;
                    if (yielded is IEnumerator nested) stack.Push(nested);
                    else yield return yielded;
                }
            }
            finally
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                ticket.Completed = true;
                // 协程自然结束时也要从清单移除；否则 scope 会一直留着已经结束的句柄，之后关闭时还会误以为它仍在运行。
                if (ticket.Handle != null) _routines.Remove(ticket.Handle);
            }
        }

        /// <summary>手动摘掉这块的全部监听者并停掉协程（不 Dispose 也能用；重复调用安全）。</summary>
        public int Close() => RevMono.RemoveAllOf(this);

        internal int StopRoutines()
        {
            // 先增加版本号：如果有协程正处于“StartCoroutine 已开始、句柄尚未登记”的窗口，它返回后就能发现 scope 已被关闭。
            // 再复制当前句柄列表后逐个停止；停止协程会执行 finally 并从原列表移除自己，若一边遍历原列表一边 Stop，列表会缩短、跳过其他协程。
            _version++;
            Coroutine[] routines = _routines.ToArray();
            _routines.Clear();
            for (int i = 0; i < routines.Length; i++) RevMono.StopCoroutine(routines[i]);
            return routines.Length;
        }

        /// <summary>退出作用域（重复 Dispose 安全）。</summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Close();
        }
    }
}

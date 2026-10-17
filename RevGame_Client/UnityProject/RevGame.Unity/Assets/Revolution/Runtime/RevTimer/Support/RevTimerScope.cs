// ============================================================
// RevTimerScope.cs —— 计时器作用域（using 一块，退出全停）
//
// 位置：Runtime\RevTimer\Support\
//
// 【它解决什么】循环计时器忘了停 = 永久泄漏 + 回调打到已销毁对象（旧框架的经典事故）。
//   王者为此专门做了一整套"域（Bank）+ 一行批量回收"的纪律。这里的等价物就是一个 using：
//
//   <code>
//   using (var scope = RevTimer.OpenScope())
//   {
//       scope.Every(1f, RefreshHp);            // 这块里建的计时器都归 scope 管
//       scope.After(3f, Close);
//   }   // ← 出块立刻全停；不写 Stop、不用记句柄
//   </code>
//
// 【什么时候用它】打开一个面板/进入一个玩法/一段过场 —— 凡是"这段时间内才有意义"的计时器。
//   比"记住 5 个句柄再逐个 Stop"可靠得多：新增计时器不用改收尾代码。
//
// 【和 CancelAllOf 的关系】作用域内部就是用自己当 owner：
//   `scope.After(...)` ≡ `RevTimer.After(..., owner: scope)`，
//   所以 `RevTimer.CancelAllOf(scope)` 与 `scope.Dispose()` 完全等价 —— 想手动停也行。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>
    /// 计时器作用域：`using` 一块，退出时把这块里创建的计时器全部停掉。
    /// ★ 只有在**作用域内创建**（走 scope 的方法或显式传 `owner: scope`）的计时器才归它管。
    /// </summary>
    public sealed class RevTimerScope : IDisposable
    {
        private bool _disposed;

        internal RevTimerScope()
        {
        }

        // 作用域 Dispose 之后不能再创建计时器：Dispose 只会清理一次，之后才创建的计时器以这个已关闭的 scope 为 owner，
        // 没有任何人会再来停它（异步流程里晚到的代码最常触发），会一直占用名额直到达到数量上限。
        // 这里统一拒绝并返回空句柄；空句柄的所有操作都是安全空操作。
        private bool IsClosed => _disposed;

        /// <summary>这块里 N 秒后一次（等价于 <c>RevTimer.After(..., owner: scope)</c>）。</summary>
        public RevTimerHandle After(float seconds, Action callback, RevTimeDomain domain = RevTimeDomain.Scaled)
            => IsClosed ? RevTimerHandle.Empty : RevTimer.After(seconds, callback, domain, this);

        /// <summary>这块里下一帧一次。</summary>
        public RevTimerHandle NextFrame(Action callback)
            => IsClosed ? RevTimerHandle.Empty : RevTimer.NextFrame(callback, this);

        /// <summary>这块里每 interval 秒一次（times = -1 无限）。</summary>
        public RevTimerHandle Every(float interval, Action callback, int times = -1,
            RevTimeDomain domain = RevTimeDomain.Scaled)
            => IsClosed ? RevTimerHandle.Empty : RevTimer.Every(interval, callback, times, domain, this);

        /// <summary>这块里每 interval 秒一次（回调带"第几次"）。</summary>
        public RevTimerHandle Every(float interval, Action<int> callback, int times = -1,
            RevTimeDomain domain = RevTimeDomain.Scaled)
            => IsClosed ? RevTimerHandle.Empty : RevTimer.Every(interval, callback, times, domain, this);

        /// <summary>手动停掉这块的全部计时器（不 Dispose 也能用；重复调用是安全的）。</summary>
        public int Cancel() => RevTimer.CancelAllOf(this);

        /// <summary>退出作用域：停掉这块的全部计时器（重复 Dispose 安全）。</summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            RevTimer.CancelAllOf(this);
        }
    }
}

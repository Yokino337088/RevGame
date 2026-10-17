// ============================================================
// RevInputScope.cs —— 一行清理的作用域（屏蔽 + 事件订阅）
//
// 位置：Runtime\RevInput\Support\
//
// 【典型用法】
// <code>
// // 打开弹窗期间：Block(World) 会屏蔽本模块的所有动作与手势（包括 ESC；当前无系统动作例外）
// using (var scope = RevInput.OpenScope())
// {
//     scope.Block();                                   // 等价于 RevInput.Block(owner: scope)
//     scope.OnPressed("Confirm", Save);                // 等价于 RevInput.OnPressed("Confirm", Save, scope)
//     … // 面板逻辑
// }   // 出块：屏蔽解除 + 事件全部退订（一行都不用写）
// </code>
//
// 【为什么值得单独一个作用域】
//   屏蔽与订阅都属于"只在这个面板活着的时候有效"的东西。
//   手工配对 `Block` / `Unblock` 与 `On` / `Off` 极易漏（漏了就是"关掉面板后按键还在响应"），
//   而作用域让它们**必然**一起消失 —— 和框架里 资源 / 音效 / 计时器 / 公共Mono 的作用域是同一个套路。
//
// 【一句等价关系】
//   <c>scope.Block()</c> ≡ <c>RevInput.Block(owner: scope)</c>；
//   <c>scope.Dispose()</c> ≡ <c>RevInput.OffAllOf(scope)</c>（屏蔽 + 事件一起清）。
// ============================================================

using System;

namespace Revolution
{
    /// <summary>输入作用域：出块自动解除屏蔽、退订事件（幂等，可重复 Dispose）。</summary>
    public sealed class RevInputScope : IDisposable
    {
        // Dispose 是幂等的：第一次释放后，后续调用会直接返回，不会再做清理。
        // 因此释放后必须拒绝新建屏蔽和事件订阅；否则新内容虽然登记成功，以后再 Dispose 也不会清掉，输入可能持续被挡或旧回调持续执行。
        private bool _disposed;

        internal RevInputScope()
        {
        }

        /// <summary>申请屏蔽（默认只挡世界输入；<paramref name="pointerId"/> 只有 <c>Pointer</c> 种类才需要）。</summary>
        public RevInputBlock Block(RevInputBlockKind kind = RevInputBlockKind.World, int pointerId = -1)
            => _disposed ? RevInputBlock.Empty : RevInput.Block(kind, pointerId, owner: this);

        /// <summary>解除本作用域申请的一次屏蔽（也可等 Dispose 一次清）。</summary>
        public bool Unblock(RevInputBlock handle) => RevInput.Unblock(handle);

        /// <summary>订阅某动作按下（owner = 本作用域）。</summary>
        public bool OnPressed(string action, Action handler) => !_disposed && RevInput.OnPressed(action, handler, owner: this);

        /// <summary>订阅某动作抬起（owner = 本作用域）。</summary>
        public bool OnReleased(string action, Action handler) => !_disposed && RevInput.OnReleased(action, handler, owner: this);

        /// <summary>订阅手势（owner = 本作用域）。</summary>
        public void OnGesture(Action<RevGestureEvent> handler, RevGestureKind kind = RevGestureKind.None)
        {
            if (!_disposed) RevInput.OnGesture(handler, kind, owner: this);
        }

        /// <summary>只清掉本作用域登记的内容（不想等出块时用）。</summary>
        public int Close()
        {
            int removed = RevInput.OffAllOf(this);
            RevInputLog.V("[RevInput] 作用域关闭：清掉 " + removed + " 项");
            return removed;
        }

        /// <summary>出块：屏蔽解除 + 事件退订（幂等）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // ★ 不能在这里转调 Close()：Close 会检查 _disposed，于是"先置位再调用"会把自己挡在门外
            //   （这个坑正是断言跑出来的 —— 出块没解除屏蔽，后面所有输入都读不到）
            RevInput.OffAllOf(this);
        }

        public override string ToString() => "RevInputScope" + (_disposed ? "(已关闭)" : string.Empty);
    }
}

// RevSequenceHandle.cs —— 句柄（值类型）
// 【职责】只做两件事：判断有效性（IsAssigned / IsValid）、取消（Stop）。
// 【要点】句柄 = 这次播放的票根：旧句柄自动失效，不会误停别的新序列。

using System;

namespace Revolution
{
    /// <summary>
    /// 一条序列运行实例的句柄：判断有效性 + 取消。
    /// <para>句柄在序列结束后自动失效（<see cref="IsValid"/> 变 false），残留句柄不会误伤新序列。</para>
    /// <para>框架不提供进度查询：要记进度 / 卡在哪一步，请在
    /// <c>OnCompleted / OnCancelled / runner.RunFinished</c> 回调里自己记。</para>
    /// </summary>
    public readonly struct RevSequenceHandle : IEquatable<RevSequenceHandle>
    {
        private readonly RevSequenceRunner _runner;     // 所属引擎（空句柄为 null）
        internal readonly long Id;                      // 运行实例的唯一 Id（单调递增，永不复用）

        internal RevSequenceHandle(RevSequenceRunner runner, long id)
        {
            _runner = runner;
            Id = id;
        }

        /// <summary>是不是一个真实句柄（非 default）</summary>
        public bool IsAssigned => _runner != null && Id != 0;

        /// <summary>这条序列现在还在运行吗（已结束 / 被取消 / 残留句柄都会返回 false）</summary>
        public bool IsValid => _runner != null && _runner.FindRun(Id) != null;

        /// <summary>
        /// 取消这条序列（等价于 <c>runner.Stop(handle)</c>）。
        /// <para>取消在<b>下一个 Tick 的步骤边界</b>生效，不打断正在执行的步骤；
        /// <paramref name="runFinally"/> 为 true 时先执行定义的 finally 步骤（收尾清理）。</para>
        /// </summary>
        /// <returns>true = 确实取消了一条正在跑的序列；false = 句柄已失效（什么都没做）</returns>
        public bool Stop(bool runFinally = true) => _runner != null && _runner.Stop(this, runFinally);

        /// <inheritdoc/>
        public bool Equals(RevSequenceHandle other) => ReferenceEquals(_runner, other._runner) && Id == other.Id;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is RevSequenceHandle other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (_runner != null ? _runner.GetHashCode() : 0) * 397 ^ Id.GetHashCode();

        /// <summary>值相等比较</summary>
        public static bool operator ==(RevSequenceHandle a, RevSequenceHandle b) => a.Equals(b);

        /// <summary>值不等比较</summary>
        public static bool operator !=(RevSequenceHandle a, RevSequenceHandle b) => !a.Equals(b);

        /// <summary>调试显示：例如 <c>Sequence#12(运行中)</c></summary>
        public override string ToString()
            => IsAssigned ? $"Sequence#{Id}{(IsValid ? "(运行中)" : "(已结束)")}" : "Sequence#(空句柄)";
    }
}

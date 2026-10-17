// RevDelegateStep.cs —— 委托步骤（.Do / .Log 的落地实现）
// 【职责】把一句业务代码包成「立即步骤」：执行一次即完成。

using System;

namespace Revolution
{
    /// <summary>
    /// 委托步骤：一步 = 一个委托（执行）+ 可选的完成谓词。
    /// <para>由 <see cref="RevSequenceBuilder"/> 的 <c>Do</c> / <c>DoBlocking</c> 创建，业务代码不直接 new 它。</para>
    /// </summary>
    internal sealed class RevDelegateStep : RevStepBase
    {
        private readonly Action<RevSequenceContext> _body;                  // 执行体
        private readonly Func<RevSequenceContext, bool> _isCompleted;       // null = 立即完成

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => _isCompleted != null;

        /// <summary>
        /// 构造一个委托步骤。
        /// </summary>
        /// <param name="name">步骤名（构建期校验：不能为空）</param>
        /// <param name="body">执行体（每步只调用一次）</param>
        /// <param name="isCompleted">完成谓词（每帧调用；传 null 表示立即完成）</param>
        internal RevDelegateStep(string name, Action<RevSequenceContext> body, Func<RevSequenceContext, bool> isCompleted = null)
        {
            Name = name;
            _body = body ?? throw new ArgumentNullException(nameof(body), $"步骤「{name}」的执行体是 null");
            _isCompleted = isCompleted;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context) => _body(context);

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
            => _isCompleted == null || _isCompleted(context);
    }
}

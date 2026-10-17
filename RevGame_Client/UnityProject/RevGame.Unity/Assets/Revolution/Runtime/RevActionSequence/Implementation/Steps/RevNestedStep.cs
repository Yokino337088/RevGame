// RevNestedStep.cs —— 嵌套序列（把另一条定义当一步跑）
// 【语义】父序列等子序列跑完；父被取消 → 子一起取消（不留孤儿演出）。
// 【用途】常用演出做成「积木」，用 .Sequence("先开箱", ChestOpen) 拼装复用。

namespace Revolution
{
    /// <summary>
    /// 嵌套步骤：播放另一条已构建好的序列，并等它结束。
    /// <para>由 <see cref="RevSequenceBuilder"/> 的 <c>Sequence(...)</c> 创建。</para>
    /// </summary>
    internal sealed class RevNestedStep : RevStepBase
    {
        private sealed class State
        {
            public RevSequenceHandle Handle;    // 子序列句柄（存在本 Run 的状态槽里 → 并发安全）
        }

        private readonly RevSequenceDefinition _nested;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => true;

        internal RevNestedStep(string name, RevSequenceDefinition nested)
        {
            Name = name;
            _nested = nested;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);

            // 子序列沿用父序列的触发者、服务容器、事件总线（走 Play(definition, context) 重载 ——
            // 以前误走了 Play(definition, object source)，子序列的 ctx.Source 变成了父序列的上下文对象）
            state.Handle = run.Runner.Play(_nested, context);

            // 启动成功 → 登记（父序列结束时连同子序列一起收掉）；
            // 启动失败（被 RejectPerSource 拒绝）→ 不登记，本步立即放行（见 IsCompleted）
            if (state.Handle.IsAssigned) run.AddChildHandle(state.Handle);
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);

            // 没启动成功（句柄未分配）或子序列已结束 → 本步完成
            return state == null || !state.Handle.IsAssigned || !state.Handle.IsValid;
        }
    }
}

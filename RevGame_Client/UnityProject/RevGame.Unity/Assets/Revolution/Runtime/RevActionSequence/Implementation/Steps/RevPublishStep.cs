// RevPublishStep.cs —— 发布事件步骤（.Publish(evt)）
// 【要点】事件对象是构建期给定的 → 只适合常量事件；要带运行期数据（ctx.Source / uid）请用
//         .Do("广播", ctx => ctx.Events.Publish(new MyEvent(ctx.Source, uid)))

namespace Revolution
{
    /// <summary>
    /// 发事件步骤（立即）：把事件广播到当前运行实例的事件总线上。
    /// <para>由 <see cref="RevSequenceBuilder"/> 的 <c>Publish(...)</c> 创建。</para>
    /// </summary>
    /// <typeparam name="TEvent">事件类型（建议用 readonly struct：零分配、语义清晰）</typeparam>
    internal sealed class RevPublishStep<TEvent> : RevStepBase
    {
        private readonly TEvent _event;

        /// <inheritdoc/>
        public override string Name { get; }

        internal RevPublishStep(string name, TEvent evt)
        {
            Name = name;
            _event = evt;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            RevSequenceEventBus bus = context.Events;
            if (bus == null) return;        // 上下文里没有事件总线就直接跳过（框架自身不打日志）

            bus.Publish(_event);
        }

        // IsCompleted 用基类默认的 true —— 立即步骤
    }
}

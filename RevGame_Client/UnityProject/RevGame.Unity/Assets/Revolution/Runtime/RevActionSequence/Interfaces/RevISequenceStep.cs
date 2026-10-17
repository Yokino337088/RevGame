// RevISequenceStep.cs —— 步骤契约（不想继承 RevStepBase 时可直接实现）
// 【职责】Name + Execute + IsCompleted + AssignSlots；SlotCount 是这条序列的状态槽总数。

namespace Revolution
{
    /// <summary>
    /// 动作序列中的一步。框架按定义顺序调用，<see cref="IsCompleted"/> 返回 false 表示阻塞后续步骤。
    /// </summary>
    public interface RevISequenceStep
    {
        /// <summary>步骤名（用于日志、调试面板与构建期校验：不能为空、不能重复）</summary>
        string Name { get; }

        /// <summary>
        /// 执行本步（由引擎调用，**每步只调用一次**）。
        /// <para>立即步骤在这里直接干活；阻塞步骤在这里"启动等待"并记录状态，然后立刻返回。</para>
        /// <para>严禁在本方法里 <c>while</c> 等待 / 睡线程 —— 那会卡死主线程。</para>
        /// </summary>
        void Execute(RevSequenceRun run, RevSequenceContext context);

        /// <summary>
        /// 本步完成了吗（引擎每帧问一次，直到返回 true 才放行下一步）。
        /// <para>立即步骤实现为 <c>return true</c>；阻塞步骤在这里做"廉价判定"（读状态字段、比较时间）。</para>
        /// </summary>
        bool IsCompleted(RevSequenceRun run, RevSequenceContext context);
    }
}

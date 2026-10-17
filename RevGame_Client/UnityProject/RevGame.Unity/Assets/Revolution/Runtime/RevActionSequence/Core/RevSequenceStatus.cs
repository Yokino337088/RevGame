// RevSequenceStatus.cs —— 状态枚举 + 并发策略枚举
// 【并发策略】Free 来几次跑几次 ｜ ReplacePerSource 同源顶替（取消旧的）｜ RejectPerSource 同源拒绝（忽略新的）。
// 【要点】「同源」= 同一个 Source；不传 Source 时所有人视为同一个 → 策略退化成「全局唯一」（框架会告警）。

namespace Revolution
{
    /// <summary>
    /// 一条序列（<see cref="RevSequenceRun"/>）当前处于什么状态。
    /// </summary>
    public enum RevSequenceStatus
    {
        /// <summary>未运行（运行实例归还池中时的状态）</summary>
        Idle = 0,

        /// <summary>运行中（含"停在某个阻塞步骤上"——挂起也是运行中）</summary>
        Running = 1,

        /// <summary>已正常跑完（所有步骤放行完毕）</summary>
        Completed = 2,

        /// <summary>已被取消（<see cref="RevSequenceRunner.Stop"/> 或句柄的 Stop）</summary>
        Cancelled = 3,
    }

    /// <summary>
    /// 并发策略：同一条序列被"同一触发者"重复触发时怎么办。
    /// <para>对应原体系"多 actor 并发触发同一序列"的问题域；改造后由每条序列自己声明。</para>
    /// </summary>
    public enum RevSequenceConcurrency
    {
        /// <summary>
        /// 自由并发（默认）：来多少次就跑多少条。
        /// <para>适合"每个触发者各跑各的、互不影响"的场景。</para>
        /// </summary>
        Free = 0,

        /// <summary>
        /// 同源顶替：同一个触发者已经有这条序列在跑 → 先取消旧的，再跑新的。
        /// <para>适合"状态型表现"：例如同一个 NPC 换了目标，旧的开场演出应该立刻让位。</para>
        /// </summary>
        ReplacePerSource = 1,

        /// <summary>
        /// 同源拒绝：同一个触发者已经有这条序列在跑 → 忽略本次触发。
        /// <para>适合"防连点""只允许触发一次"这类场景。</para>
        /// </summary>
        RejectPerSource = 2,
    }
}

// RevSequenceRun.cs —— 运行实例（这一次播放的现场，池化复用）
// 【职责】步骤进度、状态槽数组、取消标记、句柄、子序列句柄。
// 【要点】同一份定义并发跑多份时，各自持有独立的状态数组 → 互不干扰。
// 【约束】归还池时必须清空状态槽，否则下次复用会读到上一次的数据。

using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 一条序列的一次运行实例（池化复用）。步骤的进度与状态都在这里，不在共享的 Definition 上。
    /// </summary>
    public sealed class RevSequenceRun
    {
        private readonly RevSequenceContext _context = new RevSequenceContext();   // 引擎拥有的上下文（随本实例复用）

        /// <summary>子序列句柄（嵌套步骤登记用；父序列收尾时会把它们一起取消）—— 用到才分配</summary>
        private List<RevSequenceHandle> _childHandles;

        internal object[] StepStates;       // 每个步骤一个状态槽（按槽位号索引）
        internal bool StepExecuted;         // 当前步骤是否已经 Execute 过（防止重复执行）
        internal bool SkipFinally;          // Stop(runFinally:false) 时置 true：结束时不跑收尾步骤
        internal bool Finalizing;           // 正在收尾（防止收尾过程中被重入收尾第二次）

        /// <summary>本运行实例的唯一编号（单调递增、永不复用；句柄靠它识别自己）</summary>
        internal long Id;

        /// <summary>所属引擎</summary>
        internal RevSequenceRunner Runner { get; private set; }

        /// <summary>本条运行实例的句柄（可查询、可取消）</summary>
        public RevSequenceHandle Handle => new RevSequenceHandle(Runner, Id);

        /// <summary>正在运行的蓝图（框架内部 / 调试显示用；归还池中时为 null）</summary>
        internal RevSequenceDefinition Definition { get; private set; }

        /// <summary>执行上下文（步骤签名里的那个 context 就是它）</summary>
        public RevSequenceContext Context => _context;

        /// <summary>触发者（框架内部 / 调试面板用；业务请读 <c>context.Source</c>）</summary>
        internal object Source => _context.Source;

        /// <summary>当前状态</summary>
        public RevSequenceStatus Status { get; internal set; } = RevSequenceStatus.Idle;

        /// <summary>当前步下标（= 已完成的步骤数）</summary>
        public int StepIndex { get; internal set; }

        /// <summary>本条序列累计运行时间（秒）—— 由引擎每帧累加，**不读 Time.time**（便于单测/快进）</summary>
        public float Elapsed { get; internal set; }

        /// <summary>本帧 deltaTime（框架内部：挂起的阻塞步骤靠它推进；业务请读 <c>context.DeltaTime</c>）</summary>
        internal float DeltaTime { get; set; }

        /// <summary>是否已被请求取消（下个 Tick 的步骤边界生效）</summary>
        public bool IsCancellationRequested { get; internal set; }

        /// <summary>是否已经结束（正常完成或被取消）</summary>
        public bool IsFinished => Status == RevSequenceStatus.Completed || Status == RevSequenceStatus.Cancelled;

        /// <summary>是否正常跑完（框架内部快捷键；业务判完成请读 <see cref="Status"/> / <see cref="IsFinished"/>）</summary>
        internal bool IsCompleted => Status == RevSequenceStatus.Completed;

        /// <summary>是不是"停在阻塞步骤上"（还在跑，只是这一步没放行）—— 框架内部 / 调试排查用</summary>
        internal bool IsSuspended => Status == RevSequenceStatus.Running && StepIndex < StepCount && !IsFinished;

        /// <summary>步骤总数（归还池中后为 0）</summary>
        public int StepCount => Definition?.StepCount ?? 0;

        /// <summary>当前步骤名（用于日志：告诉你"卡在哪一步"）</summary>
        public string CurrentStepName
        {
            get
            {
                RevISequenceStep[] steps = Definition?.StepArray;
                if (steps == null || StepIndex < 0 || StepIndex >= steps.Length) return string.Empty;
                return steps[StepIndex].Name;
            }
        }

        /// <summary>进度（0~1），用于表现层做进度条</summary>
        public float Progress01
        {
            get
            {
                int count = StepCount;
                return count == 0 ? 1f : (float)StepIndex / count;
            }
        }

        /// <summary>
        /// 取消这条序列（框架内部语法糖，等价于 <c>Handle.Stop()</c>）。
        /// <para>业务侧请用 <c>run.Handle.Stop()</c> 或 <c>runner.Stop(handle)</c> —— 句柄是唯一的取消入口，
        /// 这样"怎么取消"只有一种写法。</para>
        /// </summary>
        internal bool Stop(bool runFinally = true) => Runner != null && Runner.Stop(Handle, runFinally);

        /// <summary>
        /// 取步骤状态（框架内部使用；自定义步骤请走 <see cref="RevStepBase"/> 的 protected GetState）。
        /// </summary>
        internal T GetStepState<T>(int slot) where T : class
        {
            if (StepStates == null || slot < 0 || slot >= StepStates.Length) return null;
            return StepStates[slot] as T;
        }

        /// <summary>写步骤状态（框架内部使用；槽位越界会被静默忽略，避免坏步骤搞崩引擎）</summary>
        internal void SetStepState(int slot, object state)
        {
            if (StepStates == null || slot < 0 || slot >= StepStates.Length) return;
            StepStates[slot] = state;
        }

        /// <summary>登记一个子序列句柄（嵌套步骤调用）—— 让父序列能对子序列负责</summary>
        internal void AddChildHandle(RevSequenceHandle handle)
        {
            (_childHandles ??= new List<RevSequenceHandle>(2)).Add(handle);
        }

        /// <summary>
        /// 取消所有还在跑的子序列（引擎在本序列收尾时调用）。
        /// <para>为什么必须有：父序列被取消 / 结束时，若子序列还挂着，"孤儿序列"会继续播完 ——
        /// 表现就是"外层流程已经结束了，内层序列还在跑"。</para>
        /// </summary>
        internal void StopChildHandles(RevSequenceRunner runner, bool runFinally)
        {
            if (_childHandles == null) return;

            for (int i = 0; i < _childHandles.Count; i++)
                runner.Stop(_childHandles[i], runFinally);

            _childHandles.Clear();      // 清引用：池化复用时不能读到上一条序列的子句柄
        }

        // ============================================================
        // 池化生命周期（由引擎调用，业务代码不要碰）
        // ============================================================

        /// <summary>从池里取出后：用新的定义/上下文重置现场（注意：不会重新分配 Context，它随本实例复用）</summary>
        internal void PrepareForReuse(RevSequenceRunner runner, long id, RevSequenceDefinition definition, RevSequenceContext template)
        {
            Runner = runner;
            Id = id;
            Definition = definition;

            Status = RevSequenceStatus.Running;
            StepIndex = 0;
            StepExecuted = false;
            SkipFinally = false;
            Finalizing = false;
            Elapsed = 0f;
            DeltaTime = 0f;
            IsCancellationRequested = false;

            EnsureStateCapacity(definition.SlotCount);
            _context.Configure(template.Source, template.Services, template.Events);
            _context.Run = this;
        }

        /// <summary>归还池前：清空状态槽与引用（★ 不清会串味：下一条序列的步骤可能读到上一条的状态对象）</summary>
        internal void ReleaseState()
        {
            // ★ Bug 修复（2026-09-30）：必须清"整个数组"而不是"当前定义用到的槽数" ——
            //   状态数组只增不减（EnsureStateCapacity 会被更大槽数的定义撑大），归还时只按当前
            //   Definition.SlotCount 清理的话，更早的大定义写在高位槽里的状态对象会残留下来；
            //   之后这个实例再被大定义复用时，"先读后写"的自定义步骤（累加器 / 复用状态那种）
            //   会通过 GetOrCreateState 拿到上上条序列的旧对象 —— 数据串味。
            //   本文件头部的约束写的就是"归还池时必须清空状态槽"，这里把清理范围补全（多清几个槽零成本）。
            if (StepStates != null)
            {
                for (int i = 0; i < StepStates.Length; i++) StepStates[i] = null;
            }

            _childHandles?.Clear();

            _context.Run = null;
            Definition = null;
            Runner = null;
            StepIndex = 0;
            StepExecuted = false;
            SkipFinally = false;
            Finalizing = false;
            Elapsed = 0f;
            DeltaTime = 0f;
            IsCancellationRequested = false;
            Status = RevSequenceStatus.Idle;
        }

        /// <summary>日志用的位置描述：<c>「宝箱开启」第 2 步「等玩家点击」</c></summary>
        internal string Where => Definition == null
            ? "（已结束的序列）"
            : $"「{Definition.Name}」第 {StepIndex + 1}/{StepCount} 步「{CurrentStepName}」";

        /// <summary>状态数组只增不减（池化实例会被不同槽数的定义复用）</summary>
        private void EnsureStateCapacity(int slotCount)
        {
            if (StepStates == null || StepStates.Length < slotCount)
                StepStates = new object[slotCount];
        }

        /// <summary>调试显示：例如 <c>「某条序列」[2/4] 某个步骤 (1.20s)</c></summary>
        public override string ToString()
        {
            if (Definition == null) return "SequenceRun(空闲)";
            return $"「{Definition.Name}」[{StepIndex}/{StepCount}] {CurrentStepName} ({Elapsed:F2}s)";
        }
    }
}

// RevCompositeSteps.cs —— 组合步骤：并行 / 重复 / 分支
// 【语义】Parallel 组内全部完成才算这步完成；Repeat 组内步骤重复 N 轮；If 运行时二选一执行一组步骤。
// 【要点】子步骤槽位在构建期分配（AssignSlots），并发跑多份也不会互相覆盖；
//         子步骤里请求了取消，同帧剩下的子步骤不再执行（与主引擎一致）。

using System;

namespace Revolution
{
    /// <summary>
    /// 子步骤游标：组合步骤用它在一帧内推进一组子步骤。
    /// <para>游标本身也是"运行期状态"，必须存在步骤的状态槽里（不能放在共享的步骤实例上）。</para>
    /// </summary>
    internal sealed class RevStepCursor
    {
        private int _index;         // 当前子步骤下标
        private bool _executed;     // 当前子步骤是否已 Execute

        /// <summary>回到起点（重复步骤开始新一轮时调用）</summary>
        public void Reset()
        {
            _index = 0;
            _executed = false;
        }

        /// <summary>
        /// 推进一组子步骤：返回 true = 全部完成；false = 卡在某个阻塞子步骤（或已被请求取消）。
        /// <para>语义与主引擎的 Advance 完全一致：立即步骤同帧连放，阻塞步骤挂起。</para>
        /// </summary>
        public bool Advance(RevSequenceRun run, RevSequenceContext context, RevISequenceStep[] steps)
        {
            while (_index < steps.Length)
            {
                if (run.IsCancellationRequested) return false;      // 取消：交给引擎收尾

                RevISequenceStep step = steps[_index];

                if (!_executed)
                {
                    _executed = true;
                    step.Execute(run, context);
                }

                if (!step.IsCompleted(run, context)) return false;   // 阻塞：挂起

                _index++;
                _executed = false;
            }

            return true;
        }
    }

    /// <summary>
    /// 并行步骤：组内每个子步骤<b>各自独立推进</b>，全部完成时本步才完成。
    /// <para>对应"同时做两件事""既等计时又等条件"这类需求。</para>
    /// </summary>
    internal sealed class RevParallelStep : RevStepBase
    {
        private struct ChildState
        {
            public bool Executed;       // 该子步骤是否已 Execute
            public bool Done;           // 该子步骤是否已完成
        }

        private sealed class State
        {
            public ChildState[] Children;   // 每个子步骤一格（随运行实例复用，稳态零分配）
        }

        private readonly RevISequenceStep[] _steps;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => AnyBlocking(_steps);

        internal RevParallelStep(string name, RevISequenceStep[] steps)
        {
            Name = name;
            _steps = steps;
        }

        /// <inheritdoc/>
        internal override void AssignSlots(ref int next)
        {
            base.AssignSlots(ref next);                     // ① 先占自己的槽
            AssignChildSlots(_steps, ref next);             // ② 再给子步骤分配（互不重叠）
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);

            if (state.Children == null || state.Children.Length != _steps.Length)
                state.Children = new ChildState[_steps.Length];

            for (int i = 0; i < state.Children.Length; i++)
                state.Children[i] = default;                // 重置：本 Run 的本次执行从头开始
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state?.Children == null) return true;       // 状态丢了：放行，避免卡死

            bool allDone = true;

            for (int i = 0; i < _steps.Length; i++)
            {
                if (run.IsCancellationRequested) return false;

                if (state.Children[i].Done) continue;

                RevISequenceStep step = _steps[i];

                if (!state.Children[i].Executed)
                {
                    state.Children[i].Executed = true;
                    step.Execute(run, context);
                }

                if (step.IsCompleted(run, context)) state.Children[i].Done = true;
                else allDone = false;                       // 还有分支没完成：本步整体挂起
            }

            return allDone;
        }
    }

    /// <summary>
    /// 重复步骤：把一组子步骤重复执行 N 轮（每轮内部仍按顺序 + 阻塞语义推进）。
    /// <para>提醒：如果组内全是立即步骤且 N 很大，会在<b>同一帧</b>里跑完 N 轮 ——
    /// 想让每轮占一帧，请在组内加一个 <c>WaitFrames(1)</c>。</para>
    /// </summary>
    internal sealed class RevRepeatStep : RevStepBase
    {
        private sealed class State
        {
            public RevStepCursor Cursor;
            public int Remaining;       // 还剩几轮
        }

        private readonly RevISequenceStep[] _steps;
        private readonly int _times;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => AnyBlocking(_steps);

        internal RevRepeatStep(string name, int times, RevISequenceStep[] steps)
        {
            Name = name;
            _times = times < 1 ? 1 : times;     // 构建期已校验，这里再兜一层
            _steps = steps;
        }

        /// <inheritdoc/>
        internal override void AssignSlots(ref int next)
        {
            base.AssignSlots(ref next);
            AssignChildSlots(_steps, ref next);
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.Cursor ??= new RevStepCursor();
            state.Cursor.Reset();
            state.Remaining = _times;
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state?.Cursor == null) return true;

            while (true)
            {
                if (!state.Cursor.Advance(run, context, _steps)) return false;   // 本轮还没跑完 / 已取消

                state.Remaining--;
                if (state.Remaining <= 0) return true;                          // 全部轮次完成

                state.Cursor.Reset();                                           // 立刻开始下一轮
            }
        }
    }

    /// <summary>
    /// 分支步骤：运行时判断一次条件，走 then 或 otherwise 其中一组（组内可以有等待）。
    /// <para>条件只在进入本步时判断一次；之后条件变了也不会换分支。</para>
    /// </summary>
    internal sealed class RevBranchStep : RevStepBase
    {
        private sealed class State
        {
            public RevStepCursor Cursor;
            public bool TakeThen;
        }

        private readonly Func<RevSequenceContext, bool> _condition;
        private readonly RevISequenceStep[] _then;
        private readonly RevISequenceStep[] _otherwise;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => AnyBlocking(_then) || AnyBlocking(_otherwise);

        internal RevBranchStep(string name, Func<RevSequenceContext, bool> condition,
                               RevISequenceStep[] then, RevISequenceStep[] otherwise)
        {
            Name = name;
            _condition = condition ?? throw new ArgumentNullException(nameof(condition));
            _then = then ?? Array.Empty<RevISequenceStep>();
            _otherwise = otherwise ?? Array.Empty<RevISequenceStep>();
        }

        /// <inheritdoc/>
        internal override void AssignSlots(ref int next)
        {
            base.AssignSlots(ref next);
            AssignChildSlots(_then, ref next);
            AssignChildSlots(_otherwise, ref next);
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.Cursor ??= new RevStepCursor();
            state.Cursor.Reset();
            state.TakeThen = _condition(context);
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state?.Cursor == null) return true;

            return state.Cursor.Advance(run, context, state.TakeThen ? _then : _otherwise);
        }
    }
}

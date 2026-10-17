// RevWaitSteps.cs —— 等待步骤：等秒数 / 等帧数 / 等条件（可带超时保护）/ 补间（Tween）
// 【机制】挂起的步骤由引擎每帧调一次 IsCompleted，返回 true 才放行。
// 【为什么要超时】条件万一永不成立会永久卡住（原体系的典型事故）→ 超时 = 放行 + 打一条告警（写明哪条序列、哪一步）。

using System;

namespace Revolution
{
    /// <summary>
    /// 等固定秒数（对应原体系"等待几秒"节点）。
    /// <para>用 <c>run.Elapsed</c> 判定，与引擎 Tick 的 dt 保持一致。</para>
    /// </summary>
    internal sealed class RevWaitSecondsStep : RevStepBase
    {
        private sealed class State
        {
            public float EndTime;       // 结束时刻（Run 内累计时间）
        }

        private readonly float _seconds;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => _seconds > 0f;

        internal RevWaitSecondsStep(string name, float seconds)
        {
            Name = name;
            _seconds = seconds;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.EndTime = run.Elapsed + _seconds;     // ★ 相对 Run 起点计时，不读 Time.time
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            return state == null || run.Elapsed >= state.EndTime;   // 状态丢了就放行，避免卡死
        }
    }

    /// <summary>
    /// 等固定帧数（引擎 Tick 次数）。<c>WaitFrames(1)</c> = 至少再等一个 Tick 才放行。
    /// <para>用 <c>run.Runner.TickCount</c> 判定：帧数语义明确，不受 dt 大小影响。</para>
    /// </summary>
    internal sealed class RevWaitFramesStep : RevStepBase
    {
        private sealed class State
        {
            public long EndTick;        // 放行时刻（引擎 Tick 计数）
        }

        private readonly int _frames;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => _frames > 0;

        internal RevWaitFramesStep(string name, int frames)
        {
            Name = name;
            _frames = frames;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.EndTick = run.Runner.TickCount + _frames;     // TickCount 在本次 Tick 开头已 +1
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            return state == null || run.Runner.TickCount >= state.EndTick;
        }
    }

    /// <summary>
    /// 等条件成立（可选超时保护）——最通用的阻塞步骤。
    /// <para>超时不是"失败"，而是"放行 + 告警"：宁可让流程继续，也不让一条序列永远卡住。
    /// 超时要做点别的（上报、切失败分支的标志位），就传 <c>onTimeout</c>。</para>
    /// </summary>
    internal sealed class RevWaitUntilStep : RevStepBase
    {
        private sealed class State
        {
            public float StartTime;     // 本步开始时刻（用于超时判定）
        }

        private readonly Func<RevSequenceContext, bool> _predicate;
        private readonly float _timeoutSeconds;     // <= 0 表示不超时
        private readonly Action<RevSequenceContext> _onTimeout;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => true;

        internal RevWaitUntilStep(string name, Func<RevSequenceContext, bool> predicate, float timeoutSeconds,
                                  Action<RevSequenceContext> onTimeout = null)
        {
            Name = name;
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate), $"等待步骤「{name}」需要一个条件");
            _timeoutSeconds = timeoutSeconds;
            _onTimeout = onTimeout;
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.StartTime = run.Elapsed;
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            if (_predicate(context)) return true;               // 条件成立：放行

            if (_timeoutSeconds <= 0f) return false;            // 没配超时：一直等

            State state = GetState<State>(run);
            if (state == null) return true;
            if (run.Elapsed - state.StartTime < _timeoutSeconds) return false;

            // 超时 = 放行（避免序列永久卡死），并告诉你是哪一步
            if (_onTimeout != null) _onTimeout(context);
            else RevLog.Warn($"[动作序列] {run.Where} 等了 {_timeoutSeconds:0.##}s 条件仍未成立 → 超时放行" +
                             "（要自己处理超时请传 onTimeout）", RevSequenceRunner.LogTag);
            return true;
        }
    }

    /// <summary>
    /// 补间步骤：在 <c>duration</c> 秒内每帧回调一次进度 t（0 → 1，已按缓动曲线换算），最后一帧必然是 1。
    /// <para>"随时间变化"的表现（移动、缩放、淡入淡出、镜头推拉、数字滚动）用它就够了，不用写自定义步骤。</para>
    /// </summary>
    internal sealed class RevTweenStep<TFrom> : RevStepBase
    {
        private sealed class State
        {
            public float StartTime;
            public TFrom From;              // begin 返回的起始值（泛型字段 → 值类型也不装箱）
        }

        private readonly float _duration;
        private readonly RevEase _ease;
        private readonly Func<RevSequenceContext, TFrom> _begin;
        private readonly Action<RevSequenceContext, TFrom, float> _update;

        /// <inheritdoc/>
        public override string Name { get; }

        internal override bool IsBlocking => _duration > 0f;

        internal RevTweenStep(string name, float duration, RevEase ease,
                              Func<RevSequenceContext, TFrom> begin, Action<RevSequenceContext, TFrom, float> update)
        {
            Name = name;
            _duration = duration;
            _ease = ease;
            _begin = begin;
            _update = update ?? throw new ArgumentNullException(nameof(update), $"补间步骤「{name}」需要每帧的更新回调");
        }

        /// <inheritdoc/>
        public override void Execute(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetOrCreateState<State>(run);
            state.StartTime = run.Elapsed;
            state.From = _begin != null ? _begin(context) : default;
        }

        /// <inheritdoc/>
        public override bool IsCompleted(RevSequenceRun run, RevSequenceContext context)
        {
            State state = GetState<State>(run);
            if (state == null) return true;

            float linear = _duration <= 0f ? 1f : (run.Elapsed - state.StartTime) / _duration;
            if (linear > 1f) linear = 1f;

            _update(context, state.From, RevEasing.Evaluate(_ease, linear));
            return linear >= 1f;
        }
    }
}

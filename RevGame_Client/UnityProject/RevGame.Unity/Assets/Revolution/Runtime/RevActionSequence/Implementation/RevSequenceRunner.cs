// RevSequenceRunner.cs —— 序列引擎（框架的心脏）
// 【职责】Play / Stop / StopAll / Tick / 池化 / 并发策略 / 异常边界。
// 【主线】Play 建运行实例 → 每帧 Tick 推进 → 步骤放行即前进 → 完成或取消后走收尾。
// 【性能】只遍历活跃列表；运行实例与上下文池化 → 稳态 Tick 零分配；无 LINQ、无装箱。
// 【约束】时间只来自 Tick(dt)（不读 Time.time）；取消在「步骤边界」生效，不打断正在执行的步骤。
// 【随便调，不会坏】在步骤 / 回调 / await 续体里调 Play、Stop、StopAll、Clear 都安全：
//   Tick 期间的取消只打标记，本次 Tick 末尾统一收尾；Tick 按快照遍历，列表怎么变都不会越界、不会重复推进。
// 【出错看日志】步骤抛异常、收尾抛异常、等待超时都会打到 RevLog（tag = ActionSequence），并写明「哪条序列、第几步」。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 动作序列执行引擎：宿主每帧调 <see cref="Tick"/>，其余交给它。
    /// <para>不是单例 —— 你可以给不同场景/不同系统各建一个（单测里更是要自己 new 一个，注入假服务与假时钟）。
    /// 一般直接用 <c>RevSequencePlayer.Default</c> 或 <c>definition.Play(source)</c> 就够了。</para>
    /// </summary>
    public sealed class RevSequenceRunner : IDisposable
    {
        /// <summary>日志 tag（静音：<c>RevLog.MuteTag("ActionSequence")</c>）</summary>
        internal const string LogTag = "ActionSequence";

        /// <summary>
        /// 触发者还活着吗（Support 层注入：Unity 下"已被 Destroy 的对象"算不活）。
        /// 为 null 时不做检查 —— 内核本身不认识 Unity。
        /// </summary>
        internal static Func<object, bool> SourceAliveCheck;

        private readonly List<RevSequenceRun> _active = new List<RevSequenceRun>(16);   // 活跃序列
        private readonly Stack<RevSequenceRun> _pool = new Stack<RevSequenceRun>(16);   // 运行实例池
        private readonly Dictionary<long, RevTaskCompletionSource<bool>> _waiters = new Dictionary<long, RevTaskCompletionSource<bool>>(4);
        private readonly RevSequenceContext _template = new RevSequenceContext();       // "参数包"：读它的 Source/Services/Events

        // Tick 快照（复用，零分配）：按快照推进，回调里怎么增删活跃表都不影响本次遍历
        private readonly List<RevSequenceRun> _tickRuns = new List<RevSequenceRun>(16);
        private readonly List<long> _tickIds = new List<long>(16);

        private long _nextId = 1L;          // 句柄 Id（单调递增，永不复用）
        private int _tickDepth;             // > 0 = 正在 Tick / 收尾中（期间的 StopAll / Clear 延后到末尾执行）
        private bool _clearPending;
        private bool _disposed;

        /// <summary>默认服务容器（步骤里 <c>context.Get&lt;T&gt;()</c> 取的就是它；你也可以在 Play 时传别的）</summary>
        public RevSequenceServices Services { get; }

        /// <summary>默认事件总线（序列里 Publish 的事件发到这里；Bind 也监听它）</summary>
        public RevSequenceEventBus Events { get; }

        /// <summary>
        /// 触发者（source）被销毁时自动取消它的序列（默认开）。
        /// <para>Unity 下：<c>Play(清单, source: gameObject)</c> 之后物体被 Destroy，序列会在下一帧按取消收尾，
        /// 不会继续去碰一个已销毁的对象（那样每一步都会抛 MissingReferenceException）。</para>
        /// </summary>
        public bool StopWhenSourceDestroyed { get; set; } = true;

        /// <summary>引擎累计 Tick 次数（框架内部："等帧数"步骤靠它判定；每帧 +1）</summary>
        internal long TickCount { get; private set; }

        /// <summary>当前活跃（还没结束）的序列条数 —— 调试 / 断言用</summary>
        public int ActiveCount => _active.Count;

        /// <summary>
        /// 全局钩子：任意序列结束时触发（统计 / 性能采样 / 业务自己记录进度用）。
        /// <para>订阅者抛异常会被记日志并吞掉，不影响收尾。</para>
        /// </summary>
        public event Action<RevSequenceRun> RunFinished;

        /// <summary>建一个引擎</summary>
        /// <param name="services">服务容器（不传则自建一个空的）</param>
        /// <param name="events">事件总线（不传则自建一个）</param>
        public RevSequenceRunner(RevSequenceServices services = null, RevSequenceEventBus events = null)
        {
            Services = services ?? new RevSequenceServices();
            Events = events ?? new RevSequenceEventBus();
        }

        // ============================================================
        // 播放
        // ============================================================

        /// <summary>
        /// 播放一条序列。
        /// <para><paramref name="source"/> = 触发者：用于判定并发策略（同源顶替 / 同源拒绝），
        /// 步骤里也能用 <c>ctx.Source</c> / <c>ctx.SourceAs&lt;T&gt;()</c> 读到它；它被销毁时序列自动取消。</para>
        /// </summary>
        public RevSequenceHandle Play(RevSequenceDefinition definition, object source = null)
        {
            _template.Configure(source, Services, Events);
            return PlayInternal(definition);
        }

        /// <summary>
        /// 用指定的上下文播放：触发者、服务容器、事件总线都取自 <paramref name="context"/>（为 null 的项用引擎默认的）。
        /// <para>典型用法：步骤里把自己的 <c>ctx</c> 传下去，子序列就和父序列用同一套服务。</para>
        /// </summary>
        public RevSequenceHandle Play(RevSequenceDefinition definition, RevSequenceContext context)
        {
            _template.Configure(context?.Source, context?.Services ?? Services, context?.Events ?? Events);
            return PlayInternal(definition);
        }

        /// <summary>
        /// 一步到位：构建并立刻播放（等价于 <c>Play(builder.Build(), source)</c>）。
        /// <para>适合"临时的一次性演出"（一句提示、一次反馈）。<b>高频触发请先构建 Definition 缓存起来</b>
        /// —— 每次 Build 都有分配，而且"同源策略"判定的是同一个定义实例。</para>
        /// </summary>
        public RevSequenceHandle Play(RevSequenceBuilder builder, object source = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder), "Play(builder) 收到了 null");

            return Play(builder.Build(), source);
        }

        /// <summary>
        /// 播放并返回可 await 的任务：正常跑完为 true；被取消 / 步骤出错 / 被并发策略拒绝为 false。
        /// <para>别在步骤里 await 同一个引擎上的序列 —— 那会等一个永远不会再被推进的 Tick。</para>
        /// </summary>
        public RevTask<bool> PlayAsync(RevSequenceDefinition definition, object source = null)
        {
            RevSequenceHandle handle = Play(definition, source);
            if (!handle.IsAssigned) return RevTask<bool>.FromResult(false);

            RevTaskCompletionSource<bool> waiter = RevTask<bool>.CreateSource();
            _waiters[handle.Id] = waiter;               // 序列结束时（FinalizeRun）会完成它
            return waiter.Task;
        }

        /// <summary>内部播放：并发策略判定 → 池里取运行实例 → 挂到活跃表</summary>
        private RevSequenceHandle PlayInternal(RevSequenceDefinition definition)
        {
            if (_disposed)
                throw new InvalidOperationException("引擎已释放（Dispose 之后不能再 Play）");

            if (definition == null)
                throw new ArgumentNullException(nameof(definition), "Play(definition) 收到了 null —— 是不是忘了 .Build()？");

            if (definition.StepCount == 0)
                throw new InvalidOperationException($"序列「{definition.Name}」没有任何步骤（构建期校验本该拦住它）");

            // ① 并发策略：只对"同一条定义 + 同一个触发者"生效
            if (definition.Concurrency != RevSequenceConcurrency.Free)
            {
                object source = _template.Source;

                if (source == null && !definition.WarnedNullSource)
                {
                    definition.WarnedNullSource = true;
                    RevLog.Warn($"[动作序列] 「{definition.Name}」的并发策略是 {definition.Concurrency}，但 Play 时没传触发者 → " +
                                "所有触发者被视为同一个，策略变成「同时只能有一条」。如果这正是你要的可以忽略；" +
                                "否则请 Play(清单, source: 触发者)。（每条序列只提醒一次）", LogTag);
                }

                for (int i = 0; i < _active.Count; i++)
                {
                    RevSequenceRun running = _active[i];
                    if (running.IsCancellationRequested || running.Finalizing) continue;   // 已在收尾路上，不算"在跑"
                    if (!ReferenceEquals(running.Definition, definition)) continue;       // 不同定义互不影响
                    if (!ReferenceEquals(running.Source, source)) continue;               // 不同触发者互不影响

                    if (definition.Concurrency == RevSequenceConcurrency.RejectPerSource)
                        return default;                                                   // 忽略：返回空句柄

                    running.IsCancellationRequested = true;                               // 顶替：旧的按取消收尾
                }
            }

            // ② 取运行实例（池化：稳态下不产生分配）
            RevSequenceRun run = _pool.Count > 0 ? _pool.Pop() : new RevSequenceRun();
            run.PrepareForReuse(this, _nextId++, definition, _template);
            _active.Add(run);

            return run.Handle;
        }

        // ============================================================
        // 控制
        // ============================================================

        /// <summary>
        /// 取消一条序列。
        /// <para>取消在<b>步骤边界</b>生效（不打断正在执行的步骤）：在步骤里 Stop 自己，后面的步骤不会再执行。
        /// <paramref name="runFinally"/> = true 时先执行 <c>.OnCancel</c> / <c>.Finally</c> 的收尾步骤。</para>
        /// </summary>
        /// <returns>true = 确实标记了一条正在跑的序列；false = 句柄已失效（什么都没做）</returns>
        public bool Stop(RevSequenceHandle handle, bool runFinally = true)
        {
            RevSequenceRun run = FindRun(handle.Id);
            if (run == null || run.Finalizing) return false;

            if (!runFinally) run.SkipFinally = true;
            run.IsCancellationRequested = true;         // 只是打标记：真正的收尾在 Tick 里做
            return true;
        }

        /// <summary>取运行实例（已结束或不存在返回 null）—— 句柄查询走它</summary>
        internal RevSequenceRun FindRun(long id)
        {
            if (id == 0) return null;

            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Id == id) return _active[i];
            }

            return null;    // 活跃数量通常只有几十条，线性查找足够
        }

        /// <summary>
        /// 取消所有活跃序列（例如场景切换、宿主销毁），并立刻收尾。
        /// <para>在步骤 / 回调里调用也安全：这时收尾会在本次 Tick 末尾统一进行。</para>
        /// </summary>
        /// <param name="runFinally">是否执行各自的收尾步骤</param>
        public void StopAll(bool runFinally = true)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                RevSequenceRun run = _active[i];
                if (run.Finalizing) continue;
                if (!runFinally) run.SkipFinally = true;
                run.IsCancellationRequested = true;
            }

            if (_tickDepth > 0) return;                 // 正在 Tick：末尾的 SweepCancelled 会收掉

            _tickDepth++;
            try { SweepCancelled(); }
            finally { _tickDepth--; }

            FlushPendingClear();
        }

        // ============================================================
        // 驱动
        // ============================================================

        /// <summary>
        /// 每帧驱动：宿主在自己的 Update / LateUpdate / 固定帧里调（用 RevSequencePlayer 就不用管）。
        /// <para>传物理帧就是"与物理对齐"，传渲染帧就是"与表现对齐"，框架不预设。</para>
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (_disposed) return;

            if (_tickDepth > 0)
            {
                RevLog.Warn("[动作序列] 在步骤 / 回调里又调用了 Tick —— 已忽略（重入 Tick 会让序列同一帧推进两次）", LogTag);
                return;
            }

            if (deltaTime < 0f) deltaTime = 0f;         // 防御：负 dt 会让"等秒数"倒着走
            TickCount++;
            _tickDepth++;

            try
            {
                // ① 快照：本帧新启动的序列从下一帧开始推进；回调里增删活跃表不影响本次遍历
                for (int i = 0; i < _active.Count; i++)
                {
                    _tickRuns.Add(_active[i]);
                    _tickIds.Add(_active[i].Id);
                }

                for (int i = 0; i < _tickRuns.Count; i++)
                {
                    RevSequenceRun run = _tickRuns[i];

                    // 已经结束 / 已被归还池并被别的序列复用（Id 变了）→ 跳过
                    if (run.Id != _tickIds[i] || run.Status != RevSequenceStatus.Running || run.Finalizing) continue;

                    run.DeltaTime = deltaTime;
                    run.Elapsed += deltaTime;

                    // ② 触发者被销毁：自动取消（不再去碰已销毁的对象）
                    if (!run.IsCancellationRequested && StopWhenSourceDestroyed && IsSourceLost(run))
                        run.IsCancellationRequested = true;

                    if (run.IsCancellationRequested)
                    {
                        FinalizeRun(run, RevSequenceStatus.Cancelled);
                        continue;
                    }

                    // ③ 推进
                    try
                    {
                        Advance(run);
                    }
                    catch (Exception e)
                    {
                        HandleFault(run, e);
                        continue;
                    }

                    // ④ 步骤里请求了取消（Stop 自己 / 被同源顶替）→ 本帧就收尾；跑完了 → 正常收尾
                    if (run.IsCancellationRequested) FinalizeRun(run, RevSequenceStatus.Cancelled);
                    else if (run.Status == RevSequenceStatus.Completed) FinalizeRun(run, RevSequenceStatus.Completed);
                }

                // ⑤ 本帧里被 Stop / StopAll / 父序列带走的，统一收掉
                SweepCancelled();
            }
            finally
            {
                _tickRuns.Clear();
                _tickIds.Clear();
                _tickDepth--;
            }

            FlushPendingClear();
        }

        /// <summary>
        /// 推进一条序列：逐步骤 Execute → 问 IsCompleted → 通过就下一步。
        /// <para>— 立即步骤在同一帧内可以连续放行（不会一步一帧）；
        /// — 阻塞步骤（IsCompleted=false）就 <b>return</b>，停在当前步等下一帧；
        /// — 每进下一步前都看一眼取消标记：步骤里 Stop 了自己，后面的步骤就不会再执行。</para>
        /// </summary>
        private static void Advance(RevSequenceRun run)
        {
            RevISequenceStep[] steps = run.Definition.StepArray;
            RevSequenceContext context = run.Context;

            while (run.StepIndex < steps.Length)
            {
                if (run.IsCancellationRequested) return;

                RevISequenceStep step = steps[run.StepIndex];

                if (!run.StepExecuted)
                {
                    run.StepExecuted = true;             // 先置位：Execute 抛异常也不会被下一帧再执行一次
                    step.Execute(run, context);
                }

                if (!step.IsCompleted(run, context)) return;    // ★ 阻塞：挂起在此，下一帧再问

                run.StepIndex++;
                run.StepExecuted = false;                // 放行下一步（同帧继续 while）
            }

            if (!run.IsCancellationRequested) run.Status = RevSequenceStatus.Completed;
        }

        private static bool IsSourceLost(RevSequenceRun run)
        {
            object source = run.Source;
            return source != null && SourceAliveCheck != null && !SourceAliveCheck(source);
        }

        /// <summary>收掉所有已被请求取消的序列（收尾回调里可能又取消了别的 → 多扫几遍，封顶防死循环）</summary>
        private void SweepCancelled()
        {
            for (int pass = 0; pass < 8; pass++)
            {
                bool any = false;

                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    if (i >= _active.Count) continue;           // 收尾回调里删了别的序列：列表变短
                    RevSequenceRun run = _active[i];
                    if (!run.IsCancellationRequested || run.Finalizing) continue;

                    any = true;
                    FinalizeRun(run, RevSequenceStatus.Cancelled);
                }

                if (!any) return;
            }
        }

        // ============================================================
        // 收尾 / 异常 / 池化
        // ============================================================

        /// <summary>步骤抛异常：打日志（带序列名与步骤），按取消收尾 —— 一条坏序列不影响其它序列</summary>
        private void HandleFault(RevSequenceRun run, Exception e)
        {
            RevLog.Exception(e, $"[动作序列] {run.Where} 抛异常 → 已按取消处理（会执行收尾步骤）", LogTag);
            FinalizeRun(run, RevSequenceStatus.Cancelled);
        }

        /// <summary>
        /// 结束一条序列：收尾步骤 → 回调 → 归还池 → 唤醒 await 者。
        /// <para>幂等：已在收尾中 / 已结束就直接返回。任何一环抛异常都只记日志，后面的环节照常执行。</para>
        /// </summary>
        private void FinalizeRun(RevSequenceRun run, RevSequenceStatus status)
        {
            if (run.Finalizing || run.Status == RevSequenceStatus.Idle) return;

            run.Finalizing = true;
            run.Status = status;
            RevSequenceDefinition definition = run.Definition;

            try
            {
                // ① 收尾步骤：取消时先跑 .OnCancel，然后无论如何跑 .Finally
                if (!run.SkipFinally)
                {
                    if (status == RevSequenceStatus.Cancelled) RunCleanup(run, definition.CancelArray, "取消收尾");
                    RunCleanup(run, definition.FinallyArray, "收尾");
                }

                // ② 结束回调
                try
                {
                    if (status == RevSequenceStatus.Completed) definition.OnCompleted?.Invoke(run);
                    else definition.OnCancelled?.Invoke(run);
                }
                catch (Exception e)
                {
                    RevLog.Exception(e, $"[动作序列] 「{definition.Name}」的 {(status == RevSequenceStatus.Completed ? "OnCompleted" : "OnCancelled")} 回调抛异常", LogTag);
                }

                // ③ 全局钩子
                try
                {
                    RunFinished?.Invoke(run);
                }
                catch (Exception e)
                {
                    RevLog.Exception(e, $"[动作序列] RunFinished 订阅者处理「{definition.Name}」时抛异常", LogTag);
                }
            }
            finally
            {
                // ★ 无论如何都要做：移出活跃表 + 取消子序列 + 清状态 + 归还池 + 唤醒等待者
                long id = run.Id;
                _active.Remove(run);
                run.StopChildHandles(this, runFinally: true);   // 父序列收尾：不留孤儿序列
                run.ReleaseState();                             // 先清状态再入池，避免下一条序列读到残留
                _pool.Push(run);

                // 最后才唤醒：await 的续体是同步执行的，它可能马上又 Play（复用刚归还的这个实例）
                if (_waiters.TryGetValue(id, out RevTaskCompletionSource<bool> waiter))
                {
                    _waiters.Remove(id);
                    try
                    {
                        waiter.SetResult(status == RevSequenceStatus.Completed);
                    }
                    catch (Exception e)
                    {
                        RevLog.Exception(e, $"[动作序列] await「{definition.Name}」之后的代码抛异常", LogTag);
                    }
                }
            }
        }

        /// <summary>
        /// 执行收尾步骤：每步同步执行一次（组合步骤会在这一次里把立即子步骤跑完）。
        /// <para>收尾不会等待：某一步没有立即完成会告警；某一步抛异常只跳过它，其余收尾照常执行。</para>
        /// </summary>
        private static void RunCleanup(RevSequenceRun run, RevISequenceStep[] steps, string label)
        {
            for (int i = 0; i < steps.Length; i++)
            {
                RevISequenceStep step = steps[i];
                try
                {
                    step.Execute(run, run.Context);
                    if (!step.IsCompleted(run, run.Context))
                        RevLog.Warn($"[动作序列] 「{run.Definition.Name}」的{label}步骤「{step.Name}」没有立即完成 —— " +
                                    "收尾不会等待，它后面的部分不会执行（收尾里只放立即步骤）", LogTag);
                }
                catch (Exception e)
                {
                    RevLog.Exception(e, $"[动作序列] 「{run.Definition.Name}」的{label}步骤「{step.Name}」抛异常（已跳过，继续执行其余收尾）", LogTag);
                }
            }
        }

        // ============================================================
        // 清理
        // ============================================================

        /// <summary>清空引擎：取消所有序列（不跑收尾）、清空池与事件订阅（宿主销毁时调用）</summary>
        public void Clear()
        {
            StopAll(runFinally: false);

            if (_tickDepth > 0) { _clearPending = true; return; }   // 在 Tick 里调用：等本次 Tick 收尾完再清
            DoClear();
        }

        /// <summary>释放（等价于 <see cref="Clear"/> + 标脏：后续 Play 抛异常、Tick 不再生效）</summary>
        public void Dispose()
        {
            if (_disposed) return;
            Clear();
            _disposed = true;
        }

        private void FlushPendingClear()
        {
            if (!_clearPending || _tickDepth > 0) return;
            _clearPending = false;
            DoClear();
        }

        private void DoClear()
        {
            _pool.Clear();

            // 正常情况下 StopAll 已经把它们都唤醒了；这里兜底，保证没有 await 永远挂着
            if (_waiters.Count > 0)
            {
                var left = new List<RevTaskCompletionSource<bool>>(_waiters.Values);
                _waiters.Clear();
                foreach (RevTaskCompletionSource<bool> waiter in left) waiter.SetResult(false);
            }

            Events.Clear();
        }

        /// <summary>调试显示：例如 <c>RevSequenceRunner(活跃 3 / 池 2)</c></summary>
        public override string ToString() => $"RevSequenceRunner(活跃 {_active.Count} / 池 {_pool.Count})";
    }
}

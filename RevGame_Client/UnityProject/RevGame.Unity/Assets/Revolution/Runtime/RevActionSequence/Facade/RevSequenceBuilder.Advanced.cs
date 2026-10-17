// RevSequenceBuilder.Advanced.cs —— 【第 3 个要看的文件】进阶能力：用到哪个再查哪个
//
//   .Tween("淡入", 0.3f, (ctx, t) => ...)                    随时间变化（t：0 → 1，可选缓动）；带起始值的重载见下
//   .If("条件", ctx => 条件, then => ..., otherwise => ...)   运行时二选一（分支里可以有等待）
//   .DoBlocking("启动+盯结束", ctx => 启动(), ctx => 完成了)   自己盯一个跨帧过程（状态必须在 ctx 或服务里）
//   .Step(new MyStep())                                      自定义步骤（需要"本次运行私有状态"时用，见 RevStepBase.cs）
//   .WaitTask(ctx => 任务, "等异步任务")                       等 RevTask（资源加载 / 网络回包）；RevTask<T> 可拿到结果
//   .Parallel("同时播", p => ...)                             组内并行，全部完成才算这步完成
//   .Repeat("闪三次", 3, r => ...)                            组内步骤重复 N 轮
//   .Sequence("先开箱", 别的清单)                              嵌套另一条序列（父被取消 → 子一起取消）
//   .Publish(new MyEvent(id))                                发强类型事件（串联别的序列 / 通知业务系统）
//   .OnCompleted / .OnCancelled                               生命周期回调（结束时的状态同步 / 上报；多次调用会叠加）
//
//  说明：本文件与 RevSequenceBuilder.cs 是同一个 partial 类 —— 分开只是为了"小白不用一次看 20 个方法"。

using System;

namespace Revolution
{
    public sealed partial class RevSequenceBuilder
    {
        // ============================================================
        // 随时间变化
        // ============================================================

        /// <summary>
        /// 补间：在 <paramref name="duration"/> 秒内每帧回调一次进度 <c>t</c>（0 → 1，已按缓动换算；最后一帧必然是 1）。
        /// <code>.Tween("淡入", 0.3f, (ctx, t) =&gt; ctx.Require&lt;IHud&gt;().Alpha = t, RevEase.OutQuad)</code>
        /// <para>Unity 物体的移动 / 缩放 / 淡入淡出有现成的：<c>.MoveTo</c> / <c>.ScaleTo</c> / <c>.FadeTo</c>。</para>
        /// </summary>
        public RevSequenceBuilder Tween(string name, float duration, Action<RevSequenceContext, float> update,
                                        RevEase ease = RevEase.Linear)
        {
            if (update == null) throw new ArgumentNullException(nameof(update), $"补间「{name}」需要每帧的更新回调");
            ValidateDuration(name, duration);

            return Add(new RevTweenStep<bool>(name, duration, ease, null, (ctx, _, t) => update(ctx, t)));
        }

        /// <summary>
        /// 补间（带起始值）：进入这一步时先用 <paramref name="begin"/> 取一次起始值（按运行实例隔离，并发安全），
        /// 之后每帧把它和进度 t 一起交给 <paramref name="update"/>。
        /// <code>
        /// .Tween("镜头推近", 0.35f,
        ///        begin:  ctx =&gt; ctx.Require&lt;ICam&gt;().Fov,                        // 起点：当前 FOV
        ///        update: (ctx, from, t) =&gt; ctx.Require&lt;ICam&gt;().Fov = from + (25f - from) * t,
        ///        RevEase.InOutQuad)
        /// </code>
        /// </summary>
        public RevSequenceBuilder Tween<TFrom>(string name, float duration,
                                               Func<RevSequenceContext, TFrom> begin,
                                               Action<RevSequenceContext, TFrom, float> update,
                                               RevEase ease = RevEase.Linear)
        {
            if (begin == null) throw new ArgumentNullException(nameof(begin), $"补间「{name}」需要起始值");
            ValidateDuration(name, duration);

            return Add(new RevTweenStep<TFrom>(name, duration, ease, begin, update));
        }

        private void ValidateDuration(string name, float duration)
        {
            if (duration < 0f)
                throw new ArgumentOutOfRangeException(nameof(duration), $"序列「{_name}」的补间「{name}」时长不能为负");
        }

        // ============================================================
        // 分支
        // ============================================================

        /// <summary>
        /// 分支：进入这一步时判断一次条件，走 <paramref name="then"/> 或 <paramref name="otherwise"/>（分支里可以有等待）。
        /// <code>
        /// .If("低画质？", ctx =&gt; ctx.Require&lt;IDevice&gt;().IsLow,
        ///     then:      b =&gt; b.Do("播简化演出", …),
        ///     otherwise: b =&gt; b.Do("播完整演出", …).Wait(2f))
        /// </code>
        /// <para>两条流程差别很大时，构建两个 Definition、在入口处二选一更清楚。</para>
        /// </summary>
        public RevSequenceBuilder If(string name, Func<RevSequenceContext, bool> condition,
                                     Action<RevSequenceBuilder> then, Action<RevSequenceBuilder> otherwise = null)
        {
            if (condition == null) throw new ArgumentNullException(nameof(condition), $"序列「{_name}」的分支「{name}」需要一个条件");

            RevISequenceStep[] thenSteps = Collect($"{name}/是", then);
            RevISequenceStep[] elseSteps = Collect($"{name}/否", otherwise);

            if (thenSteps.Length == 0 && elseSteps.Length == 0)
                throw new InvalidOperationException($"序列「{_name}」的分支「{name}」两边都是空的");

            return Add(new RevBranchStep(name, condition, thenSteps, elseSteps));
        }

        // ============================================================
        // 立即 / 阻塞步骤
        // ============================================================

        /// <summary>
        /// 阻塞步骤：执行一次 <paramref name="execute"/>，之后每帧问 <paramref name="isCompleted"/>，
        /// 返回 true 才放行下一步。
        /// <code>.DoBlocking("等加载", ctx =&gt; StartLoad(), ctx =&gt; LoadDone)</code>
        /// </summary>
        public RevSequenceBuilder DoBlocking(string name,
                                             Action<RevSequenceContext> execute,
                                             Func<RevSequenceContext, bool> isCompleted)
        {
            if (isCompleted == null)
                throw new ArgumentNullException(nameof(isCompleted), $"阻塞步骤「{name}」需要完成条件（不需要等就用 Do）");

            return Add(new RevDelegateStep(name, execute, isCompleted));
        }

        /// <summary>
        /// 加入一个<b>自定义步骤</b>（继承 <see cref="RevStepBase"/> 实现）。
        /// <para>需要"跨帧私有状态"时用它 —— 状态存在运行实例的状态槽里，天然按运行实例隔离。
        /// ★ 每处都 <c>new</c> 一个：同一个实例放两次会在 Build 时报错。</para>
        /// <code>.Step(new RevWaitClickStep())</code>
        /// </summary>
        public RevSequenceBuilder Step(RevISequenceStep step) => Add(step);

        // ============================================================
        // 事件
        // ============================================================

        /// <summary>
        /// 发一个强类型事件（立即步骤）：用来串联别的序列或通知业务系统。
        /// <code>.Publish(new MyEvent(id: 1))</code>
        /// <para>★ 载荷是<b>构建期</b>给定的 → 只适合常量事件；要带运行期数据请用下面带工厂的重载。</para>
        /// </summary>
        public RevSequenceBuilder Publish<TEvent>(TEvent evt, string name = null)
            => Add(new RevPublishStep<TEvent>(name ?? $"发布事件 {typeof(TEvent).Name}", evt));

        /// <summary>
        /// 发一个强类型事件，载荷在<b>运行时</b>创建（可以带 ctx.Source / uid 等运行期数据）。
        /// <code>.Publish(ctx =&gt; new ChestOpenedEvent(ctx.Source, uid))</code>
        /// </summary>
        public RevSequenceBuilder Publish<TEvent>(Func<RevSequenceContext, TEvent> create, string name = null)
        {
            if (create == null) throw new ArgumentNullException(nameof(create));

            return Add(new RevDelegateStep(name ?? $"发布事件 {typeof(TEvent).Name}",
                ctx => ctx.Events?.Publish(create(ctx))));
        }

        // ============================================================
        // 异步等待
        // ============================================================

        /// <summary>
        /// 等一个异步任务（RevTask）完成 —— 把"等资源加载 / 等网络回包"接进序列。
        /// <para>★ 传的是<b>工厂</b>（<c>ctx =&gt; 任务</c>）：任务在<b>运行时</b>才创建，清单可以缓存复用、并发触发也互不干扰。</para>
        /// <code>.WaitTask(ctx =&gt; ctx.Require&lt;IProtocolService&gt;().RequestAsync(uid), "等服务器回包")</code>
        /// </summary>
        /// <param name="taskFactory">运行时创建任务</param>
        /// <param name="name">步骤名</param>
        /// <param name="cancelOnFailure">任务失败时：true = 取消整条序列（走收尾）；false = 记日志后继续下一步</param>
        public RevSequenceBuilder WaitTask(Func<RevSequenceContext, RevTask> taskFactory, string name = "等异步任务",
                                           bool cancelOnFailure = false)
        {
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory), "WaitTask 需要一个 ctx => 任务 的工厂方法");

            return Add(new RevWaitTaskStep(name, taskFactory, cancelOnFailure));
        }

        /// <summary>
        /// 等一个带返回值的异步任务，完成后把结果交给 <paramref name="onResult"/>。
        /// <code>
        /// .WaitTask(ctx =&gt; LoadPrefabAsync(path),
        ///           (ctx, prefab) =&gt; ctx.Require&lt;IStage&gt;().Show(prefab), "加载演出资源")
        /// </code>
        /// </summary>
        public RevSequenceBuilder WaitTask<T>(Func<RevSequenceContext, RevTask<T>> taskFactory,
                                              Action<RevSequenceContext, T> onResult,
                                              string name = "等异步任务", bool cancelOnFailure = false)
        {
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory), "WaitTask 需要一个 ctx => 任务 的工厂方法");

            return Add(new RevWaitTaskStep<T>(name, taskFactory, onResult, cancelOnFailure));
        }

        // ============================================================
        // 组合步骤
        // ============================================================

        /// <summary>
        /// 并行分组：组内步骤各自推进，全部完成才算这步完成。
        /// <code>.Parallel("同时播", p =&gt; p.Do("音乐", ...).Wait(1f))</code>
        /// </summary>
        public RevSequenceBuilder Parallel(string name, Action<RevSequenceBuilder> build)
        {
            RevISequenceStep[] steps = Collect(name, build);

            if (steps.Length == 0)
                throw new InvalidOperationException($"序列「{_name}」的并行分组「{name}」是空的 —— 并行组至少要有一个步骤");

            return Add(new RevParallelStep(name, steps));
        }

        /// <summary>
        /// 重复分组：把组内步骤重复 N 轮。
        /// <code>.Repeat("闪三次", 3, r =&gt; r.Do("亮", ...).WaitFrames(6).Do("灭", ...))</code>
        /// </summary>
        public RevSequenceBuilder Repeat(string name, int times, Action<RevSequenceBuilder> build)
        {
            if (times < 1) throw new ArgumentOutOfRangeException(nameof(times), "重复次数必须 ≥ 1");

            RevISequenceStep[] steps = Collect(name, build);

            if (steps.Length == 0)
                throw new InvalidOperationException($"序列「{_name}」的重复分组「{name}」是空的 —— 重复组至少要有一个步骤");

            return Add(new RevRepeatStep(name, times, steps));
        }

        /// <summary>
        /// 嵌套播放另一条已构建好的序列（父序列等它跑完；父序列被取消时它也会被取消）。
        /// <para>子序列沿用父序列的触发者、服务与事件总线。</para>
        /// <code>.Sequence("先开箱", ChestOpen)</code>
        /// </summary>
        public RevSequenceBuilder Sequence(string name, RevSequenceDefinition nested)
        {
            if (nested == null)
                throw new ArgumentNullException(nameof(nested), $"序列「{_name}」的嵌套步骤「{name}」需要一个已构建好的 Definition（是不是它的 static 字段声明在后面，还没初始化？）");

            return Add(new RevNestedStep(name, nested));
        }

        // ============================================================
        // 生命周期回调（构建期设定）
        // ============================================================

        /// <summary>正常跑完时回调（多次调用会叠加）</summary>
        public RevSequenceBuilder OnCompleted(Action<RevSequenceRun> callback)
        {
            _onCompleted += callback;
            return this;
        }

        /// <summary>被取消时回调（在收尾步骤之后触发；多次调用会叠加）</summary>
        public RevSequenceBuilder OnCancelled(Action<RevSequenceRun> callback)
        {
            _onCancelled += callback;
            return this;
        }
    }
}

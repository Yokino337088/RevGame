// RevSequenceContext.cs —— 上下文（步骤与宿主之间的唯一通道）
// 【职责】给步骤读 Source（谁触发的）/ Get<T>() 服务 / Events / Elapsed / DeltaTime。
// 【要点】框架不认识任何业务类型 —— 业务能力全靠 ctx.Get<T>() 注入。
// 【取服务】Get<T>() 取不到返回 null（可选服务）；Require<T>() 取不到直接报错（必需服务，推荐）。

using System;

namespace Revolution
{
    /// <summary>
    /// 动作序列的执行上下文：步骤与宿主之间的唯一通道（触发者 + 服务 + 事件 + 时间）。
    /// </summary>
    public sealed class RevSequenceContext
    {
        /// <summary>触发者（谁触发了这条序列：任意对象；框架不关心它的类型，只用于判定并发策略与日志）</summary>
        public object Source { get; internal set; }

        /// <summary>业务服务容器（步骤里用 <see cref="Get{T}"/> 取）</summary>
        public RevSequenceServices Services { get; internal set; }

        /// <summary>强类型事件总线（序列之间串联、对外广播）</summary>
        public RevSequenceEventBus Events { get; internal set; }

        /// <summary>当前运行实例（由引擎注入；外部自建的"参数包"上下文里为 null）</summary>
        public RevSequenceRun Run { get; internal set; }

        /// <summary>本帧的 deltaTime（引擎每帧注入；外部自建上下文里为 0）</summary>
        public float DeltaTime => Run != null ? Run.DeltaTime : 0f;

        /// <summary>本条序列已经跑了多久（秒）—— 做"超时保护""整体节奏控制"用</summary>
        public float Elapsed => Run != null ? Run.Elapsed : 0f;

        /// <summary>本条序列是否已被请求取消（步骤可据此提前收手，做"优雅退出"）</summary>
        public bool IsCancellationRequested => Run != null && Run.IsCancellationRequested;

        /// <summary>当前运行实例的句柄（可用于"序列 A 启动序列 B 并记住句柄"）</summary>
        public RevSequenceHandle Handle => Run != null ? Run.Handle : default;

        /// <summary>建一个上下文（通常只用于传给 <c>runner.Play(def, ctx)</c>）</summary>
        /// <param name="source">触发者（可为 null）</param>
        /// <param name="services">业务服务容器（可为 null）</param>
        /// <param name="events">事件总线（可为 null）</param>
        public RevSequenceContext(object source = null,
                                  RevSequenceServices services = null,
                                  RevSequenceEventBus events = null)
        {
            Source = source;
            Services = services;
            Events = events;
        }

        /// <summary>
        /// 重新配置上下文（引擎内部使用：运行实例被池化复用时，用新的触发者/服务/总线覆盖旧值）。
        /// <para>业务代码不要调用它 —— 它的存在是为了让"每条运行实例一个上下文对象"也能做到零分配复用。</para>
        /// </summary>
        internal void Configure(object source, RevSequenceServices services, RevSequenceEventBus events)
        {
            Source = source;
            Services = services;
            Events = events;
        }

        /// <summary>
        /// 取业务服务（等价于 <c>Services.Get&lt;T&gt;()</c>）；没注册返回 null。
        /// <para>步骤里所有业务调用都应走这里 —— 这是"框架零业务依赖"的落点。</para>
        /// </summary>
        public T Get<T>() where T : class => Services != null ? Services.Get<T>() : null;

        /// <summary>
        /// 取业务服务；<b>没注册就抛异常</b>（异常信息告诉你该怎么注册）。
        /// <para>这个服务"必须有"时用它：比 <c>Get&lt;T&gt;()?.X()</c> 静默跳过好查得多 ——
        /// 抛出后引擎会打出"哪条序列、第几步"，并按取消走收尾。</para>
        /// </summary>
        public T Require<T>() where T : class
        {
            T service = Get<T>();
            if (service != null) return service;

            throw new InvalidOperationException(
                $"没有注册服务 {typeof(T).Name}：请先 runner.Services.Add<{typeof(T).Name}>(实现)" +
                "（用全局引擎就是 RevSequencePlayer.Default.Services.Add<…>(…)）");
        }

        /// <summary>
        /// 把触发者转成指定类型（转不了返回 null）。
        /// <code>ctx.SourceAs&lt;GameObject&gt;()?.SetActive(false)</code>
        /// </summary>
        public T SourceAs<T>() where T : class => Source as T;

        /// <summary>调试显示：触发者类型 + 当前步</summary>
        public override string ToString()
        {
            string source = Source == null ? "（无触发者）" : Source.GetType().Name;
            return Run == null ? $"Context({source})" : $"Context({source}) @ {Run.Definition.Name}[{Run.StepIndex}]";
        }
    }
}

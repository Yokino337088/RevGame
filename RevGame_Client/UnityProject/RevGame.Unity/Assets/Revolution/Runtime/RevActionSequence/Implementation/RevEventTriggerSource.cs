// RevEventTriggerSource.cs —— 事件触发源（框架内部）+ Bind / BindEvents 扩展方法
// 【用法】runner.BindEvents<ChestOpenedEvent>(runner.Events, 门开启清单, e => e.Opener) —— 一行接上事件。
// 【要点】sourceSelector 决定「触发者是谁」，从而决定并发策略按谁判定。

using System;

namespace Revolution
{
    /// <summary>
    /// 把"某类强类型事件"变成触发源：事件一发生就喊一声。
    /// <para>框架内部使用 —— 由 <see cref="RevSequenceTriggerExtensions.BindEvents{TEvent}"/>
    /// 自动创建，业务不需要手动 new（所以它是 internal）。</para>
    /// </summary>
    /// <typeparam name="TEvent">事件类型（与 <see cref="RevSequenceEventBus.Subscribe{TEvent}"/> 对应）</typeparam>
    internal sealed class RevEventTriggerSource<TEvent> : RevITriggerSource
    {
        private readonly Func<TEvent, object> _sourceSelector;
        private IDisposable _subscription;      // 事件总线的订阅句柄（Dispose 时反注册）

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public event Action<RevTriggerSignal> Triggered;

        /// <summary>构造：订阅总线上的 TEvent，转成触发信号</summary>
        /// <param name="name">来源名（日志用）</param>
        /// <param name="bus">事件总线</param>
        /// <param name="sourceSelector">从事件里取"触发者"；不传则用事件对象本身</param>
        internal RevEventTriggerSource(string name, RevSequenceEventBus bus, Func<TEvent, object> sourceSelector = null)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));

            Name = string.IsNullOrEmpty(name) ? typeof(TEvent).Name : name;
            _sourceSelector = sourceSelector;
            _subscription = bus.Subscribe<TEvent>(OnEvent);
        }

        private void OnEvent(TEvent evt)
        {
            object source = _sourceSelector != null ? _sourceSelector(evt) : evt;
            Triggered?.Invoke(new RevTriggerSignal(source, Name));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _subscription?.Dispose();       // 先退订，再断开 Triggered 订阅
            _subscription = null;
            Triggered = null;               // 断开所有订阅，防止宿主销毁后仍被回调
        }
    }

    /// <summary>
    /// 触发源 × 序列的绑定扩展：一行把"触发"接到"播放"上。
    /// </summary>
    public static class RevSequenceTriggerExtensions
    {
        /// <summary>
        /// 把任意触发源绑到一条序列上：源每触发一次，就 <c>runner.Play(definition, signal.Source)</c> 一次。
        /// <para>返回值 Dispose 即解绑（推荐与宿主生命周期成对）。</para>
        /// </summary>
        public static IDisposable Bind(this RevSequenceRunner runner, RevITriggerSource source, RevSequenceDefinition definition)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            // 源每触发一次就播一次（并发策略在 Play 内部判定，被拒绝时返回空句柄）
            void OnTriggered(RevTriggerSignal signal) => runner.Play(definition, signal.Source);

            source.Triggered += OnTriggered;

            return new RevSequenceSubscription(() => source.Triggered -= OnTriggered, null);
        }

        /// <summary>
        /// 一行绑定"事件 → 序列"（对应王者 listenEventActions 的用法）。
        /// <code>runner.BindEvents&lt;MyEvent&gt;(bus, mySequence, evt =&gt; evt.Id);</code>
        /// </summary>
        /// <param name="runner">执行引擎</param>
        /// <param name="bus">事件总线（通常就是 <c>runner.Events</c>）</param>
        /// <param name="definition">要播放的序列</param>
        /// <param name="sourceSelector">从事件取触发者（并发策略按它判定）；不传则用事件对象本身</param>
        /// <returns>Dispose 即解绑（同时退订事件总线）</returns>
        public static IDisposable BindEvents<TEvent>(this RevSequenceRunner runner,
                                                     RevSequenceEventBus bus,
                                                     RevSequenceDefinition definition,
                                                     Func<TEvent, object> sourceSelector = null)
        {
            var source = new RevEventTriggerSource<TEvent>(typeof(TEvent).Name, bus, sourceSelector);
            IDisposable binding = runner.Bind(source, definition);

            // 解绑时把触发源一起销毁（否则事件总线上会留下一个永远没人播放的订阅）
            return new RevSequenceSubscription(
                () =>
                {
                    binding.Dispose();
                    source.Dispose();
                },
                () => source != null);
        }
    }
}

// RevITriggerSource.cs —— 触发源契约（+ 触发信号 RevTriggerSignal）
// 【唯一写法】实现本接口（MonoBehaviour 也能实现）—— 框架不提供基类：MonoBehaviour 受 C# 单基类限制
//             只能走接口，两条路并存只会让人踩坑。
// 【怎么用】区域检测到 → Triggered?.Invoke(new RevTriggerSignal(谁, Name)) → runner.Bind(源, 清单)。

using System;

namespace Revolution
{
    /// <summary>
    /// 一次触发信号：谁触发的（Source）+ 来自哪个源（Tag，仅用于日志定位）。
    /// </summary>
    public readonly struct RevTriggerSignal
    {
        /// <summary>触发者（会成为序列的 <c>context.Source</c>；可为 null）</summary>
        public object Source { get; }

        /// <summary>来源标签（例如"某个区域""某个事件"），只用于日志</summary>
        public string Tag { get; }

        /// <summary>构造一个触发信号</summary>
        public RevTriggerSignal(object source, string tag = null)
        {
            Source = source;
            Tag = tag;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"Trigger({Tag ?? "未命名"} ← {(Source == null ? "（无触发者）" : Source.GetType().Name)})";
    }

    /// <summary>
    /// 触发源：任何能"启动一条序列"的东西（区域、事件、定时器、服务端指令……）。
    /// <para>实现它是可选的 —— 手动调用 <c>runner.Play(...)</c> 永远是最直接的入口。</para>
    /// <code>
    /// // ★ 最常用的一种：MonoBehaviour 自己实现本接口，把 Unity 物理回调接进来
    /// sealed class MyZone : MonoBehaviour, RevITriggerSource
    /// {
    ///     public string Name =&gt; "某触发区域";
    ///     public event Action&lt;RevTriggerSignal&gt; Triggered;
    ///     public void Dispose() =&gt; Triggered = null;
    ///
    ///     private void OnTriggerEnter(Collider other)
    ///         =&gt; Triggered?.Invoke(new RevTriggerSignal(other.gameObject, Name));
    /// }
    /// // 然后：runner.Bind(zone, 我的序列);
    /// </code>
    /// </summary>
    public interface RevITriggerSource : IDisposable
    {
        /// <summary>来源名（日志与调试面板用）</summary>
        string Name { get; }

        /// <summary>触发时回调（订阅方负责反订阅；Dispose 时请一并断开）</summary>
        event Action<RevTriggerSignal> Triggered;
    }

}

// RevSequence.cs —— ★ 小白从这里开始（3 分钟就能跑起来）
//
// ┌─ 三步走 ──────────────────────────────────────────────────────────────────────────┐
// │ 1) 构建清单（只建一次，加载期缓存到 static readonly）                               │
// │    static readonly RevSequenceDefinition ChestOpen = RevSequence                   │
// │        .Create("宝箱开箱")                                                          │
// │        .ScaleTo("宝箱弹出", Vector3.one, 0.25f, RevEase.OutBack)                     │
// │        .WaitUntil("等玩家点击", ctx => ctx.Require<IInput>().Clicked, 15f)            │
// │        .Do("开门", ctx => ctx.Require<IDoor>().Open())                              │
// │        .Finally(f => f.Do("恢复镜头", ctx => ctx.Require<ICamera>().Restore()))      │
// │        .Build();                                                                    │
// │                                                                                     │
// │ 2) 播放（一行，零配置：框架自动每帧驱动；宝箱物体被销毁时序列自动取消）               │
// │    RevSequenceHandle handle = ChestOpen.Play(宝箱物体);                              │
// │                                                                                     │
// │ 3) 取消（被取消时必然执行 .OnCancel / .Finally 里写的收尾）                            │
// │    handle.Stop();                                                                   │
// └─────────────────────────────────────────────────────────────────────────────────────┘
//
// 【接下来看哪】（只有前两个是你必须看的）
//   ① 本文件                            构建入口 + 三条铁律
//   ② RevSequenceBuilder.cs             常用方法：Do / Wait / WaitUntil / Tween / If / OnCancel / Finally / Build
//   ③ RevSequenceBuilder.Advanced.cs    需要时才看：并行 / 重复 / 嵌套 / 事件 / 等异步 / 自定义步骤 / 埋点
//   ④ Support\RevSequenceUnityExtensions.cs   Unity 现成步骤：MoveTo / ScaleTo / FadeTo / SetActive
//   ⑤ RevStepBase.cs                    只有"需要跨帧私有状态"、上面都满足不了时才看
//
// 【三条铁律】
//   ① 清单只 Build 一次并缓存 —— 别在运行期反复构建
//   ② 状态别存在构建期字段里 —— 用业务服务、Tween 的起始值，或自定义步骤的状态槽（否则两个玩家同时触发会互踩）
//   ③ 占用了什么（生成的物件 / 推近的镜头），就在 .Finally（或只在取消时要做的 .OnCancel）里还回去
//
// 【出了问题看 Console】步骤抛异常、等待超时、收尾出错都会打日志（tag = ActionSequence），写明哪条序列、第几步。
//
// 完整教程：Revolution.Document\动作序列\（使用说明 = 上手；使用指南 = 设计与王者真实场景对照）

namespace Revolution
{
    /// <summary>
    /// 动作序列的构建入口 —— 唯一必须先看懂的类。
    /// <code>
    /// // ① 构建一次（加载期；Definition 是不可变蓝图，可以反复播、可以跨场景复用）
    /// static readonly RevSequenceDefinition Flow = RevSequence
    ///     .Create("一段流程")
    ///     .Do("做一件事", ctx =&gt; ctx.Get&lt;IMyService&gt;()?.Do())
    ///     .Wait(0.5f)
    ///     .Build();
    ///
    /// // ② 播放（运行期；零构建成本）
    /// Flow.Play(gameObject);
    /// </code>
    /// </summary>
    public static class RevSequence
    {
        /// <summary>
        /// 开始构建一条序列。
        /// </summary>
        /// <param name="name">序列名（日志与调试面板靠它定位；不能为空）</param>
        /// <param name="concurrency">
        /// 并发策略：同一个触发者重复触发这条序列时怎么办。
        /// 默认 <see cref="RevSequenceConcurrency.Free"/>（各跑各的）；防连点用 <see cref="RevSequenceConcurrency.RejectPerSource"/>。
        /// </param>
        public static RevSequenceBuilder Create(string name, RevSequenceConcurrency concurrency = RevSequenceConcurrency.Free)
            => new RevSequenceBuilder(name, concurrency);
    }
}

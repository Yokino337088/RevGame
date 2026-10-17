// RevSequenceDefinition.cs —— 不可变蓝图（Build 的产物）
// 【职责】存步骤数组、收尾步骤、并发策略、生命周期回调、状态槽数量。
// 【要点】构建一次即可无限次播放、可跨场景复用、可 static readonly 缓存。
// 【并发】并发策略按「同一条定义 + 同一个 Source」判定。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 一条动作序列的蓝图（不可变）：步骤列表 + 收尾步骤 + 并发策略 + 生命周期回调。
    /// <para>由 <see cref="RevSequenceBuilder"/> 的 Build() 产出；用 <c>definition.Play(source)</c> 或 <c>runner.Play(definition, ...)</c> 播放。</para>
    /// </summary>
    public sealed class RevSequenceDefinition
    {
        /// <summary>步骤数组（引擎热路径直接用数组遍历，不经过接口的 IReadOnlyList 包装）</summary>
        internal readonly RevISequenceStep[] StepArray;

        /// <summary>取消时的收尾步骤（<c>.OnCancel</c>；必须都是立即步骤）</summary>
        internal readonly RevISequenceStep[] CancelArray;

        /// <summary>无论跑完还是取消都执行的收尾步骤（<c>.Finally</c>；必须都是立即步骤）</summary>
        internal readonly RevISequenceStep[] FinallyArray;

        /// <summary>序列名（日志、调试面板与 Build 校验用）</summary>
        public string Name { get; }

        /// <summary>并发策略（同源重复触发时怎么办，见 <see cref="RevSequenceConcurrency"/>）</summary>
        public RevSequenceConcurrency Concurrency { get; }

        /// <summary>步骤列表（只读视图；数组内部维护，请勿修改）</summary>
        public IReadOnlyList<RevISequenceStep> Steps => StepArray;

        /// <summary>取消时的收尾步骤（只读视图，对应 <c>.OnCancel</c>）</summary>
        public IReadOnlyList<RevISequenceStep> CancelSteps => CancelArray;

        /// <summary>总会执行的收尾步骤（只读视图，对应 <c>.Finally</c>）</summary>
        public IReadOnlyList<RevISequenceStep> FinallySteps => FinallyArray;

        /// <summary>步骤数量</summary>
        public int StepCount => StepArray.Length;

        /// <summary>
        /// 这条序列需要多少状态槽（= 步骤树里所有步骤数，含组合步骤的子步骤）。
        /// <para>框架内部使用：运行实例按它开状态数组，业务不用关心。</para>
        /// </summary>
        internal int SlotCount { get; }

        /// <summary>并发策略 ≠ Free 却没传触发者时，只提醒一次（见 RevSequenceRunner.PlayInternal）</summary>
        internal bool WarnedNullSource;

        /// <summary>正常跑完时回调</summary>
        public Action<RevSequenceRun> OnCompleted { get; internal set; }

        /// <summary>被取消时回调（先跑收尾步骤，再回调本事件）</summary>
        public Action<RevSequenceRun> OnCancelled { get; internal set; }

        internal RevSequenceDefinition(string name,
                                       RevSequenceConcurrency concurrency,
                                       List<RevISequenceStep> steps,
                                       List<RevISequenceStep> cancelSteps,
                                       List<RevISequenceStep> finallySteps)
        {
            Name = name;
            Concurrency = concurrency;
            StepArray = steps.ToArray();
            CancelArray = cancelSteps != null ? cancelSteps.ToArray() : Array.Empty<RevISequenceStep>();
            FinallyArray = finallySteps != null ? finallySteps.ToArray() : Array.Empty<RevISequenceStep>();

            // 给步骤树分配状态槽：组合步骤（并行/重复/分支）会递归给自己的子步骤继续分配。
            // 派生自 RevStepBase 的步骤由基类递归分配；直接实现接口的步骤也占一个槽 —— 保证槽位绝不重叠。
            // ★ 同一个步骤实例出现两次会在这里抛异常（见 RevStepBase.AssignSlots）。
            int nextSlot = 0;
            RevStepBase.AssignChildSlots(StepArray, ref nextSlot);
            RevStepBase.AssignChildSlots(CancelArray, ref nextSlot);
            RevStepBase.AssignChildSlots(FinallyArray, ref nextSlot);
            SlotCount = nextSlot;
        }

        /// <summary>调试显示：例如 <c>「某条序列」(4 步/ReplacePerSource)</c></summary>
        public override string ToString() => $"「{Name}」({StepCount} 步/{Concurrency})";
    }
}

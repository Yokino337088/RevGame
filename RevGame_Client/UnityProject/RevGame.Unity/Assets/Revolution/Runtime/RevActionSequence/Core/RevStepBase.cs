// RevStepBase.cs —— 自定义步骤基类（需要「本次运行私有状态」时用）
// 【三件事】① 内部类 State ② Execute 里 GetOrCreateState<T>(run) ③ IsCompleted 里判断。
// 【铁律】状态存槽里、时间从 run 取 —— 在 lambda 里闭包捕获字段会被并发触发互踩。
// 【先想想要不要写它】"随时间变化"用 .Tween、"二选一"用 .If、"等条件"用 .WaitUntil —— 这三个覆盖了大部分自定义步骤的需求。

using System;

namespace Revolution
{
    /// <summary>
    /// 动作序列步骤基类：提供"按运行实例隔离的状态槽"与"子步骤槽位分配"两件事。
    /// <para>不想继承它也行（直接实现 <see cref="RevISequenceStep"/>），但那样就得自己找地方放状态。</para>
    /// <para>★ 一个步骤实例只能放进<b>一条</b>序列的<b>一个</b>位置（槽号记在实例上）：要复用就每处 <c>new</c> 一个。</para>
    /// </summary>
    public abstract class RevStepBase : RevISequenceStep
    {
        private int _slotIndex = -1;        // 构建期由 RevSequenceDefinition 分配

        /// <summary>步骤名（用于日志与调试面板：告诉你卡在哪一步）</summary>
        public abstract string Name { get; }

        /// <summary>执行本步（每步只调用一次）</summary>
        public abstract void Execute(RevSequenceRun run, RevSequenceContext context);

        /// <summary>
        /// 本步完成了吗（每帧问一次）。默认 <c>true</c> = <b>立即步骤</b>；
        /// 阻塞步骤请重写它（返回 false 会让序列挂起在此步）。
        /// </summary>
        public virtual bool IsCompleted(RevSequenceRun run, RevSequenceContext context) => true;

        /// <summary>本步骤占用的状态槽号（引擎构建期分配；-1 = 尚未分配）</summary>
        internal int SlotIndex => _slotIndex;

        /// <summary>
        /// 内置步骤是否"一定会跨帧等待"（构建期校验用：收尾步骤里不允许出现）。
        /// 自定义步骤无法静态判断，默认 false —— 收尾时运行期再检查一次。
        /// </summary>
        internal virtual bool IsBlocking => false;

        /// <summary>
        /// 分配状态槽（构建期调用一次）。
        /// <para>组合步骤（并行/重复/分支）重写本方法时，<b>必须先调 base.AssignSlots(ref next)</b>，
        /// 再给子步骤分配 —— 否则子步骤会占用父步骤的槽位。</para>
        /// </summary>
        internal virtual void AssignSlots(ref int next)
        {
            // 同一个实例被放进两条序列（或同一条里放了两次）：后一次分配会覆盖前一次的槽号 →
            // 两处运行时读写同一个槽、或读到越界槽（等待被直接放行）。这种错运行时极难查，构建期直接拦下。
            if (_slotIndex != -1)
                throw new InvalidOperationException(
                    $"步骤「{Name}」这个实例已经被放进过一条序列了 —— 一个步骤实例只能用在一个位置。" +
                    $"请每处都 new 一个（例如 .Step(new {GetType().Name}(...))），不要把实例存成字段共用。");

            _slotIndex = next++;
        }

        /// <summary>读本步骤在"本次运行"中的状态（未写过则为 null）</summary>
        protected T GetState<T>(RevSequenceRun run) where T : class => run.GetStepState<T>(_slotIndex);

        /// <summary>写本步骤在"本次运行"中的状态</summary>
        protected void SetState(RevSequenceRun run, object state) => run.SetStepState(_slotIndex, state);

        /// <summary>取状态，取不到就创建并写入（省掉每次判空；状态对象随运行实例池化复用）</summary>
        protected T GetOrCreateState<T>(RevSequenceRun run) where T : class, new()
        {
            T state = run.GetStepState<T>(_slotIndex);
            if (state == null)
            {
                state = new T();
                run.SetStepState(_slotIndex, state);
            }

            return state;
        }

        /// <summary>给一组子步骤分配槽位（组合步骤共用）</summary>
        internal static void AssignChildSlots(RevISequenceStep[] steps, ref int next)
        {
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] is RevStepBase baseStep) baseStep.AssignSlots(ref next);
                else next++;
            }
        }

        /// <summary>一组子步骤里有没有内置阻塞步骤</summary>
        internal static bool AnyBlocking(RevISequenceStep[] steps)
        {
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] is RevStepBase s && s.IsBlocking) return true;
            return false;
        }

        /// <inheritdoc/>
        public override string ToString() => Name;
    }
}

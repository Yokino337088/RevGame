// RevSequenceBuilder.cs —— 【第 2 个要看的文件】常用方法（学会这几个就能写完 90% 的演出）
//
//   .Do("做什么", ctx => ...)                              立刻做一件事（播音乐 / 开界面 / 发奖励）；名字可省：.Do(ctx => ...)
//   .Wait(1.5f)   /   .WaitFrames(6)                       等一会儿（秒 / 帧；时间来自引擎 Tick，不读 Time.time）
//   .WaitUntil("等条件", ctx => 条件, timeoutSeconds: 15f)   等外部状态（超时放行 + 日志告警，不会卡死）
//   .Tween("淡入", 0.3f, (ctx, t) => ...)                   随时间变化（t 从 0 到 1）；Unity 里还有 .MoveTo / .ScaleTo / .FadeTo
//   .If("低画质？", ctx => 条件, then => ..., otherwise => ...)   运行时二选一
//   .OnCancel(f => f.Do(...))                              被取消时执行的收尾
//   .Finally(f => f.Do(...))                               跑完或被取消都执行的收尾（恢复镜头 / HUD 写这里，只写一遍）
//   .Build()                                               收尾：静态校验 + 生成不可变清单（只调用一次）
//   然后：清单.Play(gameObject)                              一行播放（全局引擎，自动驱动；物体销毁时自动取消）
//
//   进阶（并行 / 重复 / 嵌套 / 事件 / 等异步 / 自定义步骤 / 埋点回调）见同目录 RevSequenceBuilder.Advanced.cs
//
// 【本文件负责】构建器状态 + 常用步骤 + Add / Build 校验；进阶方法在另一个 partial 文件里。

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Revolution
{
    /// <summary>
    /// 流式构建器：链式把步骤与回调串起来，最后 <see cref="Build"/> 成不可变的
    /// <see cref="RevSequenceDefinition"/>。
    /// <para>构建器是"一次性"的：Build 之后再调用链式方法会抛异常（避免你以为改了蓝图其实没改）。</para>
    /// </summary>
    public sealed partial class RevSequenceBuilder
    {
        private readonly string _name;
        private readonly RevSequenceConcurrency _concurrency;
        private readonly bool _isSubBuilder;        // 子构建器（Parallel/Repeat/If/OnCancel/Finally 内部用），不能单独 Build
        private readonly List<RevISequenceStep> _steps = new List<RevISequenceStep>(8);
        private readonly List<RevISequenceStep> _cancelSteps = new List<RevISequenceStep>(2);
        private readonly List<RevISequenceStep> _finallySteps = new List<RevISequenceStep>(2);
        private bool _built;

        // 生命周期回调（构建期设定，Build 时写入 Definition；多次调用会叠加，不会覆盖）
        private Action<RevSequenceRun> _onCompleted;
        private Action<RevSequenceRun> _onCancelled;

        internal RevSequenceBuilder(string name, RevSequenceConcurrency concurrency, bool isSubBuilder = false)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                throw new ArgumentException("动作序列必须有名字（日志与调试面板靠它定位）", nameof(name));

            _name = name;
            _concurrency = concurrency;
            _isSubBuilder = isSubBuilder;
        }

        // ============================================================
        // 1. 立即步骤：执行完就放行下一步
        // ============================================================

        /// <summary>
        /// 立即步骤：执行一句业务代码，执行完就继续下一步（不阻塞）。
        /// <code>.Do("做一件事", ctx =&gt; ctx.Require&lt;IMyService&gt;().DoSomething())</code>
        /// </summary>
        public RevSequenceBuilder Do(string name, Action<RevSequenceContext> body)
            => Add(new RevDelegateStep(name, body));

        /// <summary>
        /// 立即步骤（省略名字）：日志里用"文件名:行号"标出这一步，照样能定位到是哪一行。
        /// <code>.Do(ctx =&gt; ctx.SourceAs&lt;GameObject&gt;()?.SetActive(false))</code>
        /// </summary>
        public RevSequenceBuilder Do(Action<RevSequenceContext> body,
                                     [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Add(new RevDelegateStep(AutoName("执行", file, line), body));

        /// <summary>打一条日志（RevLog.Info，tag = ActionSequence），调试流程走到哪用</summary>
        public RevSequenceBuilder Log(string message)
            => Add(new RevDelegateStep("日志：" + message,
                ctx => RevLog.Info($"[动作序列] {ctx.Run?.Definition?.Name}：{message}", RevSequenceRunner.LogTag)));

        // ============================================================
        // 2. 等待步骤：这一步没放行，后面的步骤不会开始
        // ============================================================

        /// <summary>等固定秒数（用引擎 Tick 的 dt 累计，不读 Time.time）</summary>
        public RevSequenceBuilder Wait(float seconds)
        {
            if (seconds < 0f) throw new ArgumentOutOfRangeException(nameof(seconds), "等待时间不能为负");
            return Add(new RevWaitSecondsStep($"等待 {seconds:0.##}s", seconds));
        }

        /// <summary>等固定帧数（引擎 Tick 次数）；<c>WaitFrames(1)</c> = 至少再等一帧</summary>
        public RevSequenceBuilder WaitFrames(int frames)
        {
            if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames), "等待帧数不能为负");
            return Add(new RevWaitFramesStep($"等待 {frames} 帧", frames));
        }

        /// <summary>
        /// 等条件成立（可带超时保护 —— 超时是"放行 + 日志告警"，不会永久卡死）。
        /// <code>.WaitUntil("等条件成立", ctx =&gt; ctx.Get&lt;IMyService&gt;()?.IsReady == true, timeoutSeconds: 10f)</code>
        /// </summary>
        /// <param name="name">步骤名</param>
        /// <param name="predicate">条件（每帧问一次）</param>
        /// <param name="timeoutSeconds">超时秒数；&lt;= 0 = 一直等</param>
        /// <param name="onTimeout">超时时要做的事（不传 = 打一条告警）；之后照常继续下一步</param>
        public RevSequenceBuilder WaitUntil(string name, Func<RevSequenceContext, bool> predicate, float timeoutSeconds = 0f,
                                            Action<RevSequenceContext> onTimeout = null)
            => Add(new RevWaitUntilStep(name, predicate, timeoutSeconds, onTimeout));

        /// <summary>等条件成立（省略名字；日志里用"文件名:行号"标出这一步）</summary>
        public RevSequenceBuilder WaitUntil(Func<RevSequenceContext, bool> predicate, float timeoutSeconds = 0f,
                                            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Add(new RevWaitUntilStep(AutoName("等条件", file, line), predicate, timeoutSeconds));

        // ============================================================
        // 3. 收尾与构建
        // ============================================================

        /// <summary>
        /// 取消时的收尾：序列被取消（Stop / 被顶替 / 触发者被销毁 / 步骤出错）时执行，<b>必须是立即步骤</b>。
        /// <para>用途：回收生成物、反注册事件。"跑完和取消都要做"的事请写在 <see cref="Finally"/>，不用写两遍。</para>
        /// <para>可以调用多次，按顺序追加。</para>
        /// </summary>
        public RevSequenceBuilder OnCancel(Action<RevSequenceBuilder> buildSteps)
        {
            AppendCleanup(_cancelSteps, $"{_name}/取消收尾", buildSteps);
            return this;
        }

        /// <summary>
        /// 总会执行的收尾：<b>正常跑完、被取消、步骤出错</b>都会执行（取消时排在 <see cref="OnCancel"/> 之后），<b>必须是立即步骤</b>。
        /// <code>.Finally(f =&gt; f.Do("恢复镜头", ctx =&gt; ctx.Require&lt;ICamera&gt;().Restore()))</code>
        /// <para>只有 <c>Stop(runFinally: false)</c> 会显式跳过它。可以调用多次，按顺序追加。</para>
        /// </summary>
        public RevSequenceBuilder Finally(Action<RevSequenceBuilder> buildSteps)
        {
            AppendCleanup(_finallySteps, $"{_name}/收尾", buildSteps);
            return this;
        }

        /// <summary>
        /// 构建成不可变蓝图（做静态校验）。
        /// <para>建议在加载期调用一次并把结果缓存到 <c>static readonly</c> 字段 —— 运行期 Play 它零构建成本。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">空序列、重复 Build、收尾里放了等待、同一个步骤实例用了两次等用法错误</exception>
        public RevSequenceDefinition Build()
        {
            if (_isSubBuilder)
                throw new InvalidOperationException($"子构建器「{_name}」（Parallel/Repeat/If/OnCancel/Finally 内部使用）不能单独 Build，请在外层序列上统一 Build()");

            if (_built)
                throw new InvalidOperationException($"序列「{_name}」已经 Build 过了 —— Definition 是不可变蓝图，请把它缓存起来复用，不要重复 Build");

            if (_steps.Count == 0)
                throw new InvalidOperationException($"序列「{_name}」没有任何步骤 —— 空序列没有意义（占位请至少加一个 Do）");

            ValidateSteps(_name, _steps);
            ValidateCleanup($"{_name} 的 OnCancel", _cancelSteps);
            ValidateCleanup($"{_name} 的 Finally", _finallySteps);

            _built = true;

            return new RevSequenceDefinition(_name, _concurrency, _steps, _cancelSteps, _finallySteps)
            {
                OnCompleted = _onCompleted,
                OnCancelled = _onCancelled,
            };
        }

        // ============================================================
        // 内部（进阶文件里的方法也复用这几个）
        // ============================================================

        private RevSequenceBuilder Add(RevISequenceStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (_built) throw new InvalidOperationException($"序列「{_name}」已经 Build 过了，不能再加步骤（要改请重新构建一条）");

            _steps.Add(step);
            return this;
        }

        private RevSequenceBuilder CreateSub(string name)
            => new RevSequenceBuilder(name, RevSequenceConcurrency.Free, isSubBuilder: true);

        /// <summary>用子构建器收集一组步骤（空组返回空数组）</summary>
        private RevISequenceStep[] Collect(string name, Action<RevSequenceBuilder> build)
        {
            if (build == null) return Array.Empty<RevISequenceStep>();

            RevSequenceBuilder sub = CreateSub(name);
            build(sub);
            return sub._steps.ToArray();
        }

        private void AppendCleanup(List<RevISequenceStep> target, string name, Action<RevSequenceBuilder> build)
        {
            if (_built) throw new InvalidOperationException($"序列「{_name}」已经 Build 过了，不能再加收尾");
            target.AddRange(Collect(name, build));
        }

        /// <summary>省略名字时的自动命名："执行（ChestOpenSequences.cs:42）"</summary>
        private static string AutoName(string kind, string file, int line)
        {
            string fileName = string.IsNullOrEmpty(file) ? "?" : System.IO.Path.GetFileName(file);
            return $"{kind}（{fileName}:{line}）";
        }

        /// <summary>步骤校验：null / 空名报错</summary>
        private static void ValidateSteps(string owner, List<RevISequenceStep> steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                RevISequenceStep step = steps[i];

                if (step == null)
                    throw new InvalidOperationException($"{owner} 的第 {i + 1} 个步骤是 null");

                if (string.IsNullOrEmpty(step.Name) || step.Name.Trim().Length == 0)
                    throw new InvalidOperationException($"{owner} 的第 {i + 1} 个步骤没有名字（报错与排查靠它定位【卡在哪一步】）");
            }
        }

        /// <summary>收尾校验：收尾只同步执行一次，里面放 Wait / WaitUntil / Tween / 嵌套序列等于写了不会生效的代码</summary>
        private static void ValidateCleanup(string owner, List<RevISequenceStep> steps)
        {
            ValidateSteps(owner, steps);

            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] is RevStepBase step && step.IsBlocking)
                    throw new InvalidOperationException(
                        $"{owner} 的第 {i + 1} 步「{step.Name}」是等待步骤 —— 收尾只会同步执行一次、不会等待，" +
                        "这里只能放 Do 这类立即步骤（要等的收尾请交给业务服务自己处理）");
            }
        }

        /// <summary>调试显示</summary>
        public override string ToString() => $"RevSequenceBuilder(「{_name}」{_steps.Count} 步{(_built ? "，已构建" : string.Empty)})";
    }
}

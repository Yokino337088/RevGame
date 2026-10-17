// ============================================================
// RevScopeDefine.cs —— 作用域系统的"数据定义"（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevScope\Core\
//
// 这里只放"数据长什么样"，没有任何逻辑：
//   · RevScopeState       一个域现在处于什么状态（开着 / 正在关 / 已关）
//   · RevScopeStep        域关闭时的 9 个回收步骤（★ 顺序就是枚举的数值顺序，框架钉死，业务不能改）
//   · RevScopeOptions     进入一个域时的可选项（绝大多数项目一个都不用填）
//   · RevScopeStepResult  某一步回收了多少个、花了多久、有没有出错
//   · RevScopeCloseReport 整个域关闭的"体检报告"（排查"回大厅后内存没降"的第一手资料）
// ============================================================
using System;
using System.Text;

namespace Revolution
{
    /// <summary>一个域的状态。</summary>
    public enum RevScopeState
    {
        /// <summary>开着：正常接收归属、正常被驱动。</summary>
        Open = 0,

        /// <summary>正在关闭：回收步骤正在跑。这期间不接受新的业务单元（RevLogic.Start 会被拒绝）。</summary>
        Closing = 1,

        /// <summary>已关闭：不可再用。往里登记归属会被明确拒绝（并提示原因）。</summary>
        Closed = 2,
    }

    /// <summary>
    /// 域关闭时的回收步骤。<b>★ 数值顺序就是执行顺序，由框架钉死</b>：
    /// 规则一句话 —— <b>谁依赖谁，谁后关</b>（被依赖的东西必须活到最后）。
    /// </summary>
    public enum RevScopeStep
    {
        /// <summary>0 关域门：标记 Closing，从这一刻起不再接受新的业务单元。</summary>
        Gate = 0,

        /// <summary>1 业务单元（RevLogic）先停：它们的 OnStop 收尾代码此时还能取服务、用资源、订事件。</summary>
        Logic = 1,

        /// <summary>2 界面（RevUI）：面板的 OnClose / OnRelease 往往要释放资源，所以必须排在资源之前。</summary>
        Ui = 2,

        /// <summary>3 每帧回调（RevMono）：先停驱动，免得"清到一半还有每帧回调在跑"。</summary>
        Mono = 3,

        /// <summary>4 计时器（RevTimer）：停掉所有"以后还会再来"的回调。</summary>
        Timer = 4,

        /// <summary>5 事件监听（RevEvent）：兜底摘监听；放在业务 / 界面之后，保证它们收尾期间派发仍然可达。</summary>
        Event = 5,

        /// <summary>6 对象池（RevPool.DestroyGroup）：★ 必须先还实例 —— 池手里端着 prefab 的资源引用。</summary>
        Pool = 6,

        /// <summary>7 资源（RevResBootstrap.Shutdown）：★ 再卸资源 —— 此时已没有池端着引用（这就是"顺序不能反"的原因）。</summary>
        Res = 7,

        /// <summary>8 服务（RevServices.UnregisterAllOf）：服务最后收，因为上面每一步都可能用到它。</summary>
        Services = 8,

        /// <summary>9 兜底扫尾：关闭过程中（来自各种收尾代码）又新长出来的注册，在这里再扫一遍。</summary>
        Sweep = 9,
    }

    /// <summary>进入一个域时的可选项。<b>绝大多数项目一个都不用填</b>，直接 <c>RevScope.Enter("Battle")</c> 即可。</summary>
    public sealed class RevScopeOptions
    {
        /// <summary>
        /// 关闭这个域时，要整组回收的资源分组（对象池 + 资源）。默认 Unknown = 不按组回收。
        /// ★ 别设成"全程共用"的组（UI / Config / Sound 这类）—— 它是整组清，会把别人还在用的也清掉。
        /// </summary>
        public RevResGroup Reclaim = RevResGroup.Unknown;

        /// <summary>
        /// 域内"没写 owner / group"的注册，是否自动归这个域（默认 true）。
        /// 关掉后域只做"驱动开关 + 手动 Attach 的回收边界"，不再替你兜底没写 owner 的注册。
        /// </summary>
        public bool TakeOverOwner = true;

        /// <summary>
        /// 同名域已经开着时再 Enter：true = 报错并返回空句柄（默认，说明你多半漏了一次 Exit）；
        /// false = 不报错，直接返回已开着的那个域（"进入或复用"）。
        /// </summary>
        public bool ReenterIsError = true;

        /// <summary>排查用备注（会出现在 Dump 里），比如"第 3 局"。</summary>
        public string DebugNote;
    }

    /// <summary>回收报告里"某一步"的结果。</summary>
    public readonly struct RevScopeStepResult
    {
        public readonly RevScopeStep Step;

        /// <summary>这一步回收了多少个（计时器 / 监听 / 面板 / 业务单元……）。跳过的步骤为 0。</summary>
        public readonly int Count;

        public readonly float ElapsedMs;

        /// <summary>是否被跳过（模块没装 / 这个域没配置回收分组 / 域门这类不回收东西的步骤）。</summary>
        public readonly bool Skipped;

        /// <summary>跳过原因 / 补充说明（人话）。</summary>
        public readonly string Note;

        /// <summary>这一步抛了异常时的异常文本；null = 没出错。★ 出错不会中断后面的步骤。</summary>
        public readonly string Error;

        public RevScopeStepResult(RevScopeStep step, int count, float elapsedMs, bool skipped, string note, string error)
        {
            Step = step;
            Count = count;
            ElapsedMs = elapsedMs;
            Skipped = skipped;
            Note = note;
            Error = error;
        }

        public bool Failed => Error != null;

        public string Name => RevScopeNames.StepName(Step);
    }

    /// <summary>域关闭的"体检报告"。<see cref="RevScope.LastCloseReport"/> 随时能取到最近一次。</summary>
    public sealed class RevScopeCloseReport
    {
        public string Domain;
        public float ElapsedMs;

        /// <summary>下标 = <see cref="RevScopeStep"/> 的数值（0~9）。</summary>
        public RevScopeStepResult[] Steps;

        /// <summary>各步骤回收数量之和（含扫尾）。</summary>
        public int TotalReclaimed;

        /// <summary>扫尾步骤抓到的"关闭过程中新长出来的注册"数量。★ 正常应当是 0；不是 0 说明有收尾代码在关闭期间又注册了东西。</summary>
        public int Stragglers;

        /// <summary>是否有任何一步出错。</summary>
        public bool HasFailure;

        /// <summary>整理成一段人话文本（可直接贴进日志 / 工单）。</summary>
        public string ToText()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("域「").Append(Domain).Append("」关闭报告：耗时 ").Append(ElapsedMs.ToString("F2"))
              .Append(" ms，回收 ").Append(TotalReclaimed).Append(" 个");
            sb.Append(HasFailure ? "（有步骤出错，见下）" : "（0 失败）");

            if (Steps == null) { return sb.ToString(); }

            for (int i = 0; i < Steps.Length; i++)
            {
                RevScopeStepResult r = Steps[i];
                sb.Append("\n  ").Append(i).Append(' ').Append(r.Name).Append("  ");

                if (r.Failed) { sb.Append("✗ 失败：").Append(r.Error); }
                else if (r.Skipped) { sb.Append("—（").Append(r.Note).Append('）'); }
                else { sb.Append(r.Count).Append(" 个  ").Append(r.ElapsedMs.ToString("F2")).Append(" ms"); }
            }

            return sb.ToString();
        }
    }

    /// <summary>名字表（只给报告 / 日志用）。</summary>
    internal static class RevScopeNames
    {
        internal static string StepName(RevScopeStep step)
        {
            switch (step)
            {
                case RevScopeStep.Gate: return "关域门";
                case RevScopeStep.Logic: return "业务单元 RevLogic";
                case RevScopeStep.Ui: return "界面 RevUI";
                case RevScopeStep.Mono: return "每帧回调 RevMono";
                case RevScopeStep.Timer: return "计时器 RevTimer";
                case RevScopeStep.Event: return "事件监听 RevEvent";
                case RevScopeStep.Pool: return "对象池 RevPool";
                case RevScopeStep.Res: return "资源 RevRes";
                case RevScopeStep.Services: return "服务 RevServices";
                case RevScopeStep.Sweep: return "兜底扫尾";
                default: return step.ToString();
            }
        }
    }

    /// <summary>作用域系统的几个固定数字（集中放这里，便于阅读与调整）。</summary>
    internal static class RevScopeLimits
    {
        /// <summary>
        /// 建议的最大嵌套深度。超过只是打一条提醒（不拒绝）：
        /// 实践中"全局 → 大厅 → 战斗"三层足够，更深的域树基本是设计出了问题。
        /// </summary>
        internal const int SuggestedMaxDepth = 3;

        /// <summary>步骤总数（0~9）。</summary>
        internal const int StepCount = 10;
    }
}

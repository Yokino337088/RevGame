// ============================================================
// RevScopeCore.cs —— 作用域内核：域栈 + 归属 + "关域回收编排"（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevScope\Implementation\
//
// 【这个文件回答三个问题】
//   ① 域是怎么排队的？      → 一个"栈"：Global → Lobby → Battle…，后进的在上面，只能自顶向下关。
//   ② 没写 owner 的注册归谁？ → 归"栈里最上面那个开着的域"（DefaultNode），见下面 ★ 说明。
//   ③ 关一个域要做什么？     → 9 个回收步骤，顺序钉死（见 RevScopeStep），每一步独立 try-catch。
//
// 【★ 为什么"没写 owner 的注册"能自动归域，还不用记账？】
//   各模块的门面（RevTimer / RevMono / RevEvent / RevResManager）在"调用方没写 owner / group"时，
//   会来问一句：RevScope.DefaultNode / DefaultGroup 是什么？
//     · 没有任何域开着 → 答案是 null / Unknown → 模块的行为和没有作用域系统时**逐字一致**（零破坏的根基）；
//     · 有域开着     → 答案是那个域 → 模块把这个域当成 owner 填进去。
//   域关闭时，只要对"域自己"调一遍 CancelAllOf / RemoveAllByOwner / RemoveAllOf，没主人的注册就全清了。
//   整个过程每次注册只多读一个静态字段，没有任何额外记账 —— 所以没有额外 GC。
//
// 【★ 关闭期间为什么 DefaultNode 要指向"上一层"】
//   关域的过程中，业务的收尾代码还在跑（RevLogic.OnStop、面板的 OnClose……），它们可能顺手打开结算面板、
//   起一个"3 秒后跳转"的计时器。这些东西是"下一阶段"的事，不该算在正在死掉的域头上，
//   否则结算面板刚弹出来就被当成"战斗域的东西"一起清掉了。
//   所以一进入关闭流程，DefaultNode / Current 立刻改指向栈里下一个还开着的域（或 null）。
//
// 【线程】只在主线程使用（和 Unity 一致）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Revolution
{
    /// <summary>作用域内核。业务不要直接用，请用门面 <see cref="RevScope"/>。</summary>
    internal sealed class RevScopeCore
    {
        /// <summary>某一步的"我不适用 / 我被跳过"信号（步骤函数返回它，而不是 0）。</summary>
        internal const int Skipped = -1;

        private readonly List<RevScopeHandle> _stack = new List<RevScopeHandle>(4);
        private readonly Func<RevScopeHandle, int>[] _steps = new Func<RevScopeHandle, int>[RevScopeLimits.StepCount];
        private readonly List<RevLogicBase> _tickBuffer = new List<RevLogicBase>(16);

        /// <summary>业务单元的运行表（RevLogic 门面直接用它）。</summary>
        internal readonly RevLogicHost Logics;

        /// <summary>栈里最上面那个"开着"的域（没有 = null）。这就是 RevScope.Current。</summary>
        internal RevScopeHandle Top;

        /// <summary>
        /// ★ 没写 owner 的注册该归谁。各模块门面每次注册都会读它（所以是普通字段，不是属性、不加锁）。
        /// null = 没有域，或当前域把 TakeOverOwner 关了 —— 此时所有模块行为与旧版逐字一致。
        /// </summary>
        internal RevScopeHandle DefaultNode;

        /// <summary>★ 没写资源分组时的兜底分组（跟随 DefaultNode；没有域时为 Unknown）。</summary>
        internal RevResGroup DefaultGroup;

        /// <summary>最近一次关域的报告。</summary>
        internal RevScopeCloseReport LastCloseReport;

        internal Action<string> Entered;
        internal Action<string> Exited;

        internal RevScopeCore()
        {
            Logics = new RevLogicHost(this);
        }

        internal int Depth => _stack.Count;

        // ==================== 步骤安装 ====================

        /// <summary>
        /// 装上"不需要 Unity"的回收步骤：业务单元 / 计时器 / 事件 / 服务。
        /// （界面 / 每帧回调 / 对象池 / 资源 这四步要用 Unity 类型，由 Support\RevScopeUnityHooks 补装。）
        /// </summary>
        internal void InstallPureSteps()
        {
            _steps[(int)RevScopeStep.Logic] = node => Logics.StopAllOf(node);
            _steps[(int)RevScopeStep.Timer] = node => SumOwners(node, RevTimer.CancelAllOf);
            _steps[(int)RevScopeStep.Event] = node => SumOwners(node, RevEvent.RemoveAllByOwner);
            _steps[(int)RevScopeStep.Services] = node => SumOwners(node, RevServices.UnregisterAllOf);
        }

        /// <summary>安装 / 替换某一步（Support 层用；工程外测试也用它塞假步骤来验证顺序）。</summary>
        internal void SetStep(RevScopeStep step, Func<RevScopeHandle, int> fn)
        {
            int index = (int)step;
            if (index <= (int)RevScopeStep.Gate || index >= (int)RevScopeStep.Sweep) { return; }

            _steps[index] = fn;
        }

        /// <summary>
        /// 对"域自己 + 它名下的每个归属对象"各调一遍清理函数，返回清理总数。
        /// ★ 每次调用独立 try-catch：某个对象的清理抛异常，不能让后面的对象漏清。
        /// </summary>
        internal static int SumOwners(RevScopeHandle node, Func<object, int> cleanup)
        {
            List<Exception> errors = null;
            int sum = 0;

            try { sum += cleanup(node); }
            catch (Exception e) { errors = errors ?? new List<Exception>(); errors.Add(e); }

            if (node.Owners.Count > 0)
            {
                object[] owners = node.SnapshotOwners();
                for (int i = 0; i < owners.Length; i++)
                {
                    try { sum += cleanup(owners[i]); }
                    catch (Exception e) { errors = errors ?? new List<Exception>(); errors.Add(e); }
                }
            }

            if (errors != null) { throw new AggregateException(errors); }

            return sum;
        }

        // ==================== 进入 ====================

        internal RevScopeHandle Enter(string domain, RevResGroup reclaim, bool takeOverOwner, bool reenterIsError, string note)
        {
            if (string.IsNullOrEmpty(domain))
            {
                RevLog.Error("RevScope.Enter 传入了空域名，已忽略。域名请用 RevDomain 常量或自己集中定义的常量（避免手写拼错）。", "Scope");
                return RevScopeHandle.Empty;
            }

            RevScopeHandle existing = Find(domain);
            if (existing != null)
            {
                if (existing.State == RevScopeState.Open && !reenterIsError) { return existing; }

                string why = existing.State == RevScopeState.Open
                    ? "域「" + domain + "」已经打开了。想复用请传 RevScopeOptions { ReenterIsError = false }；想重开请先 Exit。"
                    : "域「" + domain + "」正在关闭中，不能同时重新进入（等它关完，或换一个域名）。";
                RevLog.Error(why, "Scope");
                return RevScopeHandle.Empty;
            }

            RevScopeHandle node = new RevScopeHandle(this, domain, reclaim, takeOverOwner, note, Stopwatch.GetTimestamp());
            _stack.Add(node);
            Refresh();

            if (_stack.Count > RevScopeLimits.SuggestedMaxDepth)
            {
                RevLog.Warn("域嵌套已经 " + _stack.Count + " 层（" + DescribeStack() + "）。实践中三层足够，" +
                            "更深的域树多半是设计出了问题：确认每一层都真的需要独立回收。", "Scope");
            }

            Raise(Entered, domain, "Entered");
            return node;
        }

        // ==================== 查找 ====================

        /// <summary>按域名找（含"正在关闭"的）。没有返回 null。</summary>
        internal RevScopeHandle Find(string domain)
        {
            if (string.IsNullOrEmpty(domain)) { return null; }

            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_stack[i].Name, domain, StringComparison.Ordinal)) { return _stack[i]; }
            }

            return null;
        }

        /// <summary>重新算 Top / DefaultNode / DefaultGroup：栈里从上往下第一个"开着"的域。</summary>
        private void Refresh()
        {
            RevScopeHandle top = null;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].State != RevScopeState.Open) { continue; }

                top = _stack[i];
                break;
            }

            Top = top;
            DefaultNode = top != null && top.TakeOverOwner ? top : null;
            DefaultGroup = DefaultNode != null ? DefaultNode.Reclaim : RevResGroup.Unknown;
        }

        // ==================== 归属 ====================

        internal void AttachTo(RevScopeHandle node, object owner)
        {
            if (node == null || owner == null || ReferenceEquals(owner, node)) { return; }

            if (node.State != RevScopeState.Open)
            {
                RevLog.Error("往" + (node.State == RevScopeState.Closing ? "正在关闭" : "已关闭") + "的域「" + node.Name + "」登记归属对象 " +
                             owner.GetType().Name + "：已拒绝。这段代码在域结束时 / 结束后才跑；请先用 RevScope.IsActive(\"" + node.Name +
                             "\") 判断，或给这个对象显式写 owner、自己负责清理。", "Scope");
                return;
            }

            // 一个对象同一时刻只归一个域：重新登记 = 搬家（最近一次登记的域说了算）
            for (int i = 0; i < _stack.Count; i++)
            {
                if (!ReferenceEquals(_stack[i], node)) { _stack[i].Owners.Remove(owner); }
            }

            node.Owners.Add(owner);
        }

        /// <summary>登记到当前"默认域"；没有域就什么也不做（给界面系统这类"有域才登记"的调用方用）。</summary>
        internal void AttachSilently(object owner)
        {
            RevScopeHandle node = DefaultNode;
            if (node != null) { AttachTo(node, owner); }
        }

        internal int Detach(object owner)
        {
            if (owner == null) { return 0; }

            int removed = 0;
            for (int i = 0; i < _stack.Count; i++)
            {
                if (_stack[i].Owners.Remove(owner)) { removed++; }
            }

            return removed;
        }

        // ==================== 退出 ====================

        internal int Exit(string domain)
        {
            RevScopeHandle node = Find(domain);
            if (node == null)
            {
                RevLog.Warn("域「" + domain + "」当前没有打开（当前栈：" + DescribeStack() + "）。", "Scope");
                return 0;
            }

            return Exit(node);
        }

        internal int Exit(RevScopeHandle node)
        {
            // 重复 Exit / 空句柄 / 已经在关：都当成"没事可做"（using + 手动 Close 并存是很常见的写法）
            if (node == null || node.State != RevScopeState.Open) { return 0; }

            int index = _stack.IndexOf(node);
            if (index < 0) { return 0; }

            // ★ 域只能"自顶向下"关：node 上面还压着别的域时，先把它们按栈序关掉，
            //   否则会出现"子域活着、父域已经死了"的悬空状态（子域里的东西可能还在引用父域的东西）。
            if (index < _stack.Count - 1)
            {
                StringBuilder above = new StringBuilder();
                for (int i = _stack.Count - 1; i > index; i--)
                {
                    if (above.Length > 0) { above.Append('、'); }
                    above.Append('「').Append(_stack[i].Name).Append('」');
                }

                RevLog.Warn("关闭「" + node.Name + "」时，它上面的 " + above + " 也被一起关了（域只能自顶向下关）。" +
                            "如果这不是你想要的，说明 Exit 的顺序写反了。", "Scope");
            }

            int total = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                if (_stack.IndexOf(node) < 0) { break; }

                RevScopeHandle top = _stack[_stack.Count - 1];

                // 栈顶不是"开着"的：说明另一次关闭正在进行（有人在收尾代码里重入了 Exit），别去抢
                if (top.State != RevScopeState.Open) { break; }

                total += CloseNode(top);
                if (ReferenceEquals(top, node)) { break; }
            }

            return total;
        }

        internal int ExitAll()
        {
            int total = 0;
            for (int guard = 0; guard < 64 && _stack.Count > 0; guard++)
            {
                RevScopeHandle top = _stack[_stack.Count - 1];
                if (top.State != RevScopeState.Open) { break; }

                total += CloseNode(top);
            }

            return total;
        }

        // ==================== ★ 关闭一个域：9 个回收步骤 ====================

        private int CloseNode(RevScopeHandle node)
        {
            if (node.State != RevScopeState.Open) { return 0; }

            long startTicks = Stopwatch.GetTimestamp();
            RevScopeStepResult[] results = new RevScopeStepResult[RevScopeLimits.StepCount];
            int total = 0;
            bool failed = false;

            // ---- 步骤 0：关域门 ----
            // 标记 Closing，并立刻让 Current / DefaultNode 改指向"上一层"（见文件头★说明）：
            // 从这一刻起，收尾代码里新开的东西不再算在这个正在死掉的域头上。
            node.State = RevScopeState.Closing;
            Refresh();
            results[(int)RevScopeStep.Gate] = new RevScopeStepResult(RevScopeStep.Gate, 0, 0f, true, "标记关闭中，不再接新东西", null);

            // ---- 步骤 1~8：★ 顺序钉死，谁依赖谁，谁后关 ----
            for (int s = (int)RevScopeStep.Logic; s <= (int)RevScopeStep.Services; s++)
            {
                RevScopeStepResult r = RunStep((RevScopeStep)s, node);
                results[s] = r;

                if (r.Failed) { failed = true; }
                else if (!r.Skipped) { total += r.Count; }
            }

            // ---- 步骤 9：兜底扫尾 ----
            // 抓"漏网之鱼"：有人手里拿着域句柄、拿它当 owner 显式注册的（关闭过程中才注册的），
            // 只对"按 owner 清理"的几步（每帧 / 计时器 / 事件 / 服务）再扫一遍。
            RevScopeStepResult sweep = Sweep(node);
            results[(int)RevScopeStep.Sweep] = sweep;
            if (sweep.Failed) { failed = true; }
            else { total += sweep.Count; }

            // ---- 收尾：出栈、清空、重新计算当前域 ----
            node.State = RevScopeState.Closed;
            node.Owners.Clear();
            node.Logics.Clear();
            node.Tickers.Clear();
            _stack.Remove(node);
            Refresh();

            float elapsedMs = ToMs(Stopwatch.GetTimestamp() - startTicks);
            RevScopeCloseReport report = new RevScopeCloseReport
            {
                Domain = node.Name,
                ElapsedMs = elapsedMs,
                Steps = results,
                TotalReclaimed = total,
                Stragglers = sweep.Failed ? 0 : sweep.Count,
                HasFailure = failed,
            };
            LastCloseReport = report;

            if (failed) { RevLog.Error(report.ToText(), "Scope"); }
            else { RevLog.Debug(report.ToText(), "Scope"); }

            Raise(Exited, node.Name, "Exited");
            return total;
        }

        /// <summary>跑一步。★ 单独 try-catch：这一步出错只记录，绝不中断后面的步骤（否则"事件系统报错 → 资源永不释放"）。</summary>
        private RevScopeStepResult RunStep(RevScopeStep step, RevScopeHandle node)
        {
            Func<RevScopeHandle, int> fn = _steps[(int)step];
            if (fn == null)
            {
                return new RevScopeStepResult(step, 0, 0f, true, "这个环境里没装这个模块（不在 Unity 里运行，或模块未接入）", null);
            }

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                int count = fn(node);
                float ms = ToMs(Stopwatch.GetTimestamp() - t0);

                if (count == Skipped)
                {
                    string note = step == RevScopeStep.Pool || step == RevScopeStep.Res
                        ? "这个域没配置回收分组（Reclaim = Unknown），不按组回收"
                        : "本步不适用";
                    return new RevScopeStepResult(step, 0, ms, true, note, null);
                }

                return new RevScopeStepResult(step, count, ms, false, null, null);
            }
            catch (Exception e)
            {
                float ms = ToMs(Stopwatch.GetTimestamp() - t0);
                string text = e.GetType().Name + "：" + e.Message;
                RevLog.Exception(e, "关闭域「" + node.Name + "」第 " + (int)step + " 步（" + RevScopeNames.StepName(step) +
                                    "）失败，已跳过并继续后面的步骤。这一步可能有残留，请对照关闭报告排查", "Scope");
                return new RevScopeStepResult(step, 0, ms, false, null, text);
            }
        }

        private RevScopeStepResult Sweep(RevScopeHandle node)
        {
            long t0 = Stopwatch.GetTimestamp();
            int stragglers = 0;
            string error = null;

            RevScopeStep[] ownerBased = { RevScopeStep.Mono, RevScopeStep.Timer, RevScopeStep.Event, RevScopeStep.Services };
            for (int i = 0; i < ownerBased.Length; i++)
            {
                Func<RevScopeHandle, int> fn = _steps[(int)ownerBased[i]];
                if (fn == null) { continue; }

                try
                {
                    int count = fn(node);
                    if (count > 0) { stragglers += count; }
                }
                catch (Exception e)
                {
                    error = error ?? (e.GetType().Name + "：" + e.Message);
                }
            }

            float ms = ToMs(Stopwatch.GetTimestamp() - t0);
            if (error != null) { return new RevScopeStepResult(RevScopeStep.Sweep, 0, ms, false, null, error); }

            return new RevScopeStepResult(RevScopeStep.Sweep, stragglers, ms, false, null, null);
        }

        // ==================== 每帧开关 / 驱动 ====================

        internal void SetTickEnabled(string domain, bool enabled)
        {
            RevScopeHandle node = Find(domain);
            if (node == null)
            {
                RevLog.Warn("RevScope.SetTickEnabled：域「" + domain + "」当前没有打开（当前栈：" + DescribeStack() + "），已忽略。", "Scope");
                return;
            }

            node.TickOn = enabled;
        }

        /// <summary>
        /// 每帧驱动业务单元的 OnTick。★ 只驱动"开着、且没被暂停"的域里的 Logic（暂停 = 整个域的每帧逻辑都停）。
        /// 先把本帧要跑的收进缓冲再逐个调：OnTick 里可能 Stop 自己 / Exit 域，直接遍历原列表会被改乱。
        /// </summary>
        internal void TickLogics(float deltaTime)
        {
            _tickBuffer.Clear();

            for (int i = 0; i < _stack.Count; i++)
            {
                RevScopeHandle node = _stack[i];
                if (node.State != RevScopeState.Open || !node.TickOn || node.Tickers.Count == 0) { continue; }

                _tickBuffer.AddRange(node.Tickers);
            }

            Logics.CollectGlobalTickers(_tickBuffer);

            for (int i = 0; i < _tickBuffer.Count; i++)
            {
                RevLogicBase logic = _tickBuffer[i];
                if (!logic.IsRunning) { continue; }

                try
                {
                    logic.InvokeTick(deltaTime);
                }
                catch (Exception e)
                {
                    // 隔离：一个 Logic 的 OnTick 出错，不影响其它 Logic 继续被驱动
                    RevLog.Exception(e, "Logic " + logic.GetType().Name + ".OnTick 抛异常（已隔离，其余 Logic 照常）", "Scope");
                }
            }

            _tickBuffer.Clear();
        }

        // ==================== 复位 / 诊断 ====================

        /// <summary>
        /// 进 Play / 换工程时复位。★ 关闭 Domain Reload 时静态数据不会清空：上一局的域栈、业务单元、
        /// 事件订阅会一直活到下一局，表现为"我明明重开了，怎么还在跑上一局的 Logic"。
        /// 这里只做"安静地清掉"，不跑回收步骤、不调 OnStop —— 上一局的对象可能已经被销毁了，再去碰它们只会报错。
        /// </summary>
        internal void ResetForNewSession()
        {
            for (int i = 0; i < _stack.Count; i++)
            {
                RevScopeHandle node = _stack[i];
                node.State = RevScopeState.Closed;
                node.Owners.Clear();
                node.Logics.Clear();
                node.Tickers.Clear();
            }

            _stack.Clear();
            Logics.ResetForNewSession();
            LastCloseReport = null;
            Top = null;
            DefaultNode = null;
            DefaultGroup = RevResGroup.Unknown;

            // 订阅者往往是引用已销毁界面的闭包，不能带进下一局（和 RevMono 复位 Failed 事件同一个理由）
            Entered = null;
            Exited = null;
        }

        internal string DescribeStack()
        {
            if (_stack.Count == 0) { return "（空）"; }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _stack.Count; i++)
            {
                if (i > 0) { sb.Append(" → "); }
                sb.Append(_stack[i].Name);
                if (_stack[i].State != RevScopeState.Open) { sb.Append('(').Append(_stack[i].State).Append(')'); }
            }

            return sb.ToString();
        }

        /// <summary>把域栈打成一棵人话文本树：谁属于哪个域、是否暂停、挂了几个对象 / Logic、上次关闭的报告。</summary>
        internal string Dump()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("[RevScope] 域栈深度 ").Append(_stack.Count);
            sb.Append("，当前 = ").Append(Top != null ? Top.Name : "（无）").Append('\n');

            if (_stack.Count == 0) { sb.Append("  （没有任何域开着）\n"); }

            for (int i = 0; i < _stack.Count; i++)
            {
                RevScopeHandle node = _stack[i];
                bool last = i == _stack.Count - 1;
                string branch = last ? "└─ " : "├─ ";
                string pad = last ? "   " : "│  ";
                double age = (Stopwatch.GetTimestamp() - node.EnteredTicks) / (double)Stopwatch.Frequency;

                sb.Append(branch).Append(node.Name).Append("  [").Append(node.State).Append("]  进入于 ").Append(age.ToString("F1"))
                  .Append(" s 前  回收分组: ").Append(node.Reclaim == RevResGroup.Unknown ? "—" : node.Reclaim.ToString())
                  .Append("  每帧: ").Append(node.TickOn ? "开" : "★暂停")
                  .Append("  兜底归属: ").Append(node.TakeOverOwner ? "开" : "关").Append('\n');

                if (!string.IsNullOrEmpty(node.DebugNote)) { sb.Append(pad).Append("备注：").Append(node.DebugNote).Append('\n'); }

                if (node.Owners.Count > 0) { sb.Append(pad).Append("归属对象 ").Append(node.Owners.Count).Append(" 个：").Append(CountByType(node.Owners)).Append('\n'); }

                if (node.Logics.Count > 0)
                {
                    sb.Append(pad).Append("业务单元 ").Append(node.Logics.Count).Append(" 个：");
                    for (int j = 0; j < node.Logics.Count; j++)
                    {
                        if (j > 0) { sb.Append("、"); }
                        sb.Append(node.Logics[j].GetType().Name);
                        if (node.Tickers.Contains(node.Logics[j])) { sb.Append("(每帧)"); }
                    }

                    sb.Append('\n');
                }
            }

            sb.Append("说明：没写 owner 的计时器 / 事件 / 每帧回调归域自己，不逐个记账（所以注册零额外开销），这里不统计它们的个数。\n");

            sb.Append(LastCloseReport != null ? "上次关闭：\n" + LastCloseReport.ToText() : "上次关闭：（还没关过任何域）");
            return sb.ToString();
        }

        private static string CountByType(HashSet<object> owners)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (object owner in owners)
            {
                string name = owner.GetType().Name;
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (sb.Length > 0) { sb.Append("、"); }
                sb.Append(pair.Key).Append('×').Append(pair.Value);
            }

            return sb.ToString();
        }

        private static float ToMs(long ticks) => (float)(ticks * 1000.0 / Stopwatch.Frequency);

        /// <summary>广播事件：某个订阅者抛异常，绝不许影响域的进出流程（和 RevScene 的事件一个原则）。</summary>
        private static void Raise(Action<string> handlers, string domain, string what)
        {
            if (handlers == null) { return; }

            foreach (Action<string> subscriber in handlers.GetInvocationList())
            {
                try { subscriber(domain); }
                catch (Exception e) { RevLog.Exception(e, "RevScope." + what + " 的订阅者抛异常（已隔离）", "Scope"); }
            }
        }
    }
}

// ============================================================
// RevTimerCore.cs —— 计时器内核（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevTimer\Implementation\
//
// 【为什么可以不碰引擎】时间**只以参数形式**进来（Tick 收 deltaTime / unscaledDeltaTime /
//   realtimeSinceStartup），所以这个文件能在普通 .NET 工程里直接跑断言（本模块就是这么验的）；
//   读表、判到期、回调隔离、回收全部与引擎无关。
//
// 【每帧做四件事（王者那套的简化版，但每条都留着）】
//   ① 累加 GameTime / RealTime（框架自己给业务读的"从进游戏到现在过了多久"）；
//   ② 线性扫槽位：按时间域过滤 → 累积（或比绝对时刻）→ 到期就排进"待触发"清单；
//   ③ **先收集后执行**：清单收集完再依次回调 → 回调里 Stop/新建计时器都安全（旧框架在这里必炸：
//      边 foreach 边改字典 → InvalidOperationException → 还顺着 async 循环把整个计时器系统打死）；
//   ④ 统一回收：本帧结束集中回池（不在派发过程中改表）。
//
// 【★ 单帧触发上限 = 每个计时器一次】间隔小于帧长时（例如 0.01 秒的间隔），余量会**保留**，
//   下一帧继续补 —— 而不是 while 循环在一帧里连爆几十次把这一帧卡死。
//   代价是"超高频间隔"实际吞吐受帧率限制，收益是**永远不会出现回调风暴**（旧框架的
//   interval=0 风暴是踩过的坑：创建时忘传间隔 → 每 100ms 疯狂回调，还没人拦）。
//
// 【★ 异常隔离】单个回调抛异常 → 只影响它自己（后续计时器照跑），异常走注入的 OnException 出口 +
//   Failed(CallbackThrew) 事件；**不自动终止该计时器**（与王者一致：循环计时器出错后下轮继续触发，
//   但每一次都会被明确上报，不会像旧框架那样"异步循环 faulted 后整个系统静默死亡"）。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>计时器内核（纯 C#；门面 <see cref="RevTimer"/> 持有一个默认实例）。</summary>
    internal sealed class RevTimerCore
    {
        private readonly RevTimerTable _table = new RevTimerTable();
        private readonly RevPoolCore<RevTimerEntry> _pool;

        /// <summary>本帧到期的计时器（复用同一个 List → 稳态零分配）。</summary>
        private readonly List<RevTimerEntry> _due = new List<RevTimerEntry>(32);

        /// <summary>是否正在派发回调（决定"停止"是立刻回收还是本帧末尾统一回收）。</summary>
        private bool _inTick;

        /// <summary>
        /// 取条目的委托（构造时缓存一次）。
        /// ★ 不能写成 <c>_table.Rent(() =&gt; _pool.Get())</c>：那样每次创建计时器都会闭包分配一次，
        ///   "10 万次 Add/Remove 零 GC"这条验收就过不了。
        /// </summary>
        private readonly Func<RevTimerEntry> _createEntry;

        internal RevTimerCore()
        {
            _pool = new RevPoolCore<RevTimerEntry>(
                name: "RevTimer",
                capacity: RevTimerLimits.PoolCapacity,
                create: () => new RevTimerEntry(),
                isAlive: null,                                  // 纯 C# 对象恒"活着"
                onTake: null,
                onPut: e => e.ResetAll());                      // ★ 池的纪律：归还即彻底归零 + 解绑全部委托

            _createEntry = () => _pool.Get();
        }

        // ==================== 状态 / 出口 ====================

        /// <summary>全局暂停（切场景/打开设置面板时冻结整个模块；不是 timeScale）。</summary>
        internal bool Paused { get; set; }

        /// <summary>
        /// 渲染帧（Update）是否已被业务手动驱动。
        /// 以前只有一个 ManualDriven：业务只在自己的 FixedUpdate 里调 TickFixed，也会让宿主的 Update 让位，
        /// 结果 Scaled / Unscaled / Server 三个域全部停摆且没有任何提示。现在渲染帧与逻辑帧各用一个标记，互不牵连。
        /// </summary>
        internal bool ManualRenderDriven { get; private set; }

        /// <summary>逻辑帧（FixedUpdate）是否已被业务手动驱动（只影响 Fixed 域）。</summary>
        internal bool ManualFixedDriven { get; private set; }

        /// <summary>是否有任何一路被手动驱动接管（兼容旧属性；宿主驱动请分别看上面两个）。</summary>
        internal bool ManualDriven => ManualRenderDriven || ManualFixedDriven;

        /// <summary>
        /// 读取"此刻真实时间"的出口（由 Support 层接到 Time.realtimeSinceStartupAsDouble；纯 C# 单测时为 null）。
        /// 用途：游戏刚启动、还没 Tick 过时就校准服务器时间，锚点不能用初值 0，否则 Server 域会整体偏快。
        /// </summary>
        internal Func<double> RealtimeProvider;

        /// <summary>游戏时间/真实时间的累计读数（秒）—— 业务做节流/统计时可直接读。</summary>
        internal double GameTime { get; private set; }
        internal double RealTime { get; private set; }

        /// <summary>最近一次 Tick 收到的 realtime（给"剩余时间"读数用；最多差一帧）。</summary>
        internal double LastRealtime { get; private set; }

        /// <summary>
        /// 最新 realtime（★ 暂停时也更新）—— 给 <see cref="RevServerClock.Sync"/> 当锚点用。
        /// ★ Bug 修复（2026-09-30）新增：不能直接用 <see cref="LastRealtime"/> 做这件事 ——
        ///   全局暂停期间 Tick 不推进，LastRealtime 停在暂停前；若此刻校准服务器时间
        ///   （暂停的面板里收到服务器时间推送是常态），锚点会"少算暂停的时长"，
        ///   恢复后 Server 域整体快出暂停时长 → 所有 At(...) 计时器提前/延后触发。
        ///   它只是"最近一次见到的 realtime"读数，更新它不破坏"暂停冻结时间"的语义。
        /// </summary>
        internal double LatestRealtime { get; private set; }

        internal readonly RevServerClock Clock = new RevServerClock();

        /// <summary>日志出口（默认标准错误；由 Support 层的 Unity 钩子接管成 Debug）。</summary>
        internal Action<string> Log;
        internal Action<Exception, string> OnException;

        /// <summary>失败事件（门面转发出去）。</summary>
        internal Action<RevTimerErrorReason, string> Failed;

        /// <summary>当前存活的计时器数量（调试/自检用）。</summary>
        internal int Count => _table.AliveCount;

        /// <summary>
        /// 首次用到计时器时的回调 —— 由 Support 层装上（自动创建隐藏宿主）。
        /// ★ 内核**不知道**宿主是什么：纯 C# 部分只负责喊一声，Unity 相关的事一点不掺。
        /// </summary>
        internal static Action EnsureDriver;

        // ==================== 创建 ====================

        /// <summary>
        /// 取校准服务器时间用的"此刻 realtime"：优先问真实时钟，没有就退回最近一次 Tick 见到的值。
        /// 原因：LatestRealtime 只在 Tick 里更新，而第一个 Tick 要等到创建过计时器、宿主挂好之后才会发生；
        /// 登录时先收到服务器时间就校准，锚点会变成 (服务器时间, 0)，之后外推 = 服务器时间 + 当前 realtime，
        /// 等于凭空多算了"游戏启动到校准那一刻"的秒数，At 计时器会提前触发。
        /// </summary>
        internal double CurrentRealtime()
        {
            Func<double> provider = RealtimeProvider;
            if (provider != null)
            {
                double value = provider();
                if (!double.IsNaN(value) && !double.IsInfinity(value)) return value;
            }

            return LatestRealtime;
        }

        /// <summary>校准服务器时间（门面入口；锚点取真实 realtime，并顺手确保驱动已就位）。</summary>
        internal void SyncServerTime(DateTime serverUtc)
        {
            EnsureDriver?.Invoke();
            Clock.Sync(serverUtc, CurrentRealtime());
        }

        internal RevTimerHandle Add(RevTimeDomain domain, double duration, double interval, long repeats,
            bool absolute, DateTime targetUtc, Action callback, Action<int> repeatCallback, object owner)
        {
            // 先确保驱动存在，再占用槽位：如果创建宿主 GameObject 时抛异常，不会留下一个调用方拿不到句柄、
            // 却会照常运行的"幽灵计时器"（以前是占完槽位、写完字段才调用 EnsureDriver）。
            EnsureDriver?.Invoke();

            // 统一成 UTC：调用方传 DateTime.Now（Local）时，直接比较 Ticks 会差出整个时区偏移。
            if (targetUtc.Kind == DateTimeKind.Local) targetUtc = targetUtc.ToUniversalTime();

            if (callback == null && repeatCallback == null)
            {
                RaiseFailed(RevTimerErrorReason.InvalidDuration, "回调是空的（既没有普通回调也没有带次数回调）");
                return RevTimerHandle.Empty;
            }

            if (absolute)
            {
                if (!Clock.Synced)
                {
                    RaiseFailed(RevTimerErrorReason.NoServerTime,
                        "Server 域计时器需要先校准服务器时间：RevTimer.SyncServerTime(服务器UTC)");
                    return RevTimerHandle.Empty;
                }

                repeats = 1;                                  // 时刻式只支持一次（"到某个时刻"没有"每 N 秒"
            }
            else
            {
                if (!IsValidSeconds(duration))
                {
                    RaiseFailed(RevTimerErrorReason.InvalidDuration,
                        $"时长非法：{duration}（必须 >= 0 且有限；After(0) 就是下一帧）");
                    return RevTimerHandle.Empty;
                }

                if (interval > 0d && !IsValidSeconds(interval))
                {
                    RaiseFailed(RevTimerErrorReason.InvalidDuration, $"间隔非法：{interval}");
                    return RevTimerHandle.Empty;
                }

                // ★ 循环计时器必须给正间隔：旧框架的 interval 默认 0 → "每 100ms 回调一次"的风暴就是这么来的。
                //   一次性（repeats == 1）允许 interval == 0，那就是 After / NextFrame。
                if (interval <= 0d && repeats != 1)
                {
                    RaiseFailed(RevTimerErrorReason.InvalidDuration,
                        $"循环计时器必须给正的间隔（收到 interval={interval}）：要一次请用 After，要无限请传 times: -1");
                    return RevTimerHandle.Empty;
                }

                if (repeats == 0)
                {
                    RaiseFailed(RevTimerErrorReason.InvalidDuration, "次数是 0（要无限次请传 -1，要一次请用 After）");
                    return RevTimerHandle.Empty;
                }

                // 文档只约定 -1 表示无限；-5 这类其他负数多半是计算出错，悄悄当成"无限循环"会造成永久泄漏，所以拒绝。
                if (repeats < -1)
                {
                    RaiseFailed(RevTimerErrorReason.InvalidDuration, $"次数非法：{repeats}（只有 -1 表示无限）");
                    return RevTimerHandle.Empty;
                }
            }

            if (_table.AliveCount >= RevTimerLimits.MaxTimers)
            {
                RaiseFailed(RevTimerErrorReason.Overflow,
                    $"计时器数量已达上限 {RevTimerLimits.MaxTimers}：本次创建被拒绝。" +
                    "常见原因：循环计时器忘了 Stop，或随对象销毁的计时器没用 owner/CancelAllOf 清理。");
                return RevTimerHandle.Empty;
            }

            int slot = _table.Rent(_createEntry, out int generation);
            if (slot < 0)
            {
                RaiseFailed(RevTimerErrorReason.Overflow, "没有空闲槽位（并发修改？）");
                return RevTimerHandle.Empty;
            }

            RevTimerEntry entry = _table.At(slot);
            if (entry == null)
            {
                // 池没能给出对象（理论不可达：本模块的条目就是 new 出来的）→ 回收槽位，别留半截状态
                _table.Release(slot);
                _table.Detach(slot);
                RaiseFailed(RevTimerErrorReason.Overflow, "计时器条目取不出来（内部状态异常）");
                return RevTimerHandle.Empty;
            }

            entry.Domain = domain;
            entry.Absolute = absolute;
            entry.TargetUtc = targetUtc;
            entry.Duration = absolute ? 0d : duration;
            entry.Interval = absolute ? 0d : interval;
            entry.RepeatsLeft = repeats;
            entry.InitialRepeats = repeats;
            entry.Callback = callback;
            entry.RepeatCallback = repeatCallback;
            entry.Owner = owner;

            return new RevTimerHandle(this, slot, generation);
        }

        private static bool IsValidSeconds(double seconds)
            => !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0d;

        // ==================== 驱动（两个入口，对齐王者的 Update / UpdateLogic）====================

        /// <summary>渲染帧驱动：推进 Scaled / Unscaled / Server 三个域（宿主每帧调）。</summary>
        internal void Tick(float scaledDelta, float unscaledDelta, double realtimeSinceStartup)
        {
            // ★ Bug 修复（2026-09-30）：realtime 读数必须在暂停判断**之前**记录 ——
            //   LatestRealtime 永远保持最新（暂停期间 SyncServerTime 的锚点靠它才不会陈旧）；
            //   LastRealtime 只在未暂停时更新（保持"暂停冻结剩余时间读数"的原有语义不变）。
            // NaN / Infinity 不是正常时间：一旦混进累计值，GameTime 和所有 Elapsed 会永久变成 NaN，
            // 而 NaN >= Duration 恒为 false，所有计时器就再也不会触发。所以非有限的 realtime 不记录。
            if (!double.IsNaN(realtimeSinceStartup) && !double.IsInfinity(realtimeSinceStartup))
                LatestRealtime = realtimeSinceStartup;
            else
                realtimeSinceStartup = LatestRealtime;

            if (Paused) return;                                  // 全局暂停：整块冻结（含 Server，见 README"暂停语义"）

            // 回调里再调用 Tick（重入）会清掉外层正在遍历的待触发清单，外层剩下的到期项就丢了，
            // 还会提前把 _inTick 改回 false。重入的这一次直接忽略，外层本来就在推进这一帧。
            if (_inTick) return;

            // "!(x >= 0)" 同时挡住负数和 NaN；Infinity 单独判断。
            if (!(scaledDelta >= 0f) || float.IsInfinity(scaledDelta)) scaledDelta = 0f;
            if (!(unscaledDelta >= 0f) || float.IsInfinity(unscaledDelta)) unscaledDelta = 0f;

            GameTime += scaledDelta;
            RealTime += unscaledDelta;
            LastRealtime = realtimeSinceStartup;

            _inTick = true;
            try
            {
                // 每个域各自保护：前一个域派发时若有异常冒出来，后面的域仍会被推进，
                // 否则这一帧的时间会永久少算，且 _inTick 会卡在 true。
                try { AdvanceAccumulated(scaledDelta, RevTimeDomain.Scaled); }
                catch (Exception e) { ReportInternalError(e, "Scaled"); }

                try { AdvanceAccumulated(unscaledDelta, RevTimeDomain.Unscaled); }
                catch (Exception e) { ReportInternalError(e, "Unscaled"); }

                try { AdvanceAbsolute(realtimeSinceStartup); }
                catch (Exception e) { ReportInternalError(e, "Server"); }
            }
            finally
            {
                _inTick = false;
            }
        }

        /// <summary>逻辑帧驱动：只推进 Fixed 域（对齐王者的 UpdateLogic —— 60fps 与 30fps 手机的 CD 推进一致）。</summary>
        internal void TickFixed(float fixedDelta)
        {
            if (Paused) return;
            if (_inTick) return;                                  // 同 Tick：回调里重入会破坏外层派发，忽略

            if (!(fixedDelta >= 0f) || float.IsInfinity(fixedDelta)) fixedDelta = 0f;   // 负数 / NaN / Infinity 都当 0

            _inTick = true;
            try
            {
                AdvanceAccumulated(fixedDelta, RevTimeDomain.Fixed);
            }
            catch (Exception e)
            {
                ReportInternalError(e, "Fixed");
            }
            finally
            {
                _inTick = false;
            }
        }

        /// <summary>手动驱动渲染帧（门面转发）：接管后宿主的 Update 让位，避免"同一帧推进两次"。只影响渲染帧三个域。</summary>
        internal void TickManual(float scaledDelta, float unscaledDelta, double realtimeSinceStartup)
        {
            ManualRenderDriven = true;
            Tick(scaledDelta, unscaledDelta, realtimeSinceStartup);
        }

        /// <summary>手动驱动逻辑帧：只让宿主的 FixedUpdate 让位，Update 仍由宿主负责（不再牵连其他域）。</summary>
        internal void TickFixedManual(float fixedDelta)
        {
            ManualFixedDriven = true;
            TickFixed(fixedDelta);
        }

        // ==================== 推进 ====================

        /// <summary>累积式（"从此刻起 X 秒"）：加 delta → 比总时长。</summary>
        private void AdvanceAccumulated(float delta, RevTimeDomain domain)
        {
            RevTimerEntry[] slots = _table.Slots;
            _due.Clear();

            for (int i = 0; i < slots.Length; i++)
            {
                RevTimerEntry e = slots[i];
                if (e == null || !e.Alive || e.Paused || e.Absolute || e.Domain != domain) continue;

                e.Elapsed += delta;

                // ★ 每帧最多触发一次：不够的部分留在 Elapsed 里，下一帧继续（不 while 连爆）
                if (e.Elapsed >= e.Duration) Schedule(e);
            }

            DispatchThenRecycle();
        }

        /// <summary>时刻式（"到某个服务器绝对时刻"）：比 UTC，和 delta 无关 → 天然免疫卡顿/挂起。</summary>
        private void AdvanceAbsolute(double realtimeSinceStartup)
        {
            RevTimerEntry[] slots = _table.Slots;
            _due.Clear();

            if (Clock.TryNowSeconds(realtimeSinceStartup, out double now))
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    RevTimerEntry e = slots[i];
                    if (e == null || !e.Alive || e.Paused || !e.Absolute) continue;

                    double target = e.TargetUtc.Ticks / (double)TimeSpan.TicksPerSecond;
                    if (now >= target) Schedule(e);
                }
            }

            DispatchThenRecycle();
        }

        /// <summary>排入待触发清单，并算好"要不要续期 / 什么时候回收"（此刻算，回调里 Stop 也不影响）。</summary>
        private void Schedule(RevTimerEntry entry)
        {
            entry.Due = true;
            _due.Add(entry);

            if (entry.Interval > 0d)
            {
                // ★ 余量结转（不是归零）：连续循环不会因帧长抖动而越走越偏
                entry.Elapsed -= entry.Duration;
                if (entry.Elapsed < 0d) entry.Elapsed = 0d;

                if (entry.RepeatsLeft > 0)
                {
                    entry.RepeatsLeft--;
                    if (entry.RepeatsLeft == 0) entry.PendingFree = true;
                }
            }
            else
            {
                entry.PendingFree = true;                        // 一次性：触发完就回收
            }
        }

        /// <summary>先派发、再统一回收（派发期间绝不动表 → 回调里增删都安全）。</summary>
        private void DispatchThenRecycle()
        {
            for (int i = 0; i < _due.Count; i++)
            {
                RevTimerEntry e = _due[i];
                if (!e.Alive) continue;                          // 已被前面的回调停掉 → 跳过

                Fire(e);
            }

            _due.Clear();

            RevTimerEntry[] slots = _table.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                RevTimerEntry e = slots[i];
                if (e == null || !e.PendingFree) continue;

                FreeSlot(i);
            }
        }

        private void Fire(RevTimerEntry entry)
        {
            entry.FiredCount++;

            try
            {
                if (entry.RepeatCallback != null) entry.RepeatCallback((int)entry.FiredCount);
                else entry.Callback?.Invoke();
            }
            catch (Exception e)
            {
                // 上报环节本身也可能出错（订阅者或日志出口抛异常）。如果不再包一层，
                // 异常会冒出派发循环：本帧后面的到期计时器不会执行，而一次性计时器已被标记待回收，会被悄悄丢掉。
                try
                {
                    string what = Describe(entry);
                    RaiseFailed(RevTimerErrorReason.CallbackThrew, what);
                    OnException?.Invoke(e,
                        $"[RevTimer] 回调抛异常（{what}）。已隔离：其余计时器照常推进；" +
                        "该计时器不会被自动终止（循环计时器下一轮仍会触发，但每次都会报到 OnException）。");
                }
                catch
                {
                    // 上报失败就放弃上报，不能让它影响其他计时器。
                }
            }
        }

        /// <summary>内核自身的意外（例如服务器时间外推溢出）：只记录，不让它打断后续域的推进。</summary>
        private void ReportInternalError(Exception e, string where)
        {
            try
            {
                OnException?.Invoke(e, $"[RevTimer] 推进 {where} 域时发生内部异常，已隔离（其他域继续推进）。");
            }
            catch
            {
                // 出口自己出错也只能忽略。
            }
        }

        // ==================== 句柄操作（过期句柄一律安全空操作）====================

        internal bool IsAlive(int slot, int generation) => _table.IsValid(slot, generation);

        internal void Stop(int slot, int generation)
        {
            if (!_table.IsValid(slot, generation)) return;

            RevTimerEntry entry = _table.At(slot);
            _table.Release(slot);                                // 立刻失效：句柄的 IsAlive 马上变 false（计数也一并减掉）

            // ★ 派发中不回收（否则条目会被同一帧的新计时器复用，而它还在待触发清单里）
            if (_inTick) entry.PendingFree = true;
            else FreeSlot(slot);
        }

        internal void SetPaused(int slot, int generation, bool paused)
        {
            if (!_table.IsValid(slot, generation)) return;

            _table.At(slot).Paused = paused;
        }

        internal void Restart(int slot, int generation)
        {
            if (!_table.IsValid(slot, generation)) return;

            RevTimerEntry entry = _table.At(slot);
            entry.Elapsed = 0d;
            entry.FiredCount = 0;
            entry.RepeatsLeft = entry.InitialRepeats;
            entry.Paused = false;

            // ★ Bug 修复（2026-09-30）：必须撤销"本帧待回收"标记。
            //   到期回调（Fire）执行时条目还没被回收（Alive 仍为 true，上面的 IsValid 能通过），
            //   但 Schedule 早已把它标成 PendingFree —— 不撤销的话，本帧末尾的回收循环
            //   会把它整个 FreeSlot：调用方明明想"重新计时"，结果计时器当场死亡、句柄随即失效。
            //   典型触发：After(2s, () => { if(需要重试) handle.Restart(); })。
            entry.PendingFree = false;
        }

        internal double LeftOf(int slot, int generation)
        {
            if (!_table.IsValid(slot, generation)) return 0d;

            RevTimerEntry entry = _table.At(slot);
            double now = Clock.TryNowSeconds(LastRealtime, out double utc) ? utc : 0d;
            return entry.Left(now);
        }

        internal double ProgressOf(int slot, int generation)
            => _table.IsValid(slot, generation) ? _table.At(slot).Progress : 1d;

        // ==================== 批量 ====================

        /// <summary>
        /// 停掉某个 owner 的全部计时器（一行防泄漏：随对象销毁时调它）。
        /// ★ owner 是**显式参数**，不像王者那样反射委托的 Target —— 闭包/静态方法/多委托都不会漏。
        /// </summary>
        internal int CancelAllOf(object owner)
        {
            if (owner == null) return 0;                          // null 会被理解成"所有 owner" → 太危险，直接拒绝

            int count = 0;
            RevTimerEntry[] slots = _table.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                RevTimerEntry e = slots[i];
                if (e == null || !e.Alive) continue;
                if (!ReferenceEquals(e.Owner, owner)) continue;

                Stop(i, e.Generation);
                count++;
            }

            return count;
        }

        /// <summary>清空全部计时器（切场景/重开一局用）。返回停掉的数量。</summary>
        internal int ClearAll()
        {
            int count = _table.AliveCount;
            RevTimerEntry[] slots = _table.Slots;

            for (int i = 0; i < slots.Length; i++)
            {
                RevTimerEntry e = slots[i];
                if (e == null || !e.Alive) continue;

                Stop(i, e.Generation);
            }

            return count;
        }

        /// <summary>起一个**已经在跑**的秒表（名字说的就是"Start"，返回后直接读 Elapsed 就有数）。</summary>
        internal RevStopwatch StartStopwatch(RevTimeDomain domain)
        {
            var stopwatch = new RevStopwatch(this, domain);
            stopwatch.Start();
            return stopwatch;
        }

        /// <summary>
        /// 进 Play / 换工程时复位（Support 层的钩子调）：清空计时器、清服务器锚点、时间读数归零。
        /// ★ 关掉 Domain Reload 时不复位 → 上一局的计时器会继续跑（"鬼故事"）。
        /// </summary>
        internal void ResetForNewSession()
        {
            _inTick = false;
            ClearAll();
            Clock.Reset();
            // 关闭 Domain Reload 时静态内核会跨 Play 保留：上一局订阅 Failed 的处理器（常捕获了已销毁的 UI）
            // 在新一局仍会收到事件。业务在本局初始化时可以重新订阅，所以这里清空是安全的。
            Failed = null;
            GameTime = 0d;
            RealTime = 0d;
            LastRealtime = 0d;
            LatestRealtime = 0d;                                 // ★ 与 LastRealtime 一同归零（2026-09-30 随 Bug 修复新增）
            Paused = false;
            ManualRenderDriven = false;
            ManualFixedDriven = false;
        }

        // ==================== 内部小工具 ====================

        private void FreeSlot(int slot)
        {
            RevTimerEntry entry = _table.At(slot);
            if (entry == null) return;

            _table.Release(slot);                                // ★ 先减存活计数（Release 对已释放的是幂等空操作）
            _table.Detach(slot);                                 // 再摘走条目（回池）
            entry.PendingFree = false;
            _pool.Return(entry);                                 // 池的 onPut 里彻底归零 + 解绑
        }

        private string Describe(RevTimerEntry entry)
        {
            string owner = entry.Owner == null ? "无 owner" : entry.Owner.GetType().Name;
            string mode = entry.Absolute
                ? $"到 {entry.TargetUtc:HH:mm:ss}（服务器时刻）"
                : (entry.Interval > 0d ? $"每 {entry.Interval:F3}s（剩 {(entry.RepeatsLeft < 0 ? "无限" : entry.RepeatsLeft.ToString())} 次）"
                                       : $"{entry.Duration:F3}s 一次");

            return $"域={entry.Domain} · {mode} · owner={owner}";
        }

        private void RaiseFailed(RevTimerErrorReason reason, string detail)
        {
            // Failed 可能有多个订阅者：逐个调用并各自捕获异常，一个写错的订阅者不能让别人收不到通知，
            // 更不能把异常抛回创建计时器的调用方或派发循环。
            Action<RevTimerErrorReason, string> handlers = Failed;
            if (handlers != null)
            {
                foreach (Action<RevTimerErrorReason, string> handler in handlers.GetInvocationList())
                {
                    try { handler(reason, detail); }
                    catch (Exception e) { RevLog.Exception(e, "RevTimer.Failed 订阅者异常", "Timer"); }
                }
            }

            string text = $"[RevTimer] {reason}：{detail}";
            try
            {
                if (Log != null) Log(text);
                else RevLog.Warn(text, "Timer");       // 没被接管时也绝不静默（以前这里是"什么都不发生"）
            }
            catch (Exception e)
            {
                RevLog.Exception(e, "RevTimer 日志出口异常", "Timer");
            }
        }
    }
}

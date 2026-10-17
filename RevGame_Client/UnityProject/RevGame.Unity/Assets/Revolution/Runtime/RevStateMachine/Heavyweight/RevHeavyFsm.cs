// ============================================================
// RevHeavyFSM.cs —— 重量级状态机（泛型状态枚举 + 宿主行为接口）
//
// 位置：Runtime\RevStateMachine\Heavyweight\
//
// 【它是什么】
//   原框架（唐老师框架）StateMachine<TStateType, TFSMObj> 的全面重构。
//   保留"状态枚举 + 行为接口"的泛型设计，砍掉臃肿部分，吸收王者四套状态机的设计哲学。
//   面向：战斗场景的怪物 AI / Boss AI，以及步骤多、要严格时序的重量级业务。
//
// 【保留了原框架的什么（骨架）】
//   · 泛型双参数：TStateEnum 状态枚举 + TOwner 宿主行为接口（RevIHeavyFSMOwner）；
//   · 状态注册表（枚举 → 状态实例）、ChangeState 切换、Update/FixedUpdate/LateUpdate 三路驱动；
//   · IsEnabled 开关、状态历史、Reset、Dispose、GetState / HasState / RemoveState。
//
// 【砍掉了什么（臃肿部分）】
//   · 分层状态机（HierarchicalStateMachine / ParentState / ChildStateMachine / GetRootStateMachine）
//     —— 需要嵌套请"组合多台 RevHeavyFSM"（如 Boss 一台管阶段、技能一台管吟唱），而不是状态里套状态机；
//   · Activator 反射创建状态 —— 改为显式 new（IL2CPP/AOT 安全、无隐藏开销、无魔数构造约定）；
//   · 每个 public 方法都包 try/catch + 每步打 Info 日志 —— 改为只在"状态回调边界"做异常隔离，
//     诊断日志 [Conditional("UNITY_EDITOR")]（正式包零开销）；
//   · 状态历史只存枚举 —— 改为带"原因 + 时刻"的结构体记录（见下）。
//
// 【吸收了王者的什么】
//   · 事务式异步切换（④ HLOD FSM<T>）：ChangeStateAsync 先 await 旧状态 PrepareExitAsync，
//     再 await 新状态 PrepareEnterAsync，双方都准备完才交割 —— 切换可跨任意多帧，
//     绝不会出现"资源还没到就切过去了"的半切换；快速连切只保留最新目标（旧请求被 Cancel，
//     对应王者④"跳过中间态"）；同步的 ChangeState 则用于"必须马上切"的场合；
//   · 异步用本框架的 RevTask 表达（状态里直接 await 资源加载 / 延迟 / 动画完成），
//     调度交给 RevTaskScheduler，不再自己造一套轮询式就绪检查；
//   · 状态计时（②③）：StateTime，交割时清零；
//   · 防重入（②③）：回调里再切状态 → DEBUG 抛异常，Release 记 Error 并拒绝；
//   · 切换原因 reason（⑤实战痛点）：ChangeState(target, reason)，历史与事件都带它 ——
//     王者 2024 年才补上的能力："AI 为什么突然切走了"有地方查；
//   · 事件解耦（⑤）：StateChanged 事件，其他系统订阅响应，而不是状态机主动去找它们；
//   · 异常隔离（与本框架 UI 系统同款哲学）：DEBUG 让异常直接抛（尽早发现），
//     Release 捕获记 Error（真机上一个状态的 bug 不拖垮整台状态机）。
//
// 【切换的两条路 —— 按需选一个】
//   ChangeState(type, reason)        同步、立即交割。不等异步准备；在途的异步切换会被取消。
//                                    适合 AI 的常规状态跳转（追击→攻击→脱战）。
//   ChangeStateAsync(type, reason)   事务式、异步交割（返回 RevTask）。
//                                    适合"技能吟唱结束才切""Boss 登场资源加载完才进二阶段"。
//
// 【异步切换的三条规则】
//   ① 只保留最新目标：在途时再发起一次，旧的立刻作废（token 被 Cancel，业务可中断在途加载）；
//   ② 交割前当前（旧）状态仍是激活状态：继续被 Update 驱动，AI 不会"卡在中间"；
//   ③ await 之后用 CurrentStateType / IsTransitioning 确认是否真的切过去了 ——
//      被取代或被 CancelAsyncTransition() 取消时，当前状态保持不变、任务正常完成（不抛异常）。
//
// 【事件顺序（两种切换完全一致）】
//   旧状态.OnExit → 换当前状态、计时清零 → 新状态.OnEnter → 记历史 → 广播 StateChanged
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 一次状态切换的历史记录（调试用："它是怎么走到现在这步的"）。
    /// </summary>
    /// <typeparam name="TStateEnum">状态枚举</typeparam>
    public readonly struct RevHeavyFSMTransition<TStateEnum>
    {
        /// <summary>从哪个状态切出（第一个状态时为空值）</summary>
        public readonly TStateEnum From;

        /// <summary>切到哪个状态</summary>
        public readonly TStateEnum To;

        /// <summary>为什么切（ChangeState 的 reason 参数）</summary>
        public readonly string Reason;

        /// <summary>切换发生的时刻（Time.time）</summary>
        public readonly float Time;

        public RevHeavyFSMTransition(TStateEnum from, TStateEnum to, string reason)
        {
            From = from;
            To = to;
            Reason = reason ?? string.Empty;
            Time = UnityEngine.Time.time;
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Reason) ? $"{From} → {To}" : $"{From} → {To}（{Reason}）";
        }
    }

    /// <summary>
    /// 重量级状态机的日志出口（跟随框架 <c>RevPoolLog</c> 的做法：
    /// 出口可被业务接管；诊断日志 <c>[Conditional("UNITY_EDITOR")]</c>，正式包里连字符串拼接都被编译器移除）。
    /// </summary>
    public static class RevHeavyFSMLog
    {
        /// <summary>日志出口（业务可替换成自己的日志系统；为空时走 UnityEngine.Debug）</summary>
        public static Action<string> Sink;

        /// <summary>告警（用法可疑但还能跑：重复注册、移除当前状态…）</summary>
        public static void Warning(string message) => Write("[RevHeavyFSM] " + message, isError: false);

        /// <summary>错误（用法错误，必须修：状态未注册、回调里切状态…）</summary>
        public static void Error(string message) => Write("[RevHeavyFSM][错误] " + message, isError: true);

        /// <summary>
        /// 诊断日志（注册、切换流水）：<c>[Conditional("UNITY_EDITOR")]</c> 让它在正式包里被编译器<b>完全移除</b>
        /// （连参数求值都不发生）—— 王者"调试代码零开销"的做法，不需要运行时开关。
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        public static void Debug(string message) => Write("[RevHeavyFSM][诊断] " + message, isError: false);

        private static void Write(string message, bool isError)
        {
            Action<string> sink = Sink;
            if (sink != null)
            {
                try { sink(message); return; }
                catch (Exception e) { RevLog.Exception(e, "状态机日志通道异常", "StateMachine"); }
            }

            // 没被业务接管时走框架日志系统（以前是直接 Debug —— 业务改不了、也不算"统一出口"）
            if (isError) RevLog.Error(message, "StateMachine");
            else RevLog.Warn(message, "StateMachine");
        }
    }

    /// <summary>
    /// 重量级状态机：泛型状态枚举 + 宿主行为接口。
    /// <para>典型用法（怪物 AI / Boss AI）：</para>
    /// <code>
    /// var fsm = new RevHeavyFSM&lt;BossStateType, IBossObj&gt;(this);
    /// fsm.AddState(BossStateType.Idle,   new BossIdleState(fsm));
    /// fsm.AddState(BossStateType.Combat, new BossCombatState(fsm));
    /// fsm.ChangeState(BossStateType.Idle, reason: "初始化");
    ///
    /// await fsm.ChangeStateAsync(BossStateType.Phase2, "血量低于 50%");   // 等资源就绪再进二阶段
    ///
    /// void Update()      =&gt; fsm.UpdateState(Time.deltaTime);
    /// void FixedUpdate() =&gt; fsm.FixedUpdateState(Time.fixedDeltaTime);
    /// void LateUpdate()  =&gt; fsm.LateUpdateState(Time.deltaTime);
    /// </code>
    /// </summary>
    /// <typeparam name="TStateEnum">状态枚举（值类型，查询/比较无 GC）</typeparam>
    /// <typeparam name="TOwner">宿主行为接口</typeparam>
    public class RevHeavyFSM<TStateEnum, TOwner>
        where TStateEnum : struct, Enum
        where TOwner : class, RevIHeavyFSMOwner
    {
        // ── 注册表（"仓库"）与当前状态（"现场"）──────────────────
        private readonly Dictionary<TStateEnum, RevHeavyFSMState<TStateEnum, TOwner>> _states =
            new Dictionary<TStateEnum, RevHeavyFSMState<TStateEnum, TOwner>>();

        private RevHeavyFSMState<TStateEnum, TOwner> _current;  // 当前状态（异步交割前仍是旧状态）
        private RevHeavyFSMState<TStateEnum, TOwner> _pending;  // 在途异步切换的目标（null = 没有事务在进行）
        private float _stateTime;                               // 进入当前状态的累计秒数
        private bool _duringTransition;                         // 防重入标记
        private int _version;                                   // 切换代次：不匹配的在途切换一律作废
        private RevCancellationTokenSource _asyncCts;           // 在途异步切换的取消源

        // ── 历史 ───────────────────────────────────────────────
        private readonly Queue<RevHeavyFSMTransition<TStateEnum>> _history = new Queue<RevHeavyFSMTransition<TStateEnum>>();
        private int _maxHistorySize = 10;

        /// <summary>宿主对象（AI 本体）</summary>
        public TOwner Owner { get; private set; }

        /// <summary>状态机是否启用（禁用后切换与驱动都被忽略）</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>当前状态实例（未切换过为 null；异步交割前是旧状态）</summary>
        public RevHeavyFSMState<TStateEnum, TOwner> CurrentState => _current;

        /// <summary>当前状态枚举（未切换过为 default）</summary>
        public TStateEnum CurrentStateType { get; private set; }

        /// <summary>上一个状态枚举</summary>
        public TStateEnum PrevStateType { get; private set; }

        /// <summary>是否已有当前状态（第一次 ChangeState 之前为 false）</summary>
        public bool HasCurrentState => _current != null;

        /// <summary>是否有异步切换在进行中（正在 await 双方的 Prepare）</summary>
        public bool IsTransitioning { get; private set; }

        /// <summary>
        /// 在途异步切换的目标枚举（仅 <see cref="IsTransitioning"/> 时有意义）。
        /// 可做"异常退出分支判断"（对应王者 tarState 的思路：离开时知道"本来要去哪"）。
        /// </summary>
        public TStateEnum? PendingStateType => _pending != null ? _pending.StateType : (TStateEnum?)null;

        /// <summary>进入当前状态多久了（秒）</summary>
        public float StateTime => _stateTime;

        /// <summary>已注册状态的只读视图（枚举 → 状态实例）</summary>
        public IReadOnlyDictionary<TStateEnum, RevHeavyFSMState<TStateEnum, TOwner>> States => _states;

        /// <summary>状态变化事件（旧枚举, 新枚举, 原因）—— 在新状态 OnEnter 之后触发，供外部系统订阅解耦</summary>
        public event Action<TStateEnum, TStateEnum, string> StateChanged;

        public RevHeavyFSM(TOwner owner)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner), "RevHeavyFSM 的宿主对象不能为空");
        }

        // ============================================================
        // 注册 / 查询 / 移除
        // ============================================================

        /// <summary>
        /// 注册状态（显式实例化 —— 刻意不做 Activator 反射创建：IL2CPP/AOT 安全、无隐藏开销）。
        /// <code>fsm.AddState(BossStateType.Idle, new BossIdleState(fsm));</code>
        /// </summary>
        public void AddState(TStateEnum type, RevHeavyFSMState<TStateEnum, TOwner> state)
        {
            if (state == null)
            {
                RevHeavyFSMLog.Error($"注册状态 {type} 失败：实例为 null");
                return;
            }
            if (_states.ContainsKey(type))
            {
                RevHeavyFSMLog.Warning($"状态 {type} 已注册过，忽略重复注册");
                return;
            }
            if (!ReferenceEquals(state.Machine, this))
            {
                RevHeavyFSMLog.Error($"状态 {state.GetType().Name} 属于另一台状态机，不能注册到本机");
                return;
            }
            if (!EqualityComparer<TStateEnum>.Default.Equals(state.StateType, type))
            {
                RevHeavyFSMLog.Warning($"状态 {state.GetType().Name}.StateType = {state.StateType}，与注册键 {type} 不一致（以注册键为准）");
            }

            _states.Add(type, state);
            RevHeavyFSMLog.Debug($"注册状态 {type}（{state.GetType().Name}）");
        }

        /// <summary>移除状态（不能移除当前正在使用的状态 —— 先切走再移除）</summary>
        public bool RemoveState(TStateEnum type)
        {
            if (!_states.TryGetValue(type, out RevHeavyFSMState<TStateEnum, TOwner> state)) return false;

            if (ReferenceEquals(state, _current))
            {
                RevHeavyFSMLog.Warning($"不能移除正在使用的当前状态 {type}（请先切到其他状态）");
                return false;
            }
            if (IsTransitioning && ReferenceEquals(state, _pending))
            {
                AbortPendingAsync();        // 顺手取消正切向它的异步事务
            }

            _states.Remove(type);
            RevHeavyFSMLog.Debug($"移除状态 {type}");
            return true;
        }

        /// <summary>状态是否已注册</summary>
        public bool HasState(TStateEnum type) => _states.ContainsKey(type);

        /// <summary>取指定状态实例（强类型）</summary>
        public TState GetState<TState>(TStateEnum type) where TState : RevHeavyFSMState<TStateEnum, TOwner>
        {
            return _states.TryGetValue(type, out RevHeavyFSMState<TStateEnum, TOwner> state) ? state as TState : null;
        }

        // ============================================================
        // 切换（同步 / 异步两条路）
        // ============================================================

        /// <summary>
        /// 同步切换（立即交割）：不等任何异步准备；若有异步切换在途，先取消它 —— 同步请求永远赢。
        /// </summary>
        /// <param name="target">目标状态枚举</param>
        /// <param name="reason">为什么切（写入历史与事件 —— 排查"AI 为什么突然切走"用）</param>
        /// <returns>true = 已切换；false = 被忽略（未启用、状态未注册、目标即当前、重入拒绝）</returns>
        /// <exception cref="InvalidOperationException">DEBUG 下在 OnEnter/OnExit 回调里调用时抛出（防重入）</exception>
        public virtual bool ChangeState(TStateEnum target, string reason = "")
        {
            if (!IsEnabled) return false;
            if (RejectIfInCallback(target)) return false;
            if (!TryGetState(target, out RevHeavyFSMState<TStateEnum, TOwner> targetState)) return false;

            // 同步请求的规则是“现在就决定，不等异步准备”。所以即使目标就是当前状态，也要先取消正在准备去其他状态的旧请求，
            // 否则旧请求稍后完成仍会把状态机带走。取消后当前状态已经正确，无需再对同一实例重复执行 OnExit/OnEnter。
            AbortPendingAsync();
            if (ReferenceEquals(targetState, _current)) return false;
            Commit(targetState, reason);
            return true;
        }

        /// <summary>
        /// 异步切换（事务式，吸收王者④）：await 旧状态 PrepareExitAsync → await 新状态 PrepareEnterAsync → 交割。
        /// <para>可跨任意多帧；交割前当前状态照常运行。在途时再发起一次 → 旧的作废，只保留最新目标。
        /// 双方都没 override Prepare（默认直接完成）时同步完成、不分配对象。</para>
        /// </summary>
        /// <param name="target">目标状态枚举</param>
        /// <param name="reason">为什么切（写入历史与事件）</param>
        /// <returns>可 await 的任务；完成后用 <see cref="CurrentStateType"/> 确认是否真的切过去了</returns>
        /// <exception cref="InvalidOperationException">DEBUG 下在 OnEnter/OnExit 回调里调用时抛出（防重入）</exception>
        public RevTask ChangeStateAsync(TStateEnum target, string reason = "")
        {
            if (!IsEnabled) return RevTask.Completed;
            if (RejectIfInCallback(target)) return RevTask.Completed;
            if (!TryGetState(target, out RevHeavyFSMState<TStateEnum, TOwner> targetState)) return RevTask.Completed;
            if (ReferenceEquals(targetState, _current))
            {
                // 当前枚举对应的状态对象已经激活。若先前异步请求正在准备切走，新请求等于撤销那次离开；取消旧请求后就应留在原地。
                // 不要再启动一次“从当前状态切到它自己”的事务，否则会重复调用准备、退出和进入回调，可能重复创建资源或重置 AI。
                if (IsTransitioning) AbortPendingAsync();
                return RevTask.Completed;
            }

            AbortPendingAsync();        // 更新的请求胜出，旧的在途切换作废

            var cts = new RevCancellationTokenSource();
            _asyncCts = cts;
            _pending = targetState;
            IsTransitioning = true;
            int version = ++_version;

            RevHeavyFSMLog.Debug($"发起异步切换 {CurrentStateType} → {target}（{reason}）");
            return RunAsyncTransition(targetState, reason, version, cts.Token);
        }

        /// <summary>
        /// 主动取消在途的异步切换：在途的 Prepare 会收到取消通知，<b>当前状态保持不变</b>。
        /// <para>等待中的 <see cref="ChangeStateAsync"/> 会正常完成（不抛异常）—— 是否取消成功请看 CurrentStateType。</para>
        /// </summary>
        public void CancelAsyncTransition() => AbortPendingAsync();

        // ============================================================
        // 驱动（宿主在 Update / FixedUpdate / LateUpdate 里各调一次）
        // ============================================================

        /// <summary>
        /// 每帧驱动：驱动当前状态。
        /// <para>异步切换在途时，当前（旧）状态照常被驱动 —— 它在交割前一直是"激活"的，AI 不会僵住。</para>
        /// </summary>
        public void UpdateState(float deltaTime)
        {
            if (!IsEnabled || _current == null) return;
            _stateTime += deltaTime;
            Guard(() => _current.OnUpdate(deltaTime), $"{_current.GetType().Name}.OnUpdate");
        }

        /// <summary>固定步长驱动（移动/寻路等物理逻辑）</summary>
        public void FixedUpdateState(float fixedDeltaTime)
        {
            if (!IsEnabled || _current == null) return;
            Guard(() => _current.OnFixedUpdate(fixedDeltaTime), $"{_current.GetType().Name}.OnFixedUpdate");
        }

        /// <summary>LateUpdate 驱动（相机跟随/表现纠偏）</summary>
        public void LateUpdateState(float deltaTime)
        {
            if (!IsEnabled || _current == null) return;
            Guard(() => _current.OnLateUpdate(deltaTime), $"{_current.GetType().Name}.OnLateUpdate");
        }

        // ============================================================
        // 历史 / 重置 / 清理
        // ============================================================

        /// <summary>历史记录的最大条数（默认 10，超出后丢弃最老的）</summary>
        public int MaxHistorySize
        {
            get => _maxHistorySize;
            set
            {
                if (value <= 0) return;
                _maxHistorySize = value;
                while (_history.Count > _maxHistorySize) _history.Dequeue();
            }
        }

        /// <summary>取切换历史（从老到新，带原因与时刻）—— 排查"AI 怎么走到这步的"用</summary>
        public RevHeavyFSMTransition<TStateEnum>[] GetHistory() => _history.ToArray();

        /// <summary>
        /// 重置到初始状态：取消在途事务、退出当前状态、清空历史（<b>保留</b>已注册的状态），再切到初始状态。
        /// </summary>
        public void Reset(TStateEnum initialState, string reason = "Reset")
        {
            AbortPendingAsync();
            ExitCurrentState();
            _history.Clear();
            _stateTime = 0f;
            ChangeState(initialState, reason);
        }

        /// <summary>清空：取消在途事务、退出当前状态、清空注册表与历史（不触发 StateChanged）</summary>
        public void Clear()
        {
            AbortPendingAsync();
            ExitCurrentState();
            _states.Clear();
            _history.Clear();
            _stateTime = 0f;
            CurrentStateType = default;            // 与 HasCurrentState = false 保持一致，不残留旧枚举值
        }

        /// <summary>释放：清空 + 禁用 + 解除宿主引用（对应原框架的 Dispose）</summary>
        public virtual void Dispose()
        {
            Clear();
            IsEnabled = false;
            Owner = null;
        }

        // ============================================================
        // 内部实现
        // ============================================================

        /// <summary>
        /// 异步交割流程（用 RevTask 写成 async 方法，可跨任意多帧）：
        ///   旧状态 PrepareExitAsync → 新状态 PrepareEnterAsync → Commit
        /// 每跨过一次 await 都验一次代次：发现自己已被更新的请求取代，就静默退出、不交割。
        /// </summary>
        private async RevTask RunAsyncTransition(RevHeavyFSMState<TStateEnum, TOwner> target, string reason,
                                                int version, RevCancellationToken token)
        {
            try
            {
                if (_current != null && !ReferenceEquals(_current, target))
                    await _current.PrepareExitAsync(token);
                if (version != _version) return;        // 已被更新的请求取代 → 放弃本次交割
                if (!IsEnabled) return;

                await target.PrepareEnterAsync(token);
                if (version != _version) return;
                if (!IsEnabled) return;

                Commit(target, reason);
            }
            catch (RevOperationCanceledException)
            {
                // 本次切换被取消（被更新的请求取代 / 主动取消）：状态保持原样，不交割、不抛给 await 方
                RevHeavyFSMLog.Debug($"异步切换 {CurrentStateType} → {target.StateType} 被取消（未交割）");
            }
            finally
            {
                // ★ 只有"最新一次"负责收尾：被取代的那次不能动新请求的状态
                if (version == _version)
                {
                    IsTransitioning = false;
                    _pending = null;
                    _asyncCts = null;
                }
            }
        }

        /// <summary>作废在途的异步切换（若有）：先让代次失效，再通知取消</summary>
        private void AbortPendingAsync()
        {
            RevCancellationTokenSource cts = _asyncCts;
            _asyncCts = null;
            _version++;                                 // ① 先让在途的交割失效（它醒来时会自己放弃）
            if (IsTransitioning)
            {
                IsTransitioning = false;
                _pending = null;
                RevHeavyFSMLog.Debug($"取消一次进行中的异步切换（{CurrentStateType} 保持不变）");
            }
            cts?.Cancel();                              // ② 再通知业务（可能同步唤醒在途 await）
        }

        /// <summary>真正交割：旧 OnExit → 换、清零 → 新 OnEnter → 历史 → 广播（同步/异步两条路共用）</summary>
        private void Commit(RevHeavyFSMState<TStateEnum, TOwner> target, string reason)
        {
            TStateEnum from = CurrentStateType;

            _duringTransition = true;
            try
            {
                if (_current != null)
                {
                    PrevStateType = _current.StateType;
                    Guard(_current.OnExit, $"{_current.GetType().Name}.OnExit");   // ① 旧状态退出
                }

                _current = target;                                                 // ② 交割
                _pending = null;
                CurrentStateType = target.StateType;
                _stateTime = 0f;

                Guard(target.OnEnter, $"{target.GetType().Name}.OnEnter");         // ③ 新状态进入
            }
            finally
            {
                _duringTransition = false;
            }

            RecordHistory(from, target.StateType, reason);                                // ④ 历史
            RevHeavyFSMLog.Debug(new RevHeavyFSMTransition<TStateEnum>(from, target.StateType, reason).ToString());
            Action<TStateEnum, TStateEnum, string> listeners = StateChanged;
            TStateEnum entered = target.StateType;
            if (listeners != null)
            {
                foreach (Action<TStateEnum, TStateEnum, string> listener in listeners.GetInvocationList())
                {
                    try { listener(from, entered, reason ?? string.Empty); }
                    catch (Exception e) { RevLog.Exception(e, "重量状态机变更监听异常", "StateMachine"); }
                }
            }
        }

        /// <summary>退出当前状态（供 Clear / Reset 复用；带防重入保护）</summary>
        private void ExitCurrentState()
        {
            if (_current == null) return;

            _duringTransition = true;
            try
            {
                Guard(_current.OnExit, $"{_current.GetType().Name}.OnExit");
            }
            finally
            {
                _duringTransition = false;
            }
            _current = null;
        }

        /// <summary>取状态实例；未注册时记错误并返回 false</summary>
        private bool TryGetState(TStateEnum type, out RevHeavyFSMState<TStateEnum, TOwner> state)
        {
            if (_states.TryGetValue(type, out state)) return true;
            RevHeavyFSMLog.Error($"切换失败：状态 {type} 没有注册");
            return false;
        }

        /// <summary>
        /// 防重入：状态回调里再切状态 = 程序错误。
        /// DEBUG 直接抛（同步方法，异常直达调用方）；Release 记 Error 并拒绝。
        /// </summary>
        private bool RejectIfInCallback(TStateEnum target)
        {
            if (!_duringTransition) return false;

#if DEBUG
            throw new InvalidOperationException(
                $"[RevHeavyFSM] 不能在 OnEnter/OnExit 回调里切换状态（{CurrentStateType} 的回调里请求切到 {target}）。" +
                "请把切换延迟到回调结束后（比如下一帧 Update 里）。");
#else
            RevHeavyFSMLog.Error($"不能在状态回调里切换状态（{CurrentStateType} → {target} 已拒绝）。请延迟到下一帧。");
            return true;
#endif
        }

        private void RecordHistory(TStateEnum from, TStateEnum to, string reason)
        {
            _history.Enqueue(new RevHeavyFSMTransition<TStateEnum>(from, to, reason));
            while (_history.Count > _maxHistorySize) _history.Dequeue();
        }

        /// <summary>
        /// 状态回调的异常隔离：
        /// DEBUG 直接抛（尽早发现 bug）；Release 捕获记 Error（真机上一个状态的 bug 不拖垮整台机器）。
        /// </summary>
        private static void Guard(Action callback, string where)
        {
#if DEBUG
            callback();
#else
            try
            {
                callback();
            }
            catch (Exception e)
            {
                RevHeavyFSMLog.Error($"{where} 抛异常：{e}");
            }
#endif
        }
    }
}

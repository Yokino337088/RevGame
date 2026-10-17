// ============================================================
// RevLightFSM.cs —— 轻量级泛型单状态机
//
// 位置：Runtime\RevStateMachine\Lightweight\Machines\
//
// 【定位】
//   给轻量业务用：UI 流程、玩法阶段、输入模式、新手引导、简单流程控制。
//   任何时刻只有一个激活状态，字段最少、无栈容器（默认零额外分配）。
//   需要"记住来路"（UI 界面栈）请用同目录的 RevLightStackFSM<T>；
//   怪物 AI / Boss AI 等重量业务请用 Heavyweight 目录的 RevHeavyFSM（枚举 + 行为接口）。
//
// 【吸收了王者的什么】
//   · 泛型类型安全（②③）：状态用泛型类，不用字符串名注册 —— 拼错在编译期就拦住；
//   · StateTime 状态计时（②③）："进入本状态多久了"，切换时自动清零；
//   · 防重入（②③）：在 OnEnter/OnExit 回调里再切状态 → 立刻抛异常。
//     王者只在 DEBUG 抛；这里 Release 也抛 —— 嵌套切换是程序错误不是运行时状况，
//     轻量业务"错了就该立刻发现"，不该带病继续跑；
//   · 异步切换（④事务式）：用本框架的 RevTask 表达 —— 状态实现 RevILightAsyncState 后，
//     ChangeToAsync 会 await 双方的 Prepare，准备完毕才交割（详见下面"两种切换"）；
//   · StateChanged 事件（⑤实战模式）：切换后广播，各系统订阅响应，而不是状态机主动找它们。
//
// 【注册（可选）—— 让"状态实例"也有落点】
//   默认用法是直接传实例（ChangeTo(new LobbyState())）；状态集合固定时，也可以启动时注册一次，
//   之后按类型切换（ChangeTo<LobbyState>()）：
//     · 键用「类型」而不是枚举 —— 不需要维护"枚举 ↔ 类型"两套东西。
//       真要"枚举 + 条件判断/查表/存档"的场景，请用重量级 RevHeavyFSM（它才是为这个设计的）；
//     · 注册的实例是复用的 → 天然避开"每次 new 导致每次都被判成新状态、反复 Exit/Enter"的坑
//       （ChangeTo 判"是否已在该状态"用的是引用相等）；
//     · 注册与直接传实例【可以混用】：注册只是给固定状态集省样板，
//       不关闭"临时传一个带参数的状态"这种能力（ChangeTo(new BattleState(roomId))）；
//     · 未注册就按类型切换 → 立刻抛异常（与防重入同一风格：这是编程错误，不该静默降级）。
//
// 【两种切换 —— 按需选一个】
//   ChangeTo(target)        同步、立即交割。不等异步准备；若当前有异步切换在途，直接把它取消。
//                           适合输入响应、UI 按钮这类"必须马上切"的场合。
//   ChangeToAsync(target)   事务式、异步交割（返回 RevTask）：
//                             await 旧状态 PrepareExitAsync → await 新状态 PrepareEnterAsync → 交割；
//                           任一方不实现 RevILightAsyncState 就跳过对应的等待（同步完成）；
//                           双方都不实现时不会分配任何对象，等于直接 ChangeTo。
//                           适合"加载完再进""动画播完再走"的场合：
//                             await sm.ChangeToAsync(new BattleState());
//
// 【异步切换的三条规则】
//   ① 只保留最新目标：在途时再发起一次，旧的那次立刻作废（token 被 Cancel，
//      业务可借此中断在途加载），只有最新的请求会交割 —— 与王者④"跳过中间态"一致；
//   ② 交割前当前（旧）状态仍是激活状态：继续被 Update 驱动，游戏不会"卡在中间"；
//   ③ await 结束后请用 CurrentState / IsTransitioning 确认是否真的切过去了 ——
//      被更新的请求取代、或被 CancelAsyncTransition() 取消时，状态保持不变、任务正常完成（不抛异常）。
//
// 【纯 C#】除了 RevTask（不含调度器的 MonoBehaviour 部分）不碰 UnityEngine，
//          可以脱离引擎做单元测试。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 轻量级泛型单状态机：任何时刻只有一个激活状态。
    /// <para>用法：业务在自己的 MonoBehaviour.Update 里每帧调 <see cref="Update"/>（与王者用法一致）。</para>
    /// </summary>
    /// <typeparam name="T">状态类型（编译期类型安全）</typeparam>
    /// <example>
    /// <code>
    /// var sm = new RevLightFSM&lt;RevLightStateBase&gt;();
    /// sm.ChangeTo(new LobbyState());          // 立即切
    /// await sm.ChangeToAsync(new BattleState());   // 等加载完再切
    /// void Update() =&gt; sm.Update(Time.deltaTime);
    ///
    /// sm.Register(new LobbyState());          // 也可以先注册，之后按类型切
    /// sm.ChangeTo&lt;BattleState&gt;();
    /// </code>
    /// </example>
    public sealed class RevLightFSM<T> where T : class, RevILightState
    {
        private T _current;                    // 当前状态
        private T _pending;                    // 异步切换的目标（null = 没有事务在进行）
        private float _stateTime;              // 进入当前状态的累计秒数
        private bool _duringTransition;        // 防重入标记
        private int _version;                  // 切换代次：不匹配的在途切换一律作废
        private RevCancellationTokenSource _asyncCts;   // 在途异步切换的取消源
        private Dictionary<Type, Func<T>> _registered;  // 注册表（类型 → 取实例的方式）；不注册就不分配

        /// <summary>状态变化事件（旧状态, 新状态）—— 在新状态 OnEnter 之后触发</summary>
        public event Action<T, T> StateChanged;

        /// <summary>当前状态（还没切过任何状态时为 null；异步交割前是旧状态）</summary>
        public T CurrentState => _current;

        /// <summary>是否有异步切换在进行中（正在 await 双方的 Prepare）</summary>
        public bool IsTransitioning { get; private set; }

        /// <summary>
        /// 异步切换的目标状态（对应王者①的 tarState：知道"本来要去哪"）。
        /// <para>旧状态在 OnExit 里读它，就能区分"正常离开"与"异常离开"（LoadingState 的真实用法）。</para>
        /// </summary>
        public T PendingState => _pending;

        /// <summary>进入当前状态多久了（秒）—— 做"待机 3 秒后播随机动作"这类逻辑用</summary>
        public float StateTime => _stateTime;

        // ============================================================
        // 切换
        // ============================================================

        /// <summary>
        /// 同步切换（立即交割）：旧 OnExit → 新 OnEnter，中间不做任何等待。
        /// <para>不等异步准备（若目标实现了 <see cref="RevILightAsyncState"/>，其 Prepare 会被跳过）；
        /// 若有异步切换在途，会先把它取消 —— 同步请求永远赢。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">在 OnEnter/OnExit 回调里调用时抛出（防重入）</exception>
        public void ChangeTo(T target)
        {
            GuardNotInCallback(target);
            if (target == null) 
                return;

            AbortPendingAsync();                        // 同步请求优先，作废在途的异步切换
            if (ReferenceEquals(target, _current)) 
                return;

            Commit(target);
        }

        /// <summary>
        /// 异步切换（事务式）：await 旧状态退出准备 + 新状态进入准备，都完成才交割。
        /// <para>双方都不实现 <see cref="RevILightAsyncState"/> 时同步完成、不分配对象，等价于 <see cref="ChangeTo"/>。</para>
        /// </summary>
        /// <param name="target">目标状态</param>
        /// <returns>可 await 的任务；完成后用 <see cref="CurrentState"/> 确认是否真的切过去了</returns>
        /// <exception cref="InvalidOperationException">在 OnEnter/OnExit 回调里调用时抛出（防重入）</exception>
        public RevTask ChangeToAsync(T target)
        {
            GuardNotInCallback(target);
            if (target == null) return RevTask.Completed;
            if (ReferenceEquals(target, _current))
            {
                // 当前实例已经是目标，不需要再调用一次 OnExit/OnEnter；重复进入可能重置计时、重复注册事件或再次加载资源。
                // 但如果之前有人正异步准备切到别处，这个新请求表示“留在当前状态”，所以先取消旧请求，再保持当前状态不变。
                if (IsTransitioning) AbortPendingAsync();
                return RevTask.Completed;
            }

            AbortPendingAsync();                        // 更新的请求胜出，旧的在途切换作废

            // ★ 双方都不需要异步准备 → 直接交割：不建取消源、不建异步状态机（常见路径零额外开销）
            if (!(_current is RevILightAsyncState) && !(target is RevILightAsyncState))
            {
                Commit(target);
                return RevTask.Completed;
            }

            var cts = new RevCancellationTokenSource();
            _asyncCts = cts;
            _pending = target;
            IsTransitioning = true;
            int version = ++_version;

            return RunAsyncTransition(target, version, cts.Token);
        }

        /// <summary>
        /// 主动取消在途的异步切换：在途的 Prepare 会收到取消通知，<b>当前状态保持不变</b>。
        /// <para>等待中的 <see cref="ChangeToAsync"/> 会正常完成（不抛异常）—— 是否取消成功请看 CurrentState。</para>
        /// </summary>
        public void CancelAsyncTransition() => AbortPendingAsync();

        /// <summary>
        /// 每帧驱动（业务在自己的 Update 里调一次）。
        /// <para>异步切换在途时，当前（旧）状态照常被驱动 —— 它在交割前一直是"激活"的。</para>
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_current == null) return;          // 没有激活状态时 StateTime 无意义，不累计
            _stateTime += deltaTime;
            _current.OnUpdate(deltaTime);
        }

        /// <summary>当前是否是某状态</summary>
        public bool Is<TState>() where TState : T => _current is TState;

        // ============================================================
        // 注册（可选）：登记"类型 → 实例"，之后按类型切换
        //   · 不调用注册时，本类行为与以前完全一致（连字典都不分配）
        //   · 键取的是「泛型参数 TState」，所以直接写 Register(new LobbyState()) 让类型推断生效
        // ============================================================

        /// <summary>
        /// 注册一个<b>状态实例</b>（同一个实例长期复用，切换时零分配）。
        /// <code>sm.Register(new LobbyState());</code>
        /// </summary>
        /// <exception cref="ArgumentNullException">state 为 null</exception>
        /// <exception cref="InvalidOperationException">同一类型重复注册（通常是复制粘贴漏改）</exception>
        public void Register<TState>(TState state) where TState : T
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            RegisterCore(typeof(TState), () => state);
        }

        /// <summary>
        /// 注册一个<b>状态工厂</b>：需要构造参数、或希望每次切换都拿新实例时用它。
        /// <code>sm.Register&lt;LoadingState&gt;(() =&gt; new LoadingState(_ctx));</code>
        /// </summary>
        /// <exception cref="ArgumentNullException">factory 为 null</exception>
        /// <exception cref="InvalidOperationException">同一类型重复注册</exception>
        public void Register<TState>(Func<T> factory) where TState : T
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            RegisterCore(typeof(TState), factory);
        }

        /// <summary>该类型是否已注册（仅"按类型切换"时需要）</summary>
        public bool HasState<TState>() where TState : T
            => _registered != null && _registered.ContainsKey(typeof(TState));

        /// <summary>
        /// 按类型切换（等价于 <c>ChangeTo(注册的实例)</c>）。
        /// <para>同一个注册实例被复用 → 已在该状态时直接跳过，不会重复 Exit/Enter。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">该类型未注册</exception>
        public void ChangeTo<TState>() where TState : T => ChangeTo(Resolve<TState>());

        /// <summary>按类型异步切换（等价于 <c>ChangeToAsync(注册的实例)</c>）</summary>
        /// <exception cref="InvalidOperationException">该类型未注册</exception>
        public RevTask ChangeToAsync<TState>() where TState : T => ChangeToAsync(Resolve<TState>());

        private void RegisterCore(Type type, Func<T> getInstance)
        {
            _registered ??= new Dictionary<Type, Func<T>>();
            if (_registered.ContainsKey(type))
                throw new InvalidOperationException(
                    $"[RevLightFSM] 状态类型 {type.Name} 已经注册过了（重复注册通常是复制粘贴漏改）。");

            _registered.Add(type, getInstance);
        }

        /// <summary>取出注册的实例；没注册就直接抛 —— 编程错误，不静默降级</summary>
        private T Resolve<TState>() where TState : T
        {
            if (_registered != null && _registered.TryGetValue(typeof(TState), out Func<T> getInstance))
                return getInstance();

            throw new InvalidOperationException(
                $"[RevLightFSM] 状态类型 {typeof(TState).Name} 没有注册。" +
                $"请先在初始化时 sm.Register(new {typeof(TState).Name}())（或 Register<{typeof(TState).Name}>(工厂)），" +
                "或改用 ChangeTo(实例) 直接传入。");
        }

        // ============================================================
        // 内部实现
        // ============================================================

        /// <summary>
        /// 异步交割流程（用 RevTask 写成 async 方法，可跨任意多帧）：
        ///   旧状态 PrepareExitAsync → 新状态 PrepareEnterAsync → Commit
        /// 每跨过一次 await 都验一次代次：发现自己已被更新的请求取代，就静默退出、不交割。
        /// </summary>
        private async RevTask RunAsyncTransition(T target, int version, RevCancellationToken token)
        {
            try
            {
                if (_current is RevILightAsyncState oldAsync)
                    await oldAsync.PrepareExitAsync(token);
                if (version != _version) return;        // 已被更新的请求取代 → 放弃本次交割

                if (target is RevILightAsyncState newAsync)
                    await newAsync.PrepareEnterAsync(token);
                if (version != _version) return;

                Commit(target);
            }
            catch (RevOperationCanceledException)
            {
                // 本次切换被取消（被更新的请求取代 / 主动取消）：状态保持原样，不交割、不抛给 await 方
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
            IsTransitioning = false;
            _pending = null;
            cts?.Cancel();                              // ② 再通知业务（可能同步唤醒在途 await）
        }

        /// <summary>真正交割：旧 OnExit → 换 → 计时清零 → 新 OnEnter → 广播</summary>
        private void Commit(T target)
        {
            _duringTransition = true;
            T last;
            try
            {
                last = _current;
                _current?.OnExit();          // ① 旧状态退出
                _current = target;
                _pending = null;
                _stateTime = 0f;             // ② 计时清零
                _current?.OnEnter();         // ③ 新状态进入
            }
            finally
            {
                _duringTransition = false;
            }

            // 状态已经提交，监听者失败只影响自己；所有人收到本次切换的同一份 from/to 快照。
            Action<T, T> listeners = StateChanged;
            T entered = _current;
            if (listeners != null)
            {
                foreach (Action<T, T> listener in listeners.GetInvocationList())
                {
                    try { listener(last, entered); }
                    catch (Exception e) { RevLog.Exception(e, "轻量状态机变更监听异常", "StateMachine"); }
                }
            }
        }

        /// <summary>防重入：状态回调里再切状态 = 程序错误，立刻抛</summary>
        private void GuardNotInCallback(T target)
        {
            if (!_duringTransition) return;
            throw new InvalidOperationException(
                $"[RevLightFSM] 不能在 OnEnter/OnExit 回调里切换状态" +
                $"（{_current?.GetType().Name} 的回调里请求切到 {target?.GetType().Name}）。" +
                "嵌套切换是程序错误：请把切换延迟到回调结束后（比如下一帧 Update 里）。");
        }
    }
}

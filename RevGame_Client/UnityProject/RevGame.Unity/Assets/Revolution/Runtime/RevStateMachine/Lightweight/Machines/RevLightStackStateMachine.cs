// ============================================================
// RevLightStackFSM.cs —— 轻量级泛型栈状态机
//
// 位置：Runtime\RevStateMachine\Lightweight\Machines\
//
// 【定位】
//   需要"记住来路"的轻量业务：UI 界面栈（主界面 → 背包 → 设置弹窗，逐级返回）、
//   输入模式切换、多步流程的中途暂离。栈顶 = 当前激活状态，只有栈顶被 Update 驱动。
//
// 【吸收了王者的什么】
//   · 栈语义（①③，四套状态机的"灵魂"）：Push 时旧栈顶 OnSuspend（被压住、不销毁），
//     Pop 时新栈顶 OnResume（重新露出）—— 普通 switch 式状态机表达不了这个；
//   · 泛型类型安全 + StateTime 计时 + 防重入（③）；
//   · tarState → TargetState（①⑤实战）：Change 的目标状态持久保留，
//     旧状态 OnExit 里能读到"我要切去哪"，做"异常退出分支判断"
//     （王者 LoadingState 的真实用法：不是去战斗 → 兜底释放战斗资源）；
//   · Change 替换栈顶（①的 ChangeState）：栈深不变的"替换"；
//   · 事件 StatePushed / StatePopped（⑤实战解耦模式）。
//
// 【为什么用 Stack<T>，而不是"List 当栈"】
//   栈的存储直接用 BCL 的 Stack<T>（Push/Pop/Peek，意图即文档，外部不可能误用下标破坏栈语义）。
//   代价是 Stack<T> 没有索引器，读不了"倒数第 N 层"。本机因此只提供两个读取口：
//     · Current  —— 栈顶（Peek，O(1)）
//     · Under    —— 栈顶下面一层（枚举器走两步，O(1)、零分配，见该属性的实现注释）
//   如果业务需要"按下标访问任意层"（例如把整条返回路径渲染成列表面包屑），
//   那需要随机访问能力 —— 与 Stack<T> 互斥，应改用数组/List 承载（或由 UI 层自己维护一份路径快照）。
//
// 【注册（可选）—— 同单机：类型当键】
//   启动时 Register(状态实例) 一次，之后可用 Push<TState>() / Change<TState>() 按类型操作：
//     · 键用「类型」而不是枚举（要"枚举 + 条件判断"请用重量级 RevHeavyFSM）；
//     · 注册的实例是复用的，但【同一个实例不能同时出现在栈里两层】——
//       无论栈顶还是更深层，Push 都会直接拒绝（Pop 时序会错乱）；
//       确需压多层，请用工厂注册：Register<TState>(() => new TState())；
//     · 未注册就按类型操作 → 立刻抛异常（编程错误不静默）；
//     · 注册与直接传实例可以混用：Pop() 弹出的是实例本身，任何来源都一样。
//
// 【为什么不支持异步切换】
//   王者也没有"栈 + 异步"的组合：Push/Pop 是结构性变化，异步交割的语义在栈上并不清晰。
//   需要"等资源就绪再切"的业务，请用 RevLightFSM<T>（await ChangeToAsync）
//   或重量级 RevHeavyFSM（await ChangeStateAsync）—— 它们用 RevTask 表达异步准备。
//   本机只做同步的栈操作。
//
// 【纯 C#】不依赖 UnityEngine，可以脱离引擎做单元测试。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 轻量级泛型栈状态机：状态压栈 / 弹栈，栈顶为激活状态。
    /// <para>用法：业务在自己的 MonoBehaviour.Update 里每帧调 <see cref="Update"/>。</para>
    /// </summary>
    /// <typeparam name="T">状态类型（编译期类型安全）</typeparam>
    /// <example>
    /// <code>
    /// var sm = new RevLightStackFSM&lt;RevLightStateBase&gt;();
    /// sm.Push(new LobbyState());     // 进大厅
    /// sm.Push(new BattleState());    // 进战斗（大厅 OnSuspend）
    /// sm.Pop();                      // 回大厅（大厅 OnResume）
    ///
    /// sm.Register(new BagState());   // 也可以先注册，之后按类型压栈
    /// sm.Push&lt;BagState&gt;();
    /// </code>
    /// </example>
    public sealed class RevLightStackFSM<T> where T : class, RevILightStackState
    {
        private readonly Stack<T> _stack = new Stack<T>(4);   // 栈本体：LIFO，只有 Push/Pop 两个改动点
        private Dictionary<Type, Func<T>> _registered;        // 注册表（类型 → 取实例的方式）；不注册就不分配
        private float _stateTime;                             // 栈顶状态的累计秒数
        private bool _duringTransition;                       // 防重入标记

        /// <summary>状态入栈事件（Push / Change 都会触发）</summary>
        public event Action<T> StatePushed;

        /// <summary>状态出栈事件（Pop / Change / Clear 都会触发；Clear 时逐层触发）</summary>
        public event Action<T> StatePopped;

        /// <summary>栈深度</summary>
        public int Count => _stack.Count;

        /// <summary>栈顶（当前激活状态；栈空为 null）</summary>
        public T Current => _stack.Count > 0 ? _stack.Peek() : null;

        /// <summary>
        /// 栈顶下面一层（被压住的；不足两层为 null）—— 做"返回预览 / 面包屑"用。
        /// <para>
        /// 【实现说明】<see cref="Stack{T}"/> 没有索引器，读"倒数第二层"没有直接办法；
        /// 这里用它的<b>枚举顺序</b>解决：<c>Stack&lt;T&gt;</c> 的枚举是 LIFO（栈顶在最前），
        /// 所以走两步就是栈顶下面一层 —— O(1)（只走固定两步）、零分配（枚举器是 struct）。
        /// 这样 Push/Pop 都<b>不需要</b>维护任何额外状态，也就不存在"缓存与真栈不同步"的隐患。
        /// </para>
        /// </summary>
        public T Under
        {
            get
            {
                if (_stack.Count < 2) return null;      // 栈空 / 只有一层，下面没人

                Stack<T>.Enumerator e = _stack.GetEnumerator();
                e.MoveNext();                            // 第 1 步：栈顶
                return e.MoveNext() ? e.Current : null;  // 第 2 步：栈顶下面一层
            }
        }

        /// <summary>栈顶状态的累计秒数（Push / Pop / Change 时清零）</summary>
        public float StateTime => _stateTime;

        /// <summary>
        /// 最近一次 <see cref="Change"/> 的目标状态（对应王者①的 tarState，Change 后持久保留）。
        /// 旧状态在 OnExit 里读它，就能区分"正常离开"与"异常离开"。
        /// <para>注意：状态类本身不持有状态机引用，需要读它的状态请让业务在构造状态时把状态机传进去。</para>
        /// </summary>
        public T TargetState { get; private set; }

        /// <summary>
        /// 压栈：旧栈顶 OnSuspend → 新状态 OnEnter。
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// 在状态回调里调用时抛出（防重入）；或压入的实例已在栈中任意一层（栈里会出现同一个对象多份）
        /// </exception>
        public void Push(T state)
        {
            if (state == null) return;

            // ★ 整条栈查重（不只栈顶）：同一个实例同时出现在两层，
            //   Pop 时会出现"对同一对象先 OnExit、再 OnResume/再 OnExit"的错乱时序。
            if (ContainsInstance(state))
                throw new InvalidOperationException(
                    $"[RevLightStackFSM] {state.GetType().Name} 已经在栈里（栈顶或更深层），不能把同一个实例再压一次" +
                    "（栈里会出现同一个对象多份，Pop 时序会错乱）。" +
                    "确需压多层请用工厂注册：Register<TState>(() => new TState())。");

            BeginTransition(nameof(Push));
            try
            {
                Current?.OnSuspend();        // ① 旧栈顶"被压住"（不销毁）
                _stack.Push(state);
                _stateTime = 0f;
                state.OnEnter();             // ② 新状态进入
            }
            finally { EndTransition(); }

            StatePushed?.Invoke(state);
        }

        /// <summary>
        /// 弹栈：栈顶 OnExit → 新栈顶 OnResume。
        /// </summary>
        /// <returns>被弹出的状态（调用方可能要善后）；栈空返回 null</returns>
        /// <exception cref="InvalidOperationException">在状态回调里调用时抛出（防重入）</exception>
        public T Pop()
        {
            if (_stack.Count == 0) return null;
            BeginTransition(nameof(Pop));

            T top = _stack.Peek();
            try
            {
                top.OnExit();                      // ① 栈顶退出
                _stack.Pop();
                _stateTime = 0f;
                Current?.OnResume();               // ② 新栈顶"重新露出"
            }
            finally { EndTransition(); }

            StatePopped?.Invoke(top);
            return top;
        }

        /// <summary>
        /// 替换栈顶（栈深不变）：旧栈顶 OnExit → 新状态 OnEnter。对应王者①的 ChangeState。
        /// <para>会先写 <see cref="TargetState"/> 再调旧状态的 OnExit —— 旧状态能读到"要去哪"。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">在状态回调里调用时抛出（防重入）</exception>
        public void Change(T state)
        {
            if (state == null) return;
            if (ReferenceEquals(Current, state)) return;
            // 栈可以保存“大厅 A 被战斗 B 压住”的结构，但每一层都必须是独立的状态对象。
            // 如果 Change 把下层的 A 再压到顶上，栈里就会出现同一个 A 两次；之后 Pop 上层 A 时会对同一实例重复 OnExit/OnResume，
            // 下层那份 A 也会变成错误的活动对象。Change 只替换栈顶，因此目标已在下层时必须拒绝。
            if (ContainsInstance(state))
                throw new InvalidOperationException(
                    $"[RevLightStackFSM] {state.GetType().Name} 已在当前栈的下层，不能用 Change 再放到栈顶；请使用新的状态实例或工厂注册。");
            BeginTransition(nameof(Change));

            T old = Current;
            try
            {
                TargetState = state;               // ★ tarState：旧状态 OnExit 里能读到去向
                if (_stack.Count > 0)
                {
                    old.OnExit();
                    _stack.Pop();                  // 弹旧栈顶
                }
                _stack.Push(state);                // 压新状态：栈深与改前一致
                _stateTime = 0f;
                state.OnEnter();
            }
            finally { EndTransition(); }

            if (old != null) StatePopped?.Invoke(old);
            StatePushed?.Invoke(state);
        }

        /// <summary>
        /// 清空栈：逐层 OnExit（<b>不</b>触发 OnResume，对应王者①的 Clear）。
        /// <para>与"逐层 Pop"的区别：Pop 会一路触发各层的 OnResume（模拟"逐级返回"），Clear 是直接清场。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">在状态回调里调用时抛出（防重入）</exception>
        public void Clear()
        {
            BeginTransition(nameof(Clear));

            // 事件统一挪到交割结束后再触发（与 Push/Pop/Change 一致）：
            // 否则监听器在 StatePopped 里操作栈会被防重入误伤。
            List<T> popped = _stack.Count > 0 ? new List<T>(_stack.Count) : null;

            try
            {
                while (_stack.Count > 0)
                {
                    T top = _stack.Peek();
                    top.OnExit();                  // 先退出，再出栈（与 Pop 保持一致的时序）
                    _stack.Pop();
                    popped?.Add(top);
                }
                _stateTime = 0f;
            }
            finally { EndTransition(); }

            if (popped != null)
                for (int i = 0; i < popped.Count; i++) StatePopped?.Invoke(popped[i]);
        }

        /// <summary>每帧驱动：<b>只驱动栈顶</b>（被压住的下层状态不更新 —— 栈语义，也省性能）</summary>
        public void Update(float deltaTime)
        {
            if (_stack.Count == 0) return;         // 空栈时 StateTime 无意义，不累计
            _stateTime += deltaTime;
            Current?.OnUpdate(deltaTime);
        }

        /// <summary>栈顶是否是某状态</summary>
        public bool Is<TState>() where TState : T => Current is TState;

        // ============================================================
        // 注册（可选）：登记"类型 → 实例"，之后按类型压栈 / 替换栈顶
        //   · 不调用注册时，本类行为与以前完全一致（连字典都不分配）
        //   · 键取的是「泛型参数 TState」，所以直接写 Register(new BagState()) 让类型推断生效
        // ============================================================

        /// <summary>
        /// 注册一个<b>状态实例</b>（同一个实例长期复用，压栈时零分配）。
        /// <code>sm.Register(new BagState());</code>
        /// <para>注意：注册的实例是同一个对象，所以它<b>不能同时出现在栈里两层</b>（Push 会拒绝，含更深层）；
        /// 需要"同一个状态压多层"时请改用工厂重载。</para>
        /// </summary>
        /// <exception cref="ArgumentNullException">state 为 null</exception>
        /// <exception cref="InvalidOperationException">同一类型重复注册（通常是复制粘贴漏改）</exception>
        public void Register<TState>(TState state) where TState : T
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            RegisterCore(typeof(TState), () => state);
        }

        /// <summary>
        /// 注册一个<b>状态工厂</b>：每次按类型操作都取新实例 —— 需要构造参数、
        /// 或"同一个状态要压多层"时用它。
        /// <code>sm.Register&lt;BagState&gt;(() =&gt; new BagState());</code>
        /// </summary>
        /// <exception cref="ArgumentNullException">factory 为 null</exception>
        /// <exception cref="InvalidOperationException">同一类型重复注册</exception>
        public void Register<TState>(Func<T> factory) where TState : T
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            RegisterCore(typeof(TState), factory);
        }

        /// <summary>该类型是否已注册（仅"按类型操作"时需要）</summary>
        public bool HasState<TState>() where TState : T
            => _registered != null && _registered.ContainsKey(typeof(TState));

        /// <summary>按类型压栈（等价于 <c>Push(注册的实例)</c>）</summary>
        /// <exception cref="InvalidOperationException">该类型未注册，或该实例已在栈顶</exception>
        public void Push<TState>() where TState : T => Push(Resolve<TState>());

        /// <summary>按类型替换栈顶（等价于 <c>Change(注册的实例)</c>）</summary>
        /// <exception cref="InvalidOperationException">该类型未注册</exception>
        public void Change<TState>() where TState : T => Change(Resolve<TState>());

        /// <summary>该实例是否已在栈中任意一层（struct 枚举器，零分配）。</summary>
        private bool ContainsInstance(T state)
        {
            foreach (T layer in _stack)
                if (ReferenceEquals(layer, state)) return true;
            return false;
        }

        private void RegisterCore(Type type, Func<T> getInstance)
        {
            _registered ??= new Dictionary<Type, Func<T>>();
            if (_registered.ContainsKey(type))
                throw new InvalidOperationException(
                    $"[RevLightStackFSM] 状态类型 {type.Name} 已经注册过了（重复注册通常是复制粘贴漏改）。");

            _registered.Add(type, getInstance);
        }

        /// <summary>取出注册的实例；没注册就直接抛 —— 编程错误，不静默降级</summary>
        private T Resolve<TState>() where TState : T
        {
            if (_registered != null && _registered.TryGetValue(typeof(TState), out Func<T> getInstance))
                return getInstance();

            throw new InvalidOperationException(
                $"[RevLightStackFSM] 状态类型 {typeof(TState).Name} 没有注册。" +
                $"请先在初始化时 sm.Register(new {typeof(TState).Name}())（或 Register<{typeof(TState).Name}>(工厂)），" +
                "或改用 Push(实例) / Change(实例) 直接传入。");
        }

        // ── 防重入 ─────────────────────────────────────────────
        // 在 OnEnter/OnExit/OnSuspend/OnResume 里再改变栈 = 程序错误，立刻抛。
        // 王者只在 DEBUG 抛；这里 Release 也抛（与 RevLightFSM 同一原则：错了就该立刻发现）。
        private void BeginTransition(string op)
        {
            if (_duringTransition)
                throw new InvalidOperationException(
                    $"[RevLightStackFSM] 不能在状态回调里执行 {op}" +
                    "（OnEnter/OnExit/OnSuspend/OnResume 中不允许改变栈）。请把操作延迟到回调结束后。");
            _duringTransition = true;
        }

        private void EndTransition() => _duringTransition = false;
    }
}

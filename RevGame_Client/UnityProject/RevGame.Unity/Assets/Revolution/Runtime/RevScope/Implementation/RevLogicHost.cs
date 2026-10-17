// ============================================================
// RevLogicHost.cs —— 业务单元（RevLogic）的运行表：谁在跑、归哪个域、怎么启动、怎么停
//
// 位置：Runtime\RevScope\Implementation\
//
// 【职责】
//   · Start：校验（变体 / 域是否开着）→ 建实例 → 登记到域 → 调 OnStart；OnStart 抛异常就整体回滚；
//   · Stop ：★ 先"自动解绑"（事件 / 计时器 / 每帧回调 / 服务），最后才调 OnStop（理由见 RevLogicBase 文件头）；
//   · 同一个类型同一时刻只有一个实例（Start 两次返回已有实例）；
//   · 记住每个类型的"元信息"（声明的域 / 变体 / 有没有重写 OnTick），★ 每个类型只用反射读一次、之后查缓存。
//
// 【为什么"有没有重写 OnTick"要在 Start 时判断一次】
//   没重写 OnTick 的 Logic 不该付任何每帧成本。做法：启动时（只一次）用反射看 OnTick 是不是基类那个，
//   不是才把它放进"每帧名单"。每帧路径上没有任何反射，只有遍历一个小列表。
//
// 【纯 C#】不引用 UnityEngine（可以链接进工程外断言）。需要 Unity 才能做的事（挂每帧驱动、摘 RevMono 回调）
//   通过 RevScopeBridge 由 Support 层补上。
// ============================================================
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Revolution
{
    /// <summary>业务单元运行表（纯 C#）。业务不要直接用，请用门面 <see cref="RevLogic"/>。</summary>
    internal sealed class RevLogicHost
    {
        /// <summary>某个 Logic 类型的元信息（每个类型只读一次）。</summary>
        private sealed class Meta
        {
            internal string Domain;
            internal string Variant;
            internal bool HasTick;
        }

        private static readonly Dictionary<Type, Meta> MetaCache = new Dictionary<Type, Meta>();

        private readonly RevScopeCore _scope;
        private readonly Dictionary<Type, RevLogicBase> _running = new Dictionary<Type, RevLogicBase>();

        /// <summary>没有归属任何域、但重写了 OnTick 的 Logic（它们不受任何域的暂停影响）。</summary>
        private readonly List<RevLogicBase> _globalTickers = new List<RevLogicBase>(2);

        /// <summary>当前变体（A/B 用）。</summary>
        internal string Variant;

        internal RevLogicHost(RevScopeCore scope)
        {
            _scope = scope;
        }

        internal int Count => _running.Count;

        // ==================== 启动 ====================

        internal T Start<T>(object param) where T : RevLogicBase, new()
        {
            Type type = typeof(T);

            // 已经在跑：直接返回（同一类型同一时刻只有一个实例）
            if (_running.TryGetValue(type, out RevLogicBase existing)) { return (T)existing; }

            Meta meta = GetMeta(type);
            string name = type.Name;

            // ① 变体校验：不匹配就明确拒绝（并说清当前变体是什么）
            if (meta.Variant != null && !string.Equals(meta.Variant, Variant, StringComparison.Ordinal))
            {
                RevLog.Error("Logic " + name + " 只在变体「" + meta.Variant + "」下生效，当前变体是「" + (Variant ?? "（未设置）") +
                             "」，已拒绝启动。变体用 RevLogic.Variant 设置（一般在启动时按配置 / 远端开关设一次）。", "Scope");
                return null;
            }

            // ② 解析归属的域
            RevScopeHandle node;
            if (meta.Domain != null)
            {
                node = _scope.Find(meta.Domain);
                if (node == null)
                {
                    RevLog.Error("Logic " + name + " 声明属于域「" + meta.Domain + "」，但这个域现在没有打开（当前栈：" + _scope.DescribeStack() +
                                 "），已拒绝启动。请先 RevScope.Enter(\"" + meta.Domain + "\")；或者去掉特性里的域名，让它跟随启动时的当前域。", "Scope");
                    return null;
                }
            }
            else
            {
                node = _scope.Top;
            }

            // ★ 域正在关闭时不接新的业务单元：否则它刚启动就面临被关，或者更糟 —— 漏过这一轮回收永远不被关
            if (node != null && node.State != RevScopeState.Open)
            {
                RevLog.Error("Logic " + name + " 想加入域「" + node.Name + "」，但这个域正在关闭，已拒绝启动。" +
                             "多半是在别的 Logic 的 OnStop / 面板关闭回调里启动的：这属于下一阶段的事，请等域关完再启动。", "Scope");
                return null;
            }

            // ③ 建实例并登记（先登记再 OnStart：OnStart 里如果又去 Get / Stop 自己，才能找得到）
            T logic = new T();
            logic.Domain = node != null ? node.Name : null;
            logic.Param = param;
            logic.ScopeNode = node;
            logic.IsRunning = true;

            _running.Add(type, logic);
            if (node != null) { node.Logics.Add(logic); }

            // ④ OnStart：★ 抛异常就整体回滚（把它已经注册的东西全解绑），不留半成品
            try
            {
                logic.InvokeStart(param);
            }
            catch (Exception e)
            {
                RevLog.Exception(e, "Logic " + name + ".OnStart 抛异常，已回滚（它在抛异常之前注册的事件 / 计时器都已清掉）", "Scope");
                Teardown(logic, false);
                return null;
            }

            // OnStart 里自己把自己 Stop 了：已经拆干净了，不用再挂每帧驱动
            if (!logic.IsRunning) { return logic; }

            // ⑤ 有重写 OnTick 才挂每帧驱动
            if (meta.HasTick)
            {
                if (node != null) { node.Tickers.Add(logic); }
                else { _globalTickers.Add(logic); }

                RevScopeBridge.EnsureTickDriver?.Invoke();
            }

            return logic;
        }

        internal T Get<T>() where T : RevLogicBase
        {
            return _running.TryGetValue(typeof(T), out RevLogicBase logic) ? logic as T : null;
        }

        internal bool IsRunning(Type type) => _running.ContainsKey(type);

        // ==================== 停止 ====================

        internal int Stop(Type type)
        {
            if (!_running.TryGetValue(type, out RevLogicBase logic)) { return 0; }

            return Teardown(logic, true);
        }

        /// <summary>停掉某个域名下的全部 Logic（后启动的先停，和"逆序释放"的约定一致）。</summary>
        internal int StopAllOf(RevScopeHandle node)
        {
            if (node.Logics.Count == 0) { return 0; }

            RevLogicBase[] snapshot = node.Logics.ToArray();
            int stopped = 0;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                stopped += Teardown(snapshot[i], true);
            }

            return stopped;
        }

        /// <summary>
        /// 拆除一个 Logic。★ 顺序是承诺：先解绑 → 再调 OnStop（为什么见 RevLogicBase 文件头）。
        /// runOnStop = false 用在"OnStart 抛异常的回滚"：它连 OnStart 都没走完，不该走 OnStop。
        /// </summary>
        private int Teardown(RevLogicBase logic, bool runOnStop)
        {
            if (!logic.IsRunning) { return 0; }

            // 先标记停止：之后哪怕 Tick 缓冲里还有它，也会被跳过
            logic.IsRunning = false;
            _running.Remove(logic.GetType());

            RevScopeHandle node = logic.ScopeNode;
            if (node != null)
            {
                node.Logics.Remove(logic);
                node.Tickers.Remove(logic);
            }

            _globalTickers.Remove(logic);

            Unbind(logic);

            if (runOnStop)
            {
                try
                {
                    logic.InvokeStop();
                }
                catch (Exception e)
                {
                    RevLog.Exception(e, "Logic " + logic.GetType().Name + ".OnStop 抛异常（已隔离，Logic 已经停掉）", "Scope");
                }
            }

            logic.ScopeNode = null;
            return 1;
        }

        /// <summary>
        /// ★ "停止即自动解绑"：把这个 Logic（owner = this）注册过的东西一次摘干净。
        /// 四件事互相独立：每件事单独 try-catch，一件出错不影响其它三件。
        /// </summary>
        private static void Unbind(RevLogicBase logic)
        {
            try { RevEvent.RemoveAllByOwner(logic); }
            catch (Exception e) { LogUnbindError("事件监听", logic, e); }

            try { RevTimer.CancelAllOf(logic); }
            catch (Exception e) { LogUnbindError("计时器", logic, e); }

            // 每帧回调（RevMono）在 Unity 侧，纯 C# 内核够不着，通过桥接口让 Support 层帮忙摘
            try { RevScopeBridge.RemoveMonoOf?.Invoke(logic); }
            catch (Exception e) { LogUnbindError("每帧回调", logic, e); }

            try { RevServices.UnregisterAllOf(logic); }
            catch (Exception e) { LogUnbindError("服务", logic, e); }
        }

        private static void LogUnbindError(string what, RevLogicBase logic, Exception e)
        {
            RevLog.Exception(e, "Logic " + logic.GetType().Name + " 停止时解绑「" + what + "」抛异常（已隔离，其余解绑照常）", "Scope");
        }

        // ==================== 驱动 / 诊断 / 复位 ====================

        internal void CollectGlobalTickers(List<RevLogicBase> buffer)
        {
            if (_globalTickers.Count > 0) { buffer.AddRange(_globalTickers); }
        }

        internal string Dump()
        {
            if (_running.Count == 0) { return "[RevLogic] 没有运行中的业务单元（变体 = " + (Variant ?? "未设置") + "）"; }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[RevLogic] 运行中 ").Append(_running.Count).Append(" 个（变体 = ").Append(Variant ?? "未设置").Append("）：");
            foreach (KeyValuePair<Type, RevLogicBase> pair in _running)
            {
                sb.Append("\n  ").Append(pair.Key.Name).Append("  域=").Append(pair.Value.Domain ?? "（无）");
                if (GetMeta(pair.Key).HasTick) { sb.Append("  每帧"); }
            }

            return sb.ToString();
        }

        /// <summary>进 Play 复位：安静地清掉（不调 OnStop —— 上一局的对象可能已经销毁了）。</summary>
        internal void ResetForNewSession()
        {
            foreach (KeyValuePair<Type, RevLogicBase> pair in _running)
            {
                pair.Value.IsRunning = false;
                pair.Value.ScopeNode = null;
            }

            _running.Clear();
            _globalTickers.Clear();
            Variant = null;
        }

        // ==================== 元信息（每个类型只反射一次）====================

        private static Meta GetMeta(Type type)
        {
            if (MetaCache.TryGetValue(type, out Meta cached)) { return cached; }

            RevLogicAttribute attribute = (RevLogicAttribute)Attribute.GetCustomAttribute(type, typeof(RevLogicAttribute), false);

            // OnTick 的声明类型不是 RevLogicBase 本身 = 子类重写了它
            MethodInfo onTick = type.GetMethod("OnTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                               null, new[] { typeof(float) }, null);

            Meta meta = new Meta
            {
                Domain = attribute != null ? attribute.Domain : null,
                Variant = attribute != null ? attribute.Variant : null,
                HasTick = onTick != null && onTick.DeclaringType != typeof(RevLogicBase),
            };

            MetaCache[type] = meta;
            return meta;
        }
    }
}

// ============================================================
// RevEventCenter.cs —— 事件派发引擎（真正干活的那个）
//
// 位置：Runtime\RevEventSystem\Core\
//
// 【它是什么】
//   一张"事件名 → 监听者集合"的表 + 派发循环。业务不直接用这个类，
//   统一走静态门面 RevEvent（RevEvent.AddEventListener / DispatchEvent / RemoveEventListener）。
//
// 【为什么是 string 事件名】
//   王者用 ulong 事件 ID（自动生成的 EventID.cs），因为它有几千个事件、要求极致性能；
//   但代价是"ID 必须从生成器拿"，业务想临时加一个事件要改生成器。
//   这里选择 string：
//     · 业务自己在业务层定义静态常量（GameEventId.HeroSkinAdd = "Hero.Skin.Add"），
//       拿到了"集中定义 + 拼错编译不过"的全部好处，而框架不必知道有哪些事件；
//     · 调试时日志里直接是 "Hero.Skin.Add" 而不是 3（可读性差很多）。
//   性能上：字典查找是 O(1) 的哈希，字符串是编译期常量（内部驻留、构建时算过哈希后备），
//   派发路径没有任何字符串拼接 —— 这点和 ulong 版本的差距远小于"人读得懂"的收益。
//
// 【与旧事件系统的区别（旧的在 唐老师框架升级版\Runtime\事件中心，1906 行）】
//   ① 参数个数：从 0~16 收到 0~4。超过 4 个参数的正确做法是"定义事件参数结构体"，
//      而不是把泛型参数堆到 16 个（旧框架为此写了 16 个 EventInfo 类）。
//   ② 签名不匹配不再静默：旧代码 (eventDic[name] as EventInfo<T>).actions?.Invoke(...)
//      在"注册的和派发的类型不一致"时会算出 null，然后"什么都不发生"——
//      最容易踩且最难查的坑。现在会明确报出两边类型。
//   ③ 事件名只有一种：旧框架枚举 + string 两套字典并存（同一件事做两遍）。
//   ④ 不做 async 派发：旧框架的 EventTriggerAsync 是 async void，异常会被吞掉、
//      顺序也没保证。"等异步结果再派发"应该在调用方 await 之后做。
//   ⑤ 派发中增删安全：旧框架是直接 List.RemoveAll / delegate +=，
//      回调里反注册会踩到遍历中改集合（见 RevEventHandlerGroup 文件头）。
//
// 【线程约定】只在主线程使用（Unity 的绝大多数 API 也都是这个约定）。
//             需要跨线程就自己把数据切回主线程再派发，不要在子线程 AddEventListener/DispatchEvent。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 事件派发引擎：一张"事件名 → 监听者集合"的表 + 派发循环。
    /// 业务请用静态门面 <see cref="RevEvent"/>。
    /// </summary>
    public sealed class RevEventCenter : RevSingleton<RevEventCenter>
    {
        // 私有构造：配合 RevSingleton 防止外部 new
        private RevEventCenter() { }

        // 字符串用 Ordinal 比较：不受系统区域设置影响（否则土耳其语环境会把 "I"/"i" 认成同一个键）
        private readonly Dictionary<string, RevEventHandlerGroup> _groups =
            new Dictionary<string, RevEventHandlerGroup>(StringComparer.Ordinal);

        private int _seq;   // 注册序号发号器（同优先级时保证"先注册先执行"）

        // ==================== 注册 ====================

        /// <summary>注册一个监听者。<paramref name="handler"/> 的实际类型决定这个事件的签名。</summary>
        public void AddEventListener(string name, Delegate handler, int priority = 0, object owner = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!RevEvent.ValidateName(name, nameof(AddEventListener))) return;

            if (!_groups.TryGetValue(name, out RevEventHandlerGroup group))
            {
                group = new RevEventHandlerGroup();
                _groups[name] = group;
            }

            // ★ 作用域：没写 owner 时，看看有没有"当前域"，有就记下来（域关闭时按它整批摘）。
            //   没有域（RevScope.DefaultNode 为 null）时 scope 就是 null —— 行为与没有作用域系统时逐字一致。
            //   写了显式 owner 就不记：显式永远优先，域不插手。
            object scope = owner == null ? RevScope.DefaultNode : null;

            group.Add(handler, owner, priority, ++_seq, scope);
        }

        /// <summary>反注册：移除该事件上这个委托的全部注册。返回是否真的移除了。</summary>
        public bool RemoveEventListener(string name, Delegate handler)
        {
            if (handler == null || !RevEvent.IsValidName(name)) return false;
            if (!_groups.TryGetValue(name, out RevEventHandlerGroup group)) return false;

            return group.Remove(handler) > 0;
        }

        /// <summary>把归属该对象的监听者全部移除（跨所有事件）。返回移除数量。</summary>
        public int RemoveAllByOwner(object owner)
        {
            if (owner == null) return 0;

            int removed = 0;
            foreach (KeyValuePair<string, RevEventHandlerGroup> pair in _groups)
                removed += pair.Value.RemoveByOwner(owner);

            return removed;
        }

        /// <summary>清空某个事件的全部监听者。</summary>
        public void Clear(string name)
        {
            if (!RevEvent.IsValidName(name)) return;
            if (_groups.TryGetValue(name, out RevEventHandlerGroup group)) group.Clear();
        }

        /// <summary>清空所有事件的全部监听者（切账号 / 回登录 / 重启逻辑时用）。</summary>
        public void ClearAll()
        {
            // 逐个 Clear 而不是直接 _groups.Clear()：
            // 这样列表里的节点能还回对象池；而且此时若正处在派发中，Clear 只会打标记，不会破坏遍历
            bool dispatching = false;
            foreach (KeyValuePair<string, RevEventHandlerGroup> pair in _groups)
            {
                // ★ 必须在 Clear 之前问：Clear 结束后派发标记就被改掉了（且组会被移出字典）
                if (pair.Value.IsDispatching) dispatching = true;
                pair.Value.Clear();
            }

            _groups.Clear();

            // ★ 只有"当前没有任何事件正在派发"时才把发号器归零：
            //   如果在某个回调里调 ClearAll（清场重开很常见），正在遍历的那批旧节点还在列表里没回收，
            //   此时归零会让接下来新注册的节点和它们拿到相同 Seq，同优先级的"先注册先执行"就不再稳定。
            //   归零本身只是为了不让 _seq 无界增长，派发中少归零一次没有任何损失。
            if (!dispatching) _seq = 0;
        }

        /// <summary>清空数据并把对象池也一并清掉（Unity 进 Play / 域重载时调用）。</summary>
        public void ResetAll()
        {
            ClearAll();
            RevEventListener.ClearPool();
        }

        // ==================== 派发（0 ~ 4 个参数）====================
        //
        // 下面 5 个方法是整个引擎的核心，结构一致：
        //   ① 取监听者集合（取不到 → 报"没人监听"并返回 0）；
        //   ② 进入派发（排优先级 + 深度计数）；
        //   ③ 按索引遍历：跳过已标记删除的 → 类型转换（失败就报签名不匹配）→ 执行（异常隔离）；
        //   ④ finally 里退出派发（保证异常也不会让派发计数卡住）。
        //
        // 为什么不用一个泛型模板方法统一掉？
        //   统一只能靠"把参数装进 object"传进来，那会让结构体参数每次派发都装箱分配 —— 
        //   泛型存在的意义就是零装箱，所以宁可重复 5 遍。

        /// <summary>派发 0 参数事件。返回实际被调用的监听者数量。</summary>
        public int DispatchEvent(string name)
        {
            RevEventHandlerGroup group = BeginDispatch(name);
            if (group == null) return 0;

            int invoked = 0;
            try
            {
                List<RevEventListener> list = group.Listeners;
                int count = group.DispatchLimit;                  // ★ 本次派发的范围在开始时锁定：派发中新增的监听者下一轮才生效

                for (int i = 0; i < count; i++)
                {
                    RevEventListener node = list[i];
                    if (node.IsAbandoned) continue;      // 被移除的（可能是刚才某个回调干的）跳过

                    if (!(node.Handler is Action typed))
                    {
                        RevEvent.ReportSignatureMismatch(name, node.Handler, typeof(Action));
                        continue;
                    }

                    try
                    {
                        typed();
                        invoked++;
                    }
                    catch (Exception e)
                    {
                        RevEvent.ReportHandlerException(name, e, node);
                        if (RevEvent.RethrowOnException) throw;   // throw; 保留原始调用栈，方便调试器停在真正出错的那行
                    }
                }
            }
            finally
            {
                group.EndDispatch();                     // 即使 handler 抛异常，派发计数也要退回去
            }

            return invoked;
        }

        /// <summary>派发 1 参数事件（★ 推荐用法：参数放结构体里）。返回实际被调用的监听者数量。</summary>
        public int DispatchEvent<T1>(string name, T1 arg1)
        {
            RevEventHandlerGroup group = BeginDispatch(name);
            if (group == null) return 0;

            int invoked = 0;
            try
            {
                List<RevEventListener> list = group.Listeners;
                int count = group.DispatchLimit;

                for (int i = 0; i < count; i++)
                {
                    RevEventListener node = list[i];
                    if (node.IsAbandoned) continue;

                    if (!(node.Handler is Action<T1> typed))
                    {
                        RevEvent.ReportSignatureMismatch(name, node.Handler, typeof(Action<T1>));
                        continue;
                    }

                    try
                    {
                        typed(arg1);
                        invoked++;
                    }
                    catch (Exception e)
                    {
                        RevEvent.ReportHandlerException(name, e, node);
                        if (RevEvent.RethrowOnException) throw;
                    }
                }
            }
            finally
            {
                group.EndDispatch();
            }

            return invoked;
        }

        /// <summary>派发 2 参数事件。返回实际被调用的监听者数量。</summary>
        public int DispatchEvent<T1, T2>(string name, T1 arg1, T2 arg2)
        {
            RevEventHandlerGroup group = BeginDispatch(name);
            if (group == null) return 0;

            int invoked = 0;
            try
            {
                List<RevEventListener> list = group.Listeners;
                int count = group.DispatchLimit;

                for (int i = 0; i < count; i++)
                {
                    RevEventListener node = list[i];
                    if (node.IsAbandoned) continue;

                    if (!(node.Handler is Action<T1, T2> typed))
                    {
                        RevEvent.ReportSignatureMismatch(name, node.Handler, typeof(Action<T1, T2>));
                        continue;
                    }

                    try
                    {
                        typed(arg1, arg2);
                        invoked++;
                    }
                    catch (Exception e)
                    {
                        RevEvent.ReportHandlerException(name, e, node);
                        if (RevEvent.RethrowOnException) throw;
                    }
                }
            }
            finally
            {
                group.EndDispatch();
            }

            return invoked;
        }

        /// <summary>派发 3 参数事件。返回实际被调用的监听者数量。</summary>
        public int DispatchEvent<T1, T2, T3>(string name, T1 arg1, T2 arg2, T3 arg3)
        {
            RevEventHandlerGroup group = BeginDispatch(name);
            if (group == null) return 0;

            int invoked = 0;
            try
            {
                List<RevEventListener> list = group.Listeners;
                int count = group.DispatchLimit;

                for (int i = 0; i < count; i++)
                {
                    RevEventListener node = list[i];
                    if (node.IsAbandoned) continue;

                    if (!(node.Handler is Action<T1, T2, T3> typed))
                    {
                        RevEvent.ReportSignatureMismatch(name, node.Handler, typeof(Action<T1, T2, T3>));
                        continue;
                    }

                    try
                    {
                        typed(arg1, arg2, arg3);
                        invoked++;
                    }
                    catch (Exception e)
                    {
                        RevEvent.ReportHandlerException(name, e, node);
                        if (RevEvent.RethrowOnException) throw;
                    }
                }
            }
            finally
            {
                group.EndDispatch();
            }

            return invoked;
        }

        /// <summary>派发 4 参数事件（框架支持的上限；再多就该定义参数结构体了）。返回实际被调用的监听者数量。</summary>
        public int DispatchEvent<T1, T2, T3, T4>(string name, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            RevEventHandlerGroup group = BeginDispatch(name);
            if (group == null) return 0;

            int invoked = 0;
            try
            {
                List<RevEventListener> list = group.Listeners;
                int count = group.DispatchLimit;

                for (int i = 0; i < count; i++)
                {
                    RevEventListener node = list[i];
                    if (node.IsAbandoned) continue;

                    if (!(node.Handler is Action<T1, T2, T3, T4> typed))
                    {
                        RevEvent.ReportSignatureMismatch(name, node.Handler, typeof(Action<T1, T2, T3, T4>));
                        continue;
                    }

                    try
                    {
                        typed(arg1, arg2, arg3, arg4);
                        invoked++;
                    }
                    catch (Exception e)
                    {
                        RevEvent.ReportHandlerException(name, e, node);
                        if (RevEvent.RethrowOnException) throw;
                    }
                }
            }
            finally
            {
                group.EndDispatch();
            }

            return invoked;
        }

        // ==================== 诊断 ====================

        /// <summary>某个事件上的监听者数量（只算活跃的：派发中已反注册的节点不计入）。</summary>
        public int GetListenerCount(string name)
        {
            if (!RevEvent.IsValidName(name)) return 0;
            return _groups.TryGetValue(name, out RevEventHandlerGroup group) ? group.Count : 0;
        }

        /// <summary>某个事件上是否还有监听者。</summary>
        public bool HasListener(string name) => GetListenerCount(name) > 0;

        /// <summary>
        /// 还有监听者的事件名清单（按 Ordinal 排序，便于对比两次快照找泄漏）。
        /// 这是调试接口，会分配 List，不要在热路径调用。
        /// </summary>
        public List<string> GetAllEventNames()
        {
            List<string> names = new List<string>(_groups.Count);

            foreach (KeyValuePair<string, RevEventHandlerGroup> pair in _groups)
            {
                if (pair.Value.Count > 0) names.Add(pair.Key);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // ==================== 内部实现 ====================

        /// <summary>取集合并进入派发；没监听者时返回 null（同时按开关报"没人监听"）。</summary>
        private RevEventHandlerGroup BeginDispatch(string name)
        {
            if (!RevEvent.IsValidName(name))
            {
                RevEvent.ValidateName(name, nameof(DispatchEvent));
                return null;
            }

            if (!_groups.TryGetValue(name, out RevEventHandlerGroup group) || group.Count == 0)
            {
                RevEvent.ReportNoListener(name);
                return null;
            }

            group.BeginDispatch();
            return group;
        }
    }
}

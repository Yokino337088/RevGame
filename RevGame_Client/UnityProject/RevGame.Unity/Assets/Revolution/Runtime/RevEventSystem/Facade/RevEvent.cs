// ============================================================
// RevEvent.cs —— 事件系统门面（业务唯一入口）
//
// 位置：Runtime\RevEventSystem\Facade\
//
// 【怎么用（推荐的 5 步）】
//
//   ① 业务侧集中定义事件名（静态常量 —— 拼错编译不过，这是 string 事件名的命门）
//        public static class GameEventId
//        {
//            public const string HeroSkinAdd  = "Hero.Skin.Add";
//            public const string BagItemChange = "Bag.Item.Change";
//        }
//
//   ② 定义事件参数结构体（只放数据；传结构体而不是传一堆参数，是在给未来的自己留退路）
//        public readonly struct HeroSkinAddArgs
//        {
//            public readonly uint HeroId;
//            public readonly uint SkinId;
//            public HeroSkinAddArgs(uint heroId, uint skinId) { HeroId = heroId; SkinId = skinId; }
//        }
//
//   ③ 监听（注册实例方法 + 传 owner，对象销毁时能一把摘干净）
//        RevEvent.AddEventListener<HeroSkinAddArgs>(GameEventId.HeroSkinAdd, OnSkinAdd, owner: this);
//
//        private void OnSkinAdd(HeroSkinAddArgs args) { ... }
//
//   ④ 派发
//        RevEvent.DispatchEvent(GameEventId.HeroSkinAdd, new HeroSkinAddArgs(heroId, skinId));
//
//   ⑤ 销毁时（★ 漏了这步，事件系统就会一直攥着已销毁的对象 → 内存泄漏）
//        private void OnDestroy() => RevEvent.RemoveAllByOwner(this);
//
// 【参数个数：0 ~ 4】
//   框架只提供 0/1/2/3/4 个参数的重载。超过 4 个参数请定义事件参数结构体 ——
//   这也是推荐姿势：参数一旦超过 2 个，裸参数列表就开始"看不懂谁是谁"了。
//   （旧事件系统排到 16 个泛型参数、配 16 个 EventInfo 类，真正用到的没几个。）
//
// 【同一个事件名只能有一种签名】
//   "Hero.Skin.Add" 的所有监听者必须都是 Action<HeroSkinAddArgs>。
//   注册成别的类型不会崩，但派发时会明确报"签名不匹配"并跳过它 —— 
//   绝不静默丢弃（旧框架用 `as` 转换，失败就什么都不发生，这是最难查的坑）。
//
// 【反注册的两种姿势】
//   · RevEvent.RemoveEventListener(name, handler)      精确摘掉某个委托（方法组、缓存的委托变量都能匹配）
//   · RevEvent.RemoveAllByOwner(this)         按归属对象批量摘（销毁时的标准动作，防泄漏）
//   ⚠️ 用匿名 lambda 注册时，Remove 匹配不上（同一个 lambda 每次执行都会生成新的委托实例），
//      要么把委托存成字段，要么用 owner 批量移除。
//
// 【性能】
//   · 结构体参数走泛型委托，全程零装箱；派发过程不复制列表、不分配对象；
//   · 监听者节点走对象池，Add/Remove 频繁也不会产生 GC。
//   （工程外验证：结构体载荷连续派发零分配，详见文档。）
//
// 【线程约定】只在主线程使用（AddEventListener / DispatchEvent / RemoveEventListener 都算）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution
{
    /// <summary>
    /// 事件系统门面：注册 / 派发 / 反注册 / 诊断。
    /// 底层引擎是 <see cref="RevEventCenter"/>，业务只需要认这一个静态类。
    /// </summary>
    public static class RevEvent
    {
        // ==================== 一、配置（策略出口）====================

        /// <summary>
        /// 普通消息出口（空事件名、签名不匹配、无人监听…）。
        /// 默认输出到标准错误；Unity 下由 RevEventUnityHooks 自动接到 Debug.LogWarning。
        /// </summary>
        public static Action<string> Log { get; set; }

        /// <summary>
        /// 监听者抛异常的出口。默认 null → 异常信息走 <see cref="Log"/>。
        /// 想上报到自己的埋点/日志系统就设这个（记得自己也把异常打出来，否则 Console 里会没有）。
        /// </summary>
        public static Action<Exception, string> OnException { get; set; }

        /// <summary>
        /// 调试开关：监听者抛异常时立刻向外抛（不隔离）。
        /// 打开后调试器会停在真正出错的那一行，代价是一个监听者出错会中断本次派发的其余监听者。
        /// 定位 bug 时打开，定完记得关。
        /// </summary>
        public static bool RethrowOnException { get; set; }

        /// <summary>
        /// 调试开关：派发时无人监听就告警（同一个事件名只提示一次，不刷屏）。
        /// 事件系统最坑的问题就是"发了没人收还一声不响"，调试期建议打开。
        /// </summary>
        public static bool LogNoListener { get; set; }

        // 已经提示过"无人监听"的事件名（避免同一个事件刷屏）
        private static readonly HashSet<string> WarnedNoListener = new HashSet<string>(StringComparer.Ordinal);

        // ==================== 二、注册 ====================

        /// <summary>注册监听（0 参数）。</summary>
        /// <param name="name">事件名（建议用静态常量）</param>
        /// <param name="handler">监听者</param>
        /// <param name="priority">优先级：越大越先执行（同优先级按注册顺序）</param>
        /// <param name="owner">归属对象，用于 <see cref="RemoveAllByOwner"/>；不传则取 handler.Target</param>
        public static void AddEventListener(string name, Action handler, int priority = 0, object owner = null)
            => RevEventCenter.Instance.AddEventListener(name, handler, priority, owner);

        /// <summary>注册监听（1 参数）。★ 推荐：参数传事件参数结构体。</summary>
        public static void AddEventListener<T1>(string name, Action<T1> handler, int priority = 0, object owner = null)
            => RevEventCenter.Instance.AddEventListener(name, handler, priority, owner);

        /// <summary>注册监听（2 参数）。</summary>
        public static void AddEventListener<T1, T2>(string name, Action<T1, T2> handler, int priority = 0, object owner = null)
            => RevEventCenter.Instance.AddEventListener(name, handler, priority, owner);

        /// <summary>注册监听（3 参数）。</summary>
        public static void AddEventListener<T1, T2, T3>(string name, Action<T1, T2, T3> handler, int priority = 0, object owner = null)
            => RevEventCenter.Instance.AddEventListener(name, handler, priority, owner);

        /// <summary>注册监听（4 参数，上限）。</summary>
        public static void AddEventListener<T1, T2, T3, T4>(string name, Action<T1, T2, T3, T4> handler, int priority = 0, object owner = null)
            => RevEventCenter.Instance.AddEventListener(name, handler, priority, owner);

        // ==================== 三、派发 ====================

        /// <summary>派发 0 参数事件。返回实际被调用的监听者数量。</summary>
        public static int DispatchEvent(string name)
            => RevEventCenter.Instance.DispatchEvent(name);

        /// <summary>派发 1 参数事件。返回实际被调用的监听者数量。</summary>
        public static int DispatchEvent<T1>(string name, T1 arg1)
            => RevEventCenter.Instance.DispatchEvent(name, arg1);

        /// <summary>派发 2 参数事件。返回实际被调用的监听者数量。</summary>
        public static int DispatchEvent<T1, T2>(string name, T1 arg1, T2 arg2)
            => RevEventCenter.Instance.DispatchEvent(name, arg1, arg2);

        /// <summary>派发 3 参数事件。返回实际被调用的监听者数量。</summary>
        public static int DispatchEvent<T1, T2, T3>(string name, T1 arg1, T2 arg2, T3 arg3)
            => RevEventCenter.Instance.DispatchEvent(name, arg1, arg2, arg3);

        /// <summary>派发 4 参数事件。返回实际被调用的监听者数量。</summary>
        public static int DispatchEvent<T1, T2, T3, T4>(string name, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
            => RevEventCenter.Instance.DispatchEvent(name, arg1, arg2, arg3, arg4);

        // ==================== 四、反注册 ====================

        /// <summary>反注册（0 参数）。返回是否真的移除了。</summary>
        public static bool RemoveEventListener(string name, Action handler)
            => RevEventCenter.Instance.RemoveEventListener(name, handler);

        /// <summary>反注册（1 参数）。返回是否真的移除了。</summary>
        public static bool RemoveEventListener<T1>(string name, Action<T1> handler)
            => RevEventCenter.Instance.RemoveEventListener(name, handler);

        /// <summary>反注册（2 参数）。返回是否真的移除了。</summary>
        public static bool RemoveEventListener<T1, T2>(string name, Action<T1, T2> handler)
            => RevEventCenter.Instance.RemoveEventListener(name, handler);

        /// <summary>反注册（3 参数）。返回是否真的移除了。</summary>
        public static bool RemoveEventListener<T1, T2, T3>(string name, Action<T1, T2, T3> handler)
            => RevEventCenter.Instance.RemoveEventListener(name, handler);

        /// <summary>反注册（4 参数）。返回是否真的移除了。</summary>
        public static bool RemoveEventListener<T1, T2, T3, T4>(string name, Action<T1, T2, T3, T4> handler)
            => RevEventCenter.Instance.RemoveEventListener(name, handler);

        /// <summary>
        /// 把归属该对象的监听者全部移除（跨所有事件）。返回移除数量。
        /// ★ 界面 / 模块销毁时的标准动作：一个对象注册了多个事件却逐个反注册，
        ///   迟早会漏一个，漏掉的那个就会把已销毁的对象一直挂住（内存泄漏）。
        /// </summary>
        public static int RemoveAllByOwner(object owner)
            => RevEventCenter.Instance.RemoveAllByOwner(owner);

        /// <summary>清空某个事件的全部监听者。</summary>
        public static void Clear(string name)
            => RevEventCenter.Instance.Clear(name);

        /// <summary>清空所有事件的全部监听者（切账号 / 回登录 / 重开一局时用）。</summary>
        public static void ClearAll()
            => RevEventCenter.Instance.ClearAll();

        /// <summary>
        /// 连对象池一起重置。Unity 进 Play / 域重载时由 RevEventUnityHooks 自动调用，
        /// 业务一般不需要手动调。
        /// </summary>
        public static void ResetAll()
        {
            RevEventCenter.Instance.ResetAll();
            WarnedNoListener.Clear();
        }

        // ==================== 五、诊断 ====================

        /// <summary>某个事件上有多少监听者（只算活跃的）。</summary>
        public static int GetListenerCount(string name)
            => RevEventCenter.Instance.GetListenerCount(name);

        /// <summary>
        /// 某个事件上是否还有监听者（只算活跃的）。
        /// ★ 派发过程中被反注册的监听者不算 —— 它们已经收不到任何后续派发了。
        /// </summary>
        public static bool HasListener(string name)
            => RevEventCenter.Instance.HasListener(name);

        /// <summary>
        /// 还有监听者的事件名清单（含排序，便于对比两次快照找泄漏）。调试接口，会分配 List。
        /// </summary>
        public static List<string> GetAllEventNames()
            => RevEventCenter.Instance.GetAllEventNames();

        // ==================== 六、内部：校验与上报 ====================
        // 派发引擎只管"机制"（表 + 遍历），"出错怎么表现"这类策略统一放在门面里。

        /// <summary>事件名是否合法。</summary>
        internal static bool IsValidName(string name)
            => !string.IsNullOrEmpty(name);

        /// <summary>校验事件名，非法就上报。返回是否合法。</summary>
        internal static bool ValidateName(string name, string api)
        {
            if (IsValidName(name)) return true;

            Write($"[RevEvent] {api} 传入了空事件名，已忽略。" +
                  $"事件名请集中定义成静态常量（例如 GameEventId.HeroSkinAdd = \"Hero.Skin.Add\"），避免手写错字。");
            return false;
        }

        /// <summary>派发时无人监听（只在 LogNoListener 打开时提示，同一事件名只提示一次）。</summary>
        internal static void ReportNoListener(string name)
        {
            if (!LogNoListener) return;
            if (!WarnedNoListener.Add(name)) return;

            Write($"[RevEvent] 事件 \"{name}\" 派发了，但一个监听者都没有。常见原因：" +
                  $"① 事件名拼错（建议改用静态常量）；" +
                  $"② 监听方注册的委托签名和派发的签名不一致；" +
                  $"③ 监听方注册的时机晚于这次派发。");
        }

        /// <summary>事件上存在签名不匹配的监听者（注册的是别的参数类型）。绝不静默跳过。</summary>
        internal static void ReportSignatureMismatch(string name, Delegate handler, Type expected)
        {
            string actual = handler == null ? "<已弃用>" : Describe(handler.GetType());

            Write($"[RevEvent] 事件 \"{name}\" 上有签名不匹配的监听者，已跳过：" +
                  $"监听者实际是 {actual}，本次派发需要 {Describe(expected)}。" +
                  $"同一个事件名的所有监听者必须使用同一种签名（★ 推荐统一用事件参数结构体，如 Action<HeroSkinAddArgs>）。");
        }

        /// <summary>
        /// 监听者抛异常：上报但隔离（其余监听者照常执行）。
        /// 一个模块的 bug 不该拖垮整个事件系统 —— 但也不能一声不响，所以必定有出口。
        /// </summary>
        internal static void ReportHandlerException(string name, Exception e, RevEventListener node)
        {
            string message = $"[RevEvent] 事件 \"{name}\" 的监听者 {node} 抛出异常（已隔离，其余监听者不受影响）：{e}";

            Action<Exception, string> hook = OnException;
            if (hook == null)
            {
                Write(message);
                return;
            }

            try
            {
                hook(e, message);
            }
            catch (Exception reportException)
            {
                Write($"{message}\n[RevEvent] OnException 上报回调也抛出了异常：{reportException}");
            }
        }

        /// <summary>统一的消息出口：没人接日志时走框架日志系统的纯 C# 兜底（它再退到标准错误），绝不静默。</summary>
        private static void Write(string message)
        {
            try
            {
                Action<string> sink = Log;
                if (sink != null)
                {
                    sink(message);
                    return;
                }

                RevLog.Warn(message, "Event");
            }
            catch (Exception sinkException)
            {
                // 日志系统属于观测出口，不能反过来打断事件派发。
                try { RevLog.Exception(sinkException, "事件系统日志出口异常", "Event"); }
                catch { }
            }
        }

        /// <summary>把类型名写成好读的形式（Action`2 → Action&lt;Int32, String&gt;），只用在出错路径上。</summary>
        private static string Describe(Type type)
        {
            if (type == null) return "<null>";
            if (!type.IsGenericType) return type.Name;

            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            Type[] args = type.GetGenericArguments();
            StringBuilder sb = new StringBuilder(name.Length + args.Length * 8);
            sb.Append(name).Append('<');

            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(args[i]));
            }

            sb.Append('>');
            return sb.ToString();
        }
    }
}

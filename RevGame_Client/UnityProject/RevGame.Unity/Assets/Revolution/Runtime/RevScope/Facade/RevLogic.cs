// ============================================================
// RevLogic.cs —— 业务单元门面：启动 / 停止 / 查询"没有界面但要活一段时间的业务"
//
// 位置：Runtime\RevScope\Facade\
//
// 【三步走】
//   ① 写一个类继承 RevLogicBase，用 [RevLogic(域名)] 声明它属于哪个域（详见 RevLogicBase 文件头）；
//   ② 启动：      RevLogic.Start<DamageBoardLogic>();
//   ③ 不用管停止：它所在的域关闭时，框架自动 Stop 它（并自动解绑它注册的一切）；想提前停：RevLogic.Stop<T>()。
//
// 【常用写法】
//     RevLogic.Start<DamageBoardLogic>();                  // 启动；已经在跑就返回已有实例（同一类型只有一个）
//     RevLogic.Start<GuideLogic>(stepInfo);                // 带参数（OnStart(object) / Param 能拿到）
//     RevLogic.Get<DamageBoardLogic>();                    // 取（没在跑返回 null）
//     if (RevLogic.TryGet(out DamageBoardLogic board)) { } // 或 TryGet
//     RevLogic.IsRunning<DamageBoardLogic>();
//     RevLogic.Stop<DamageBoardLogic>();                   // 手动停
//
// 【为什么 Start 可能返回 null】（都会有明确的人话报错，不会悄悄失败）
//   · 它声明属于的域现在没有打开           → 先 RevScope.Enter(...)
//   · 它声明只在某个变体下生效，当前变体不符  → 检查 RevLogic.Variant
//   · 它的域正在关闭                       → 这属于"下一阶段"的事，等域关完再启动
//   · OnStart 抛了异常                     → 已经整体回滚，什么都没留下
//
// 【A/B 变体（最小版本）】
//     [RevLogic(RevDomain.Battle, Variant = "Season")]  public sealed class SeasonBoardLogic  : RevLogicBase { }
//     [RevLogic(RevDomain.Battle, Variant = "Classic")] public sealed class ClassicBoardLogic : RevLogicBase { }
//     RevLogic.Variant = "Season";                      // 启动时按配置 / 远端开关设一次
//   诚实说明：这里只做"变体过滤"（不匹配的 Start 会被拒绝）。如果你要的是"按接口取、实现随变体换"，
//   请用 RevServices（服务定位器本来就是"接口 → 实现"的现成机制），不要在这里再长一套容器。
//
// 【线程】只在主线程使用。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>业务单元门面：Start / Stop / Get / 变体 / 诊断。</summary>
    public static class RevLogic
    {
        private static RevLogicHost Host => RevScope.Core.Logics;

        // ============================================================
        // 启动 / 停止
        // ============================================================

        /// <summary>启动业务单元。已经在跑就返回已有实例；被拒绝（域没开 / 变体不符 / OnStart 抛异常）返回 null 并有明确报错。</summary>
        public static T Start<T>() where T : RevLogicBase, new() => Host.Start<T>(null);

        /// <summary>启动业务单元（带参数：<c>OnStart(object)</c> / <see cref="RevLogicBase.Param"/> 能拿到）。</summary>
        public static T Start<T>(object param) where T : RevLogicBase, new() => Host.Start<T>(param);

        /// <summary>
        /// 停止业务单元：★ 先自动解绑它注册的事件 / 计时器 / 每帧回调 / 服务，最后才调 OnStop。
        /// 没在跑返回 0（重复调用安全）。
        /// </summary>
        public static int Stop<T>() where T : RevLogicBase => Host.Stop(typeof(T));

        /// <summary>停掉某个域下的全部业务单元（域关闭时框架会自动调，一般不用手动）。域不存在返回 0。</summary>
        public static int StopAll(string domain)
        {
            RevScopeHandle node = RevScope.Core.Find(domain);
            return node == null ? 0 : Host.StopAllOf(node);
        }

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>取运行中的业务单元（没在跑返回 null）。</summary>
        public static T Get<T>() where T : RevLogicBase => Host.Get<T>();

        /// <summary>尝试取：拿到返回 true，否则 false 且 <paramref name="logic"/> 为 null。</summary>
        public static bool TryGet<T>(out T logic) where T : RevLogicBase
        {
            logic = Host.Get<T>();
            return logic != null;
        }

        /// <summary>它现在在跑吗。</summary>
        public static bool IsRunning<T>() where T : RevLogicBase => Host.IsRunning(typeof(T));

        /// <summary>运行中的业务单元个数。</summary>
        public static int Count => Host.Count;

        // ============================================================
        // 变体 / 诊断
        // ============================================================

        /// <summary>当前变体（A/B 用）。启动时按配置设一次；声明了 Variant 的 Logic 只有和它相同才允许 Start。</summary>
        public static string Variant
        {
            get => Host.Variant;
            set => Host.Variant = value;
        }

        /// <summary>把运行中的业务单元打成一段文本（类型 / 所在域 / 是否每帧）。</summary>
        public static string Dump() => Host.Dump();
    }
}

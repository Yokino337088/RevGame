// ============================================================
// RevMono.cs —— 公共 Mono 模块唯一入口（小白只需要读这一个文件）
//
// 位置：Runtime\RevPublicMono\Facade\
//
// 【什么时候用它】你有一个**纯 C# 类**（管理器 / 服务 / 工具），它：
//   · 想每帧做点事（但它没有 Update）→ <see cref="AddUpdate"/> / AddLateUpdate / AddFixedUpdate
//   · 想跑一段协程（但它不能 StartCoroutine）→ <see cref="StartCoroutine(System.Collections.IEnumerator)"/>
//
// 【三条铁律】
//   ① **选对相位**：跟相机/UI 的跟随逻辑用 LateUpdate；物理/帧率无关用 FixedUpdate；其余用 Update。
//   ② **随对象销毁一行清干净**：加监听时给 `owner: this`，销毁时 `RevMono.RemoveAllOf(this)`；
//      或者 `using (RevMono.OpenScope()) { ... }` 出一块全停。忘记移除是这类模块唯一的泄漏方式。
//   ③ **别在监听里做重活**：它就跑在主线程的 Update 里，重活会直接吃掉这一帧（要异步请用 RevTask）。
//
// 【最短上手】
// <code>
// RevMono.AddUpdate(OnTick);                              // 每帧一次（重复调用只生效一次）
// RevMono.AddLateUpdate(FollowCamera, owner: this);       // 每帧最后（相机跟随）
// RevMono.AddFixedUpdate(Step, owner: this);              // 物理帧
// RevMono.StartCoroutine(LoadThenPlay());                 // 跑协程（返回 Coroutine，可单独停）
// RevMono.StopAllCoroutines();                            // 全停
// </code>
//
// 【零配置】第一次用到就自动创建一个隐藏宿主（DontDestroyOnLoad）驱动这些回调；
//   不需要摆场景物体、不需要挂脚本、不需要自己写 Update。
//
// 【和框架里另外两个设施的分工（别选错）】
//   RevTimer → "多久之后 / 每隔多久"；RevTask → "await 一帧 / 等资源"；本模块 → "给我一个每帧回调 / 跑协程"。
// ============================================================
using System;
using System.Collections;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 公共 Mono 模块门面：给纯 C# 代码每帧回调与协程能力。
    /// 门面是静态的（业务调用最省事），内核 <see cref="RevMonoCore"/> 是实例。
    /// </summary>
    public static class RevMono
    {
        internal static readonly RevMonoCore Core = new RevMonoCore();

        // ==================== 每帧回调（三个相位）====================

        /// <summary>每帧回调（跟 Update 同频）。重复加同一个委托只生效一次，返回是否真的加进去了。</summary>
        public static bool AddUpdate(Action action, object owner = null) => Core.Add(RevMonoPhase.Update, action, owner);

        /// <summary>移除每帧回调（没加过返回 false；重复移除安全）。</summary>
        public static bool RemoveUpdate(Action action) => Core.Remove(RevMonoPhase.Update, action);

        /// <summary>每帧最后的回调（跟 LateUpdate 同频）：相机/UI 跟随这类"要在别人之后"的逻辑。</summary>
        public static bool AddLateUpdate(Action action, object owner = null) => Core.Add(RevMonoPhase.LateUpdate, action, owner);

        /// <summary>移除每帧最后的回调。</summary>
        public static bool RemoveLateUpdate(Action action) => Core.Remove(RevMonoPhase.LateUpdate, action);

        /// <summary>物理帧回调（跟 FixedUpdate 同频，一帧可能 0 次或多次）。</summary>
        public static bool AddFixedUpdate(Action action, object owner = null) => Core.Add(RevMonoPhase.FixedUpdate, action, owner);

        /// <summary>移除物理帧回调。</summary>
        public static bool RemoveFixedUpdate(Action action) => Core.Remove(RevMonoPhase.FixedUpdate, action);

        /// <summary>按相位加（上面三个入口的通用版）。</summary>
        public static bool Add(RevMonoPhase phase, Action action, object owner = null) => Core.Add(phase, action, owner);

        /// <summary>按相位移除。</summary>
        public static bool Remove(RevMonoPhase phase, Action action) => Core.Remove(phase, action);

        // ==================== 清理（防泄漏）====================

        /// <summary>
        /// 移除某个 owner 的全部监听者（随对象销毁一行清干净）。
        /// <code>void OnDestroy() =&gt; RevMono.RemoveAllOf(this);</code>
        /// </summary>
        public static int RemoveAllOf(object owner)
        {
            int removed = Core.RemoveAllOf(owner);
            if (owner is RevMonoScope scope) removed += scope.StopRoutines();
            return removed;
        }

        /// <summary>清空全部业务监听者，保留框架基础驱动（切场景 / 重开一局用）。返回清掉的数量。</summary>
        public static int Clear()
        {
            return Core.Clear(preserveInfrastructure: true);
        }

        // 仅供框架 Support 安装心跳；业务的 Clear 不应使 UI / 服务 / 域逻辑永久失去驱动。
        internal static bool AddInfrastructureUpdate(Action action, object owner)
        {
            return Core.Add(RevMonoPhase.Update, action, owner, infrastructure: true);
        }

        /// <summary>
        /// 开一个作用域：`using` 一块，退出时把这块里加的监听者全摘掉、协程全停。
        /// <code>using (var scope = RevMono.OpenScope()) { scope.AddUpdate(OnTick); scope.StartCoroutine(Run()); }</code>
        /// </summary>
        public static RevMonoScope OpenScope() => new RevMonoScope();

        // ==================== 协程 ====================

        /// <summary>
        /// 跑一段协程（纯 C# 类也能用）。返回 Unity 的 <see cref="Coroutine"/>，可单独停。
        /// ★ 协程里抛异常会被隔离并报到统一日志（不会像原生那样静默中断）。
        /// </summary>
        public static Coroutine StartCoroutine(IEnumerator routine) => RevMonoDriver.StartRoutine(routine);

        /// <summary>停一个协程（传 StartCoroutine 的返回值）。</summary>
        public static void StopCoroutine(Coroutine routine) => RevMonoDriver.StopRoutine(routine);

        /// <summary>停掉本模块启动的全部协程。</summary>
        public static void StopAllCoroutines() => RevMonoDriver.StopAllRoutines();

        // ==================== 读数（调试/自检）====================

        /// <summary>Update 相位的监听者数量。</summary>
        public static int UpdateCount => Core.CountOf(RevMonoPhase.Update);

        /// <summary>LateUpdate 相位的监听者数量。</summary>
        public static int LateUpdateCount => Core.CountOf(RevMonoPhase.LateUpdate);

        /// <summary>FixedUpdate 相位的监听者数量。</summary>
        public static int FixedUpdateCount => Core.CountOf(RevMonoPhase.FixedUpdate);

        /// <summary>三个相位的监听者总数。</summary>
        public static int Count => UpdateCount + LateUpdateCount + FixedUpdateCount;

        /// <summary>隐藏宿主是否已就绪（第一次加监听/跑协程时自动创建）。</summary>
        public static bool IsRunning => RevMonoCore.DriverReady;

        // ==================== 出口（可观测）====================

        /// <summary>失败事件：参数 =（原因码, 说明）。超上限、监听者/协程抛异常时触发。</summary>
        public static event Action<RevMonoErrorReason, string> Failed
        {
            add => Core.Failed += value;
            remove => Core.Failed -= value;
        }

        /// <summary>异常出口（默认接到框架日志系统；想上报自己的埋点就覆盖它）。</summary>
        public static Action<Exception, string> OnException
        {
            get => Core.OnException;
            set => Core.OnException = value;
        }

        /// <summary>进 Play / 换工程时复位（由 <c>Support\RevMonoUnityHooks</c> 调用，业务不用管）。</summary>
        internal static void ResetForNewSession() => Core.ResetForNewSession();
    }
}

// ============================================================
// RevTimer.cs —— 计时器唯一入口（小白只需要读这一个文件）
//
// 位置：Runtime\RevTimer\Facade\
//
// 【三条铁律】
//   ① **一行就能用**：`RevTimer.After(2f, () => RevUI.Close("Loading"))` ——
//      不摆场景物体、不挂脚本、不写 Update（首次使用自动创建隐藏宿主驱动）。
//   ② **循环计时器一定要能停**：给它一个 owner（`owner: this`）或用 `using (RevTimer.OpenScope())`，
//      随对象销毁时一行 `RevTimer.CancelAllOf(this)` —— 这就是"忘取消 → 永久泄漏"的正解。
//   ③ **选对时间域**：UI 倒计时用 Unscaled（不受 timeScale 影响）、表现演出用 Scaled（默认）、
//      战斗推进用 Fixed、活动/榜单用 Server（绝对时刻，防改设备时间）。选错 = 上线后"切后台回来全错"。
//
// 【最短上手】
// <code>
// RevTimer.After(2f, () => RevUI.Close("Loading"));                 // 2 秒后一次（最常用）
// RevTimer.NextFrame(() => go.SetActive(true));                     // 下一帧
// RevTimer.Every(1f, () => Refresh(), times: 10);                    // 每秒一次，共 10 次（times 省略 = 无限）
// RevTimer.Every(1f, i => count.text = i.ToString(), times: 3);       // 带次数参数的重载：i = 第几次（1 开始）
// RevTimer.At(activityEndUtc, OnActivityEnd);                        // 到某个服务器绝对时刻（需先 SyncServerTime）
// var t = RevTimer.Every(0.5f, UpdateCountdown);                     // 句柄：可停/可暂停/可读剩余
// float left = (float)t.Left;  float fill = (float)t.Progress;
// </code>
//
// 【零配置】不摆物体、不挂脚本。第一次用到计时器时会自动创建一个隐藏宿主（DontDestroyOnLoad）每帧推进。
//   想自己驱动（例如希望跟着你的逻辑帧走）：在 Update 里调一次 `RevTimer.Tick(...)`，宿主会自动让位。
//
// 【框架不提供"你还在跑吗"的轮询哲学】要感知"到点了"就写在回调里；
//   只有"剩余多久/进度多少"这类**数据**才提供读数（Left / Progress）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>
    /// 计时器门面。门面是静态的（业务调用最省事），内核 <see cref="RevTimerCore"/> 是实例
    /// （可多实例、可脱离门面在工程外单测）。
    /// </summary>
    public static class RevTimer
    {
        /// <summary>默认内核（高级用法：想自己 new 一个独立内核时用；日常直接用静态门面）。</summary>
        internal static readonly RevTimerCore Core = new RevTimerCore();

        // ============================================================
        // 一行播放（4 个创建入口，够用了：到点 / 下一帧 / 每间隔 / 到绝对时刻）
        // ============================================================

        /// <summary>
        /// N 秒后做一件事（一次性）。
        /// <code>RevTimer.After(1.5f, () => RevUI.Close("Tips"));</code>
        /// </summary>
        /// <param name="seconds">时长（秒）。传 0 = "下一帧"（见 <see cref="NextFrame"/>）</param>
        /// <param name="domain">时间域：UI 倒计时/超时请传 <see cref="RevTimeDomain.Unscaled"/></param>
        /// <param name="owner">所有者：随对象销毁时用 <see cref="CancelAllOf"/> 一行清干净（可选）。
        /// ★ 不写 owner 时：如果当前开着一个作用域（<see cref="RevScope"/>），计时器自动归这个域，域关闭时一起停；
        /// 没有域时和从前完全一样（没人管，只能靠句柄 / Clear 停）。</param>
        public static RevTimerHandle After(float seconds, Action callback,
            RevTimeDomain domain = RevTimeDomain.Scaled, object owner = null)
            => Core.Add(domain, seconds, interval: 0d, repeats: 1, absolute: false,
                targetUtc: default, callback, repeatCallback: null, owner ?? RevScope.DefaultNode);

        /// <summary>下一帧做一件事（等价于 <c>After(0f, ...)</c>）。</summary>
        public static RevTimerHandle NextFrame(Action callback, object owner = null)
            => Core.Add(RevTimeDomain.Scaled, 0d, 0d, repeats: 1, absolute: false,
                targetUtc: default, callback, repeatCallback: null, owner ?? RevScope.DefaultNode);

        /// <summary>
        /// 每 <paramref name="interval"/> 秒一次（<paramref name="times"/> = -1 表示无限，必须记得 Stop）。
        /// <code>RevTimer.Every(1f, () => RefreshHp());              // 无限（记得停）</code>
        /// <code>RevTimer.Every(0.2f, () => Send(), times: 5);      // 5 次后自动结束</code>
        /// </summary>
        public static RevTimerHandle Every(float interval, Action callback, int times = -1,
            RevTimeDomain domain = RevTimeDomain.Scaled, object owner = null)
            => Core.Add(domain, interval, interval, times, absolute: false,
                targetUtc: default, callback, repeatCallback: null, owner ?? RevScope.DefaultNode);

        /// <summary>
        /// 每 <paramref name="interval"/> 秒一次，回调带"第几次"（从 1 开始）。
        /// <code>RevTimer.Every(1f, i => tip.text = $"{i}/10", times: 10);</code>
        /// </summary>
        public static RevTimerHandle Every(float interval, Action<int> callback, int times = -1,
            RevTimeDomain domain = RevTimeDomain.Scaled, object owner = null)
            => Core.Add(domain, interval, interval, times, absolute: false,
                targetUtc: default, callback: null, repeatCallback: callback, owner ?? RevScope.DefaultNode);

        /// <summary>
        /// 到某个**服务器绝对时刻**做一件事（防改设备时间、挂起无关）。
        /// ★ 用之前必须先校准：<see cref="SyncServerTime(System.DateTime)"/>。
        /// <code>RevTimer.At(activityEndUtc, () => RefreshActivity());</code>
        /// </summary>
        public static RevTimerHandle At(DateTime serverUtc, Action callback, object owner = null)
            => Core.Add(RevTimeDomain.Server, duration: 0d, interval: 0d, repeats: 1, absolute: true,
                targetUtc: serverUtc, callback, repeatCallback: null, owner ?? RevScope.DefaultNode);

        // ============================================================
        // 秒表（和计时器共用同一套时间源，不用自己算时间）
        // ============================================================

        /// <summary>
        /// 起一个秒表。测"游戏内实际耗时"用 <see cref="RevTimeDomain.Scaled"/>（默认），
        /// 测"真实经过时间"（性能/网络/加载）用 <see cref="RevTimeDomain.Unscaled"/>。
        /// <code>var sw = RevTimer.StartStopwatch(); ... float ms = (float)sw.Elapsed * 1000f;</code>
        /// </summary>
        public static RevStopwatch StartStopwatch(RevTimeDomain domain = RevTimeDomain.Scaled)
            => Core.StartStopwatch(domain);

        // ============================================================
        // 清理（防泄漏：这两行能省掉一整类线上问题）
        // ============================================================

        /// <summary>
        /// 停掉某个 owner 的全部计时器。随对象销毁（OnDestroy/OnDisable）调它，代替满世界找句柄 Stop。
        /// <code>RevTimer.CancelAllOf(this);</code>
        /// </summary>
        /// <returns>实际停掉的数量</returns>
        public static int CancelAllOf(object owner) => Core.CancelAllOf(owner);

        /// <summary>清空全部计时器（切场景/重开一局用）。返回停掉的数量。</summary>
        public static int Clear() => Core.ClearAll();

        /// <summary>
        /// 开一个作用域：`using` 一块，退出时把这块里创建的计时器全停。
        /// <code>using (var scope = RevTimer.OpenScope()) { scope.Every(1f, Refresh); }   // 出块即全停</code>
        /// </summary>
        public static RevTimerScope OpenScope() => new RevTimerScope();

        // ============================================================
        // 全局开关与读数
        // ============================================================

        /// <summary>
        /// 全局暂停：整个模块停止推进（含 Server 域与 Fixed 域），时间读数也随之冻结。
        /// ★ 这是"模块开关"，不是 Unity 的 <c>Time.timeScale</c>（后者管的是 Scaled 域的时间源）。
        /// 用法：打开设置面板/切场景加载时置 true，回来后置 false。
        /// </summary>
        public static bool Paused
        {
            get => Core.Paused;
            set => Core.Paused = value;
        }

        /// <summary>游戏时间（受 timeScale 影响的累计秒数，从进 Play 起）。</summary>
        public static double GameTime => Core.GameTime;

        /// <summary>真实时间（不受 timeScale 影响的累计秒数，从进 Play 起）。</summary>
        public static double RealTime => Core.RealTime;

        /// <summary>当前存活的计时器数量（调试/自检用）。</summary>
        public static int Count => Core.Count;

        /// <summary>是否已被手动驱动接管（<see cref="Tick"/> 被调用过之后为 true）。</summary>
        public static bool ManualDriven => Core.ManualDriven;

        // ============================================================
        // 服务器时间（Server 域的前提）
        // ============================================================

        /// <summary>
        /// 校准服务器时间：把"此刻的服务器 UTC"记成锚点，之后用本地 realtime 外推。
        /// ★ 每次收到服务器时间都调一次是预期用法（抑制本地计时漂移）。
        /// ★ Bug 修复（2026-09-30）：锚点改用 <c>LatestRealtime</c>（暂停时也更新）——
        ///   旧实现用 <c>LastRealtime</c>，全局暂停期间它会停在暂停前，此刻校准会把
        ///   "暂停的时长"整个丢掉，恢复后 Server 域凭空快出暂停时长（At 计时器全错位）。
        /// </summary>
        /// <remarks>
        /// 锚点取"此刻真实的 realtime"（由驱动层接到 Unity 时钟），而不是最近一次 Tick 的值：
        /// 游戏刚启动、还没创建过计时器时 Tick 一次都没跑过，旧值是 0，会让 Server 域整体偏快 "启动到校准"那么多秒。
        /// </remarks>
        public static void SyncServerTime(DateTime serverUtc) => Core.SyncServerTime(serverUtc);

        /// <summary>校准服务器时间（显式给出本地 realtime 锚点，精度更高）。</summary>
        public static void SyncServerTime(DateTime serverUtc, double realtimeSinceStartup)
            => Core.Clock.Sync(serverUtc, realtimeSinceStartup);

        /// <summary>服务器时间是否已校准（未校准就用 Server 域会报 NoServerTime）。</summary>
        public static bool ServerTimeSynced => Core.Clock.Synced;

        // ============================================================
        // 驱动（默认不用管；想自己驱动时调 Tick / TickFixed）
        // ============================================================

        /// <summary>
        /// 手动驱动（在自己的 Update 里调一次）。调用之后隐藏宿主会自动让位 ——
        /// 这是刻意的：计时器最怕"同一帧被推进两次"，那会让所有倒计时走快一倍。
        /// <code>void Update() => RevTimer.Tick(Time.deltaTime, Time.unscaledDeltaTime, Time.realtimeSinceStartup);</code>
        /// </summary>
        public static void Tick(float scaledDeltaTime, float unscaledDeltaTime, double realtimeSinceStartup)
            => Core.TickManual(scaledDeltaTime, unscaledDeltaTime, realtimeSinceStartup);

        /// <summary>手动驱动逻辑帧（在自己的 FixedUpdate 里调一次）：推进 Fixed 域。</summary>
        public static void TickFixed(float fixedDeltaTime) => Core.TickFixedManual(fixedDeltaTime);

        // ============================================================
        // 可观测性（框架不打日志、不做上报 —— 要就订阅这里）
        // ============================================================

        /// <summary>
        /// 失败事件：参数 =（原因码, 说明）。创建被拒（时长非法/超上限/没校准服务器时间）、
        /// 业务回调抛异常都会走到这里。
        /// </summary>
        public static event Action<RevTimerErrorReason, string> Failed
        {
            add => Core.Failed += value;
            remove => Core.Failed -= value;
        }

        /// <summary>日志出口（默认标准错误；Unity 下由钩子接管成 Debug.LogWarning）。</summary>
        public static Action<string> Log
        {
            get => Core.Log;
            set => Core.Log = value;
        }

        /// <summary>异常出口（默认标准错误；Unity 下由钩子接管成 Debug.LogException）。</summary>
        public static Action<Exception, string> OnException
        {
            get => Core.OnException;
            set => Core.OnException = value;
        }

        /// <summary>进 Play / 换工程时复位（由 <c>Support\RevTimerUnityHooks</c> 调用，业务不用管）。</summary>
        internal static void ResetForNewSession() => Core.ResetForNewSession();
    }
}

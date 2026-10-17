// ============================================================
// RevTimerDefines.cs —— 时间域、失败原因、容量上限（纯 C#）
//
// 位置：Runtime\RevTimer\Core\
//
// 【★ 四时间域：这套计时器最需要学的一件事】
//   王者原版把"挂起语义 / 精度 / 权威源"做成四个通道（Normal / FrameSync / Accurate / Server），
//   逼调用方在**写代码时**选清楚 —— 错误在编码期暴露，而不是上线后"切后台回来倒计时全错"。
//
//   这里保留这个思想，但只留三个真正需要的维度（Accurate 用 double 累积一步到位，不再单列通道）：
//
//   ┌ 域 ─────────── 时间基准 ─────────── 用在哪 ──────────────────────── 切后台 ─┐
//   │ Scaled      Time.deltaTime      受 timeScale 影响：表现/动画/战斗演出      不走   │
//   │ Unscaled    unscaledDeltaTime   真实经过时间：UI 倒计时/轮询/超时/音频    不走   │
//   │ Fixed       Time.fixedDeltaTime 逻辑帧：帧率无关的战斗推进（对齐王者 FrameSync）│ 走   │
//   │ Server      服务器 UTC 绝对时刻  活动结束/榜单刷新：防改设备时间、挂起无关    无关 ★│
//   └────────────────────────────────────────────────────────────────────────────┘
//
//   ★ Server 域是**时刻式**（"到某个绝对时刻"），不是"从此刻起 X 秒" —— 所以它天然不受
//     切后台、掉帧、卡顿影响；王者那份"恢复帧一次性补 60 万毫秒"的补偿尖峰问题，在这里
//     由"选 Server 域"直接消化掉（不需要任何补偿代码）。
//
// 【为什么不叫"优先级/精度"】王者有 Accurate 通道是因为它的 Normal 用 (int) 毫秒截断
//   （每帧最多丢 0.67ms，30 分钟能差几秒，且被"误差契约"锁死不敢修）。
//   这里全程 double 秒累积 → 不存在截断漂移，所以不需要那个通道。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 时间域：决定这个计时器跟着哪条时间轴走。
    /// ★ 选错了是这类系统最常见的线上事故（切后台回来倒计时全错），所以它是**显式参数**。
    /// </summary>
    public enum RevTimeDomain
    {
        /// <summary>游戏时间（受 timeScale 影响）：表现、动画、战斗演出。默认值。</summary>
        Scaled = 0,

        /// <summary>真实时间（不受 timeScale 影响）：UI 倒计时、轮询、超时、音频收尾。</summary>
        Unscaled = 1,

        /// <summary>逻辑帧时间（FixedUpdate 驱动）：帧率无关的战斗推进，对齐王者的 FrameSync 通道。</summary>
        Fixed = 2,

        /// <summary>服务器绝对时刻（需先 <see cref="RevTimer.SyncServerTime(System.DateTime)"/> 校准）：活动/榜单，防改设备时间、挂起无关。</summary>
        Server = 3,
    }

    /// <summary>
    /// 计时器没能正常工作的原因（配 <see cref="RevTimer.Failed"/> 事件用）。
    /// ★ 框架不吞错误、也不打日志：失败一定有原因码，业务想上报就订阅这一个事件。
    /// </summary>
    public enum RevTimerErrorReason
    {
        /// <summary>时长/间隔非法：&lt;= 0（只有 After(0) 例外，表示"下一帧"）、NaN、无限大，或次数为 0。</summary>
        InvalidDuration = 0,

        /// <summary>计时器数量超过上限（见 <see cref="RevTimerLimits.MaxTimers"/>）：本次创建被拒绝。</summary>
        Overflow = 1,

        /// <summary>Server 域还没校准服务器时间（要先调 <see cref="RevTimer.SyncServerTime(System.DateTime)"/>）。</summary>
        NoServerTime = 2,

        /// <summary>业务回调抛了异常（已隔离：只影响这一个计时器，其余照常走）。</summary>
        CallbackThrew = 3,
    }

    /// <summary>容量与池的默认值（全部集中在这里，便于阅读与调优）。</summary>
    internal static class RevTimerLimits
    {
        /// <summary>
        /// 同时存在的计时器上限。★ 到上限后**拒绝新建并报 Overflow**，不做淘汰 ——
        /// 淘汰别人的计时器是"静默改变业务行为"，比明确失败更难查。
        ///
        /// 为什么是"固定数组"而不是可增长容器：王者那份的结论是"量级不需要的地方用复杂算法是负资产"
        /// （主线程线性遍历，十年无 bug）。这里同样线性遍历 + 固定槽位：
        /// 1024 个槽位一次扫描在每帧成本上完全可忽略，且行为完全可预测。
        /// </summary>
        internal const int MaxTimers = 1024;

        /// <summary>对象池初始容量（LIFO 复用，避免创建/销毁计时器产生 GC）。</summary>
        internal const int PoolCapacity = 64;
    }
}

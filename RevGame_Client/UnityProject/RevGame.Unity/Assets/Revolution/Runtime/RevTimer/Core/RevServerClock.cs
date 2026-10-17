// ============================================================
// RevServerClock.cs —— 服务器时间：一次校准 + 本地外推（纯 C#）
//
// 位置：Runtime\RevTimer\Core\
//
// 【为什么需要它】
//   本地时钟不可信：玩家改设备时间就能骗过"活动还剩 5 分钟"这类逻辑。
//   王者的做法是：登录时拿到服务器 UTC 后**记一个锚点**（本地 realtime + 当时的服务器时刻），
//   之后用 realtime 外推 —— realtime 不受"改设备时间"影响、也不受 timeScale 影响。
//
// 【为什么不直接用 DateTime.UtcNow】
//   DateTime.UtcNow 就是本地时钟（可被改、可被 NTP 拨动）→ 只适合"给个大概"，不能当权威。
//
// 【精度说明（王者的痛点之一，这里如实写清楚）】
//   · 外推误差来自"本地 realtime 的长期漂移"：小时级会有数秒偏差（realtimeSinceStartup 是浮点计时）；
//   · 所以**允许反复校准**：每次收到服务器时间就再 Sync 一次，漂移不会累积；
//   · 这里全程用 double 承担累加，不做毫秒取整（旧框架 (int) 截断正是漂移来源）。
//
// 【不算在内的】网络单向延迟（收到包时服务器时间已经"过去半个 RTT"）——
//   要更准就把对时协议里的 RTT/2 一起传给 RevTimer.SyncServerTime 的业务侧自行补偿。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>服务器时间的锚点与外推（纯 C#，可脱离引擎单测）。</summary>
    public sealed class RevServerClock
    {
        private DateTime _anchorUtc;
        private double _anchorRealtime;
        private bool _synced;

        /// <summary>是否校准过（没校准就用 Server 域 → 会报 <see cref="RevTimerErrorReason.NoServerTime"/>）。</summary>
        public bool Synced => _synced;

        /// <summary>
        /// 校准：把"此刻的服务器 UTC"和"此刻的本地 realtime 秒数"绑成锚点。
        /// 反复调用是预期用法（每次收到服务器时间都校准一次，抑制漂移）。
        /// </summary>
        internal void Sync(DateTime utcNow, double realtimeSeconds)
        {
            // 传入 DateTime.Now（Local）时 Ticks 是本地钟点，直接当 UTC 用会整体差出一个时区；统一转成 UTC。
            // Unspecified 无法判断，按"调用方已经给的是 UTC"处理（服务器协议通常如此）。
            if (utcNow.Kind == DateTimeKind.Local) utcNow = utcNow.ToUniversalTime();

            _anchorUtc = utcNow;
            _anchorRealtime = realtimeSeconds;
            _synced = true;
        }

        /// <summary>外推当前服务器 UTC（未校准 → false，调用方按失败处理，不要瞎猜）。</summary>
        internal bool TryNowUtc(double realtimeSeconds, out DateTime utc)
        {
            if (!_synced)
            {
                utc = default;
                return false;
            }

            utc = _anchorUtc.AddSeconds(realtimeSeconds - _anchorRealtime);
            return true;
        }

        /// <summary>外推当前服务器 UTC 的"秒数"（给时刻比较用，避免每次构造 DateTime）。</summary>
        internal bool TryNowSeconds(double realtimeSeconds, out double seconds)
        {
            if (!TryNowUtc(realtimeSeconds, out DateTime utc))
            {
                seconds = 0d;
                return false;
            }

            seconds = utc.Ticks / (double)TimeSpan.TicksPerSecond;
            return true;
        }

        /// <summary>清锚点（进 Play / 换账号 / 重登时调）。</summary>
        internal void Reset()
        {
            _anchorUtc = default;
            _anchorRealtime = 0d;
            _synced = false;
        }
    }
}

// ============================================================
// RevStopwatch.cs —— 秒表（纯 C#，不含回调）
//
// 位置：Runtime\RevTimer\Core\
//
// 【为什么计时器系统里要有秒表】
//   王者把它列为"三形态"之一：闹钟（到点做事）/ 秒表（测多久）/ 时间轮（海量调度）。
//   "给闹钟加耗时统计"是过度设计，"给秒表加回调"更是 —— 形态跟需求本质走。
//
// 【★ 它跟 RevTimer 共用同一套时间源，所以不用自己算时间】
//   Time.time 与 unscaledDeltaTime 的差别、timeScale 暂停时谁冻结谁不冻结，
//   这些坑已经在 RevTimeDomain 里定义清楚了；秒表只是"选一个域，取差值"：
//     · Scaled  ：测"玩家实际经历的游戏内耗时"（timeScale=0 时冻结，切后台不走）
//     · Unscaled：测"真实经过时间"（性能采样/网络超时/加载耗时）
//
// 【实现为什么这么简单】不注册、不订阅、不进任何列表 —— 起止时只记"当时的读数"，
//   读 Elapsed 时做一次减法。因此它**不可能泄漏**（旧框架那种"每创建一个计时器就要记得销毁"的纪律，这里不需要）。
// ============================================================
namespace Revolution
{
    /// <summary>秒表（仿 .NET <c>System.Diagnostics.Stopwatch</c> 的用法：Start / Stop / Reset / Restart / Elapsed）。</summary>
    public sealed class RevStopwatch
    {
        private readonly RevTimerCore _core;
        private double _accumulated;
        private double _startMark;
        private bool _running;

        internal RevStopwatch(RevTimerCore core, RevTimeDomain domain)
        {
            _core = core;
            Domain = domain == RevTimeDomain.Unscaled ? RevTimeDomain.Unscaled : RevTimeDomain.Scaled;
        }

        /// <summary>用哪个时间域（构造时定；见文件头"两种域的差别"）。</summary>
        public RevTimeDomain Domain { get; }

        public bool IsRunning => _running;

        /// <summary>已累计秒数（读的时候实时算，不会"读到上一步的旧值"）。</summary>
        public double Elapsed => _running ? _accumulated + (Now() - _startMark) : _accumulated;

        /// <summary>开始 / 继续（已在跑则什么都不做）。</summary>
        public void Start()
        {
            if (_running) return;

            _startMark = Now();
            _running = true;
        }

        /// <summary>停表（累计值保留，可再 Start 继续）。</summary>
        public void Stop()
        {
            if (!_running) return;

            _accumulated += Now() - _startMark;
            _running = false;
        }

        /// <summary>清零并停表。</summary>
        public void Reset()
        {
            _accumulated = 0d;
            _running = false;
        }

        /// <summary>清零并重新开始。</summary>
        public void Restart()
        {
            _accumulated = 0d;
            _startMark = Now();
            _running = true;
        }

        private double Now() => Domain == RevTimeDomain.Unscaled ? _core.RealTime : _core.GameTime;
    }
}

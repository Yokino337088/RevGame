// 纯逻辑围棋时钟。外层用单调时钟测量一手耗时，本类不读取 DateTime，也不自行启线程或计时器。
using System;

namespace RevGame.Pvp
{
    /// <summary>可复制的时钟快照；单位秒。网络通常重放每手 elapsedMs，快照用于本地保存与测试。</summary>
    public struct GoClockSnapshot
    {
        public double MainSeconds;
        public double ByoyomiSeconds;
        public int ByoyomiCount;
        public double BlackMainRemaining;
        public double WhiteMainRemaining;
        public int BlackPeriods;
        public int WhitePeriods;
        public bool BlackTimedOut;
        public bool WhiteTimedOut;
    }

    /// <summary>
    /// 每局一份、两色各自计时。只有已提交的落子/pass 才 Advance 一次；显示调用 Peek，不累计扣两遍。
    /// 主时限耗尽进入日式读秒；每消耗完整周期丢掉一次，落子后最后未用尽周期回满。
    /// </summary>
    public sealed class GoClock
    {
        private readonly double _mainSeconds;
        private readonly double _byoyomiSeconds;
        private readonly int _byoyomiCount;
        private readonly double[] _main = new double[3];
        private readonly int[] _periods = new int[3];
        private readonly bool[] _timedOut = new bool[3];

        /// <summary>所有时间数值由配置传入；数组长度 3 只是 Empty/Black/White 的协议结构。</summary>
        public GoClock(double mainSeconds, double byoyomiSeconds, int byoyomiCount)
        {
            if (!ValidSeconds(mainSeconds) || !ValidSeconds(byoyomiSeconds) || byoyomiSeconds <= 0 || byoyomiCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(mainSeconds), "主时限须非负，读秒周期与次数须为正数。");
            }
            _mainSeconds = mainSeconds;
            _byoyomiSeconds = byoyomiSeconds;
            _byoyomiCount = byoyomiCount;
            _main[1] = mainSeconds;
            _main[2] = mainSeconds;
            _periods[1] = byoyomiCount;
            _periods[2] = byoyomiCount;
        }

        /// <summary>
        /// 提交该方这一手的完整思考时长（秒）。false 表示参数非法/该方已超时，状态不变；
        /// 本次刚耗尽时间仍返回 true，并通过 IsTimedOut 报告，外层据此生成确定的超时命令。
        /// </summary>
        public bool Advance(GoSide side, double elapsedSeconds)
        {
            if (!ValidSide(side) || !ValidSeconds(elapsedSeconds) || _timedOut[(int)side])
            {
                return false;
            }
            Read(side, elapsedSeconds, out double mainRemaining, out int periods, out _, out bool timedOut);
            _main[(int)side] = mainRemaining;
            _periods[(int)side] = periods;
            _timedOut[(int)side] = timedOut;
            return true;
        }

        /// <summary>当前主时限剩余，主时限为零时返回本次读秒剩余；不修改时钟，可每帧用于显示。</summary>
        public double PeekRemaining(GoSide side, double elapsedSeconds = 0)
        {
            if (!ValidSide(side) || !ValidSeconds(elapsedSeconds))
            {
                return 0;
            }
            Read(side, elapsedSeconds, out double mainRemaining, out _, out double periodRemaining, out bool timedOut);
            return timedOut ? 0 : mainRemaining > 0 ? mainRemaining : periodRemaining;
        }

        public int PeekPeriods(GoSide side, double elapsedSeconds = 0)
        {
            if (!ValidSide(side) || !ValidSeconds(elapsedSeconds))
            {
                return 0;
            }
            Read(side, elapsedSeconds, out _, out int periods, out _, out _);
            return periods;
        }

        public double MainRemaining(GoSide side)
        {
            return ValidSide(side) ? _main[(int)side] : 0;
        }

        public bool IsTimedOut(GoSide side, double elapsedSeconds = 0)
        {
            if (!ValidSide(side) || !ValidSeconds(elapsedSeconds))
            {
                return false;
            }
            Read(side, elapsedSeconds, out _, out _, out _, out bool timedOut);
            return timedOut;
        }

        public GoClockSnapshot Capture()
        {
            return new GoClockSnapshot
            {
                MainSeconds = _mainSeconds,
                ByoyomiSeconds = _byoyomiSeconds,
                ByoyomiCount = _byoyomiCount,
                BlackMainRemaining = _main[1],
                WhiteMainRemaining = _main[2],
                BlackPeriods = _periods[1],
                WhitePeriods = _periods[2],
                BlackTimedOut = _timedOut[1],
                WhiteTimedOut = _timedOut[2]
            };
        }

        /// <summary>快照全量预检再提交；不能让错误的重连包只恢复黑方、白方却仍停在旧时钟。</summary>
        public bool TryRestore(GoClockSnapshot snapshot)
        {
            if (snapshot.MainSeconds != _mainSeconds || snapshot.ByoyomiSeconds != _byoyomiSeconds || snapshot.ByoyomiCount != _byoyomiCount)
            {
                return false;
            }
            if (!ValidState(snapshot.BlackMainRemaining, snapshot.BlackPeriods, snapshot.BlackTimedOut) ||
                !ValidState(snapshot.WhiteMainRemaining, snapshot.WhitePeriods, snapshot.WhiteTimedOut))
            {
                return false;
            }
            _main[1] = snapshot.BlackMainRemaining;
            _main[2] = snapshot.WhiteMainRemaining;
            _periods[1] = snapshot.BlackPeriods;
            _periods[2] = snapshot.WhitePeriods;
            _timedOut[1] = snapshot.BlackTimedOut;
            _timedOut[2] = snapshot.WhiteTimedOut;
            return true;
        }

        public GoClock Clone()
        {
            GoClock clone = new GoClock(_mainSeconds, _byoyomiSeconds, _byoyomiCount);
            clone.TryRestore(Capture());
            return clone;
        }

        private void Read(GoSide side, double elapsed, out double mainRemaining, out int periods, out double periodRemaining, out bool timedOut)
        {
            int index = (int)side;
            mainRemaining = Math.Max(0, _main[index] - elapsed);
            periods = _periods[index];
            periodRemaining = _byoyomiSeconds;
            timedOut = _timedOut[index];
            if (timedOut)
            {
                periodRemaining = 0;
                return;
            }
            if (elapsed <= _main[index] && _main[index] > 0)
            {
                return;
            }
            double overtime = Math.Max(0, elapsed - _main[index]);
            // 先比较总预算，避免恶意巨大 elapsed 在逐周期循环里拖慢主线程或整数转换溢出。
            if (overtime >= _byoyomiSeconds * periods)
            {
                periods = 0;
                periodRemaining = 0;
                timedOut = true;
                return;
            }
            int used = (int)Math.Floor(overtime / _byoyomiSeconds);
            periods -= used;
            periodRemaining = _byoyomiSeconds - (overtime - used * _byoyomiSeconds);
        }

        private bool ValidState(double mainRemaining, int periods, bool timedOut)
        {
            if (!ValidSeconds(mainRemaining) || mainRemaining > _mainSeconds || periods < 0 || periods > _byoyomiCount)
            {
                return false;
            }
            return timedOut ? mainRemaining == 0 && periods == 0 : periods > 0;
        }

        private static bool ValidSeconds(double seconds)
        {
            return seconds >= 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds);
        }

        private static bool ValidSide(GoSide side)
        {
            return side == GoSide.Black || side == GoSide.White;
        }
    }
}

#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
using System;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>本局安卓试验的线程档位与热状态，只有隔离调试入口创建，换局和离场时释放。</summary>
    internal sealed class PvpAndroidTacticalTrial : IDisposable
    {
        /// <summary>前台常规采样每两秒一次。真正发起试算前仍读最新热状态。</summary>
        private const double SampleIntervalSeconds = 2;
        /// <summary>初开或降档后，同一个较高档位必须连续可用十秒才能启用。</summary>
        private const double RecoverySeconds = 10;
        /// <summary>负值表示本局尚无样本。安卓用真实秒，编辑器用测试提供的秒。</summary>
        private double _sampledAt = -1;
        /// <summary>下一次可恢复的组数，零表示未在确认恢复。</summary>
        private int _recoveringGroups;
        /// <summary>当前确认窗口的结束时刻，单位秒，与采样使用同一时钟。</summary>
        private double _recoverAt;
#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>仅 Android Player 持有本局的系统电源服务，失效或离场时归还 Java 引用。</summary>
        private AndroidJavaObject _power;
#endif

        /// <summary>本局请求的组数，用于实战搜索，改变请求不会清掉热态历史。</summary>
        internal int RequestedGroups { get; set; } = 1;
        /// <summary>设备和恢复窗口当前允许的上限。搜索本身还会根据棋盘工作量降低它。</summary>
        internal int AllowedGroups { get; private set; } = 1;
        /// <summary>最近系统热等级，负一表示未知。</summary>
        internal int ThermalStatus { get; private set; } = -1;
        /// <summary>最近一次采样是否启用了省电模式。</summary>
        internal bool PowerSave { get; private set; }
        /// <summary>最近一次采样是否在前台。</summary>
        internal bool Focused { get; private set; }
        /// <summary>最后一个被确认的恢复窗口还需等待多少秒，零表示当前无恢复窗口。</summary>
        internal double RecoveryRemaining => _recoveringGroups == 0 ? 0 : Math.Max(0, _recoverAt - _sampledAt);

        /// <summary>回退原因供只读状态解释。棋面工作量带来的降组由搜索结果单独报告。</summary>
        internal string Reason
        {
            get
            {
                if (!Focused)
                {
                    return "background";
                }
                if (ThermalStatus < 0)
                {
                    return "thermal-unavailable";
                }
                if (PowerSave)
                {
                    return "power-save";
                }
                if (ThermalStatus >= 2)
                {
                    return "thermal-limited";
                }
                if (_recoveringGroups > 0)
                {
                    return "cooldown";
                }
                if (ThermalStatus == 1)
                {
                    return "thermal-light";
                }
                return AllowedGroups < 4 ? "processor-count" : "ready";
            }
        }

        /// <summary>初次读取设备状态并从单组开始确认。编辑器后续只消费显式测试样本。</summary>
        internal PvpAndroidTacticalTrial()
        {
#if UNITY_EDITOR
            Observe(0, false, true, 0);
#else
            Application.focusChanged += OnFocusChanged;
            Refresh(beforeSearch: true);
#endif
        }

        /// <summary>把当前的请求限制在设备允许范围内。实战和对照都用这个入口。</summary>
        internal int Limit(int requested)
        {
            Refresh(beforeSearch: true);
            return Math.Min(requested, AllowedGroups);
        }

        /// <summary>只在隔离对局启用试验后轮询。发任务前强制复查，以便热态变化作用于这一手。</summary>
        internal void Refresh(bool beforeSearch = false)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            double now = Time.realtimeSinceStartupAsDouble;
            if (!Application.isFocused)
            {
                Observe(-1, false, false, now);
                return;
            }
            if (!beforeSearch && now - _sampledAt < SampleIntervalSeconds && Focused)
            {
                return;
            }
            if (_sampledAt >= 0 && now - _sampledAt > 2 * SampleIntervalSeconds)
            {
                // 可能经过后台暂停或长时间没有游戏帧。之前的样本不能证明这段时间一直够凉。
                Observe(-1, false, false, now);
            }
            ReadPower(out int thermal, out bool saving);
            Observe(thermal, saving, true, now);
#endif
        }

#if UNITY_EDITOR
        /// <summary>编辑器测试可推进虚拟秒并喂入热、焦点和省电样本，采用与 Android 一样的降档和恢复逻辑。</summary>
        internal void Simulate(int thermalStatus, bool powerSave, bool focused, double advanceSeconds)
        {
            Observe(thermalStatus, powerSave, focused, _sampledAt + advanceSeconds);
        }
#endif

        /// <summary>更新调度上限。上限下降立即生效，上升必须连续收到同一可用档位的样本。</summary>
        private void Observe(int thermalStatus, bool powerSave, bool focused, double now)
        {
            int possible = GoTacticalSearch.ResolveAndroidTrialPartitions(4, thermalStatus, powerSave, focused,
                thermallyReady: true, processorCount: SystemInfo.processorCount);
            if (possible <= AllowedGroups)
            {
                AllowedGroups = possible;
                _recoveringGroups = 0;
            }
            else if (_recoveringGroups != possible)
            {
                _recoveringGroups = possible;
                _recoverAt = now + RecoverySeconds;
            }
            else if (now >= _recoverAt)
            {
                AllowedGroups = possible;
                _recoveringGroups = 0;
            }
            _sampledAt = now;
            ThermalStatus = thermalStatus;
            PowerSave = powerSave;
            Focused = focused;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>失焦立即清掉健康窗口。回到前台时下一次采样再按实际系统状态开始确认。</summary>
        private void OnFocusChanged(bool focused)
        {
            if (!focused)
            {
                Observe(-1, false, false, Time.realtimeSinceStartupAsDouble);
            }
        }

        /// <summary>在 Unity 主线程读 Android API 29 以上的电源服务。拿不到有效热状态时用未知值回退。</summary>
        private void ReadPower(out int thermal, out bool powerSave)
        {
            thermal = -1;
            powerSave = false;
            try
            {
                if (_power == null)
                {
                    using var version = new AndroidJavaClass("android.os.Build$VERSION");
                    if (version.GetStatic<int>("SDK_INT") < 29)
                    {
                        return;
                    }
                    using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                    _power = activity?.Call<AndroidJavaObject>("getSystemService", "power");
                }
                if (_power == null)
                {
                    return;
                }

                powerSave = _power.Call<bool>("isPowerSaveMode");
                int status = _power.Call<int>("getCurrentThermalStatus");
                thermal = status >= 0 && status <= 6 ? status : -1;
            }
            catch (Exception)
            {
                // 当前信号不可信。丢弃 Java 引用，让后续采样可以重新取得服务。
                _power?.Dispose();
                _power = null;
                thermal = -1;
            }
        }
#endif

        /// <summary>本局退出后停止焦点通知并释放电源服务，测试选择不写入配置或存档。</summary>
        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Application.focusChanged -= OnFocusChanged;
            _power?.Dispose();
            _power = null;
#endif
        }
    }
}
#endif

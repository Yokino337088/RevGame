// ============================================================
// RevAppLifecycle.cs —— 应用"前后台 / 焦点 / 退出"生命周期事件（门面）
//
// 位置：Runtime\RevAppLifecycle\
//
// 【为什么需要它】
//   在移动端与小游戏上，"切后台"从来不是一个小动作，它会一次带走好几样东西：
//     · 音频被系统打断（来电、别的 App 抢走声音通道）；
//     · 长连接 / 轮询静默失效 —— 回来时连接早断了，业务却以为还连着；
//     · 时间跨度被跳过（玩家可能一后台就是半小时，体力 / 活动 / 签到全都要重算）；
//     · 小游戏还要考虑"切回来要不要重新拉一次资源 / 重新登录"。
//   框架的输入模块已经在失焦时自己复位了，但**业务也需要知道这件事** ——
//   所以这里把 Unity 的应用级回调收敛成一组事件：业务订阅即可，不用自己在场景里挂脚本。
//
// 【怎么用（最省事）】
//   RevEvent.AddEventListener(RevAppLifecycle.Resumed, OnResumed, owner: this);
//   private void OnResumed() { 重连(); 校准时间(); }
//   （owner 传 this，面板 / 对象销毁时框架会连同事件一起摘掉，不用手写反注册。）
//
// 【★ 去重：移动端切后台会"同时"触发两个回调】
//   OnApplicationPause(true) 与 OnApplicationFocus(false) 往往一前一后到达，
//   天真的实现会派发两次 Paused —— 业务的重连逻辑就被触发两遍（甚至两遍连接互踩）。
//   所以本类只在"前后台状态真的翻转"时派发一次，其它重复通知直接吃掉。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>应用生命周期门面（前后台 / 焦点 / 退出）。</summary>
    public static class RevAppLifecycle
    {
        /// <summary>进入后台 / 失去焦点（0 参数事件；同一轮切换只派发一次）。</summary>
        public const string Paused = "App.Paused";

        /// <summary>回到前台 / 重新获得焦点。</summary>
        public const string Resumed = "App.Resumed";

        /// <summary>
        /// 正在退出。
        /// ★ 移动端不保证触发（被系统杀掉时什么回调都没有）——需要落盘的数据要随存随写，
        ///   绝不能指望在这里"最后存一次"。
        /// </summary>
        public const string Quitting = "App.Quitting";

        /// <summary>当前是否处于后台 / 失焦状态。</summary>
        public static bool IsBackground { get { return _isBackground; } }

        /// <summary>最近一次在后台待了多久（秒；从没切过后台时为 0）。</summary>
        public static float LastBackgroundSeconds { get { return _lastBackgroundSeconds; } }

        /// <summary>累计切到后台的次数（排查"怎么老是掉线"时，先看这个数）。</summary>
        public static int PauseCount { get { return _pauseCount; } }

        private static bool _isBackground;
        private static float _lastBackgroundSeconds;
        private static int _pauseCount;
        private static DateTime _backgroundAt;

        /// <summary>
        /// 交给宿主调用（业务不要直接调）。
        ///
        /// ★ 后台时长用 DateTime.UtcNow 计算，而不是 Time.realtimeSinceStartup：
        ///   切到后台后引擎自己的时间会停走，只有真实时钟还在往前 —— 用错时钟会把半小时算成 0 秒。
        /// </summary>
        public static void SetBackground(bool background)
        {
            if (_isBackground == background) { return; }        // ★ 去重：重复通知不重复派发

            _isBackground = background;

            if (background)
            {
                _pauseCount++;
                _backgroundAt = DateTime.UtcNow;
                RevEvent.DispatchEvent(Paused);
                return;
            }

            _lastBackgroundSeconds = (float)(DateTime.UtcNow - _backgroundAt).TotalSeconds;
            RevEvent.DispatchEvent(Resumed);
        }

        /// <summary>交给宿主调用（业务不要直接调）。</summary>
        public static void NotifyQuitting()
        {
            RevEvent.DispatchEvent(Quitting);
        }

        /// <summary>进 Play / 域重载复位（由 Support\RevAppLifecycleDriver 调用，业务不用管）。</summary>
        public static void ResetForNewSession()
        {
            _isBackground = false;
            _lastBackgroundSeconds = 0f;
            _pauseCount = 0;
        }
    }
}

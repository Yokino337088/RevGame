// 有界声音频率门禁：业务仍逐次结算/发事件，这里只决定本次声音能否进入RevSound播放。
// 显式传入现实时间，避免暂停时声音预算冻结；纯C#可用固定时间序列验证边界。
namespace RevGame.View
{
    /// <summary>单声部最短间隔门禁。实例归相应场景/表现所有者，退出Reset，不维护无限事件历史。</summary>
    public sealed class GameAudioRateLimit
    {
        /// <summary>统一的玩家/帮手声部间隔（现实秒，纯听感配置）；两个模式与诊断共用，防测试和生产漂移。</summary>
        public const float PlayerSeconds = .10f, HelperSeconds = .45f;
        /// <summary>允许下一声开始的现实秒时刻；默认0让首次声音立即可用。</summary>
        private float _due;
        /// <summary>当前观察期允许和舍弃的请求数；只供诊断，不作为真实AudioSource播放成功的证据。</summary>
        public int Accepted
        {
            get;
            private set;
        }
        /// <summary>本观察期被最短间隔拒绝的请求次数，Reset清零。</summary>
        public int Suppressed
        {
            get;
            private set;
        }
        /// <summary>到达边界才允许；拒绝不推迟边界，因此高频输入不能令声音永久饥饿。</summary>
        public bool Try(float now, float spacing)
        {
            if (now < _due)
            {
                Suppressed++;
                return false;
            }
            _due = now + spacing;
            Accepted++;
            return true;
        }
        /// <summary>场景/回合开始清观察期，不让上次未来期限影响新场景。</summary>
        public void Reset()
        {
            _due = 0;
            Accepted = Suppressed = 0;
        }
    }
}

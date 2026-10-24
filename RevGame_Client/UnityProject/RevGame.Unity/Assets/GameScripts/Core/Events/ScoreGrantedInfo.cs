namespace RevGame.Core
{
    /// <summary>积分到账的值快照；来源棋死亡后仍能播飞星，动画完成绝不再加分。</summary>
    public readonly struct ScoreGrantedInfo
    {
        /// <summary>得分目标的棋盘锚点与身体尺寸，供表现计算屏幕中心。</summary>
        public readonly BoardPos Position;
        public readonly int Size;
        /// <summary>本次已乘倍率的实际到账分数、本回合积分、永久总积分。</summary>
        public readonly long Added, RoundScore, LifetimeScore;
        public readonly float Multiplier;
        /// <summary>致死暴击可同时为true，服务合成一次通知，避免两组飞星挤在一起。</summary>
        public readonly bool Kill, Critical;
        public ScoreGrantedInfo(BoardPos position, int size, long added, long round, long lifetime, float multiplier, bool kill, bool critical)
        {
            Position = position; Size = size; Added = added; RoundScore = round;
            LifetimeScore = lifetime; Multiplier = multiplier; Kill = kill; Critical = critical;
        }
    }
}

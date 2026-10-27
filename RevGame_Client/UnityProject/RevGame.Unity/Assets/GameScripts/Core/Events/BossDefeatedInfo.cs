namespace RevGame.Core
{
    /// <summary>真实Boss战胜利的不可变快照。由服务在终局结算后发出一次，演出不再读已移除的棋子。</summary>
    public sealed class BossDefeatedInfo
    {
        public readonly int Chapter, Round, BossId, BodySize;
        public readonly BoardPos Position;
        public readonly long ChapterScore;
        /// <summary>残血封气胜利也播放演出；模型不因此额外扣血或重复加击杀积分。</summary>
        public readonly bool Captured;
        public BossDefeatedInfo(int chapter, int round, int bossId, BoardPos position, int bodySize, long score, bool captured)
        { Chapter = chapter; Round = round; BossId = bossId; Position = position; BodySize = bodySize; ChapterScore = score; Captured = captured; }
    }
}

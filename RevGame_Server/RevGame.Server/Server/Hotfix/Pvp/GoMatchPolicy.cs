namespace Fantasy;

/// <summary>围棋 1v1 的棋力分及等待窗口。窗口以双方中等待较短的一方为准。</summary>
internal static class GoMatchPolicy
{
    public const int InitialRating = 1500;
    private const int TierWidth = 200;
    private const int WaitStepMs = 8000;
    private const int MaxExpansionSteps = 2;
    private const int RatingGapPerStep = 100;
    private const int EloK = 32;

    /// <summary>返回段差、分差。相同距离沿用队列顺序；-1 表示双方窗口尚未重叠。</summary>
    public static int Distance(int firstRating, int secondRating, long shorterWaitMs)
    {
        int steps = (int)Math.Clamp(shorterWaitMs / WaitStepMs, 0, MaxExpansionSteps);
        int tierGap = Math.Abs(firstRating / TierWidth - secondRating / TierWidth);
        int ratingGap = Math.Abs(firstRating - secondRating);
        if (tierGap > steps || ratingGap > (steps + 1) * RatingGapPerStep)
        {
            return -1;
        }
        return tierGap * 1000 + ratingGap;
    }

    /// <summary>将排队分数换算成黑方预期胜率，房间只需要保留这个结算参数。</summary>
    public static double BlackExpectedResult(int blackRating, int whiteRating)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, (whiteRating - blackRating) / 400.0));
    }

    /// <summary>按实际胜负计算黑方本局 Elo 分差。白方取相反值，和棋结果取 0.5。</summary>
    public static int BlackRatingDelta(double expectedBlackResult, double actualBlackResult)
    {
        return (int)Math.Round(EloK * (actualBlackResult - expectedBlackResult), MidpointRounding.AwayFromZero);
    }
}

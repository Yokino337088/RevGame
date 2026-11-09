namespace Fantasy;

/// <summary>围棋 1v1 的棋力分及等待窗口。窗口以双方中等待较短的一方为准。</summary>
internal static class GoMatchPolicy
{
    /// <summary>客户端尚无本地棋力分时使用的排队起始分。</summary>
    public const int InitialRating = 1500;
    /// <summary>每个棋力段包含的分数宽度，用整除得到段号。</summary>
    private const int TierWidth = 200;
    /// <summary>排队窗口每次放宽需要的等待时长，单位毫秒。</summary>
    private const int WaitStepMs = 8000;
    /// <summary>匹配窗口最多放宽的次数，限制跨段范围。</summary>
    private const int MaxExpansionSteps = 2;
    /// <summary>初始允许的分差，也是每次放宽增加的分差。</summary>
    private const int RatingGapPerStep = 100;
    /// <summary>Elo 更新系数，控制实际结果偏离预期时的调分幅度。</summary>
    private const int EloK = 32;

    /// <summary>将段差与分差合成为匹配距离。相同距离沿用队列顺序，-1 表示超出当前匹配窗口。</summary>
    public static int Distance(int firstRating, int secondRating, long shorterWaitMs)
    {
        // 以双方等待较短的一方为准，让刚入队的人也能用自己的等待窗口。
        int steps = (int)Math.Clamp(shorterWaitMs / WaitStepMs, 0, MaxExpansionSteps);
        int tierGap = Math.Abs(firstRating / TierWidth - secondRating / TierWidth);
        int ratingGap = Math.Abs(firstRating - secondRating);
        if (tierGap > steps || ratingGap > (steps + 1) * RatingGapPerStep)
        {
            return -1;
        }
        // 段差权重高于允许的分差，数值越小的候选越优先。
        return tierGap * 1000 + ratingGap;
    }

    /// <summary>将排队分数换算成黑方预期胜率，房间只需要保留这个结算参数。</summary>
    public static double BlackExpectedResult(int blackRating, int whiteRating)
    {
        // 双方同分时黑方预期得分为 0.5，白方分数越高，黑方预期越低。
        return 1.0 / (1.0 + Math.Pow(10.0, (whiteRating - blackRating) / 400.0));
    }

    /// <summary>按实际胜负计算黑方本局 Elo 分差。白方取相反值，和棋结果取 0.5。</summary>
    public static int BlackRatingDelta(double expectedBlackResult, double actualBlackResult)
    {
        return (int)Math.Round(EloK * (actualBlackResult - expectedBlackResult), MidpointRounding.AwayFromZero);
    }
}

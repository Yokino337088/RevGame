// RevEase.cs —— 缓动曲线（.Tween / .MoveTo / .FadeTo 用）
// 【用法】.Tween("淡入", 0.3f, (ctx, t) => group.alpha = t, RevEase.OutQuad)
// 【要点】纯数学，零分配；t 进出都在 0~1（OutBack 中途会略超过 1，做"弹一下"用）。

namespace Revolution
{
    /// <summary>缓动曲线</summary>
    public enum RevEase
    {
        /// <summary>匀速</summary>
        Linear = 0,

        /// <summary>先慢后快（起步）</summary>
        InQuad,

        /// <summary>先快后慢（到位）—— 移动 / 淡入最常用</summary>
        OutQuad,

        /// <summary>两头慢中间快（镜头推拉）</summary>
        InOutQuad,

        /// <summary>比 OutQuad 更"急停"</summary>
        OutCubic,

        /// <summary>冲过头再回弹（弹窗出现）</summary>
        OutBack,
    }

    /// <summary>缓动计算</summary>
    public static class RevEasing
    {
        /// <summary>按曲线把线性进度 t（0~1）换算成缓动后的进度</summary>
        public static float Evaluate(RevEase ease, float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;

            switch (ease)
            {
                case RevEase.InQuad: return t * t;
                case RevEase.OutQuad: return 1f - (1f - t) * (1f - t);
                case RevEase.InOutQuad: return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
                case RevEase.OutCubic:
                {
                    float u = 1f - t;
                    return 1f - u * u * u;
                }
                case RevEase.OutBack:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float u = t - 1f;
                    return 1f + c3 * u * u * u + c1 * u * u;
                }
                default: return t;
            }
        }
    }
}

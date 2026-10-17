// ============================================================
// RevUIAnimSpec.cs —— UI 动画的"规格"与预设（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【什么是规格】
//   一个动画 = 三个通道（透明度 / 缩放 / 位移）+ 时间参数（时长、延迟、曲线、循环）。
//   通道用**掩码**表示（UseAlpha / UseScale / UseOffset）—— 只开你要动的那几个，
//   没开的通道**完全不写目标**（不会把你在 Inspector 里调好的值覆盖掉）。
//
//   位移用"相对基准位置的**比例**"表示（OffsetXFrom / OffsetYFrom）：
//     · 面板预制体大小不定，写死像素值换个分辨率就穿帮；
//     · 于是约定 -1 = 往负方向挪"自身一整屏高/宽"（= 从屏幕外滑入）。
//   具体像素由 Support 侧按 RectTransform 的实际尺寸换算（Core 不碰 Unity 类型）。
//
// 【预设】
//   面板 / Part 的"一行搞定显示隐藏"就是走预设（见 RevUIAnimPreset）：
//   想换手感就换预设名，不用自己算曲线和起止值。
// ============================================================

namespace Revolution
{
    /// <summary>循环模式（正交语义轴之一：怎么循环）。</summary>
    public enum RevUIAnimWrap : byte
    {
        /// <summary>播一次就结束（面板显示/隐藏用它）</summary>
        Once = 0,

        /// <summary>循环（到点从头再来，余量结转不丢帧）</summary>
        Loop = 1,

        /// <summary>往返（到头反向播，余量结转不丢帧）—— 呼吸/闪烁用它</summary>
        PingPong = 2,
    }

    /// <summary>UI 动画预设：面板 / Part / 控件显示隐藏的常用手感（一行搞定）。</summary>
    public enum RevUIAnimPreset : byte
    {
        /// <summary>不做动画（默认；行为与没有动画库时完全一致）</summary>
        None = 0,

        /// <summary>淡入（0 → 1）</summary>
        FadeIn,
        /// <summary>淡出（1 → 0）</summary>
        FadeOut,

        /// <summary>弹入：淡入 + 从 0.85 缩到 1，带**回弹**（弹窗/面板默认手感）</summary>
        PopIn,
        /// <summary>弹出：淡出 + 缩到 0.9</summary>
        PopOut,

        /// <summary>放大进入（只缩放，不透明度不动）</summary>
        ScaleIn,
        /// <summary>缩小退出（只缩放）</summary>
        ScaleOut,

        /// <summary>从上滑入（从"屏幕上方"往下落到位）</summary>
        SlideInFromTop,
        /// <summary>从下滑入</summary>
        SlideInFromBottom,
        /// <summary>从左滑入</summary>
        SlideInFromLeft,
        /// <summary>从右滑入</summary>
        SlideInFromRight,

        /// <summary>向上滑出</summary>
        SlideOutToTop,
        /// <summary>向下滑出</summary>
        SlideOutToBottom,
        /// <summary>向左滑出</summary>
        SlideOutToLeft,
        /// <summary>向右滑出</summary>
        SlideOutToRight,
    }

    /// <summary>一个 UI 动画的完整规格（值类型；引擎按它采样，不持有 Unity 引用）。</summary>
    public struct RevUIAnimSpec
    {
        // ── 时间 ──
        /// <summary>时长（秒）；≤ 0 视为"瞬间完成"（引擎会走一次终态采样）</summary>
        public float Duration;

        /// <summary>延迟（秒）；延迟期间**不采样**，但余量会结转到正式播放（掉帧不缩短动画）</summary>
        public float Delay;

        /// <summary>缓动曲线</summary>
        public RevUIEase Ease;

        /// <summary>循环模式</summary>
        public RevUIAnimWrap Wrap;

        /// <summary>循环次数（-1 = 无限；Once 模式忽略它）</summary>
        public int Loops;

        // ── 三个通道的起止值 ──
        /// <summary>透明度：起始值（UseAlpha 为真时有效）</summary>
        public float AlphaFrom;
        /// <summary>透明度：结束值</summary>
        public float AlphaTo;

        /// <summary>缩放：起始倍率（UseScale 为真时有效，1 = 原大小）</summary>
        public float ScaleFrom;
        /// <summary>缩放：结束倍率</summary>
        public float ScaleTo;

        /// <summary>位移：起始偏移（按"自身宽/高的比例"，见文件头注释）</summary>
        public float OffsetXFrom;
        /// <summary>位移：起始偏移（纵向比例）</summary>
        public float OffsetYFrom;
        /// <summary>位移：结束偏移（通常 0 = 回到基准位置）</summary>
        public float OffsetXTo;
        /// <summary>位移：结束偏移（通常 0）</summary>
        public float OffsetYTo;

        // ── 通道掩码（只动开了的通道）──
        /// <summary>是否参与透明度通道</summary>
        public bool UseAlpha;
        /// <summary>是否参与缩放通道</summary>
        public bool UseScale;
        /// <summary>是否参与位移通道</summary>
        public bool UseOffset;

        /// <summary>这个规格有没有实际内容（没有就不该起动画）</summary>
        public bool IsValid => UseAlpha || UseScale || UseOffset;

        /// <summary>默认时长：淡入淡出类</summary>
        public const float DefaultFadeSeconds = 0.18f;

        /// <summary>默认时长：弹入/缩放类</summary>
        public const float DefaultPopSeconds = 0.25f;

        /// <summary>默认时长：滑入滑出类</summary>
        public const float DefaultSlideSeconds = 0.28f;

        /// <summary>按预设生成规格；<paramref name="duration"/> ≤ 0 = 用该预设的默认时长。</summary>
        public static RevUIAnimSpec For(RevUIAnimPreset preset, float duration = 0f)
        {
            var spec = new RevUIAnimSpec
            {
                Ease = RevUIEase.CubicOut,
                Wrap = RevUIAnimWrap.Once,
                Loops = 1,
                ScaleFrom = 1f,
                ScaleTo = 1f,
                AlphaFrom = 1f,
                AlphaTo = 1f,
                OffsetXTo = 0f,
                OffsetYTo = 0f,
            };

            switch (preset)
            {
                case RevUIAnimPreset.FadeIn:
                    spec.UseAlpha = true;
                    spec.AlphaFrom = 0f;
                    spec.AlphaTo = 1f;
                    spec.Duration = duration > 0f ? duration : DefaultFadeSeconds;
                    break;

                case RevUIAnimPreset.FadeOut:
                    spec.UseAlpha = true;
                    spec.AlphaFrom = 1f;
                    spec.AlphaTo = 0f;
                    spec.Duration = duration > 0f ? duration : DefaultFadeSeconds;
                    break;

                case RevUIAnimPreset.PopIn:
                    spec.UseAlpha = true;
                    spec.UseScale = true;
                    spec.AlphaFrom = 0f;
                    spec.AlphaTo = 1f;
                    spec.ScaleFrom = 0.85f;
                    spec.ScaleTo = 1f;
                    spec.Ease = RevUIEase.BackOut;              // 回弹：弹窗的"劲"
                    spec.Duration = duration > 0f ? duration : DefaultPopSeconds;
                    break;

                case RevUIAnimPreset.PopOut:
                    spec.UseAlpha = true;
                    spec.UseScale = true;
                    spec.AlphaFrom = 1f;
                    spec.AlphaTo = 0f;
                    spec.ScaleFrom = 1f;
                    spec.ScaleTo = 0.9f;
                    spec.Ease = RevUIEase.QuadIn;               // 收起来要"快走"
                    spec.Duration = duration > 0f ? duration : DefaultFadeSeconds;
                    break;

                case RevUIAnimPreset.ScaleIn:
                    spec.UseScale = true;
                    spec.ScaleFrom = 0.9f;
                    spec.ScaleTo = 1f;
                    spec.Duration = duration > 0f ? duration : DefaultFadeSeconds;
                    break;

                case RevUIAnimPreset.ScaleOut:
                    spec.UseScale = true;
                    spec.ScaleFrom = 1f;
                    spec.ScaleTo = 0.9f;
                    spec.Duration = duration > 0f ? duration : DefaultFadeSeconds;
                    break;

                case RevUIAnimPreset.SlideInFromTop:
                case RevUIAnimPreset.SlideInFromBottom:
                case RevUIAnimPreset.SlideInFromLeft:
                case RevUIAnimPreset.SlideInFromRight:
                    spec.UseOffset = true;
                    spec.UseAlpha = false;                          // 滑入不淡入：保持"整块滑进来"的观感
                    spec.OffsetYFrom = preset == RevUIAnimPreset.SlideInFromTop ? 1f
                                     : preset == RevUIAnimPreset.SlideInFromBottom ? -1f : 0f;
                    spec.OffsetXFrom = preset == RevUIAnimPreset.SlideInFromRight ? 1f
                                     : preset == RevUIAnimPreset.SlideInFromLeft ? -1f : 0f;
                    spec.Duration = duration > 0f ? duration : DefaultSlideSeconds;
                    break;

                case RevUIAnimPreset.SlideOutToTop:
                case RevUIAnimPreset.SlideOutToBottom:
                case RevUIAnimPreset.SlideOutToLeft:
                case RevUIAnimPreset.SlideOutToRight:
                    spec.UseOffset = true;
                    spec.OffsetYTo = preset == RevUIAnimPreset.SlideOutToTop ? 1f
                                   : preset == RevUIAnimPreset.SlideOutToBottom ? -1f : 0f;
                    spec.OffsetXTo = preset == RevUIAnimPreset.SlideOutToRight ? 1f
                                   : preset == RevUIAnimPreset.SlideOutToLeft ? -1f : 0f;
                    spec.Duration = duration > 0f ? duration : DefaultSlideSeconds;
                    break;

                default:                                            // None
                    return default;
            }

            return spec;
        }

        /// <summary>这个预设是不是"隐藏类"（面板关闭 / Part 收起时用；决定滑出方向是"往外"）。</summary>
        public static bool IsHidePreset(RevUIAnimPreset preset)
            => preset == RevUIAnimPreset.FadeOut || preset == RevUIAnimPreset.PopOut
            || preset == RevUIAnimPreset.ScaleOut || preset == RevUIAnimPreset.SlideOutToTop
            || preset == RevUIAnimPreset.SlideOutToBottom || preset == RevUIAnimPreset.SlideOutToLeft
            || preset == RevUIAnimPreset.SlideOutToRight;
    }
}

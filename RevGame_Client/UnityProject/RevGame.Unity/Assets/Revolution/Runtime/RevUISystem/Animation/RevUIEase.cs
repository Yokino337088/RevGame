// ============================================================
// RevUIEase.cs —— 缓动函数（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【为什么单独一个文件】
//   缓动是动画库里**唯一没有状态**的部分，也最容易被写错（边界必须恒等、单调、
//   In/Out 镜像对称）。放在纯 C# 里就能被工程外断言逐条验（见《架构解析》验收一节）。
//
// 【三条约定】
//   ① Evaluate(ease, 0) == 0、Evaluate(ease, 1) == 1 —— 除 Elastic/Bounce/Back 这三个
//      "允许过冲/回弹"的曲线外，其余都在 [0,1] 内（UI 用它们做回弹手感）。
//   ② 输入 t 会被夹到 [0,1]，调用方不用自己 clamp。
//   ③ 命名与主流习惯一致（Quad/Cubic/Quart/Expo/Circ/Sine/Back/Elastic/Bounce × In/Out/InOut），
//      迁移到本项目时不用重学。
// ============================================================

namespace Revolution
{
    /// <summary>UI 动画用的缓动曲线（够用且好记；不做"强度混合"那类进阶参数）。</summary>
    public enum RevUIEase : byte
    {
        /// <summary>匀速（少用；机械感强）</summary>
        Linear = 0,

        QuadIn,
        QuadOut,
        QuadInOut,

        /// <summary>三次方减速 —— **UI 默认曲线**（快进慢出，最像"有重量"）</summary>
        CubicOut,
        CubicIn,
        CubicInOut,

        /// <summary>更陡的减速（适合短距离、要"啪"一下的）</summary>
        QuartOut,

        ExpoOut,
        ExpoIn,

        SineIn,
        SineOut,
        SineInOut,

        CircOut,

        /// <summary>回弹进入（会过冲到 1 以外再收回来）—— **弹窗默认曲线**</summary>
        BackOut,
        BackIn,

        /// <summary>弹性进入（多次回弹，适合奖励/提示）</summary>
        ElasticOut,

        /// <summary>落地弹跳（适合"掉下来"的物件）</summary>
        BounceOut,
    }

    /// <summary>缓动求值（纯函数，无状态、零分配）。</summary>
    public static class RevUIEaseUtil
    {
        /// <summary>求值：t 会被夹到 [0,1]；返回该缓动在 t 处的系数。</summary>
        public static float Evaluate(RevUIEase ease, float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;

            switch (ease)
            {
                case RevUIEase.Linear: return t;

                case RevUIEase.QuadIn: return t * t;
                case RevUIEase.QuadOut: return 1f - (1f - t) * (1f - t);
                case RevUIEase.QuadInOut:
                    return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);

                case RevUIEase.CubicIn: return t * t * t;
                case RevUIEase.CubicOut:
                {
                    float u = 1f - t;
                    return 1f - u * u * u;
                }
                case RevUIEase.CubicInOut:
                {
                    if (t < 0.5f) return 4f * t * t * t;
                    float u = 1f - t;
                    return 1f - 4f * u * u * u;
                }

                case RevUIEase.QuartOut:
                {
                    float u = 1f - t;
                    return 1f - u * u * u * u;
                }

                case RevUIEase.ExpoOut: return 1f - Pow2(-10f * t);
                case RevUIEase.ExpoIn: return Pow2(10f * (t - 1f));

                case RevUIEase.SineIn: return 1f - Cos(t * HalfPi);
                case RevUIEase.SineOut: return Sin(t * HalfPi);
                case RevUIEase.SineInOut: return 0.5f * (1f - Cos(Pi * t));

                case RevUIEase.CircOut:
                {
                    float u = t - 1f;
                    return Sqrt(1f - u * u);
                }

                case RevUIEase.BackOut:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float u = t - 1f;
                    return 1f + c3 * u * u * u + c1 * u * u;
                }

                case RevUIEase.BackIn:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    return c3 * t * t * t - c1 * t * t;
                }

                case RevUIEase.ElasticOut:
                {
                    const float c4 = 2f * Pi / 3f;
                    return Pow2(-10f * t) * Sin((t * 10f - 0.75f) * c4) + 1f;
                }

                default:   // BounceOut
                    return BounceOut(t);
            }
        }

        /// <summary>取反：把 In 换成 Out（倒放动画时用，比"抄两份公式"省一半代码）。</summary>
        public static RevUIEase Reverse(RevUIEase ease)
        {
            switch (ease)
            {
                case RevUIEase.QuadIn: return RevUIEase.QuadOut;
                case RevUIEase.QuadOut: return RevUIEase.QuadIn;
                case RevUIEase.CubicIn: return RevUIEase.CubicOut;
                case RevUIEase.CubicOut: return RevUIEase.CubicIn;
                case RevUIEase.ExpoIn: return RevUIEase.ExpoOut;
                case RevUIEase.ExpoOut: return RevUIEase.ExpoIn;
                case RevUIEase.SineIn: return RevUIEase.SineOut;
                case RevUIEase.SineOut: return RevUIEase.SineIn;
                case RevUIEase.BackIn: return RevUIEase.BackOut;
                case RevUIEase.BackOut: return RevUIEase.BackIn;
                default: return ease;              // InOut / Linear / 单边曲线：原样
            }
        }

        // ── 极简数学（避免引用 System.Math 的 double 往返）────

        private const float Pi = 3.14159265f;
        private const float HalfPi = 1.57079633f;

        private static float Sqrt(float x) => (float)System.Math.Sqrt(x);

        private static float Sin(float x) => (float)System.Math.Sin(x);

        private static float Cos(float x) => (float)System.Math.Cos(x);

        private static float Pow2(float x) => (float)System.Math.Pow(2d, x);

        private static float BounceOut(float t)
        {
            const float n1 = 7.5625f;
            const float d1 = 2.75f;

            if (t < 1f / d1) return n1 * t * t;
            if (t < 2f / d1)
            {
                t -= 1.5f / d1;
                return n1 * t * t + 0.75f;
            }
            if (t < 2.5f / d1)
            {
                t -= 2.25f / d1;
                return n1 * t * t + 0.9375f;
            }

            t -= 2.625f / d1;
            return n1 * t * t + 0.984375f;
        }
    }
}

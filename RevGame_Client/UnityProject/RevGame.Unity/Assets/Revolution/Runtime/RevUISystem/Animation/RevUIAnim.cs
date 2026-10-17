// ============================================================
// RevUIAnim.cs —— UI 动画门面（业务只用这一个类）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【一行搞定】
//     protected override void OnOpen()  => RevUIAnim.PopIn(this);        // 面板弹出
//     protected override void OnClose() => RevUIAnim.PopOut(this);       // 面板收起
//     RevUIAnim.FadeIn(icon);                                            // 任意控件淡入
//     RevUIAnim.SlideIn(this, RevUISlideDirection.Top);                   // 从上滑入
//     RevUIAnim.AddHoverFeedback(btnClose);                               // 按钮悬停/按下反馈
//
//   更省事的做法：在面板/Part 上重写一行预设属性，框架会在打开/关闭时自动播，
//   **并且等动画播完才回调**（关闭动画播完才真正池化/隐藏）：
//     protected override RevUIAnimPreset ShowAnimation => RevUIAnimPreset.PopIn;
//     protected override RevUIAnimPreset HideAnimation => RevUIAnimPreset.PopOut;
//
// 【三条约定】
//   ① 目标随便传：面板自己（this）、Part、Image、Text、Button、RectTransform、CanvasGroup 都行 ——
//      内部自己找 RectTransform / CanvasGroup（没有 CanvasGroup 会自动补一个）。
//   ② **同一个控件上只留一个动画**：再起一个会自动停掉上一个（不会两个动画抢同一个属性）。
//   ③ 一定要给 <paramref name="owner"/>（面板/Part 传自己）—— 销毁时框架会
//      <c>StopAllOf(owner)</c> 一行清干净，不会留在池化过的面板上继续算。
//
// 【性能】
//   引擎零 GC（运行时对象池化 + swap-remove）；每个动画起播时分配一个闭包（一次性），
//   之后每帧只是数值写入。全局关掉动画：<c>RevUISetting.UIAnimationsEnabled = false</c>
//   （此时所有预设直接写终态，业务代码一行都不用改）。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>滑入 / 滑出方向</summary>
    public enum RevUISlideDirection : byte
    {
        /// <summary>上方（从上往下落 / 往上飞走）</summary>
        Top = 0,
        /// <summary>下方</summary>
        Bottom = 1,
        /// <summary>左方</summary>
        Left = 2,
        /// <summary>右方</summary>
        Right = 3,
    }

    /// <summary>UI 动画门面：全部是静态方法，一行一个动画。</summary>
    public static class RevUIAnim
    {
        /// <summary>全局倍速（1 = 原速，0 = 冻结；做"动画速度"设置项时改它一个数）</summary>
        private static float _globalSpeed = 1f;
        public static float GlobalSpeed
        {
            get { return _globalSpeed; }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "动画倍速必须是大于等于 0 的有限数值");
                }
                _globalSpeed = value;
            }
        }

        /// <summary>正在播的动画数（诊断用）</summary>
        public static int ActiveCount => RevUIAnimEngine.ActiveCount;

        /// <summary>一行诊断</summary>
        public static string Dump() => RevUIAnimEngine.Dump();

        // ── 预设播放（最常用）────────────────────────────────

        /// <summary>按预设播一个动画。<paramref name="duration"/> ≤ 0 = 用该预设的默认时长。</summary>
        public static RevUIAnimHandle Play(Component target, RevUIAnimPreset preset,
            float duration = 0f, Action onDone = null, object owner = null)
        {
            if (target == null || preset == RevUIAnimPreset.None)
            {
                onDone?.Invoke();
                return RevUIAnimHandle.None;
            }

            return PlaySpec(target, RevUIAnimSpec.For(preset, duration), onDone, owner);
        }

        /// <summary>淡入（0 → 1）</summary>
        public static RevUIAnimHandle FadeIn(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.FadeIn, duration, onDone, owner);

        /// <summary>淡出（1 → 0）</summary>
        public static RevUIAnimHandle FadeOut(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.FadeOut, duration, onDone, owner);

        /// <summary>弹入：淡入 + 缩放回弹（面板默认手感）</summary>
        public static RevUIAnimHandle PopIn(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.PopIn, duration, onDone, owner);

        /// <summary>弹出：淡出 + 缩小</summary>
        public static RevUIAnimHandle PopOut(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.PopOut, duration, onDone, owner);

        /// <summary>放大进入（只缩放）</summary>
        public static RevUIAnimHandle ScaleIn(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.ScaleIn, duration, onDone, owner);

        /// <summary>缩小退出（只缩放）</summary>
        public static RevUIAnimHandle ScaleOut(Component target, float duration = 0f,
            Action onDone = null, object owner = null)
            => Play(target, RevUIAnimPreset.ScaleOut, duration, onDone, owner);

        /// <summary>从某个方向滑入</summary>
        public static RevUIAnimHandle SlideIn(Component target, RevUISlideDirection from,
            float duration = 0f, Action onDone = null, object owner = null)
            => Play(target, SlidePreset(from, entering: true), duration, onDone, owner);

        /// <summary>往某个方向滑出</summary>
        public static RevUIAnimHandle SlideOut(Component target, RevUISlideDirection to,
            float duration = 0f, Action onDone = null, object owner = null)
            => Play(target, SlidePreset(to, entering: false), duration, onDone, owner);

        // ── 目标值动画（控件反馈用）──────────────────────────

        /// <summary>
        /// 缩放到某个倍率（**相对基准缩放**：1 = 原大小）。悬停放大、按下缩小都走它 ——
        /// 因为总是朝同一个目标收敛，连点/快速进出也不会"越缩越小"。
        /// </summary>
        public static RevUIAnimHandle ScaleTo(Component target, float scale, float duration = 0.12f,
            RevUIEase ease = RevUIEase.CubicOut, Action onDone = null, object owner = null)
        {
            var spec = new RevUIAnimSpec
            {
                Duration = duration,
                Ease = ease,
                Wrap = RevUIAnimWrap.Once,
                Loops = 1,
                UseScale = true,
                ScaleFrom = 1f,          // 相对基准：1 → 目标
                ScaleTo = scale,
            };
            return PlaySpec(target, spec, onDone, owner);
        }

        /// <summary>淡到某个透明度（0..1）</summary>
        public static RevUIAnimHandle FadeTo(Component target, float alpha, float duration = 0.12f,
            RevUIEase ease = RevUIEase.CubicOut, Action onDone = null, object owner = null)
        {
            var spec = new RevUIAnimSpec
            {
                Duration = duration,
                Ease = ease,
                Wrap = RevUIAnimWrap.Once,
                Loops = 1,
                UseAlpha = true,
                AlphaFrom = 1f,
                AlphaTo = alpha,
            };
            return PlaySpec(target, spec, onDone, owner);
        }

        /// <summary>呼吸/闪烁：在 from/to 之间无限往返（提示玩家"点这里"）</summary>
        public static RevUIAnimHandle Breathe(Component target, float duration = 0.6f,
            float from = 0.45f, float to = 1f, bool useScale = false)
        {
            var spec = new RevUIAnimSpec
            {
                Duration = duration,
                Ease = RevUIEase.SineInOut,
                Wrap = RevUIAnimWrap.PingPong,
                Loops = -1,
                UseAlpha = !useScale,
                AlphaFrom = from,
                AlphaTo = to,
                UseScale = useScale,
                ScaleFrom = from,
                ScaleTo = to,
            };
            RevUIAnimHandle handle = PlaySpec(target, spec, null, target);
            return handle;
        }

        // ── 控件反馈（悬停 / 按下）──────────────────────────

        /// <summary>
        /// 给一个可点控件加上"悬停放大 + 按下缩小"的反馈（重复调用只会更新参数）。
        /// 无动画需求的场景不用它；要更花哨的手感就自己组合 <see cref="ScaleTo"/>。
        /// </summary>
        public static void AddHoverFeedback(Component target,
            float hoverScale = 1.06f, float pressScale = 0.94f, float duration = 0.09f)
        {
            if (target == null) return;

            RevUIWidgetFeedback feedback = target.GetComponent<RevUIWidgetFeedback>();
            if (feedback == null) feedback = target.gameObject.AddComponent<RevUIWidgetFeedback>();

            feedback.HoverScale = hoverScale;
            feedback.PressScale = pressScale;
            feedback.Duration = duration;
        }

        // ── 立即应用（关掉动画 / 需要瞬间到位时）─────────────

        /// <summary>直接把某个预设的**终态**写上（不播动画）。关掉动画开关时框架内部也走这里。</summary>
        public static void ApplyEnd(Component target, RevUIAnimPreset preset)
        {
            if (target == null || preset == RevUIAnimPreset.None) return;

            RevUIAnimTarget t = RevUIAnimTarget.Get(target);
            if (t == null) return;

            t.ApplyEndState(RevUIAnimSpec.For(preset));
        }

        // ── 控制 ────────────────────────────────────────────

        /// <summary>停一个动画（句柄失效后调用是安全空操作）</summary>
        public static void Stop(RevUIAnimHandle handle) => RevUIAnimEngine.Stop(handle);

        /// <summary>停掉某个归属对象（面板 / Part / 控件自己）的全部动画，返回停掉几个</summary>
        public static int StopAllOf(object owner) => RevUIAnimEngine.StopAllOf(owner);

        /// <summary>停掉全部动画（关所有界面 / 换场景兜底）</summary>
        public static void StopAll() => RevUIAnimEngine.StopAll();

        /// <summary>这个控件上还有在播的动画吗</summary>
        public static bool IsPlaying(Component target)
        {
            if (target == null) return false;

            RevUIAnimTarget t = target.GetComponent<RevUIAnimTarget>();
            return t != null && t.Handle.IsValid;
        }

        /// <summary>把控件恢复成动画库记录下来的基准态（位置 / 缩放 / 透明度）。</summary>
        public static void RestoreBase(Component target)
        {
            if (target == null) return;

            RevUIAnimTarget t = target.GetComponent<RevUIAnimTarget>();
            if (t == null) return;

            // ★ Bug 修复（2026-09-30）：原来 StopAllOf(t) 传的 owner 是 RevUIAnimTarget 组件本身 ——
            //   但动画注册的 owner 是"播放方"（面板 this / RevUIWidgetFeedback 组件等），
            //   永远不会是 RevUIAnimTarget → 这一句永远停不掉任何动画。
            //   后果：RevUIWidgetFeedback.OnDisable → RestoreBase 后基准值虽被恢复，
            //   但引擎下一帧仍在这个控件上覆写 scale（池化复用时旧动画还在写新主人的值）。
            //   改用 StopTarget：这正是 PlaySpec"新动画顶掉旧动画"用的同一套机制（按句柄停）。
            StopTarget(t);

            t.RestoreBase();
        }

        // ── 内部 ────────────────────────────────────────────

        private static RevUIAnimHandle PlaySpec(Component target, RevUIAnimSpec spec,
            Action onDone, object owner)
        {
            if (target == null)
            {
                onDone?.Invoke();
                return RevUIAnimHandle.None;
            }

            RevUIAnimTarget t = RevUIAnimTarget.Get(target);
            if (t == null)
            {
                onDone?.Invoke();
                return RevUIAnimHandle.None;
            }

            // 同一个控件上只留一个动画：新动画自动顶掉旧的（否则两个动画抢同一个属性）
            StopTarget(t);

            // 全局关掉动画：直接写终态，业务代码不用改
            if (!RevUISetting.UIAnimationsEnabled)
            {
                t.ApplyEndState(spec);
                onDone?.Invoke();
                return RevUIAnimHandle.None;
            }

            RevUIAnimDriver.Install();                  // 第一次用时把驱动挂上

            RevUIAnimSpec s = spec;                     // 闭包捕获一份（热路径零分配）
            RevUIAnimHandle handle = RevUIAnimEngine.Play(spec,
                eased =>
                {
                    if (s.UseAlpha) t.ApplyAlpha(Mathf.LerpUnclamped(s.AlphaFrom, s.AlphaTo, eased));
                    if (s.UseScale) t.ApplyScale(Mathf.LerpUnclamped(s.ScaleFrom, s.ScaleTo, eased));
                    if (s.UseOffset)
                        t.ApplyOffset(Mathf.LerpUnclamped(s.OffsetXFrom, s.OffsetXTo, eased),
                                      Mathf.LerpUnclamped(s.OffsetYFrom, s.OffsetYTo, eased));
                },
                () =>
                {
                    t.Handle = RevUIAnimHandle.None;
                    onDone?.Invoke();
                },
                owner);

            t.Handle = handle;
            return handle;
        }

        /// <summary>恢复根节点及其后代已被动画写入的基准状态（面板回池复用时使用）。</summary>
        internal static void RestoreAllBasesIn(Component root)
        {
            if (root == null) return;

            RevUIAnimTarget[] targets = root.GetComponentsInChildren<RevUIAnimTarget>(true);
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null) targets[i].RestoreBase();
        }

        private static void StopTarget(RevUIAnimTarget target)
        {
            if (!target.Handle.IsValid) return;

            RevUIAnimEngine.Stop(target.Handle);
            target.Handle = RevUIAnimHandle.None;
        }

        private static RevUIAnimPreset SlidePreset(RevUISlideDirection dir, bool entering)
        {
            switch (dir)
            {
                case RevUISlideDirection.Top:
                    return entering ? RevUIAnimPreset.SlideInFromTop : RevUIAnimPreset.SlideOutToTop;
                case RevUISlideDirection.Bottom:
                    return entering ? RevUIAnimPreset.SlideInFromBottom : RevUIAnimPreset.SlideOutToBottom;
                case RevUISlideDirection.Left:
                    return entering ? RevUIAnimPreset.SlideInFromLeft : RevUIAnimPreset.SlideOutToLeft;
                default:
                    return entering ? RevUIAnimPreset.SlideInFromRight : RevUIAnimPreset.SlideOutToRight;
            }
        }
    }
}

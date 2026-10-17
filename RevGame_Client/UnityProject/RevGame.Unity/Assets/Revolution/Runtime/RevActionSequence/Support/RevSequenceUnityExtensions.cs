// RevSequenceUnityExtensions.cs —— Unity 下的"傻瓜式"便捷层
//
//   清单.Play(gameObject)                               一行播放（全局引擎，自动每帧驱动）
//   await 清单.PlayAsync(gameObject)                    播完再往下走（true = 跑完，false = 被取消 / 出错）
//   .MoveTo("飞向玩家", ctx => 目标点, 0.35f)            移动触发者（或 target 指定的物体）
//   .ScaleTo("弹出", Vector3.one, 0.2f, RevEase.OutBack)
//   .FadeTo("淡出", 0f, 0.3f)                           CanvasGroup 透明度
//   .SetActive("隐藏", false)
//   ctx.SourceGameObject() / ctx.SourceTransform()      触发者转成 GameObject / Transform（传 GameObject 或任意组件都行）
//
// 【自动安全】Play(清单, source: gameObject) 之后物体被 Destroy → 序列下一帧按取消收尾（见 RevSequenceRunner.StopWhenSourceDestroyed）。
// 【为什么放 Support】内核（Core / Implementation）不认识 Unity；这里是唯一碰 UnityEngine 的地方。

using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>动作序列的 Unity 便捷方法</summary>
    public static class RevSequenceUnityExtensions
    {
        // ============================================================
        // 播放
        // ============================================================

        /// <summary>
        /// 用全局默认引擎播放（等价于 <c>RevSequencePlayer.Default.Play(definition, source)</c>）。
        /// <code>ChestOpen.Play(gameObject);</code>
        /// </summary>
        public static RevSequenceHandle Play(this RevSequenceDefinition definition, object source = null)
            => RevSequencePlayer.Default.Play(definition, source);

        /// <summary>用全局默认引擎播放并返回可 await 的任务（true = 正常跑完，false = 被取消 / 出错 / 被拒绝）</summary>
        public static RevTask<bool> PlayAsync(this RevSequenceDefinition definition, object source = null)
            => RevSequencePlayer.Default.PlayAsync(definition, source);

        // ============================================================
        // 触发者
        // ============================================================

        /// <summary>触发者的 GameObject（source 是 GameObject 或任意组件都行；已销毁 / 没传返回 null）</summary>
        public static GameObject SourceGameObject(this RevSequenceContext ctx)
        {
            switch (ctx?.Source)
            {
                case GameObject go: return go != null ? go : null;
                case Component c: return c != null ? c.gameObject : null;
                default: return null;
            }
        }

        /// <summary>触发者的 Transform（source 是 GameObject 或任意组件都行；已销毁 / 没传返回 null）</summary>
        public static Transform SourceTransform(this RevSequenceContext ctx)
        {
            GameObject go = ctx.SourceGameObject();
            return go != null ? go.transform : null;
        }

        // ============================================================
        // 常用表现步骤（都是 Tween 的现成包装）
        // ============================================================

        /// <summary>
        /// 移动到目标点（世界坐标）。目标点每帧重新取一次 —— 飞向一个还在移动的玩家也能追上。
        /// </summary>
        /// <param name="target">要移动的物体；不传 = 触发者</param>
        public static RevSequenceBuilder MoveTo(this RevSequenceBuilder builder, string name,
                                                Func<RevSequenceContext, Vector3> to, float duration,
                                                RevEase ease = RevEase.OutQuad,
                                                Func<RevSequenceContext, Transform> target = null)
        {
            if (to == null) throw new ArgumentNullException(nameof(to), $"MoveTo「{name}」需要目标点");

            return builder.Tween(name, duration,
                begin: ctx =>
                {
                    Transform t = Resolve(ctx, target, name);
                    return new TransformStart(t, t.position);
                },
                update: (ctx, s, k) =>
                {
                    if (s.Target != null) s.Target.position = Vector3.LerpUnclamped(s.From, to(ctx), k);
                },
                ease);
        }

        /// <summary>移动到固定的世界坐标</summary>
        public static RevSequenceBuilder MoveTo(this RevSequenceBuilder builder, string name, Vector3 to, float duration,
                                                RevEase ease = RevEase.OutQuad,
                                                Func<RevSequenceContext, Transform> target = null)
            => builder.MoveTo(name, _ => to, duration, ease, target);

        /// <summary>缩放到目标值（localScale）</summary>
        public static RevSequenceBuilder ScaleTo(this RevSequenceBuilder builder, string name, Vector3 to, float duration,
                                                 RevEase ease = RevEase.OutQuad,
                                                 Func<RevSequenceContext, Transform> target = null)
            => builder.Tween(name, duration,
                begin: ctx =>
                {
                    Transform t = Resolve(ctx, target, name);
                    return new TransformStart(t, t.localScale);
                },
                update: (ctx, s, k) =>
                {
                    if (s.Target != null) s.Target.localScale = Vector3.LerpUnclamped(s.From, to, k);
                },
                ease);

        /// <summary>
        /// CanvasGroup 透明度渐变。
        /// </summary>
        /// <param name="target">要渐变的 CanvasGroup；不传 = 触发者身上的 CanvasGroup（没有会报错并提示）</param>
        public static RevSequenceBuilder FadeTo(this RevSequenceBuilder builder, string name, float alpha, float duration,
                                                RevEase ease = RevEase.Linear,
                                                Func<RevSequenceContext, CanvasGroup> target = null)
            => builder.Tween(name, duration,
                begin: ctx =>
                {
                    CanvasGroup group = target != null ? target(ctx) : ctx.SourceGameObject()?.GetComponent<CanvasGroup>();
                    if (group == null)
                        throw new InvalidOperationException(
                            $"FadeTo「{name}」找不到 CanvasGroup：触发者身上加一个 CanvasGroup，或者用 target 参数指定");
                    return new FadeStart(group, group.alpha);
                },
                update: (ctx, s, k) =>
                {
                    if (s.Group != null) s.Group.alpha = Mathf.LerpUnclamped(s.From, alpha, k);
                },
                ease);

        /// <summary>显示 / 隐藏物体（立即步骤）</summary>
        /// <param name="target">要显隐的物体；不传 = 触发者</param>
        public static RevSequenceBuilder SetActive(this RevSequenceBuilder builder, string name, bool active,
                                                   Func<RevSequenceContext, GameObject> target = null)
            => builder.Do(name, ctx =>
            {
                GameObject go = target != null ? target(ctx) : ctx.SourceGameObject();
                if (go == null)
                    throw new InvalidOperationException(
                        $"SetActive「{name}」找不到物体：Play 时 source 传一个 GameObject / 组件，或者用 target 参数指定");
                go.SetActive(active);
            });

        // ============================================================
        // 内部
        // ============================================================

        private readonly struct TransformStart
        {
            public readonly Transform Target;
            public readonly Vector3 From;

            public TransformStart(Transform target, Vector3 from)
            {
                Target = target;
                From = from;
            }
        }

        private readonly struct FadeStart
        {
            public readonly CanvasGroup Group;
            public readonly float From;

            public FadeStart(CanvasGroup group, float from)
            {
                Group = group;
                From = from;
            }
        }

        /// <summary>取要操作的 Transform；取不到直接抛异常，并说明该怎么改（引擎会带上序列名与步骤打日志）</summary>
        private static Transform Resolve(RevSequenceContext ctx, Func<RevSequenceContext, Transform> target, string name)
        {
            Transform t = target != null ? target(ctx) : ctx.SourceTransform();
            if (t == null)
                throw new InvalidOperationException(
                    $"「{name}」找不到要操作的物体：Play 时 source 传一个 GameObject / 组件，或者用 target 参数指定");
            return t;
        }

        /// <summary>
        /// 注入"触发者还活着吗"的判断：Unity 对象被 Destroy 后 == null 为 true（引用本身还在）。
        /// SubsystemRegistration：每次进入播放模式都会执行（关闭域重载时也是）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallHooks()
            => RevSequenceRunner.SourceAliveCheck = source => !(source is UnityEngine.Object obj) || obj != null;
    }
}

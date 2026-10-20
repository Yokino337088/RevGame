// ============================================================
// BossSkillFxView.cs —— Boss 技能特效：一个池对象播放“一次预警”或“一段命中”。
//
// 【职责边界】
// 伤害早已在 BattleService 里结算完，这里只把 BossSkillInfo 快照画出来：
// 预警圈、准星、激光、炸弹、火球、冲击波、传送门、漩涡……动画结束自己回池。
// 本类绝不读写血量、占格，也不保存 Piece 引用——快照里只有坐标和编号。
//
// 【怎么画】
// · 一个池对象内部预先建好 MaxSlots 个子 SpriteRenderer（Awake 时一次性创建，之后反复复用）；
// · 每个“片”共享同一个材质 BossSkill.mat，通过 MaterialPropertyBlock 设置自己的
//   模式（_Mode）、进度（_Progress）、颜色（_Tint）——不克隆材质；
// · DOTween 负责时间轴：缩放、移动、旋转、淡出，以及把进度从 0 推到 1。
//
// 【和框架的关系】
// RevPool 负责创建/复用（[RevPoolPrefab]），IRevPoolable 的 OnPoolReturn 里 Kill 动画、隐藏所有片，
// 防止上一次的残影带到下一次。BoardView 负责“什么时候取一个来播”和“换回合时全部收回”。
// ============================================================
using System;
using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    [RevPoolPrefab(RevResPath.Effects_Combat, "BossSkillFx", Group = RevResGroup.Battle)]
    public sealed class BossSkillFxView : MonoBehaviour, IRevPoolable
    {
        /// <summary>Shader 的效果模式；数值必须与 BossSkill.shader 里的分支一致。</summary>
        private enum FxMode
        {
            Telegraph = 0,
            Shockwave = 1,
            Laser = 2,
            Explosion = 3,
            Sprite = 4,
            Portal = 5,
            Drain = 6,
        }

        [Header("共享材质（BossSkill.mat）：所有片共用，逐片参数走 MaterialPropertyBlock")]
        [SerializeField] private Material material;
        [Header("特效贴图（CC0 替代美术，换图不需要改代码）")]
        [SerializeField] private Sprite glow;
        [SerializeField] private Sprite ring;
        [SerializeField] private Sprite rune;
        [SerializeField] private Sprite spark;
        [SerializeField] private Sprite beam;
        [SerializeField] private Sprite flame;
        [SerializeField] private Sprite scorch;
        [SerializeField] private Sprite slash;
        [SerializeField] private Sprite twirl;
        [SerializeField] private Sprite smoke;
        [SerializeField] private Sprite reticle;
        [SerializeField] private Sprite star;

        /// <summary>
        /// 一个池对象最多同时用几片。弹幕风暴的子弹最多，按 16 发 + 冲击波 + 中心光预留；
        /// 表里把子弹数配得更多时只画前 MaxSlots-4 发，伤害不受影响（伤害在服务里只算一次）。
        /// </summary>
        private const int MaxSlots = 22;
        /// <summary>地面预警的排序：高于棋盘格（≤6）、低于棋子（≥9），预警画在棋子脚下，不遮挡棋子。</summary>
        private const int GroundOrder = 7;
        /// <summary>命中特效的排序：高于棋子（≤25）、低于伤害飘字（≥89），爆炸盖在棋子上但不挡数字。</summary>
        private const int ImpactOrder = 40;

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int ShapeId = Shader.PropertyToID("_Shape");
        private static readonly int AdditiveId = Shader.PropertyToID("_Additive");
        private static readonly int FxUvId = Shader.PropertyToID("_FxUV");

        /// <summary>所有片的父节点：放在技能中心，旋转它就能让激光/刀光整体环绕 Boss 转动。</summary>
        private Transform _pivot;
        private SpriteRenderer[] _renderers;
        /// <summary>每片的当前参数；进度变化时整块重新写入（MaterialPropertyBlock 写入是整体覆盖）。</summary>
        private FxMode[] _modes;
        private Color[] _tints;
        private float[] _progress;
        private float[] _shapes;
        private float[] _additive;
        /// <summary>写参数用的临时块，全对象复用一个，不在动画每帧分配。</summary>
        private MaterialPropertyBlock _block;
        /// <summary>已经分配出去的片数；每次播放从 0 开始重新分配。</summary>
        private int _used;
        /// <summary>本次播放的时间轴；回池时 Kill(false)，不会触发完成回调。</summary>
        private Sequence _sequence;
        private Action<BossSkillFxView> _onFinished;

        /// <summary>首次实例化时建好全部子片，之后只启用/隐藏，不再 new GameObject。</summary>
        private void Awake()
        {
            _block = new MaterialPropertyBlock();
            _pivot = new GameObject("pivot").transform;
            _pivot.SetParent(transform, false);
            _renderers = new SpriteRenderer[MaxSlots];
            _modes = new FxMode[MaxSlots];
            _tints = new Color[MaxSlots];
            _progress = new float[MaxSlots];
            _shapes = new float[MaxSlots];
            _additive = new float[MaxSlots];
            for (int i = 0; i < MaxSlots; i++)
            {
                var child = new GameObject("fx" + i);
                child.transform.SetParent(_pivot, false);
                SpriteRenderer renderer = child.AddComponent<SpriteRenderer>();
                renderer.sharedMaterial = material;
                renderer.enabled = false;
                _renderers[i] = renderer;
            }
        }

        public void OnPoolGet()
        {
            ResetSlots();
        }

        /// <summary>回池：先停动画（不触发完成回调），再隐藏所有片，下一次借出是干净的。</summary>
        public void OnPoolReturn()
        {
            Sequence previous = _sequence;
            _sequence = null;
            previous?.Kill(false);
            _onFinished = null;
            ResetSlots();
        }

        /// <summary>
        /// 播放一次 Boss 技能特效。
        /// </summary>
        /// <param name="info">技能快照（坐标与段数都来自服务，不会是过期对象）。</param>
        /// <param name="telegraph">true = 蓄力预警；false = 第 info.Index 段命中。</param>
        /// <param name="onFinished">动画完整播完时回调，由 BoardView 负责回池。</param>
        public void Play(BossSkillInfo info, bool telegraph, Action<BossSkillFxView> onFinished)
        {
            OnPoolReturn();
            _onFinished = onFinished;
            _sequence = DOTween.Sequence();
            Vector3 center = Center(info.Origin, info.OriginSize);
            _pivot.position = center;
            Color tint = TintOf(info.Kind, info.Enraged);
            float lifetime = telegraph ? BuildTelegraph(info, center, tint) : BuildCast(info, center, tint);
            // 用一个空回调把总时长“撑住”：所有片的动画都在 lifetime 内结束，最后统一回池。
            _sequence.InsertCallback(lifetime, Finish);
        }

        // ================================================================ 预警
        /// <summary>按技能画危险区域，返回整段预警的时长（与蓄力时间一致）。</summary>
        private float BuildTelegraph(BossSkillInfo info, Vector3 center, Color tint)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            float duration = info.Windup;
            Color warning = Color.Lerp(tint, new Color(1f, 0.15f, 0.1f, 1f), 0.45f);
            switch (info.Kind)
            {
                case BossSkillKind.Cleave:
                {
                    // 方形危险区 = Boss 身体 + 四周 Radius 格；刀光在头顶缓慢旋转“蓄势”。
                    Zone(center, (info.OriginSize + info.Radius * 2) * Consts.CellSize, 1f, warning, duration);
                    int s = Take(slash, FxMode.Sprite, tint, 0.8f, ImpactOrder);
                    Place(s, center, info.OriginSize * Consts.CellSize * 1.3f);
                    _sequence.Insert(0f, _renderers[s].transform.DOLocalRotate(new Vector3(0, 0, -200f), duration, RotateMode.FastBeyond360).SetEase(Ease.InQuad));
                    _sequence.Insert(0f, _renderers[s].transform.DOScale(_renderers[s].transform.localScale * 1.25f, duration).SetEase(Ease.InBack));
                    break;
                }
                case BossSkillKind.Snipe:
                {
                    // 瞄准线由细变亮，准星从大到小收紧并旋转——越收越紧代表越接近开枪。
                    Vector3 target = Center(info.A, 1);
                    int sight = Take(beam, FxMode.Laser, warning, 0.9f, GroundOrder + 1);
                    Stretch(sight, center, target, Consts.CellSize * 0.18f);
                    TweenProgress(sight, 0.15f, 0.5f, duration);
                    Zone(target, Consts.CellSize * 1.1f, 0f, warning, duration);
                    int r = Take(reticle, FxMode.Sprite, warning, 0.85f, ImpactOrder);
                    Place(r, target, Consts.CellSize * 2.2f);
                    _sequence.Insert(0f, _renderers[r].transform.DOScale(_renderers[r].transform.localScale * 0.45f, duration).SetEase(Ease.OutQuad));
                    _sequence.Insert(0f, _renderers[r].transform.DOLocalRotate(new Vector3(0, 0, 270f), duration, RotateMode.FastBeyond360));
                    break;
                }
                case BossSkillKind.Bombard:
                {
                    // 锁定框持续到最后一发落下；三个落点的准星依次亮起，顺序与真实落弹顺序一致。
                    float total = duration + Mathf.Max(0, info.Count - 1) * def.Interval;
                    Zone(Center(info.A, 1), (1 + info.Radius * 2) * Consts.CellSize, 1f, warning, total);
                    BoardPos[] points = { info.A, info.B, info.C };
                    for (int i = 0; i < 3; i++)
                    {
                        int r = Take(reticle, FxMode.Sprite, warning, 0.85f, ImpactOrder);
                        Place(r, Center(points[i], 1), 0.01f);
                        float delay = i * 0.18f;
                        _sequence.Insert(delay, _renderers[r].transform.DOScale(Vector3.one * SpriteScale(reticle, Consts.CellSize * 1.1f), 0.35f).SetEase(Ease.OutBack));
                        _sequence.Insert(delay, _renderers[r].transform.DOLocalRotate(new Vector3(0, 0, 360f), total - delay, RotateMode.FastBeyond360));
                    }
                    return total;
                }
                case BossSkillKind.BulletRing:
                case BossSkillKind.LaserSpin:
                {
                    // 圆形危险区：半径包含 Boss 半个身体。中心聚光逐渐变亮，激光技能额外画四根预瞄线缓慢转动。
                    float diameter = (info.Radius * 2 + info.OriginSize) * Consts.CellSize;
                    Zone(center, diameter, 0f, warning, duration);
                    int core = Take(glow, FxMode.Sprite, tint, 0.9f, ImpactOrder);
                    Place(core, center, 0.01f);
                    _sequence.Insert(0f, _renderers[core].transform.DOScale(Vector3.one * SpriteScale(glow, info.OriginSize * Consts.CellSize * 1.4f), duration).SetEase(Ease.InCubic));
                    if (info.Kind == BossSkillKind.LaserSpin)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            int b = Take(beam, FxMode.Laser, warning, 0.95f, GroundOrder + 1);
                            Ray(b, i * 90f, diameter * 0.5f, Consts.CellSize * 0.12f);
                            TweenProgress(b, 0.1f, 0.4f, duration);
                        }
                        _sequence.Insert(0f, _pivot.DOLocalRotate(new Vector3(0, 0, 45f), duration).SetEase(Ease.InOutSine));
                    }
                    break;
                }
                case BossSkillKind.BlinkBlast:
                {
                    // 落点处传送门打开，原地冒烟——玩家能同时看到“从哪来、到哪去、炸多大”。
                    Vector3 destination = Center(info.A, info.OriginSize);
                    Zone(destination, (info.OriginSize + info.Radius * 2) * Consts.CellSize, 1f, warning, duration);
                    int portal = Take(glow, FxMode.Portal, tint, 0.7f, GroundOrder + 1);
                    Place(portal, destination, (info.OriginSize + 0.6f) * Consts.CellSize);
                    TweenProgress(portal, 0f, 0.5f, duration);
                    int fume = Take(smoke, FxMode.Sprite, new Color(tint.r, tint.g, tint.b, 0.7f), 0.3f, ImpactOrder);
                    Place(fume, center, info.OriginSize * Consts.CellSize);
                    _sequence.Insert(0f, _renderers[fume].transform.DOLocalRotate(new Vector3(0, 0, 120f), duration));
                    break;
                }
                case BossSkillKind.SoulDrain:
                {
                    // 被锁定的残血棋子脚下出现漩涡，Boss 身上亮起汲取光——锁人不锁格，躲不掉，所以只做提示。
                    foreach (BoardPos pos in new[] { info.A, info.B })
                    {
                        int v = Take(glow, FxMode.Drain, tint, 0.6f, ImpactOrder);
                        Place(v, Center(pos, 1), Consts.CellSize * 1.4f);
                        TweenProgress(v, 0f, 0.6f, duration);
                    }
                    int aura = Take(twirl, FxMode.Sprite, tint, 0.8f, ImpactOrder);
                    Place(aura, center, info.OriginSize * Consts.CellSize * 1.4f);
                    _sequence.Insert(0f, _renderers[aura].transform.DOLocalRotate(new Vector3(0, 0, -360f), duration, RotateMode.FastBeyond360));
                    break;
                }
                case BossSkillKind.Summon:
                {
                    int circle = Take(rune, FxMode.Sprite, tint, 0.75f, GroundOrder + 1);
                    Place(circle, center, 0.01f);
                    _sequence.Insert(0f, _renderers[circle].transform.DOScale(Vector3.one * SpriteScale(rune, (info.OriginSize + 2) * Consts.CellSize), duration).SetEase(Ease.OutCubic));
                    _sequence.Insert(0f, _renderers[circle].transform.DOLocalRotate(new Vector3(0, 0, 180f), duration));
                    break;
                }
                case BossSkillKind.Enrage:
                {
                    // 狂暴蓄力：血色漩涡在 Boss 身上越转越快、越胀越大。
                    int v = Take(glow, FxMode.Drain, tint, 0.5f, ImpactOrder);
                    Place(v, center, (info.OriginSize + 1) * Consts.CellSize);
                    TweenProgress(v, 0.6f, 0f, duration);
                    _sequence.Insert(0f, _renderers[v].transform.DOScale(_renderers[v].transform.localScale * 1.4f, duration).SetEase(Ease.InExpo));
                    break;
                }
            }
            return duration;
        }

        // ================================================================ 命中
        /// <summary>按技能播放命中特效，返回本段动画时长。</summary>
        private float BuildCast(BossSkillInfo info, Vector3 center, Color tint)
        {
            switch (info.Kind)
            {
                case BossSkillKind.Cleave:
                {
                    // 刀光转一整圈并放大，冲击波向外推开；最后一下星芒闪光。
                    float size = (info.OriginSize + info.Radius * 2) * Consts.CellSize;
                    int s = Take(slash, FxMode.Sprite, tint, 0.7f, ImpactOrder);
                    Place(s, center, size * 0.75f);
                    _sequence.Insert(0f, _renderers[s].transform.DOLocalRotate(new Vector3(0, 0, -360f), 0.35f, RotateMode.FastBeyond360).SetEase(Ease.OutCubic));
                    _sequence.Insert(0f, _renderers[s].transform.DOScale(_renderers[s].transform.localScale * 1.35f, 0.35f).SetEase(Ease.OutBack));
                    TweenProgress(s, 0f, 1f, 0.45f, 0.1f);
                    Shockwave(center, size * 1.15f, tint, 0.5f);
                    Flash(center, info.OriginSize * Consts.CellSize, 0.3f);
                    return 0.6f;
                }
                case BossSkillKind.Snipe:
                {
                    // 射线“一闪而过”：0.3 秒内开光→收光；目标处星芒 + 小冲击波。
                    Vector3 target = Center(info.A, 1);
                    int shot = Take(beam, FxMode.Laser, tint, 0.85f, ImpactOrder);
                    Stretch(shot, center, target, Consts.CellSize * 0.45f);
                    TweenProgress(shot, 0f, 1f, 0.32f);
                    Shockwave(target, Consts.CellSize * 1.8f, tint, 0.4f);
                    Flash(target, Consts.CellSize * 1.2f, 0.35f);
                    return 0.5f;
                }
                case BossSkillKind.Bombard:
                {
                    // 炸弹从高处落下（0.22 秒），着地瞬间火球膨胀、冲击波扩散，焦痕留在地面慢慢淡去。
                    BoardPos[] points = { info.A, info.B, info.C };
                    Vector3 point = Center(points[info.Index % 3], 1);
                    int bomb = Take(flame, FxMode.Sprite, tint, 0.6f, ImpactOrder + 1);
                    Place(bomb, point + Vector3.up * 3f, Consts.CellSize * 0.9f);
                    _renderers[bomb].transform.localRotation = Quaternion.Euler(0, 0, 180f);
                    _sequence.Insert(0f, _renderers[bomb].transform.DOMove(point, 0.22f).SetEase(Ease.InQuad));
                    _sequence.InsertCallback(0.22f, () => _renderers[bomb].enabled = false);
                    float size = (1 + info.Radius * 2) * Consts.CellSize;
                    int fire = Take(glow, FxMode.Explosion, tint, 0.55f, ImpactOrder);
                    Place(fire, point, size);
                    _renderers[fire].enabled = false;
                    _sequence.InsertCallback(0.22f, () => _renderers[fire].enabled = true);
                    TweenProgress(fire, 0f, 1f, 0.6f, 0.22f);
                    int mark = Take(scorch, FxMode.Sprite, new Color(0.15f, 0.08f, 0.05f, 0.8f), 0f, GroundOrder);
                    Place(mark, point, size * 0.8f);
                    _renderers[mark].enabled = false;
                    _sequence.InsertCallback(0.22f, () => _renderers[mark].enabled = true);
                    TweenProgress(mark, 0f, 1f, 1.1f, 0.4f);
                    Shockwave(point, size * 1.2f, tint, 0.45f, 0.22f);
                    return 1.5f;
                }
                case BossSkillKind.BulletRing:
                {
                    // 子弹等角度分布，从 Boss 身边飞到半径边缘并淡出；同时一圈冲击波标出射程。
                    float radius = (info.Radius + info.OriginSize * 0.5f) * Consts.CellSize;
                    int bullets = Mathf.Min(info.Count, MaxSlots - 4);
                    for (int i = 0; i < bullets; i++)
                    {
                        float angle = 360f / bullets * i;
                        Vector3 direction = Quaternion.Euler(0, 0, angle) * Vector3.right;
                        int b = Take(spark, FxMode.Sprite, tint, 0.8f, ImpactOrder);
                        Place(b, center + direction * info.OriginSize * Consts.CellSize * 0.5f, Consts.CellSize * 0.55f);
                        _renderers[b].transform.localRotation = Quaternion.Euler(0, 0, angle);
                        // 错开 0.015 秒依次射出，读起来像“扫射”而不是同时爆开。
                        float delay = i * 0.015f;
                        _sequence.Insert(delay, _renderers[b].transform.DOMove(center + direction * radius, 0.45f).SetEase(Ease.OutQuad));
                        TweenProgress(b, 0f, 1f, 0.2f, delay + 0.35f);
                    }
                    Shockwave(center, radius * 2.1f, tint, 0.55f);
                    return 0.8f;
                }
                case BossSkillKind.BlinkBlast:
                {
                    // 出发点烟雾散开；落点火球 + 冲击波（Boss 图像的瞬移由 BoardView 负责瞬间对齐）。
                    Vector3 from = Center(info.Origin, info.OriginSize);
                    Vector3 to = Center(info.A, info.OriginSize);
                    int fume = Take(smoke, FxMode.Sprite, new Color(tint.r, tint.g, tint.b, 0.8f), 0.3f, ImpactOrder);
                    Place(fume, from, info.OriginSize * Consts.CellSize);
                    _sequence.Insert(0f, _renderers[fume].transform.DOScale(_renderers[fume].transform.localScale * 1.6f, 0.5f));
                    TweenProgress(fume, 0f, 1f, 0.5f);
                    float size = (info.OriginSize + info.Radius * 2) * Consts.CellSize;
                    int fire = Take(glow, FxMode.Explosion, tint, 0.55f, ImpactOrder);
                    Place(fire, to, size);
                    TweenProgress(fire, 0f, 1f, 0.7f);
                    Shockwave(to, size * 1.25f, tint, 0.5f);
                    Flash(to, info.OriginSize * Consts.CellSize, 0.3f);
                    return 0.8f;
                }
                case BossSkillKind.LaserSpin:
                {
                    // 六道激光环绕 Boss，每段转 120°；狂暴时转得更快、颜色更红（tint 已含狂暴色）。
                    float length = (info.Radius + info.OriginSize * 0.5f) * Consts.CellSize;
                    for (int i = 0; i < 6; i++)
                    {
                        int b = Take(beam, FxMode.Laser, tint, 0.9f, ImpactOrder);
                        Ray(b, i * 60f + info.Index * 20f, length, Consts.CellSize * 0.32f);
                        TweenProgress(b, 0f, 1f, 0.5f);
                    }
                    float spin = info.Enraged ? 180f : 120f;
                    _sequence.Insert(0f, _pivot.DOLocalRotate(new Vector3(0, 0, -spin), 0.5f, RotateMode.FastBeyond360).SetEase(Ease.Linear));
                    Flash(center, info.OriginSize * Consts.CellSize, 0.25f);
                    return 0.55f;
                }
                case BossSkillKind.SoulDrain:
                {
                    // 两条绿色光束从目标“抽”回 Boss，Boss 身上漩涡收缩表示吸收完成。
                    foreach (BoardPos pos in new[] { info.A, info.B })
                    {
                        int line = Take(beam, FxMode.Laser, tint, 0.85f, ImpactOrder);
                        Stretch(line, Center(pos, 1), center, Consts.CellSize * 0.3f);
                        TweenProgress(line, 0f, 1f, 0.6f);
                    }
                    int vortex = Take(glow, FxMode.Drain, tint, 0.6f, ImpactOrder);
                    Place(vortex, center, (info.OriginSize + 0.5f) * Consts.CellSize);
                    TweenProgress(vortex, 0f, 1f, 0.7f);
                    _sequence.Insert(0f, _renderers[vortex].transform.DOScale(_renderers[vortex].transform.localScale * 0.5f, 0.7f).SetEase(Ease.InCubic));
                    return 0.75f;
                }
                case BossSkillKind.Summon:
                {
                    int portal = Take(glow, FxMode.Portal, tint, 0.65f, GroundOrder + 1);
                    Place(portal, center, (info.OriginSize + 2) * Consts.CellSize);
                    TweenProgress(portal, 0.3f, 1f, 0.7f);
                    Flash(center, (info.OriginSize + 1) * Consts.CellSize, 0.4f);
                    return 0.75f;
                }
                case BossSkillKind.Enrage:
                {
                    // 两道血色冲击波先后扩散，配合 HUD 弹幕，明确告诉玩家“Boss 进入二阶段”。
                    float size = (info.OriginSize + 6) * Consts.CellSize;
                    Shockwave(center, size, tint, 0.7f);
                    Shockwave(center, size * 1.3f, tint, 0.7f, 0.2f);
                    Flash(center, (info.OriginSize + 1) * Consts.CellSize, 0.5f);
                    return 0.95f;
                }
            }
            return 0.1f;
        }

        // ================================================================ 片的通用操作
        /// <summary>取一片并设置贴图、模式、颜色、叠加程度与排序。片不够时复用最后一片（只会少画，不会报错）。</summary>
        private int Take(Sprite sprite, FxMode mode, Color tint, float additive, int order)
        {
            int index = Mathf.Min(_used, MaxSlots - 1);
            _used++;
            SpriteRenderer renderer = _renderers[index];
            renderer.sprite = sprite;
            renderer.sortingOrder = order;
            renderer.enabled = sprite != null;
            Transform t = renderer.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            _modes[index] = mode;
            _tints[index] = tint;
            _progress[index] = 0f;
            _shapes[index] = 0f;
            _additive[index] = additive;
            Apply(index);
            return index;
        }

        /// <summary>
        /// 把一片的全部参数写进参数块。
        /// ★ 必须显式写 _MainTex：SpriteRenderer 一旦设置了自定义参数块，Shader 读到的 _MainTex 会退回白图。
        /// _FxUV 是当前 Sprite 在图集页里的矩形，Shader 用它换算回 0~1 的局部坐标。
        /// </summary>
        private void Apply(int index)
        {
            SpriteRenderer renderer = _renderers[index];
            Sprite sprite = renderer.sprite;
            if (sprite == null)
            {
                return;
            }
            Vector4 outer = DataUtility.GetOuterUV(sprite);
            _block.Clear();
            _block.SetTexture(MainTexId, sprite.texture);
            _block.SetVector(FxUvId, new Vector4(outer.x, outer.y, outer.z - outer.x, outer.w - outer.y));
            _block.SetFloat(ModeId, (float)_modes[index]);
            _block.SetFloat(ProgressId, _progress[index]);
            _block.SetColor(TintId, _tints[index]);
            _block.SetFloat(ShapeId, _shapes[index]);
            _block.SetFloat(AdditiveId, _additive[index]);
            renderer.SetPropertyBlock(_block);
        }

        /// <summary>把片放到世界坐标 world，并缩放到 worldSize 宽（贴图原始尺寸不同也能对齐格子）。</summary>
        private void Place(int index, Vector3 world, float worldSize)
        {
            Transform t = _renderers[index].transform;
            t.position = new Vector3(world.x, world.y, 0f);
            t.localScale = Vector3.one * SpriteScale(_renderers[index].sprite, worldSize);
        }

        /// <summary>把一张横向光束图拉伸成 from→to 的线段，thickness 是线宽（世界单位）。</summary>
        private void Stretch(int index, Vector3 from, Vector3 to, float thickness)
        {
            Transform t = _renderers[index].transform;
            Vector3 delta = to - from;
            Vector2 size = _renderers[index].sprite.bounds.size;
            t.position = new Vector3((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f, 0f);
            t.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            t.localScale = new Vector3(delta.magnitude / Mathf.Max(0.001f, size.x), thickness / Mathf.Max(0.001f, size.y), 1f);
        }

        /// <summary>从中心沿角度 angle 发出一条长 length 的射线（挂在 pivot 下，旋转 pivot 即整体旋转）。</summary>
        private void Ray(int index, float angle, float length, float thickness)
        {
            Transform t = _renderers[index].transform;
            Vector2 size = _renderers[index].sprite.bounds.size;
            Vector3 direction = Quaternion.Euler(0, 0, angle) * Vector3.right;
            t.localPosition = direction * (length * 0.5f);
            t.localRotation = Quaternion.Euler(0, 0, angle);
            t.localScale = new Vector3(length / Mathf.Max(0.001f, size.x), thickness / Mathf.Max(0.001f, size.y), 1f);
        }

        /// <summary>画一块地面预警区：shape 0=圆 1=方；进度在 duration 内从 0 到 1（越接近 1 闪得越急）。</summary>
        private void Zone(Vector3 world, float worldSize, float shape, Color tint, float duration)
        {
            int z = Take(glow, FxMode.Telegraph, tint, 0.15f, GroundOrder);
            _shapes[z] = shape;
            Place(z, world, worldSize);
            Apply(z);
            TweenProgress(z, 0f, 1f, duration);
            // 出现时轻微弹一下，让“危险区刚出现”这个瞬间更醒目。
            Transform t = _renderers[z].transform;
            Vector3 target = t.localScale;
            t.localScale = target * 0.85f;
            _sequence.Insert(0f, t.DOScale(target, 0.2f).SetEase(Ease.OutBack));
        }

        /// <summary>一圈冲击波：进度 0→1 时圆环由内向外扩散并变细变淡。</summary>
        private void Shockwave(Vector3 world, float worldSize, Color tint, float duration, float delay = 0f)
        {
            int w = Take(glow, FxMode.Shockwave, tint, 0.7f, ImpactOrder);
            Place(w, world, worldSize);
            if (delay > 0f)
            {
                _renderers[w].enabled = false;
                _sequence.InsertCallback(delay, () => _renderers[w].enabled = true);
            }
            TweenProgress(w, 0f, 1f, duration, delay);
        }

        /// <summary>命中瞬间的星芒闪光：迅速放大再淡出。</summary>
        private void Flash(Vector3 world, float worldSize, float duration)
        {
            int f = Take(star, FxMode.Sprite, Color.white, 0.9f, ImpactOrder + 2);
            Place(f, world, worldSize * 0.4f);
            Transform t = _renderers[f].transform;
            _sequence.Insert(0f, t.DOScale(t.localScale * 2.5f, duration).SetEase(Ease.OutExpo));
            TweenProgress(f, 0f, 1f, duration);
        }

        /// <summary>用 DOTween 把某片的 _Progress 从 from 推到 to；每次变化都重写参数块。</summary>
        private void TweenProgress(int index, float from, float to, float duration, float delay = 0f)
        {
            _progress[index] = from;
            Apply(index);
            _sequence.Insert(delay, DOTween.To(() => _progress[index], value =>
            {
                _progress[index] = value;
                Apply(index);
            }, to, Mathf.Max(0.01f, duration)).SetEase(Ease.Linear));
        }

        private void Finish()
        {
            Action<BossSkillFxView> callback = _onFinished;
            _onFinished = null;
            _sequence = null;
            callback?.Invoke(this);
        }

        /// <summary>隐藏所有片并复位 pivot；回池与重新播放前都会调用。</summary>
        private void ResetSlots()
        {
            _used = 0;
            if (_renderers == null)
            {
                return;
            }
            if (_pivot != null)
            {
                _pivot.localRotation = Quaternion.identity;
            }
            foreach (SpriteRenderer renderer in _renderers)
            {
                renderer.enabled = false;
                renderer.SetPropertyBlock(null);
            }
        }

        /// <summary>技能主色；狂暴后整体偏向血红色，玩家一眼能看出“二阶段”。</summary>
        private static Color TintOf(BossSkillKind kind, bool enraged)
        {
            Color color;
            switch (kind)
            {
                case BossSkillKind.Cleave: color = new Color(1f, 0.75f, 0.3f); break;
                case BossSkillKind.Snipe: color = new Color(1f, 0.25f, 0.2f); break;
                case BossSkillKind.Bombard: color = new Color(1f, 0.5f, 0.15f); break;
                case BossSkillKind.BulletRing: color = new Color(0.78f, 0.45f, 1f); break;
                case BossSkillKind.BlinkBlast: color = new Color(0.35f, 0.85f, 1f); break;
                case BossSkillKind.LaserSpin: color = new Color(0.55f, 0.8f, 1f); break;
                case BossSkillKind.SoulDrain: color = new Color(0.45f, 1f, 0.55f); break;
                case BossSkillKind.Summon: color = new Color(0.6f, 0.35f, 1f); break;
                default: color = new Color(1f, 0.12f, 0.18f); break;
            }
            return enraged ? Color.Lerp(color, new Color(1f, 0.1f, 0.15f), 0.5f) : color;
        }

        /// <summary>棋盘格 → 世界坐标：取整块身体的中心，而不是左下角那一格。</summary>
        private static Vector3 Center(BoardPos pos, int size)
        {
            Vector3 world = pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            world += new Vector3(size - 1, size - 1, 0f) * (Consts.CellSize * 0.5f);
            world.z = 0f;
            return world;
        }

        /// <summary>让贴图显示成 worldSize 宽所需的缩放倍数。</summary>
        private static float SpriteScale(Sprite sprite, float worldSize)
        {
            return sprite == null ? 1f : worldSize / Mathf.Max(0.001f, sprite.bounds.size.x);
        }
    }
}

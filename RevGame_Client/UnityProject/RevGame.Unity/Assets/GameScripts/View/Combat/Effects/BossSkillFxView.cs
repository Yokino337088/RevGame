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
using RevGame.Battle;
using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    [RevPoolPrefab(RevResPath.Effects_Combat, "BossSkillFx", Group = RevResGroup.Battle)]
    public sealed partial class BossSkillFxView : MonoBehaviour, IRevPoolable
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
            ColoredLaser = 7,
            MagicOrb = 8,
            EnergyBomb = 9,
        }

        [Header("能量巨大：炸弹轮廓素材（Lorc CC BY 3.0）")]
        [SerializeField] private Sprite energyBomb;
        [Header("新增九种技能的Kenney CC0效果图，按技能9~17顺序")]
        [SerializeField] private Sprite[] extendedSprites;
        [Header("共享材质（BossSkill.mat）：所有片共用，逐片参数走 MaterialPropertyBlock")]
        [SerializeField] private Material material;
        [Header("中心光晕 / 通用特效底图（Glow）")]
        [Tooltip("用于环绕激光蓄力的中心光，以及预警区、冲击波、爆炸、传送门和汲取效果的底图。建议使用居中光晕 Sprite；部分外观由 Shader 生成，换图不等于替换整个效果。")]
        [SerializeField] private Sprite glow;

        [Header("暗红魔法球 / 弹幕网格底图（Ring）")]
        [Tooltip("用于弹幕蓄力和飞行阶段。建议保留完整、居中的圆形 Sprite 网格；球体、裂纹和光环由 Shader 生成，仅换这张图不能直接替换魔法球外观。")]
        [SerializeField] private Sprite ring;

        [Header("召唤蓄力法阵（Rune）")]
        [Tooltip("用于召唤技能蓄力时，Boss 脚下放大并旋转的法阵。可替换为居中的圆形符文 / 魔法阵 Sprite。")]
        [SerializeField] private Sprite rune;

        [Header("火花预留贴图（Spark，当前未使用）")]
        [Tooltip("当前技能播放逻辑未使用此字段，替换它不会改变现有特效。")]
        [SerializeField] private Sprite spark;

        [Header("狙击 / 环绕激光 / 汲取光束（Beam）")]
        [Tooltip("用于狙击瞄准与发射、环绕激光预瞄与发射，以及灵魂汲取连线。使用沿图片横向延伸的光束 Sprite；光束颜色和部分形状由 Shader 控制。")]
        [SerializeField] private Sprite beam;

        [Header("轰炸下落火球（Flame）")]
        [Tooltip("用于轰炸命中前从高处落下的火球，不是落地后的爆炸。可替换为火焰 / 火球 Sprite，播放时会旋转 180 度。")]
        [SerializeField] private Sprite flame;

        [Header("轰炸落地焦痕（Scorch）")]
        [Tooltip("用于轰炸落地后留在棋盘上、逐渐淡出的焦痕。可替换为居中的焦黑印记 / 弹坑 Sprite，显示时会染成深褐色。")]
        [SerializeField] private Sprite scorch;

        [Header("横扫蓄力与命中刀光（Slash）")]
        [Tooltip("用于横扫技能蓄力和施放时，围绕 Boss 旋转、放大的刀光。可替换为居中的弧形斩击 Sprite。")]
        [SerializeField] private Sprite slash;

        [Header("灵魂汲取蓄力漩涡（Twirl）")]
        [Tooltip("用于灵魂汲取蓄力时，Boss 身上旋转的漩涡。可替换为居中的旋涡 Sprite；目标脚下与命中后的汲取效果由 Glow 配合 Shader 绘制。")]
        [SerializeField] private Sprite twirl;

        [Header("瞬移出发点烟雾（Smoke）")]
        [Tooltip("用于瞬移爆破蓄力和施放时，Boss 原位置的烟雾。可替换为居中的烟团 Sprite，播放时会旋转或放大淡出。")]
        [SerializeField] private Sprite smoke;

        [Header("狙击锁定 / 轰炸落点准星（Reticle）")]
        [Tooltip("用于狙击目标处收紧旋转的准星，以及轰炸三个落点的锁定标记。可替换为居中的瞄准框 / 十字准星 Sprite。")]
        [SerializeField] private Sprite reticle;

        [Header("通用命中瞬间星芒（Star）")]
        [Tooltip("用于多种技能命中或施放瞬间的闪光，迅速放大再淡出。可替换为居中的星芒 / 闪光 Sprite，显示时使用白色。")]
        [SerializeField] private Sprite star;

        /// <summary>结构容量：支持表格上限 32 发子弹与额外中心光，避免静默裁掉表内配置的弹幕数量。</summary>
        // 新技能可预警整盘81格；容量为格数+中心装饰，不在播放中动态扩容。
        private const int MaxSlots = 90;

        /// <summary>表现飞行终点越过屏幕最远角；不参与技能合法性和伤害判定。</summary>
        private static float FullScreenTravelDistance(Vector3 center)
        {
            float distance = Consts.BoardSize * Consts.CellSize * Mathf.Sqrt(2f);
            Camera camera = Camera.main;
            if (camera != null)
            {
                float depth = camera.WorldToViewportPoint(center).z;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 corner = camera.ViewportToWorldPoint(new Vector3(i % 2, i / 2, depth));
                    distance = Mathf.Max(distance, Vector3.Distance(center, corner));
                }
            }
            return distance + Consts.CellSize;
        }
        /// <summary>地面预警的排序：高于棋盘格（≤6）、低于棋子（≥9），预警画在棋子脚下，不遮挡棋子。</summary>
        private const int GroundOrder = 7;
        /// <summary>命中特效的排序：高于棋子（≤25）、低于伤害飘字（≥89），爆炸盖在棋子上但不挡数字。</summary>
        private const int ImpactOrder = 40;
        /// <summary>狙击光束相对原美术宽度加粗 50%；只影响显示，不是伤害范围，因此不进玩法表。</summary>
        private const float SnipeLaserWidthScale = 1.5f;
        /// <summary>环绕光束相对原美术宽度加粗一倍；预瞄与命中共用倍率，不改变技能判定，因此不进玩法表。</summary>
        private const float LaserSpinWidthScale = 2f;

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

        /// <summary>能量炸弹只播动画；真实爆炸命中由规则事件另行通知，Tween取消不会漏结算或补伤害。</summary>
        public void PlayEnergyBomb(EnergyBombInfo info, bool warning, Action<BossSkillFxView> onFinished)
        {
            OnPoolReturn();
            _onFinished = onFinished;
            _sequence = DOTween.Sequence();
            Vector3 center = Center(info.Position, 1);
            _pivot.position = center;
            Color tint = new Color(1f, 0.69f, 0.1f);
            if (warning)
            {
                Zone(center, Consts.CellSize * 2.2f, 0f, tint, info.WarningSeconds);
                int bomb = Take(energyBomb, FxMode.EnergyBomb, tint, 0f, ImpactOrder + 2);
                Place(bomb, center + Vector3.up * Consts.CellSize * 4f, Consts.CellSize * 1.45f);
                _sequence.Insert(0f, _renderers[bomb].transform.DOMove(center, info.WarningSeconds * 0.55f).SetEase(Ease.InQuad));
                _sequence.Insert(info.WarningSeconds * 0.55f, _renderers[bomb].transform.DOShakeRotation(info.WarningSeconds * 0.4f, new Vector3(0, 0, 16f), 16));
                _sequence.InsertCallback(info.WarningSeconds, Finish);
            }
            else
            {
                int flash = Take(glow, FxMode.Explosion, tint, 0.35f, ImpactOrder);
                Place(flash, center, Consts.CellSize * 4.5f);
                TweenProgress(flash, 0f, 1f, 0.75f);
                Shockwave(center, Consts.BoardSize * Consts.CellSize * 1.45f, tint, 0.85f);
                Flash(center, Consts.CellSize * 4f, 0.35f);
                _sequence.InsertCallback(0.9f, Finish);
            }
        }

        /// <summary>预警逐格调用与伤害完全相同的几何函数；命中后使用各技能独立外部素材、主色和Shader模式。</summary>
        private float BuildPattern(BossSkillInfo info, bool warning, Color tint)
        {
            var plan = new BossSkillPlan { Kind = info.Kind, A = info.A, B = info.B, C = info.C };
            float duration = warning ? info.Windup + Mathf.Max(0, info.Count - 1) * BossSkillCatalog.Get(info.Kind).Interval : 0.48f;
            int spriteIndex = (int)info.Kind - 9;
            Sprite art = extendedSprites != null && spriteIndex < extendedSprites.Length ? extendedSprites[spriteIndex] : glow;
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    BoardPos pos = new BoardPos(x, y);
                    bool affected = false;
                    int from = warning ? 0 : info.Index, to = warning ? info.Count : info.Index + 1;
                    for (int index = from; index < to; index++)
                    {
                        if (BossPatternGeometry.Contains(info.Kind, info.Origin, info.OriginSize, plan, info.Radius, index, pos)) { affected = true; break; }
                    }
                    if (!affected) { continue; }
                    if (warning) { Zone(Center(pos, 1), Consts.CellSize * 0.94f, 1f, tint, duration); }
                    else
                    {
                        FxMode mode = info.Kind == BossSkillKind.GravityWell ? FxMode.Drain : info.Kind == BossSkillKind.MeteorShower ? FxMode.Explosion : FxMode.Sprite;
                        int slot = Take(art, mode, tint, 0.22f, ImpactOrder);
                        Place(slot, Center(pos, 1), Consts.CellSize * 1.05f);
                        TweenProgress(slot, 0f, 1f, duration);
                        _sequence.Insert(0f, _renderers[slot].transform.DOScale(_renderers[slot].transform.localScale * 1.25f, duration).SetEase(Ease.OutQuad));
                    }
                }
            }
            if (!warning) { BuildPatternBeams(info, tint, duration); }
            return duration;
        }

        /// <summary>
        /// 线形技能在逐格命中标记上叠加连续光束：十字激光不能只剩零散小星点。
        /// 端点均来自施法快照；不改伤害集合，不追踪移动后的Boss，不额外申请材质。
        /// </summary>
        private void BuildPatternBeams(BossSkillInfo info, Color tint, float duration)
        {
            BoardPos center = new BoardPos(info.Origin.X + info.OriginSize / 2, info.Origin.Y + info.OriginSize / 2);
            if (info.Kind == BossSkillKind.CrossLaser)
            {
                PatternBeam(Center(new BoardPos(0, center.Y), 1), Center(new BoardPos(Consts.BoardSize - 1, center.Y), 1), tint, duration);
                PatternBeam(Center(new BoardPos(center.X, 0), 1), Center(new BoardPos(center.X, Consts.BoardSize - 1), 1), tint, duration);
            }
            else if (info.Kind == BossSkillKind.IceLance)
            {
                PatternBeam(Center(new BoardPos(0, info.A.Y), 1), Center(new BoardPos(Consts.BoardSize - 1, info.A.Y), 1), tint, duration);
            }
            else if (info.Kind == BossSkillKind.ChainLightning)
            {
                BoardPos from = info.Index == 0 ? center : info.Index % 3 == 1 ? info.A : info.B;
                BoardPos to = info.Index % 3 == 0 ? info.A : info.Index % 3 == 1 ? info.B : info.C;
                PatternBeam(Center(from, 1), Center(to, 1), tint, duration);
            }
        }

        /// <summary>纯表现线宽与柔边，不表示新增伤害范围；序列停止时随池对象一起回收。</summary>
        private void PatternBeam(Vector3 from, Vector3 to, Color tint, float duration)
        {
            if ((to - from).sqrMagnitude < 0.001f) { return; }
            Vector3 edge = (to - from).normalized * Consts.CellSize * 0.5f;
            int slot = Take(beam, FxMode.ColoredLaser, tint, 0.12f, ImpactOrder + 1);
            Stretch(slot, from - edge, to + edge, Consts.CellSize * 0.65f);
            TweenProgress(slot, 0.08f, 1f, duration);
        }

        // ================================================================ 预警
        /// <summary>按技能画危险区域，返回整段预警的时长（与蓄力时间一致）。</summary>
        private float BuildTelegraph(BossSkillInfo info, Vector3 center, Color tint)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            if ((int)info.Kind >= 18) { return BuildExclusive(info, true); }
            if ((int)info.Kind >= 9) { return BuildPattern(info, true, tint); }
            float duration = info.Windup;
            // 两种激光从预瞄到命中保持红/黄识别色，避免通用红色预警把黄色染成橙色。
            bool coloredLaser = info.Kind == BossSkillKind.Snipe || info.Kind == BossSkillKind.LaserSpin;
            Color warning = coloredLaser ? tint : Color.Lerp(tint, new Color(1f, 0.15f, 0.1f, 1f), 0.45f);
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
                    // 保留彩色光芯并降低纯叠加比例，浅色棋盘上也能读出红色线身。
                    int sight = Take(beam, FxMode.ColoredLaser, warning, 0.15f, GroundOrder + 1);
                    Stretch(sight, center, target, Consts.CellSize * 0.18f * SnipeLaserWidthScale);
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
                {
                    // 全屏招式不画旧的局部危险圆：Boss 身体周围先生成完整一圈子弹。
                    // 这是蓄力表现，伤害仍由技能状态统一结算，不在 Tween 中扣血。
                    int bullets = Mathf.Min(info.Count, MaxSlots - 4);
                    float launchRadius = info.OriginSize * Consts.CellSize * 0.6f;
                    for (int i = 0; i < bullets; i++)
                    {
                        float angle = 360f / bullets * i;
                        Vector3 direction = Quaternion.Euler(0f, 0f, angle) * Vector3.right;
                        // 圆形替代图只提供完整的 Sprite 网格；球体、裂纹和光晕由 Shader 生成，不依赖细闪电像素。
                        int b = Take(ring, FxMode.MagicOrb, tint, 0.12f, ImpactOrder);
                        Place(b, center + direction * launchRadius, Consts.CellSize * 1.05f);
                        _renderers[b].transform.localRotation = Quaternion.Euler(0f, 0f, angle);
                        _sequence.Insert(0f, _renderers[b].transform.DOScale(_renderers[b].transform.localScale * 1.2f, duration).SetEase(Ease.InQuad));
                    }
                    break;
                }
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
                            int b = Take(beam, FxMode.ColoredLaser, warning, 0.15f, GroundOrder + 1);
                            Ray(b, i * 90f, diameter * 0.5f, Consts.CellSize * 0.12f * LaserSpinWidthScale);
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
            if ((int)info.Kind >= 18) { return BuildExclusive(info, false); }
            if ((int)info.Kind >= 9) { return BuildPattern(info, false, tint); }
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
                    int shot = Take(beam, FxMode.ColoredLaser, tint, 0.25f, ImpactOrder);
                    Stretch(shot, center, target, Consts.CellSize * 0.45f * SnipeLaserWidthScale);
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
                    // 所有子弹先位于同一个圆周，沿各自径向同时出发；不再逐发延迟或在 Radius 处消失。
                    // 飞行距离覆盖棋盘对角线与相机可见范围，Boss 在屏幕角落也能扫完整个画面。
                    float radius = FullScreenTravelDistance(center);
                    int bullets = Mathf.Min(info.Count, MaxSlots - 4);
                    float launchRadius = info.OriginSize * Consts.CellSize * 0.6f;
                    // 飞行时长由技能表的 BulletSpeed（格/秒）反算，不再写死：
                    //   · 距离要乘 CellSize 才能把"格"换成世界单位；
                    //   · 子弹从圆周出发，所以实际飞行距离是 radius − launchRadius；
                    //   · 表里配 0 属于坏配置（校验层已拦），这里兜底成 0.9 秒，避免子弹卡在 Boss 身上不动。
                    float speed = BossSkillCatalog.Get(info.Kind).BulletSpeed;
                    float travelDistance = Mathf.Max(0.01f, radius - launchRadius);
                    float flightSeconds = speed > 0.01f ? travelDistance / (speed * Consts.CellSize) : 0.9f;
                    for (int i = 0; i < bullets; i++)
                    {
                        float angle = 360f / bullets * i;
                        Vector3 direction = Quaternion.Euler(0, 0, angle) * Vector3.right;
                        // 圆形替代图只提供完整的 Sprite 网格；球体、裂纹和光晕由 Shader 生成，不依赖细闪电像素。
                        int b = Take(ring, FxMode.MagicOrb, tint, 0.12f, ImpactOrder);
                        Place(b, center + direction * launchRadius, Consts.CellSize * 1.05f);
                        _renderers[b].transform.localRotation = Quaternion.Euler(0, 0, angle);
                        _sequence.Insert(0f, _renderers[b].transform.DOMove(center + direction * radius, flightSeconds).SetEase(Ease.Linear));
                        TweenProgress(b, 0f, 1f, 0.15f, flightSeconds);
                    }
                    Flash(center, info.OriginSize * Consts.CellSize, 0.3f);
                    return flightSeconds + 0.15f;
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
                    // 六道黄色激光环绕 Boss，每段转 120°；狂暴时加快转速，保留黄色便于识别技能。
                    float length = (info.Radius + info.OriginSize * 0.5f) * Consts.CellSize;
                    for (int i = 0; i < 6; i++)
                    {
                        int b = Take(beam, FxMode.ColoredLaser, tint, 0.25f, ImpactOrder);
                        Ray(b, i * 60f + info.Index * 20f, length, Consts.CellSize * 0.32f * LaserSpinWidthScale);
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

        /// <summary>技能美术识别色，不参与玩法数值所以不进表；激光固定红/黄，其余技能狂暴后偏血红。</summary>
        private static Color TintOf(BossSkillKind kind, bool enraged)
        {
            Color color;
            switch (kind)
            {
                case BossSkillKind.Cleave: color = new Color(1f, 0.75f, 0.3f); break;
                case BossSkillKind.Snipe: return new Color(1f, 0.04f, 0.06f);
                case BossSkillKind.Bombard: color = new Color(1f, 0.5f, 0.15f); break;
                case BossSkillKind.BulletRing: return new Color(0.72f, 0.025f, 0.055f);
                case BossSkillKind.BlinkBlast: color = new Color(0.35f, 0.85f, 1f); break;
                case BossSkillKind.LaserSpin: return new Color(1f, 0.85f, 0.03f);
                case BossSkillKind.SoulDrain: color = new Color(0.45f, 1f, 0.55f); break;
                case BossSkillKind.Summon: color = new Color(0.6f, 0.35f, 1f); break;
                case BossSkillKind.MeteorShower: color = new Color(1f, 0.35f, 0.08f); break;
                case BossSkillKind.CrossLaser: color = new Color(1f, 0.76f, 0.1f); break;
                case BossSkillKind.FrostNova: color = new Color(0.4f, 0.86f, 1f); break;
                case BossSkillKind.ChainLightning: color = new Color(0.65f, 0.7f, 1f); break;
                case BossSkillKind.PoisonMarsh: color = new Color(0.5f, 0.88f, 0.22f); break;
                case BossSkillKind.FlameWall: color = new Color(1f, 0.26f, 0.04f); break;
                case BossSkillKind.IceLance: color = new Color(0.6f, 0.95f, 1f); break;
                case BossSkillKind.GravityWell: color = new Color(0.75f, 0.35f, 1f); break;
                case BossSkillKind.DoomSigil: color = new Color(1f, 0.7f, 0.15f); break;
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

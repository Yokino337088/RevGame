// ============================================================
// BattleEffectView.cs —— RevPool 复用物体，DOTween 负责弹道、冲击和伤害飘字。
// 伤害已经在服务中结算，本类只播放快照；动画完成不能再次扣血。
// ============================================================
using DG.Tweening;
using Revolution;
using RevGame.Core;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    // 表现分型：数字、远程飞行、近战冲击。类型只决定如何播放，不决定伤害计算。
    public enum BattleEffectKind
    {
        Damage, Projectile, Melee
    }
    [RevPoolPrefab(RevResPath.Effects_Combat, "BattleEffect", Group = RevResGroup.Battle)]
    // RevPool持有的可复用反馈对象。一个对象同一时刻只播一种效果、一条Tween。
    // 攻击事件已经结算，这里只显示位置/颜色/文字；结束和回池不允许再次扣血。
    public sealed class BattleEffectView : MonoBehaviour, IRevPoolable
    {
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TextMesh damageText;
        /// <summary>五种远程职业的共享资产配方；预制体持有引用，由战斗资源组和图集共同管理。</summary>
        [System.Serializable]
        public sealed class ProjectileAppearance
        {
            // 职业键与原画引用在编辑器装配；禁止运行时回查已死亡的攻击者。
            public PieceKind Kind;
            public Sprite Sprite;
            public Material BodyMaterial, TrailMaterial, AuraMaterial;
            // 本体/尾迹的世界单位尺寸；与原图像素分辨率解耦。
            public Vector2 WorldSize;
            public Color TrailColor = Color.white;
            public Vector2 TrailSize;
            // 一份配方归一件池对象持有。首次/图集重绑读取尺寸与UV；只缓存值，不额外保活Texture像素。
            [System.NonSerialized] internal int BindingVersion = -1;
            [System.NonSerialized] internal Sprite BoundSprite;
            [System.NonSerialized] internal Vector3 SpriteSize;
            [System.NonSerialized] internal Vector4 Uv;
            internal void EnsureGeometry()
            {
                if (BindingVersion == GameSpriteAtlasService.BindingVersion && BoundSprite == Sprite)
                {
                    return;
                }
                BindingVersion = GameSpriteAtlasService.BindingVersion;
                BoundSprite = Sprite;
                SpriteSize = Sprite.bounds.size;
                Uv = DataUtility.GetOuterUV(Sprite);
                // 每种职业只有一份原画，三层共享材质也只对应这份Sprite。图集晚绑定时统一更新材质UV，
                // 不再为每发弹道的三个Renderer各传一个相同参数块；仍保留颜色、排序和原画三层。
                BindMaterial(BodyMaterial);
                BindMaterial(TrailMaterial);
                BindMaterial(AuraMaterial);
            }
            private void BindMaterial(Material material)
            {
                if (material == null)
                {
                    return;
                }
                material.SetVector(SpriteUvId, Uv);
                // _MainTex仍由SpriteRenderer的Sprite/图集绑定提供；不将Texture挂到共享材质保活上章资源。
            }
        }
        [SerializeField] private ProjectileAppearance[] projectileStyles = new ProjectileAppearance[0];
        [SerializeField] private Sprite swordSlash, shieldImpact;
        [SerializeField] private Material swordMaterial, shieldMaterial, assassinMaterial;
        private bool _magicAura;
        [Tooltip("最长飞行秒数，仅表现；命中伤害已由服务结算。")]
        [SerializeField] private float projectileDuration = 0.36f;
        // 每个池对象只创建一个参数块；只在发射时写入图集矩形，不克隆材质、不逐帧取Sprite。
        private MaterialPropertyBlock _projectileProperties;
        private static readonly int SpriteUvId = Shader.PropertyToID("_SpriteUV");
        private Color _trailColor;
        public PieceKind ProjectileKind
        {
            get; private set;
        }
        public Sprite ProjectileArt => spriteRenderer.sprite;
        [Tooltip("同一个池对象内的深色轮廓，保证浅色棋盘上也能看清弹头。")]
        [SerializeField] private SpriteRenderer projectileOutline;
        [Tooltip("同一个池对象内的短拖尾；不是另外申请的粒子、弹道或持续增长的轨迹。")]
        [SerializeField] private SpriteRenderer projectileTrail;
        [SerializeField] private TextMesh[] damageOutlines = new TextMesh[0];
        // 每个池对象只拥有一条动画。回池 Kill(false)：停止即可，不能强制完成再触发回池回调。
        private Tween _animation;
        // 当前借用这件池对象的BoardView回调；Finish先清字段再调用，回池后不能继续引用旧棋盘。
        private System.Action<BattleEffectView> _onFinished;
        // 当前动画统一进度0～1。DOTween只推进此值，位置/缩放/透明度由WriteAnimationProgress一起计算。
        private float _progress;
        // Tween委托每个池对象只创建一次；当前参数存字段，回池先Kill后覆盖，避免闭包保存上一发轨迹。
        // 读进度委托，Awake创建一次并随对象复用，避免每次发射产生新的getter。
        private DG.Tweening.Core.DOGetter<float> _readProgress;
        // 写进度委托，Awake创建一次；动画参数放对象字段而不捕获在每发闭包中。
        private DG.Tweening.Core.DOSetter<float> _writeProgress;
        // 统一结束回调委托，连接Finish；回池时Kill(false)不会人为执行完成再重复归池。
        private TweenCallback _finishAnimation;
        // 本发起点/终点（世界坐标）、基础缩放（倍数）、近战方向（单位向量）。
        // 开始新反馈前停止旧Tween并覆盖，不能让旧动画继续读取新一发字段。
        private Vector3 _animationStart, _animationEnd, _animationScale, _animationDirection;
        // 持续时间单位秒，朝向单位度；仅美术表现，不改变棋子攻速/攻击时钟。
        private float _animationDuration, _animationAngle;
        // 口号使用较小抖动，盾击使用固定角度；模式细节由本发启动时写入，不回查已死亡棋子。
        private bool _animationAttack, _animationShield;
        // 统一动画分支：0=飘字、1=弹道、2=近战。替代各类型每次创建捕获其参数的setter。
        private int _animationMode;
        // 透明度分成动画淡出与遮挡淡化两部分，相乘后落屏；避让刷新不能把快消失的旧字重新点亮。
        private float _fadeAlpha = 1f, _occlusionAlpha = 1f;
        // 基础缩放/峰值为倍数，上浮距离为世界单位；TextReservationRect将未来放大和上浮换算成像素。
        private float _baseTextScale = 1f, _textPeak = 1f, _textRise;
        private Renderer _textRenderer;
        /// <summary>只在首次实例化时查找描边Renderer；反复出入池不再查找或分配数组。</summary>
        private Renderer[] _outlineRenderers;
        private Color _projectileColor;
        // 弹道额外持有寿命上限。即使外部意外 Kill/Pause 了 Tween，也不能留一根永远不动的黄条。
        // 使用游戏时间：正常暂停时整个战斗一起停，恢复后再继续回收。
        private bool _projectileActive;
        private float _projectileExpiresAt;
        public bool IsFloatingText
        {
            get; private set;
        }
        public bool IsAttackText
        {
            get; private set;
        }
        public int TextOrder
        {
            get; private set;
        }
        // 每条飘字同一帧会被新入场文字反复查询避让；缓存仍按位置/缩放失效，不复用动画移动前的矩形。
        // 相机只在本场景寿命内借用，Unity销毁后自动判空，下一次读取重新取得。
        private Camera _textCamera;
        // 上次矩形计算的Unity帧号，-1强制重算；同帧还要比较变换和相机，不能仅凭帧号复用。
        private int _rectFrame = -1;
        // 文字子Renderer上次世界位置和世界缩放，动画移动或缩放后矩形失效。
        private Vector3 _rectPosition, _rectScale;
        // 上次文字子节点旋转；根节点没变但子节点变了，也要重算。旋转非零时回退真实Renderer.bounds。
        private Quaternion _rectRotation;
        // 计算好的屏幕像素边界，包含三像素读字余量；只由TextScreenRect更新，调用方只拿值副本。
        private Rect _cachedTextRect;
        // 字形网格的局部边界，坐标属于文字子Renderer。变换不改字形，可复用它推导世界边界。
        private Bounds _localTextBounds;
        // 这件池对象上次配置的文字。相同文字/字号不重新给正文和四描边赋text，减少字形重建。
        private string _configuredContent;
        // 上次配置字号，-1表示未配置；与内容一起组成局部字形缓存条件，字号改变必重取bounds。
        private int _configuredFontSize = -1;
        // 本地字形范围是否有效；新文案/新字号/Font.textureRebuilt会置false，不能拿旧宽度避让新字。
        private bool _localTextBoundsValid;
        // 上次相机视图/投影矩阵；镜头移动、Boss特写或投影变化都会使世界→屏幕换算结果不同。
        private Matrix4x4 _rectCameraView, _rectCameraProjection;
        // 上次相机像素视口；窗口改变时即使相机世界位置相同，也不能复用旧像素矩形。
        private Rect _rectViewport;
        private FeedbackProjection _projection;
        internal void UseProjection(FeedbackProjection projection)
        {
            _projection = projection;
        }
        /// <summary>屏幕上真实文字的矩形，供整个棋盘统一避让；不使用渲染位置以外的固定“第几条通道”。</summary>
        public Rect TextScreenRect
        {
            get
            {
                Camera camera = _textCamera != null ? _textCamera : (_textCamera = Camera.main);

                if (camera == null)
                {
                    return new Rect();
                }
                if (_textRenderer == null)
                {
                    _textRenderer = damageText.GetComponent<Renderer>();
                }
                // localBounds属于文字子节点，必须用该Renderer的变换；根节点的缩放不能代替子节点偏移。
                Transform textTransform = _textRenderer.transform;
                Vector3 position = textTransform.position;
                Vector3 scale = textTransform.lossyScale;
                Quaternion rotation = textTransform.rotation;
                bool shared = _projection != null && _projection.Camera == camera && _projection.Frame == Time.frameCount;
                Matrix4x4 view = shared ? _projection.View : camera.worldToCameraMatrix;
                Matrix4x4 projection = shared ? _projection.Projection : camera.projectionMatrix;
                Rect viewport = shared ? _projection.Viewport : camera.pixelRect;

                // 缓存必须同时满足帧号、文字变换、相机矩阵和像素视口相同。
                // 只缓存Time.frameCount会漏掉本帧Arrange修改位置后再次查询的情况。
                if (_rectFrame == Time.frameCount && _rectPosition == position && _rectScale == scale && _rectRotation == rotation && _rectCameraView == view && _rectCameraProjection == projection && _rectViewport == viewport)
                {
                    return _cachedTextRect;
                }
                _rectFrame = Time.frameCount;
                _rectPosition = position;
                _rectScale = scale;
                _rectRotation = rotation;
                _rectCameraView = view;
                _rectCameraProjection = projection;
                _rectViewport = viewport;

                // 内容/字号未变则不再向原生Renderer查localBounds；失效后仅取一次并记在本对象。
                if (!_localTextBoundsValid)
                {
                    _localTextBounds = _textRenderer.localBounds;
                    _localTextBoundsValid = true;
                }
                Bounds bounds;

                if (rotation == Quaternion.identity)
                {
                    // 飘字只平移/缩放，局部字形不变：避免每次新字摆位都向原生Renderer重查bounds。
                    // 局部中心用文字子节点TransformPoint转世界坐标，局部尺寸乘世界缩放。
                    // 必须用文字子节点，不能用池对象根节点代替，否则子节点偏移会导致避让位置不准。
                    bounds = new Bounds(textTransform.TransformPoint(_localTextBounds.center), Vector3.Scale(_localTextBounds.size, scale));
                }
                else
                {
                    bounds = _textRenderer.bounds;
                }
                Vector3 min = shared ? _projection.WorldToScreen(bounds.min) : camera.WorldToScreenPoint(bounds.min);
                Vector3 max = shared ? _projection.WorldToScreen(bounds.max) : camera.WorldToScreenPoint(bounds.max);
                _cachedTextRect = Rect.MinMaxRect(Mathf.Min(min.x, max.x) - 3f, Mathf.Min(min.y, max.y) - 3f,
                    Mathf.Max(min.x, max.x) + 3f, Mathf.Max(min.y, max.y) + 3f);
                return _cachedTextRect;
            }
        }
        /// <summary>提前把后续放大和上浮也算入占用范围，避免初始不重叠、动画一放大又挤在一起。</summary>
        public Rect TextReservationRect
        {
            get
            {
                Rect rect = TextScreenRect;
                float scale = _baseTextScale * _textPeak / Mathf.Max(0.01f, transform.localScale.x);
                Vector2 center = rect.center;
                rect.size *= Mathf.Max(1f, scale);
                rect.center = center;
                Camera camera = _textCamera != null ? _textCamera : (_textCamera = Camera.main);

                if (camera != null)
                {
                    float rise = camera.orthographic
                        ? _textRise * (_projection != null && _projection.Camera == camera && _projection.Frame == Time.frameCount ? _projection.PixelsPerWorldY : camera.pixelHeight / (2f * camera.orthographicSize))
                        : camera.WorldToScreenPoint(transform.position + Vector3.up * _textRise).y - camera.WorldToScreenPoint(transform.position).y;
                    rect.height += Mathf.Max(0f, rise);
                }
                return rect;
            }
        }
        /// <summary>数字越大越靠前，文字与自己的描边一起排序，不能让旧描边挡住新字。</summary>
        public void SetTextOrder(int order)
        {
            TextOrder = order;
            _textRenderer.sortingOrder = 100 + order * 2;
            foreach (Renderer outline in _outlineRenderers)
            {
                outline.sortingOrder = 99 + order * 2;
            }
        }
        /// <summary>缓存组件不复制材质；TextMesh及其描边继续共享字体材质，保留动态合批条件。</summary>
        private void Awake()
        {
            _readProgress = ReadAnimationProgress;
            _writeProgress = WriteAnimationProgress;
            _finishAnimation = Finish;
            _projectileProperties = new MaterialPropertyBlock();
            damageText.fontStyle = FontStyle.Bold;
            damageText.anchor = TextAnchor.MiddleCenter;
            damageText.alignment = TextAlignment.Center;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.fontStyle = FontStyle.Bold;
                outline.anchor = TextAnchor.MiddleCenter;
                outline.alignment = TextAlignment.Center;
            }
            _textRenderer = damageText.GetComponent<Renderer>();
            // 动态字体扩容会重建字形网格。即使文案没变，也必须重取局部范围，否则避让会读旧宽度。
            Font.textureRebuilt += OnFontTextureRebuilt;
            _outlineRenderers = new Renderer[damageOutlines.Length];
            for (int i = 0; i < damageOutlines.Length; i++)
            {
                _outlineRenderers[i] = damageOutlines[i].GetComponent<Renderer>();
            }
        }
        /// <summary>空间不足时，下面那条提示降至35%透明度；动画本身的淡出比例仍然保留。</summary>
        public void SetTextOccluded(bool covered)
        {
            float alpha = covered ? 0.35f : 1f;

            // 遮挡关系没有变化就不重复写5份TextMesh颜色。Tween淡出仍由SetNumberAlpha逐帧更新。
            if (Mathf.Approximately(alpha, _occlusionAlpha))
            {
                return;
            }
            _occlusionAlpha = alpha;
            ApplyNumberAlpha();
        }
        /// <summary>供开发验收读取；不参与战斗规则。</summary>
        public bool HasActiveAnimation
        {
            get
            {
                return _animation != null && _animation.IsActive();
            }
        }
        /// <summary>伤害只显示已结算数值；arrange在创建Tween前安排位置，避免动画下一帧把避让位置覆盖掉。</summary>
        public void Show(BattleEffectKind kind, Vector3 position, int damage, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy, System.Action<BattleEffectView> arrange = null)
        {
            if (kind != BattleEffectKind.Damage)
            {
                throw new System.InvalidOperationException("数字特效只能显示已经结算的伤害；弹道应使用 ShowProjectile");
            }
            ShowDamage(position, damage, BattleDamageKind.Normal, onFinished, targetSide, arrange);
        }
        /// <summary>样式直接取服务发出的来源；纯表现函数，不查询棋子、不推测暴击、不再次结算伤害。</summary>
        public void ShowDamage(Vector3 position, int damage, BattleDamageKind kind, System.Action<BattleEffectView> onFinished, GameSide targetSide, System.Action<BattleEffectView> arrange = null)
        {
            ResetVisual(position, onFinished);
            damageText.gameObject.SetActive(true);
            BattleText config = GameTextConfig.Damage(kind, targetSide);
            DamageKind = kind;
            Color color = GameTextConfig.ColorOf(config);
            ConfigureText(config, GameTextConfig.FormatDamage(config, damage), color);
            StartFloatingText(config, position + new Vector3(0f, 0.35f, -0.5f), false, arrange);
        }
        /// <summary>低频攻击口号；文案与基础字号仍来自Excel，整场数量和频率由BoardView统一控制。</summary>
        public void ShowAttack(BattleText config, Vector3 position, System.Action<BattleEffectView> onFinished, System.Action<BattleEffectView> arrange = null)
        {
            ResetVisual(position, onFinished);
            damageText.gameObject.SetActive(true);
            ConfigureText(config, config.Text, GameTextConfig.ColorOf(config));
            StartFloatingText(config, position + new Vector3(0f, 0.55f, -0.5f), true, arrange);
        }
        /// <summary>只读诊断：用于核对真实颜色和文案，避免测试只看Tween存在。</summary>
        public BattleDamageKind DamageKind
        {
            get; private set;
        }
        public Color TextColor => damageText.color;
        public string TextContent => damageText.text;

        // 先判断内容和字号是否改变，只有改变才更新五份TextMesh字形；颜色/透明度仍独立初始化。
        // 回池不清text，因此相同文案复用有效，但字体图集重建会另行使范围失效。
        private void ConfigureText(BattleText config, string content, Color color)
        {
            _rectFrame = -1;
            spriteRenderer.enabled = false;
            damageText.color = color;

            if (_configuredContent != content || _configuredFontSize != config.FontSize)
            {
                _configuredContent = content;
                _configuredFontSize = config.FontSize;
                _localTextBoundsValid = false;
                damageText.text = content;
                damageText.fontSize = config.FontSize;
                foreach (TextMesh outline in damageOutlines)
                {
                    outline.text = content;
                    outline.fontSize = config.FontSize;
                }
            }

            // 复用时文字/颜色已替换，必须强制初始化描边；后续恒定透明度无需每帧重建五份字形颜色。
            _fadeAlpha = 1f;
            ApplyNumberAlpha();
        }
        /// <summary>先限定可见字宽，再避让，最后记录Tween起终点；文字最多保留约一秒，不积压上一轮攻击。</summary>
        private void StartFloatingText(BattleText config, Vector3 position, bool attack, System.Action<BattleEffectView> arrange)
        {
            IsFloatingText = true;
            IsAttackText = attack;
            transform.position = KeepTextOnScreen(position);
            _textPeak = Mathf.Clamp(config.PeakScale, 1f, attack ? 1.12f : 1.5f);
            _textRise = Consts.CellSize * (attack ? 0.30f : 0.45f);
            Camera camera = Camera.main;
            // 本轮在原尺寸上整体放大20%；宽度上限与基础缩放一起改，避免长文案被旧上限重新压小。
            // BoardView随后用实际屏幕包围盒避让，所以放大后仍共享20条预算和重叠半透明规则。
            float maxWidth = camera == null ? 144f : camera.pixelWidth * (attack ? 0.072f : DamageKind == BattleDamageKind.Normal ? 0.048f : 0.105f);
            // 短文案也统一缩小，不能只有长句受限、两个字的口号仍然盖住整颗棋子。
            _baseTextScale = Mathf.Min(attack ? 0.864f : 0.984f, maxWidth / Mathf.Max(1f, TextScreenRect.width * _textPeak));
            transform.localScale = Vector3.one * _baseTextScale;
            arrange?.Invoke(this);
            Vector3 start = transform.position;
            Vector3 end = start + Vector3.up * _textRise;

            if (arrange == null)
            {
                end = KeepTextOnScreen(end);
            }
            float duration = Mathf.Min(config.Duration, attack ? 0.9f : 1.1f);
            transform.localScale = Vector3.one * (_baseTextScale * 0.72f);
            // 一条DOTween同时驱动弹出、衰减振动、上浮、淡出，替代每条文字的Sequence+4个子Tween。
            // 振幅是表现尺度而非玩法数值；临界缩放和上浮范围仍交给BoardView预约避让。
            _animationMode = 0;
            _animationStart = start;
            _animationEnd = end;
            _animationDuration = duration;
            _animationAttack = attack;
            _animation = DOTween.To(_readProgress, _writeProgress, 1f, duration).SetEase(Ease.Linear).OnComplete(_finishAnimation);
        }
        /// <summary>只播放已发生攻击的轨迹；扩大弹头、附带短尾与发射脉冲，动画绝不再次造成伤害。</summary>
        public void ShowProjectile(Vector3 source, Vector3 target, System.Action<BattleEffectView> onFinished, GameSide targetSide = GameSide.Enemy, PieceKind sourceKind = PieceKind.Archer)
        {
            ResetVisual(source, onFinished);
            Vector3 direction = target - source;
            float distance = direction.magnitude;

            // ★ 同格/极短距离没有可飞行空间：直接结束，禁止在原点播放一根“没有位移的子弹”。
            if (distance < 0.05f)
            {
                Finish();
                return;
            }
            Vector3 unit = direction / distance;
            float inset = Mathf.Min(Consts.CellSize * 0.46f, distance * 0.30f);
            source += unit * inset;
            target -= unit * inset;
            transform.position = source;
            // 固定时长会让相邻棋子之间的弹道长时间贴脸；短轨迹更快消失，长轨迹保留可读飞行时间。
            float duration = Mathf.Clamp(projectileDuration * distance / (Consts.CellSize * 4f), 0.10f, projectileDuration);
            _projectileActive = true;
            _projectileExpiresAt = Time.time + duration + 0.12f;
            damageText.gameObject.SetActive(false);
            spriteRenderer.enabled = true;
            ProjectileAppearance style = null;
            for (int i = 0; i < projectileStyles.Length; i++)
            {
                if (projectileStyles[i].Kind == sourceKind)
                {
                    style = projectileStyles[i];
                    break;
                }
            }

            if (style == null || style.Sprite == null || style.BodyMaterial == null || style.TrailMaterial == null)
            {
                RevLog.Warn("远程弹道缺少职业素材：" + sourceKind, "RevGame");
                Finish();
                return;
            }
            ProjectileKind = sourceKind;
            _magicAura = style.AuraMaterial != null;
            spriteRenderer.sprite = style.Sprite;
            spriteRenderer.sharedMaterial = style.BodyMaterial;
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            style.EnsureGeometry();
            Vector3 size = style.SpriteSize;
            float length = Mathf.Min(style.WorldSize.x, Vector3.Distance(source, target) * 0.8f);
            Vector3 flightScale = new Vector3(length / Mathf.Max(0.01f, size.x), style.WorldSize.y / Mathf.Max(0.01f, size.y), 1f);
            // 使用实际packed矩形还原局部UV，Shader的流动和拖尾不会采到图集中相邻图片。
            // 职业材质共享局部UV，原图像素由SpriteRenderer图集绑定；不再逐弹道传相同参数块。
            _projectileProperties.Clear();
            spriteRenderer.SetPropertyBlock(null);
            ConfigureProjectileLayer(projectileOutline, style.Sprite, _magicAura ? style.AuraMaterial : style.BodyMaterial,
                Vector3.zero, _magicAura ? new Vector3(1.6f, 1.8f, 1f) : new Vector3(1.1f, 1.25f, 1f), spriteRenderer.sortingOrder - 2);
            ConfigureProjectileLayer(projectileTrail, style.Sprite, style.TrailMaterial, Vector3.left * (size.x * 1.1f),
                new Vector3(style.TrailSize.x / style.WorldSize.x, style.TrailSize.y / style.WorldSize.y, 1f), spriteRenderer.sortingOrder - 1);
            // 保留下载原画色彩；敌方仅略染暖色，职业仍由原画与共享Shader辨识。
            _projectileColor = targetSide == GameSide.Enemy ? Color.white : new Color(1f, 0.83f, 0.78f);
            _trailColor = style.TrailColor;
            transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            Vector3 normalScale = flightScale;
            transform.localScale = normalScale * 0.88f;
            SetProjectileAlpha(1f);
            // 一条Tween同时推进飞行、发射脉冲和末段淡出；三个Sprite随同一个池对象回收，不额外创建计时器。
            _animationMode = 1;
            _animationStart = source;
            _animationEnd = target;
            _animationScale = normalScale;
            _animation = DOTween.To(_readProgress, _writeProgress, 1f, duration).SetEase(Ease.Linear).OnComplete(_finishAnimation);
        }
        /// <summary>生命周期保险：寿命到期先停止旧动画再归池；不能让失去Tween驱动的弹头永久停留在棋子上。</summary>
        private void LateUpdate()
        {
            if (_projectileActive && Time.time >= _projectileExpiresAt)
            {
                StopAnimation();
                Finish();
            }
        }

        /// <summary>子图复用原画与职业共享材质；尾巴尺寸来自美术配方，跟随归一化根节点，不累计轨迹。</summary>
        private void ConfigureProjectileLayer(SpriteRenderer layer, Sprite art, Material material, Vector3 offset, Vector3 scale, int order)
        {
            if (layer == null)
            {
                return;
            }
            layer.enabled = true;
            layer.sprite = art;
            layer.drawMode = SpriteDrawMode.Simple;
            layer.sharedMaterial = material;
            layer.SetPropertyBlock(null);
            layer.sortingLayerID = spriteRenderer.sortingLayerID;
            layer.sortingOrder = order;
            layer.transform.localPosition = offset;
            layer.transform.localRotation = Quaternion.identity;
            layer.transform.localScale = scale;
        }
        private void SetProjectileAlpha(float alpha)
        {
            Color color = _projectileColor;
            color.a = alpha;
            spriteRenderer.color = color;

            if (projectileOutline != null)
            {
                projectileOutline.color = _magicAura ? new Color(1, 1, 1, alpha) : new Color(0.12f, 0.07f, 0.02f, alpha * 0.85f);
            }
            if (projectileTrail != null)
            {
                Color tail = _trailColor;
                tail.a = alpha;
                projectileTrail.color = tail;
            }
        }

        /// <summary>这个预制体承担数字、口号和弹道；复用为文字前必须关尾巴，避免旧弹道“粘”在文字上。</summary>
        private void HideProjectileLayers()
        {
            if (projectileOutline != null)
            {
                projectileOutline.enabled = false;
            }
            if (projectileTrail != null)
            {
                projectileTrail.enabled = false;
            }
        }

        /// <summary>
        /// 真实近战命中后播放原画斩击/盾击。斩弧扫向目标，盾环沿冲击方向压扁再弹开。
        /// 这里只画0.34秒的命中余波，不延迟伤害、不从已死亡的棋子查询方向；池回收会停掉同一Tween。
        /// </summary>
        public void ShowMelee(Vector3 source, Vector3 target, PieceKind kind, System.Action<BattleEffectView> onFinished)
        {
            ResetVisual(Vector3.Lerp(source, target, 0.72f), onFinished);
            bool shield = kind == PieceKind.Shield;
            Sprite art = shield ? shieldImpact : swordSlash;
            Material material = shield ? shieldMaterial : (kind == PieceKind.Assassin ? assassinMaterial : swordMaterial);

            if (art == null || material == null)
            {
                RevLog.Error("近战特效资源未装配", "RevGame");
                Finish();
                return;
            }
            damageText.gameObject.SetActive(false);
            spriteRenderer.enabled = true;
            spriteRenderer.sprite = art;
            spriteRenderer.sharedMaterial = material;
            _projectileProperties.Clear();
            _projectileProperties.SetTexture("_MainTex", art.texture);
            _projectileProperties.SetVector(SpriteUvId, DataUtility.GetOuterUV(art));
            _projectileProperties.SetFloat("_Progress", 0);
            spriteRenderer.SetPropertyBlock(_projectileProperties);
            float angle = Mathf.Atan2(target.y - source.y, target.x - source.x) * Mathf.Rad2Deg;
            Vector3 origin = transform.position;
            Vector3 direction = (target - source).normalized;
            Vector3 baseScale = new Vector3((shield ? 1.2f : 1.6f) / art.bounds.size.x, 1.6f / art.bounds.size.y, 1);
            _projectileActive = true;
            _projectileExpiresAt = Time.time + .5f;
            _animationMode = 2;
            _animationStart = origin;
            _animationDirection = direction;
            _animationAngle = angle;
            _animationScale = baseScale;
            _animationShield = shield;
            _animation = DOTween.To(_readProgress, _writeProgress, 1f, .34f).SetEase(Ease.OutCubic).OnComplete(_finishAnimation);
        }
        // Tween稳定getter只读0～1进度；不捕获每发起点、终点或回调。
        private float ReadAnimationProgress()
        {
            return _progress;
        }
        /// <summary>统一单Tween驱动，只有当前模式更新对应图层；动画不提交伤害或占格。</summary>
        // 一条Tween驱动所有属性：根据模式读本发参数字段，计算位移、缩放、抖动和透明度。
        // value为0～1，秒数=value×持续时间；这个方法不发伤害事件、不改棋子模型。
        private void WriteAnimationProgress(float value)
        {
            _progress = value;

            // 飘字把“弹出放大→轻抖→上浮→淡出”合并到同一进度；不是删除动画阶段来换性能。
            if (_animationMode == 0)
            {
                float seconds = value * _animationDuration;
                float rise = 1f - seconds / .14f;
                float scale = seconds < .14f ? Mathf.Lerp(.72f, _textPeak, 1f - rise * rise)
                    : Mathf.Lerp(_textPeak, 1f, Mathf.Clamp01((seconds - .14f) / .18f));
                float shake = _animationAttack ? 0f : Mathf.Sin(seconds * 72f) * Mathf.Max(0f, 1f - seconds / .42f) * Consts.CellSize * .045f;
                transform.localScale = Vector3.one * (_baseTextScale * scale);
                transform.position = Vector3.Lerp(_animationStart, _animationEnd, 1f - (1f - value) * (1f - value)) + Vector3.right * shake;
                SetNumberAlpha(Mathf.Clamp01((_animationDuration - seconds) / .28f));
            }
            // 弹道仍匀速从修正后的起点飞到终点，起步脉冲与末段淡出复用同一进度。
            else

                if (_animationMode == 1)
                {
                    transform.position = Vector3.Lerp(_animationStart, _animationEnd, value);
                    float pulse = value < .25f ? Mathf.Lerp(.88f, 1.08f, value / .25f) : Mathf.Lerp(1.08f, 1f, (value - .25f) / .75f);
                    transform.localScale = _animationScale * pulse;
                    SetProjectileAlpha(Mathf.Clamp01((1f - value) / .18f));
                }
                else
                {
                    transform.position = _animationStart + _animationDirection * value * .22f;
                    _projectileProperties.SetFloat("_Progress", value);
                    spriteRenderer.SetPropertyBlock(_projectileProperties);
                    transform.rotation = Quaternion.Euler(0, 0, _animationAngle + (_animationShield ? 0 : Mathf.Lerp(-60, 65, value)));
                    transform.localScale = _animationScale * Mathf.Lerp(.55f, 1.2f, Mathf.Sin(value * Mathf.PI * .5f));
                    spriteRenderer.color = new Color(1, 1, 1, Mathf.Clamp01((1 - value) * 2.4f));
                }
        }
        /// <summary>文字离边缘留出可读空间；顶排命中时缩短上浮距离，防止数字被手机屏幕裁掉。</summary>
        private static Vector3 KeepTextOnScreen(Vector3 position, float top = 0.88f)
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                return position;
            }
            // 视口坐标 0~1 表示整块屏幕，与手机实际像素无关。上方还留有回合 HUD，不能飘进其区域。
            Vector3 viewport = camera.WorldToViewportPoint(position);
            viewport.x = Mathf.Clamp(viewport.x, 0.08f, 0.92f);
            viewport.y = Mathf.Clamp(viewport.y, 0.08f, top);
            return camera.ViewportToWorldPoint(viewport);
        }
        // 新借用先停止旧Tween、隐藏旧层，再覆盖回调/进度/透明度/变换。
        // 先Kill再写字段防止旧setter读取新参数；文字缓存可保留，因为ConfigureText会校验它。
        private void ResetVisual(Vector3 position, System.Action<BattleEffectView> onFinished)
        {
            StopAnimation();
            HideProjectileLayers();
            _projectileActive = false;
            _onFinished = onFinished;
            _progress = 0f;
            _fadeAlpha = _occlusionAlpha = 1f;
            IsFloatingText = IsAttackText = false;
            TextOrder = 0;
            transform.position = position;
            transform.localScale = Vector3.one;
            transform.rotation = Quaternion.identity;
        }
        // 动画淡出与遮挡淡化相互独立；相同值跳过颜色写入，但改变时仍更新正文和描边。
        private void SetNumberAlpha(float alpha)
        {
            if (Mathf.Approximately(_fadeAlpha, alpha))
            {
                return;
            }
            _fadeAlpha = alpha;
            ApplyNumberAlpha();
        }
        // 最终alpha=动画淡出×遮挡透明度，范围0～1。
        // 否则新字避让刷新可能把快消失的旧字恢复到100%不透明，产生闪亮或叠字。
        private void ApplyNumberAlpha()
        {
            float alpha = _fadeAlpha * _occlusionAlpha;
            Color color = damageText.color;
            color.a = alpha;
            damageText.color = color;
            foreach (TextMesh outline in damageOutlines)
            {
                outline.color = new Color(0f, 0f, 0f, alpha);
            }
        }
        /// <summary>满屏时由棋盘替换最旧反馈；先停止动画，再走同一回池回调，不残留旧Tween。</summary>
        public void RetireFeedback()
        {
            StopAnimation();
            Finish();
        }
        // 先清活动/图层，再摘掉旧回调引用并通知棋盘，最后RevPool.Return。
        // 对象回池后可能立即借给另一发，之后不可再读写旧效果字段。
        private void Finish()
        {
            _projectileActive = false;
            // 先撤掉所有图层，再通知棋盘和对象池。即使回池延迟到帧末，也不会再渲染最后一帧黄条。
            HideProjectileLayers();
            spriteRenderer.enabled = false;
            // 先清句柄再回池，避免 OnPoolReturn 去 Kill 正在完成回调的序列。
            _animation = null;
            System.Action<BattleEffectView> callback = _onFinished;
            _onFinished = null;
            callback?.Invoke(this);
            RevPool.Return(this, 0);
        }
        // 先置空句柄再Kill(false)，停止旧动画但不强制完成；避免完成回调二次归池或误用被池复用的Tween。
        private void StopAnimation()
        {
            Tween previous = _animation;
            _animation = null;
            previous?.Kill(false);
        }
        // 借出时清旧活动层和动画；正式Show会覆盖本发参数，Awake创建的稳定委托继续复用。
        public void OnPoolGet()
        {
            StopAnimation();
            HideProjectileLayers();
            spriteRenderer.enabled = false;
        }
        // 归还时清Tween、活动层、参数块和棋盘回调；保留有限的文字/字号/局部字形缓存。
        // 保留字符串只省下一次相同字形的重建，不意味着该字仍在屏幕渲染。
        public void OnPoolReturn()
        {
            _projection = null;
            StopAnimation();
            HideProjectileLayers();
            spriteRenderer.enabled = false;
            spriteRenderer.SetPropertyBlock(null);

            if (projectileOutline != null)
            {
                projectileOutline.SetPropertyBlock(null);
            }
            if (projectileTrail != null)
            {
                projectileTrail.SetPropertyBlock(null);
            }
            _projectileActive = false;
            _onFinished = null;
            IsFloatingText = IsAttackText = false;
            _fadeAlpha = _occlusionAlpha = 1f;
            // 池对象失活后不再渲染；保留有限字形缓存可在下次相同文案时复用，不清空再重建五份网格。
        }
        private void OnDisable()
        {
            StopAnimation();
            HideProjectileLayers();
            spriteRenderer.enabled = false;
            _projectileActive = false;
        }
        // 动态字体增加字形时可能重建网格；只有本对象使用的Font改变才使bounds缓存失效。
        // 即使内容与字号相同，也不能继续使用字体重建前的范围。
        private void OnFontTextureRebuilt(Font font)
        {
            if (damageText.font != font)
            {
                return;
            }
            _localTextBoundsValid = false;
            _rectFrame = -1;
        }
        // Font.textureRebuilt是静态事件，销毁必须退订同一个实例方法，避免字体事件一直引用已销毁池对象。
        private void OnDestroy()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
            StopAnimation();
        }
    }
}

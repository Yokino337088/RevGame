// ============================================================
// PieceView.cs
// 棋子视图可反复回池，绑定模型和启动动画分开；每次停用取消 owner 计时器，防止旧动画继续跑。
// ============================================================
using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 一颗棋子的**表现**组件（挂在 GameRes/Pieces/Unit/SlimePiece.prefab 的根节点上）。
    ///
    /// 【一个预制体装所有棋子】它不关心"我是盾战还是火法" —— 只认一张
    /// <see cref = "PieceArtConfig"/>：业务调 <see cref = "Bind"/> 告诉它"这是敌方 2★ 弓箭手"，
    /// 它就去表里取对应的帧序列、底座、被围标识，然后自己播动画。
    ///
    /// 【为什么不用 Animator】见 <see cref = "PieceArtConfig"/> 的注释：几十颗棋子各挂一台
    /// 状态机是纯浪费；这里用框架的 `RevTimer.Every` 每 50ms 推一次，换 Sprite 就行，
    /// 而且计时器带 `owner: this`，对象销毁自动停，不会泄漏。
    ///
    /// 【分工】它只管画：血量比例、锁死标识都是**被动**接收（业务层算完再告诉它），
    /// 它自己绝不计算规则 —— 表现层不碰规则，是这套架构的底线。
    /// </summary>
    [RevPoolPrefab(RevResPath.Pieces_Unit, "SlimePiece", Group = RevResGroup.Battle)]
    public sealed class PieceView : MonoBehaviour, IRevPoolable
    {
        [Header("美术配置表（由编辑器工具自动填入）")]
        [SerializeField]
        private PieceArtConfig config;
        [Header("渲染器（子节点，由预制体预先挂好）")]
        [SerializeField]
        private SpriteRenderer bodyRenderer;
        [SerializeField]
        private SpriteRenderer baseRenderer;
        [SerializeField]
        private SpriteRenderer hpFillRenderer;
        [SerializeField]
        private SpriteRenderer lockedRenderer;
        [SerializeField]
        private SpriteRenderer crownRenderer;
        [SerializeField]
        private SpriteRenderer haloRenderer;
        private PieceKind _boundKind;
        private StarTier _boundStar;
        private GameSide _boundSide;
        /// <summary>用于验收真实换帧，不把缩放静态图当作序列帧动画。</summary>
        public int AnimationFrameIndex => _frameIndex;
        public int AnimationFrameCount => _frames == null ? 0 : _frames.Length;
        [Header("动画节奏")]
        [Tooltip("动画推进的检查间隔（秒）。50ms 足够，别用每帧")]
        [SerializeField]
        private float tickInterval = 0.05f;
        private PieceArtConfig.Entry _entry;
        private Sprite[] _frames;
        private float _frameInterval = 0.2f;
        private int _frameIndex;
        private float _frameTimer;
        // 标识和身体是两条独立时间轴：身体只有一张图时，头顶三帧标识仍要循环。
        private int _lockedFrameIndex;
        private float _lockedFrameTimer;
        public int LockedAnimationFrameIndex => _lockedFrameIndex;
        private bool _locked;
        private float _hpRatio = 1f;
        private Vector3 _hpFillScale = Vector3.one;
        private Vector3 _hpFillBasePosition = Vector3.zero;
        private Vector3 _moveStart;
        private Vector3 _moveTarget;
        private float _moveElapsed;
        private bool _hasPosition;
        private bool _moving;
        // 三种特效只改原棋子身体；共享一条 Tween 与逐实例参数块，归池先 Kill(false) 再清干净，防串对象。
        private Tween _visualTween;
        private MaterialPropertyBlock _visualProperties;
        private float _deathProgress;
        private float _birthProgress;
        private float _stormProgress;
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int DissolveUvId = Shader.PropertyToID("_DissolveUV");
        private static readonly int BirthId = Shader.PropertyToID("_Birth");
        private static readonly int StormId = Shader.PropertyToID("_Storm");
        // 仅渲染时长，不改变玩法数值：不放入 Excel；所有入口使用相同的表现约定。
        // 时长与 Shader 里的可见区间对齐（可见内容正好铺满整个时长），不留"画面空转"的结尾顿挫。
        private const float BirthSeconds = 0.55f;
        private const float StormSeconds = 0.7f;
        private const float DeathSeconds = 0.6f;
        /// <summary>原棋子表现进度（0~1），供编辑器验证回池/重入；正常待机均为 0。</summary>
        public float DeathDissolveProgress { get { return _deathProgress; } }
        public float BirthProgress { get { return _birthProgress; } }
        public float StormProgress { get { return _stormProgress; } }
        /// <summary>当前绑定的业务棋子 Id（0 = 还没绑定）。</summary>
        public int PieceId { get; private set; }

        /// <summary>当前是否处于"被锁死"表现。</summary>
        public bool IsLockedVisual
        {
            get
            {
                return _locked;
            }
        }

        private void Awake()
        {
            _visualProperties = new MaterialPropertyBlock();
            if (hpFillRenderer != null)
            {
                _hpFillScale = hpFillRenderer.transform.localScale;
                _hpFillBasePosition = hpFillRenderer.transform.localPosition;
            }
        // 动画由一个 20Hz 的计时器统一推进：
        // times = -1 表示无限循环；owner: this → 物体销毁时框架自动取消这个计时器，不会泄漏。
        }

        private void OnEnable()
        {
            // ★ 回池会停用物体，Awake 不会再次执行；所以动画在 OnEnable 启动，在 OnDisable 停止。
            RevTimer.Every(tickInterval, OnTick, -1, RevTimeDomain.Scaled, this);
            RevMono.AddUpdate(AdvanceMove, owner: this);
        }

        private void OnDisable()
        {
            StopBodyVisual();
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
        }

        public void OnPoolGet()
        {
            StopBodyVisual();
            _hasPosition = false;
            _moving = false;
            // 出池后的第一次定位直接落到出生格，不能从上一颗棋子的旧位置飞过来。
        }

        public void OnPoolReturn()
        {
            StopBodyVisual();
            Clear();
        }

        /// <summary>落子后在当前棋子身体上从火焰中显形；正常落子和援军都走同一个入口，不借特效预制体。</summary>
        public void PlayBirthFire()
        {
            if (!PrepareBodyVisual(BirthId)) { return; }
            SetBirthProgress(0.001f);
            // 必须用 Linear：火焰前沿在 Shader 里与进度成正比，OutCubic 会让前沿提前爬出头顶，
            // 剩下一半时长画面纹丝不动——这就是之前"特效最后顿一下"的元凶。
            _visualTween = DOTween.To(() => _birthProgress, SetBirthProgress, 1f, BirthSeconds)
                .SetEase(Ease.Linear).OnComplete(FinishBodyVisual);
        }

        /// <summary>原位升级后由同一颗棋子的身体掀起闪电与雷暴，反复升级会中断旧效果而非叠加实例。</summary>
        public void PlayUpgradeStorm()
        {
            if (!PrepareBodyVisual(StormId)) { return; }
            SetStormProgress(0.001f);
            _visualTween = DOTween.To(() => _stormProgress, SetStormProgress, 1f, StormSeconds)
                .SetEase(Ease.Linear).OnComplete(FinishBodyVisual);
        }

        private bool PrepareBodyVisual(int shaderProperty)
        {
            Sprite sprite = bodyRenderer != null ? bodyRenderer.sprite : null;
            Material material = bodyRenderer != null ? bodyRenderer.sharedMaterial : null;
            if (sprite == null || material == null || !material.HasProperty(shaderProperty))
            {
                RevLog.Warn("棋子入场/升级特效缺少当前帧或 ArcanePiece Shader 参数，编号 " + PieceId, "RevGame");
                return false;
            }
            StopBodyVisual();
            SetVisualUv(sprite);
            return true;
        }

        private void SetVisualUv(Sprite sprite)
        {
            // Sprite Atlas V2 打包位置不是 0~1；先得到当前帧的 UV 矩形，三种效果都按棋子局部坐标演算。
            Vector4 outer = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);
            _visualProperties.Clear();
            // ★ 白块根因：SpriteRenderer 一旦被设置了自定义 MaterialPropertyBlock，shader 的 _MainTex 会退回默认白图，
            // 棋子就只剩一个白色剪影。这里显式把当前帧的图集纹理写进去，shader 才能对序列帧真实采样。
            _visualProperties.SetTexture(MainTexId, sprite.texture);
            _visualProperties.SetVector(DissolveUvId, new Vector4(outer.x, outer.y, outer.z - outer.x, outer.w - outer.y));
        }

        private void SetBirthProgress(float value)
        {
            _birthProgress = value;
            _visualProperties.SetFloat(BirthId, value);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }

        private void SetStormProgress(float value)
        {
            _stormProgress = value;
            _visualProperties.SetFloat(StormId, value);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }

        /// <summary>
        /// 规则层已立即判死、腾出占格；仅让这颗原棋子在画面上多留一小段表现寿命。
        /// 不换 Sprite、不复制材质、不借 BattleEffect，只有身体的真实像素随 Shader 像液体般融下去。
        /// </summary>
        public bool BeginDeathDissolve(System.Action<PieceView> onFinished)
        {
            Sprite sprite = bodyRenderer != null ? bodyRenderer.sprite : null;
            Material material = bodyRenderer != null ? bodyRenderer.sharedMaterial : null;
            if (sprite == null || material == null || !material.HasProperty(DissolveId)) { return false; }

            StopBodyVisual();
            _moving = false;
            // 冻住死亡瞬间的动画帧并隐藏血条/锁死标识等非身体层；不再从模型读取已死棋子。
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
            _frames = null;
            if (baseRenderer != null) { baseRenderer.enabled = false; }
            if (hpFillRenderer != null) { hpFillRenderer.enabled = false; }
            if (lockedRenderer != null) { lockedRenderer.enabled = false; }
            if (crownRenderer != null) { crownRenderer.enabled = false; }
            if (haloRenderer != null) { haloRenderer.enabled = false; }
            SetVisualUv(sprite);
            SetDeathProgress(0.001f);
            _visualTween = DOTween.To(() => _deathProgress, SetDeathProgress, 1f, DeathSeconds)
                .SetEase(Ease.Linear).OnComplete(() =>
                {
                    _visualTween = null;
                    onFinished?.Invoke(this);
                });
            return true;
        }

        private void SetDeathProgress(float value)
        {
            _deathProgress = value;
            _visualProperties.SetFloat(DissolveId, value);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }

        /// <summary>换阶段时只撤入场/升级；已离开规则棋盘的死棋还要完成液体消融，不能一并取消。</summary>
        public void StopTransientVisuals()
        {
            if (_birthProgress > 0f || _stormProgress > 0f) { StopBodyVisual(); }
        }

        private void FinishBodyVisual()
        {
            _visualTween = null;
            StopBodyVisual();
        }

        private void StopBodyVisual()
        {
            Tween previous = _visualTween;
            _visualTween = null;
            previous?.Kill(false);
            _deathProgress = 0f;
            _birthProgress = 0f;
            _stormProgress = 0f;
            if (bodyRenderer != null) { bodyRenderer.SetPropertyBlock(null); }
        }

        /// <summary>只平滑图像，不修改逻辑落点；同一目标重复刷新不会让动画一直从头开始。</summary>
        public void MoveTo(Vector3 position)
        {
            if (!_hasPosition)
            {
                transform.position = position;
                _moveTarget = position;
                _hasPosition = true;
                return;
            }

            if ((_moveTarget - position).sqrMagnitude < 0.0001f)
            {
                return;
            }

            _moveStart = transform.position;
            _moveTarget = position;
            _moveElapsed = 0f;
            _moving = true;
        }

        private void AdvanceMove()
        {
            if (!_moving)
            {
                return;
            }

            _moveElapsed += Time.deltaTime;
            float ratio = Mathf.Clamp01(_moveElapsed / Consts.PieceMoveSeconds);
            transform.position = Vector3.Lerp(_moveStart, _moveTarget, Mathf.SmoothStep(0f, 1f, ratio));
            _moving = ratio < 1f;
        }

        /// <summary>
        /// 绑定一颗棋子的外观。
        /// </summary>
        /// <param name = "pieceId">业务棋子 Id（只用于排查/日志，不参与逻辑）</param>
        /// <param name = "kind">种类（9 种外形）</param>
        /// <param name = "star">星级（1~3）</param>
        /// <param name = "side">阵营（决定用玩家帧还是敌方帧）</param>
        public void Bind(int pieceId, PieceKind kind, StarTier star, GameSide side)
        {
            // ★ 原位升级的Id不会变化，要比较星级才能换外观；相同绑定不能每次刷新都把动画归零。
            if (PieceId == pieceId && _entry != null && _boundKind == kind && _boundStar == star && _boundSide == side) { return; }
            PieceId = pieceId;
            _boundKind = kind;
            _boundStar = star;
            _boundSide = side;
            _entry = config != null ? config.Find(kind, star, side) : null;
            if (_entry == null)
            {
                // 表里没配：保留预制体自带的图，并报一次错（不静默降级成白块）。
                RevLog.Error("棋子美术配置缺失：" + kind + " " + star + " " + side, "RevGame");
                return;
            }

            _frames = _entry.idleFrames;
            _frameInterval = _entry.frameInterval > 0f ? _entry.frameInterval : 0.2f;
            _frameIndex = 0;
            _frameTimer = 0f;
            if (bodyRenderer != null)
            {
                if (_frames != null && _frames.Length > 0)
                {
                    bodyRenderer.sprite = _frames[0];
                }
            }

            if (baseRenderer != null)
            {
                baseRenderer.sprite = _entry.baseSprite;
                baseRenderer.enabled = _entry.baseSprite != null;
            }

            SetLocked(false);
            SetHp(1f);
        }

        /// <summary>
        /// 设置血量比例（0~1）。只改血条长度，不改棋子本体缩放。
        /// </summary>
        public void SetHp(float ratio)
        {
            _hpRatio = Mathf.Clamp01(ratio);
            if (hpFillRenderer == null)
            {
                return;
            }

            // 用缩放表示血量。Sprite 的锚点在图片中心，所以除了缩 X 还要把位置往左补，
            // 视觉上才是"从左往右缩短"而不是"从两头往中间缩"。
            Vector3 scale = _hpFillScale;
            scale.x = _hpFillScale.x * _hpRatio;
            hpFillRenderer.transform.localScale = scale;
            Vector3 pos = _hpFillBasePosition;
            float spriteWidth = hpFillRenderer.sprite == null ? 1f : hpFillRenderer.sprite.bounds.size.x;
            pos.x = _hpFillBasePosition.x - spriteWidth * _hpFillScale.x * (1f - _hpRatio) * 0.5f;
            hpFillRenderer.transform.localPosition = pos;
            hpFillRenderer.enabled = _hpRatio > 0.001f;
        }

        /// <summary>身体放大与占格数据一致；王冠只标主将，玩家能清楚知道要击败谁。</summary>
        public void SetBodySize(int size, bool boss, bool king)
        {
            transform.localScale = Vector3.one * (0.72f * size * Consts.CellSize);
            // ★ 原版Boss素材只有单张立绘，没有可播放的序列。大棋复用图鉴内敌方重剑的真实帧序列。
            // 此处仅改身体尺寸和饰件，绝不覆盖 Bind/OnTick 当前帧；否则每次HUD刷新都会把Boss定格。

            if (crownRenderer != null)
            {
                crownRenderer.enabled = king;
            }

            if (haloRenderer != null)
            {
                haloRenderer.enabled = boss || size > 1;
            }
        }

        /// <summary>
        /// 设置"被锁死"表现（策划案 §06：气=0 时不能动、不能打、挨双倍伤害，UI 要给明显标识）。
        /// </summary>
        public void SetLocked(bool locked)
        {
            // HUD每次刷新都会再次传入同一状态；只在状态切换时归零，否则永远停在第一帧。
            if (_locked != locked) { _lockedFrameIndex = 0; _lockedFrameTimer = 0f; }
            _locked = locked;
            if (lockedRenderer == null)
            {
                return;
            }

            if (!locked || _entry == null || _entry.lockedFrames == null || _entry.lockedFrames.Length == 0)
            {
                lockedRenderer.enabled = false;
                return;
            }

            lockedRenderer.enabled = true;
            lockedRenderer.sprite = _entry.lockedFrames[_lockedFrameIndex % _entry.lockedFrames.Length];
        }

        /// <summary>回池/回收时调用：清掉绑定，避免复用成"上一颗棋子"。</summary>
        public void Clear()
        {
            PieceId = 0;
            _entry = null;
            _frames = null;
            _frameIndex = 0;
            _frameTimer = 0f;
            _hpRatio = 1f;
            _locked = false;
            _lockedFrameIndex = 0;
            _lockedFrameTimer = 0f;

            if (crownRenderer != null)
            {
                crownRenderer.enabled = false;
            }

            if (haloRenderer != null)
            {
                haloRenderer.enabled = false;
            }

            if (bodyRenderer != null)
            {
                bodyRenderer.sprite = null;
            }

            if (baseRenderer != null)
            {
                baseRenderer.enabled = false;
            }

            if (hpFillRenderer != null)
            {
                hpFillRenderer.enabled = false;
            }

            if (lockedRenderer != null)
            {
                lockedRenderer.enabled = false;
            }
        }

        /// <summary>计时器回调：按帧间隔换 Sprite。</summary>
        private void OnTick()
        {
            // 被围标识仍正常换帧；只有本体 Sprite 在短暂浴火/雷暴期间需要固定帧与图集UV。
            if (_locked && lockedRenderer != null && _entry != null && _entry.lockedFrames != null && _entry.lockedFrames.Length > 0)
            {
                _lockedFrameTimer += tickInterval;
                float interval = Mathf.Max(0.05f, _entry.lockedInterval);
                while (_lockedFrameTimer >= interval)
                {
                    _lockedFrameTimer -= interval;
                    _lockedFrameIndex = (_lockedFrameIndex + 1) % _entry.lockedFrames.Length;
                }
                lockedRenderer.sprite = _entry.lockedFrames[_lockedFrameIndex];
            }
            // 原棋子短暂定格的是身体动画帧，不是业务血量、占格或锁死状态。
            if (_visualTween != null && _visualTween.IsActive()) { return; }
            if (_frames == null || _frames.Length <= 1)
            {
                return;
            }

            _frameTimer += tickInterval;
            if (_frameTimer < _frameInterval)
            {
                return;
            }

            _frameTimer -= _frameInterval;
            _frameIndex = (_frameIndex + 1) % _frames.Length;
            if (bodyRenderer != null)
            {
                bodyRenderer.sprite = _frames[_frameIndex];
            }
        }
    }
}

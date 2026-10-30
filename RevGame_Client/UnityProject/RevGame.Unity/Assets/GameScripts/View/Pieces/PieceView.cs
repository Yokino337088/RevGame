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
    public sealed partial class PieceView : MonoBehaviour, IRevPoolable
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
        // 血条由本视图独占。绑定、回池或Boss演出隐藏血条时失效，首次刷新必须重新落屏。
        // 上次血条几何是否已经落屏。绑定/出池/Clear强制失效，首次一定重写，不沿用上一颗棋子的长度。
        private bool _hpVisualValid;
        // 上次血条使用图片；相同比例换了Sprite宽度时也要重算左锚点补偿，不能只比较HpRatio。
        private Sprite _hpVisualSprite;
        private Vector3 _hpFillScale = Vector3.one;
        private Vector3 _hpFillBasePosition = Vector3.zero;
        private Vector3 _moveStart;
        private Vector3 _moveTarget;
        private float _moveElapsed;
        private bool _hasPosition;
        private bool _moving;
        // 本地攻击姿态只缩放身体子节点，绝不缩放根节点；根节点的尺寸由占格大小控制。
        // 用现有RevMono更新复用一个时间戳，不为每次攻击新增Tween、协程或临时特效对象。
        private Vector3 _bodyRestScale;
        // 大于零表示姿态有效，值为结束时的游戏时间（秒）；绝对时间不会误扣出手前那一帧的deltaTime。
        private float _attackPulseEndsAt;
        private const float AttackPulseSeconds = 0.20f;
        // 三种特效只改原棋子身体；共享一条 Tween 与逐实例参数块，归池先 Kill(false) 再清干净，防串对象。
        private Tween _visualTween;
        /// <summary>整队的根节点位移动画，归当前池租约持有；与身体Shader动画分开取消。</summary>
        private Tween _formationTween;
        public bool IsFormationMoving => _formationTween != null && _formationTween.IsActive();
        // 死亡后的视图只拥有短暂表现寿命；回收不再只依赖 Tween 的完成回调。
        // 句柄与回调都属于本次借出的棋子，回池/复用前必须取消，避免旧期限收走新棋子。
        private RevTimerHandle _deathRecycleTimer;
        private System.Action<PieceView> _onDeathFinished;
        private MaterialPropertyBlock _visualProperties;
        private float _deathProgress;
        // 身体燃烧的归一化时间（0~1）；火焰强弱由 Shader 的首尾包络控制，结束/归池都恢复为 0。
        private float _birthProgress;
        private float _stormProgress;
        /// <summary>驻眼阵营编码：0关闭、1玩家、2敌方；只由模型同步，归池或死亡清零。</summary>
        private float _eyeGarrison;
        public bool IsEyeGarrisonVisual => _eyeGarrison > 0f;
        private static readonly int EyeGarrisonId = Shader.PropertyToID("_EyeGarrison");
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int DissolveUvId = Shader.PropertyToID("_DissolveUV");
        private static readonly int BirthId = Shader.PropertyToID("_Birth");
        private static readonly int StormId = Shader.PropertyToID("_Storm");
        // 仅渲染时长，不改变玩法数值：不放入 Excel；所有入口使用相同的表现约定。
        // 1.35秒让玩家看清身体上翻卷的火舌；最后30%渐熄，不追加闪白或额外的收尾特效。
        private const float BirthSeconds = 1.35f;
        // 雷暴留出多次跳电的可读时间；死亡留出“浸染→破孔→残丝消失”的过程（单位：秒）。
        // 延长的只有视图寿命：规则层仍立即移除死棋，不能延迟伤害或占格释放。
        private const float StormSeconds = 0.95f;
        private const float DeathSeconds = 1.05f;
        /// <summary>原棋子表现进度（0~1），供编辑器验证回池/重入；正常待机均为 0。</summary>
        public float DeathDissolveProgress
        {
            get
            {
                return _deathProgress;
            }
        }
        public float BirthProgress
        {
            get
            {
                return _birthProgress;
            }
        }
        public float StormProgress
        {
            get
            {
                return _stormProgress;
            }
        }
        /// <summary>当前绑定的业务棋子 Id（0 = 还没绑定）。</summary>
        public int PieceId
        {
            get; private set;
        }
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

            if (bodyRenderer != null)
            {
                _bodyRestScale = bodyRenderer.transform.localScale;
            }
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
            SetEyeGarrison(false);
            StopFormationMove();
            StopAttackPulse();
            StopBodyVisual();
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
        }
        public void OnPoolGet()
        {
            _hpVisualValid = false;
            SetEyeGarrison(false);
            StopAttackPulse();
            StopBodyVisual();
            _hasPosition = false;
            _moving = false;
            // 出池后的第一次定位直接落到出生格，不能从上一颗棋子的旧位置飞过来。
        }
        public void OnPoolReturn()
        {
            ClearBossSkillVisual();
            ClearBossDeath();
            SetEyeGarrison(false);
            StopFormationMove();
            StopAttackPulse();
            StopBodyVisual();
            Clear();
        }
        /// <summary>
        /// 落子/援军共用的身体燃烧：序列帧照常播放，Shader 只给当前帧的真实像素染上流动火舌。
        /// 只修改本视图的参数块，不克隆共享材质；新效果、切阶段或回池会取消旧 Tween。
        /// </summary>
        public void PlayBirthFire()
        {
            if (!PrepareBodyVisual(BirthId))
            {
                return;
            }
            SetBirthProgress(0.001f);
            // 这里推进的是时间，不是亮度。Linear 让火舌匀速向上翻卷；渐燃/渐熄由 Shader 单独计算，
            // 不能再用收尾闪光来掩盖“带火身体 → 普通身体”的切换。
            _visualTween = DOTween.To(() => _birthProgress, SetBirthProgress, 1f, BirthSeconds)
                .SetEase(Ease.Linear).OnComplete(FinishBodyVisual).OnKill(ClearVisualTween);
        }
        /// <summary>原位升级后由同一颗棋子的身体掀起闪电与雷暴，反复升级会中断旧效果而非叠加实例。</summary>
        public void PlayUpgradeStorm()
        {
            if (!PrepareBodyVisual(StormId))
            {
                return;
            }
            SetStormProgress(0.001f);
            _visualTween = DOTween.To(() => _stormProgress, SetStormProgress, 1f, StormSeconds)
                .SetEase(Ease.Linear).OnComplete(FinishBodyVisual).OnKill(ClearVisualTween);
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
        /// <summary>
        /// 同步当前帧的图集纹理与局部UV；燃烧中每次换帧都要同步，否则会采到上一帧或图集邻图。
        /// 不清空参数块：同一个块还保存正在推进的燃烧/雷暴进度，换图不能把效果时间归零。
        /// </summary>
        private void SetVisualUv(Sprite sprite)
        {
            // Sprite Atlas V2 打包位置不是 0~1；先得到当前帧的 UV 矩形，三种效果都按棋子局部坐标演算。
            Vector4 outer = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);
            // ★ 白块根因：SpriteRenderer 一旦被设置了自定义 MaterialPropertyBlock，shader 的 _MainTex 会退回默认白图，
            // 棋子就只剩一个白色剪影。这里显式把当前帧的图集纹理写进去，shader 才能对序列帧真实采样。
            _visualProperties.SetTexture(MainTexId, sprite.texture);
            _visualProperties.SetVector(DissolveUvId, new Vector4(outer.x, outer.y, outer.z - outer.x, outer.w - outer.y));
        }
        /// <summary>DOTween 的写入端：0~1 进度只属于此棋子，最后一帧已无火，再清理参数不会跳色。</summary>
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
        /// 冻结死亡当刻的原始Sprite，不复制材质、不借BattleEffect；Shader用固定噪声逐步吃掉alpha，
        /// 同时让剩余图案像毒液般下淌。已经侵蚀的孔洞不会随时间重新长回。
        /// </summary>
        public bool BeginDeathDissolve(System.Action<PieceView> onFinished)
        {
            SetEyeGarrison(false);
            Sprite sprite = bodyRenderer != null ? bodyRenderer.sprite : null;
            Material material = bodyRenderer != null ? bodyRenderer.sharedMaterial : null;

            if (sprite == null || material == null || !material.HasProperty(DissolveId))
            {
                return false;
            }
            StopBodyVisual();
            StopAttackPulse();
            StopFormationMove();
            _moving = false;
            // 冻住死亡瞬间的动画帧并隐藏血条/锁死标识等非身体层；不再从模型读取已死棋子。
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
            _frames = null;

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
            if (crownRenderer != null)
            {
                crownRenderer.enabled = false;
            }
            if (haloRenderer != null)
            {
                haloRenderer.enabled = false;
            }
            SetVisualUv(sprite);
            SetDeathProgress(0.001f);
            _onDeathFinished = onFinished;
            // 这是纯表现的游戏时间期限（动画1.05秒 + 0.10秒调度余量），不是延迟模型死亡。
            // 即使 Tween 被外部误停，独立的 owner 计时器仍会归还尸体；暂停游戏时它随战斗一起暂停。
            _deathRecycleTimer = RevTimer.After(DeathSeconds + 0.10f, FinishDeathDissolve, owner: this);
            _visualTween = DOTween.To(() => _deathProgress, SetDeathProgress, 1f, DeathSeconds)
                .SetEase(Ease.Linear).OnComplete(() =>
                {
                    _visualTween = null;
                    FinishDeathDissolve();
                }).OnKill(ClearVisualTween);
            return true;
        }
        /// <summary>外部中断也要解除句柄，防止回收期限再去Kill已经被复用给其他对象的Tween。</summary>
        private void ClearVisualTween()
        {
            _visualTween = null;
        }
        /// <summary>自然完成与到期保护共用一个出口；先撤销旧回调，再回池，因此最多通知一次。</summary>
        private void FinishDeathDissolve()
        {
            System.Action<PieceView> callback = _onDeathFinished;
            // StopBodyVisual 会停期限、清回调及未完成的 Tween；归池又调用它也安全。
            // 先拷贝再清空，防止 OnPoolReturn/OnDisable 重入时再次归还同一实例。
            StopBodyVisual();
            callback?.Invoke(this);
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
            // 阶段结束时占格已经确定：取消整队后对齐目标，不能把上一轮半空中的棋留到准备期。
            if (IsFormationMoving)
            {
                StopFormationMove();
                transform.position = _moveTarget;
            }
            StopAttackPulse();

            if (_birthProgress > 0f || _stormProgress > 0f)
            {
                StopBodyVisual();
            }
        }

        /// <summary>
        /// 每次真实攻击触发0.2秒的身体蓄力/回弹，哪怕全场飘字或弹道名额已满也能看见出手。
        /// 重复调用只重新开始同一条轻量时间轴；死亡、切阶段和回池都会恢复原始大小，防止残留到下一颗棋。
        /// </summary>
        public void PlayAttackPulse()
        {
            // 旧AB尚未更新时可能仍把身体挂在根上；宁可跳过这层姿态，也不能改变整颗棋的占格显示大小。
            if (bodyRenderer == null || bodyRenderer.transform == transform || bodyRenderer.sprite == null || _deathProgress > 0f)
            {
                return;
            }
            _attackPulseEndsAt = Time.time + AttackPulseSeconds;
            // 当场轻压，让刚出手的棋子这一帧就有动作；下一帧再逐渐回弹，不等待额外的特效加载。
            bodyRenderer.transform.localScale = Vector3.Scale(_bodyRestScale, new Vector3(1.10f, 0.92f, 1f));
        }
        /// <summary>恢复身体基准缩放；只在有攻击动作时写Transform，正常待机不增加逐帧写入。</summary>
        private void StopAttackPulse()
        {
            if (_attackPulseEndsAt <= 0f)
            {
                return;
            }
            _attackPulseEndsAt = 0f;

            if (bodyRenderer != null)
            {
                bodyRenderer.transform.localScale = _bodyRestScale;
            }
        }

        /// <summary>身体轻压再回弹，最大横向放大10%、纵向压缩8%；不改变棋子的逻辑位置与攻击距离。</summary>
        private void AdvanceAttackPulse()
        {
            if (_attackPulseEndsAt <= 0f || bodyRenderer == null)
            {
                return;
            }
            float left = Mathf.Max(0f, _attackPulseEndsAt - Time.time);

            if (left <= 0f)
            {
                StopAttackPulse();
                return;
            }
            // 使用出手之后的实际游戏时间，而非当前帧的完整deltaTime：低帧率下也不误把刚启动的动作扣完。
            float phase = 1f - left / AttackPulseSeconds;
            float pulse = Mathf.Cos(phase * Mathf.PI * 0.5f);
            bodyRenderer.transform.localScale = Vector3.Scale(_bodyRestScale, new Vector3(1f + pulse * 0.10f, 1f - pulse * 0.08f, 1f));
        }
        private void FinishBodyVisual()
        {
            _visualTween = null;
            StopBodyVisual();
        }
        /// <summary>
        /// 当前视图所有身体特效的统一收尾；可重复调用，回池也必须调用。
        /// 取消旧回调、清理进度后重新交还Sprite贴图绑定，避免末帧白闪与下次借出串状态。
        /// </summary>
        private void StopBodyVisual()
        {
            // Boss演出拥有独立参数块；旧阶段/驻眼回调不能清空它，否则会把进度与贴图一起撤掉。
            if (_bossDeathActive)
            {
                return;
            }
            _deathRecycleTimer.Stop();
            _onDeathFinished = null;
            Tween previous = _visualTween;
            _visualTween = null;
            previous?.Kill(false);
            _deathProgress = 0f;
            _birthProgress = 0f;
            _stormProgress = 0f;
            // ★ 不仅清 Renderer，也清复用的草稿。下一次换帧只更新纹理和UV，不能带回旧死亡进度。
            _visualProperties?.Clear();
            ApplyBossShader();

            if (bodyRenderer != null)
            {
                bodyRenderer.SetPropertyBlock(null);

                if (bodyRenderer.sprite != null)
                {
                    // ★ SpriteRenderer把当前帧纹理也放在内部参数块里，直接清空会短暂采到默认白图。
                    // 同一次方法内先撤下再重新绑定同一Sprite，强制引擎重建贴图绑定；中间不会渲染，
                    // 不会出现空帧，也不用给待机棋子永久保留自定义参数块或克隆材质。
                    Sprite current = bodyRenderer.sprite;
                    bodyRenderer.sprite = null;
                    bodyRenderer.sprite = current;
                }
                // 入场火焰/升级雷暴结束只撤短效，驻眼持续光效仍属于当前棋子，不能被一并清掉。
                if (_eyeGarrison > 0f)
                {
                    ApplyEyeGarrison();
                }
            }
        }
        /// <summary>绑定规则层的驻眼状态。复用原有参数块和共享材质，不逐棋克隆材质、不生成额外光效物体。</summary>
        public void SetEyeGarrison(bool active)
        {
            float value = active ? (_boundSide == GameSide.Player ? 1f : 2f) : 0f;

            if (_eyeGarrison == value)
            {
                return;
            }
            _eyeGarrison = value;
            ApplyEyeGarrison();
        }
        /// <summary>每次换动画帧同时更新图集局部UV；光带始终在本棋轮廓内流动，透明区不出现方块。</summary>
        private void ApplyEyeGarrison()
        {
            if (_bossDeathActive)
            {
                return;
            }
            if (bodyRenderer == null || _visualProperties == null || bodyRenderer.sprite == null)
            {
                return;
            }
            SetVisualUv(bodyRenderer.sprite);
            _visualProperties.SetFloat(EyeGarrisonId, _eyeGarrison);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }
        /// <summary>
        /// 瞬间对齐到 position，打断正在进行的平滑移动。Boss 瞬移技能使用：
        /// 平滑滑过去会让玩家误以为 Boss 是“走”过去的，瞬移必须一帧到位。
        /// </summary>
        public void SnapTo(Vector3 position)
        {
            StopFormationMove();
            _moving = false;
            _hasPosition = true;
            _moveTarget = position;
            transform.position = position;
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
            StopFormationMove();
            _moveStart = transform.position;
            _moveTarget = position;
            _moveElapsed = 0f;
            _moving = true;
        }
        /// <summary>集结时沿抛物线跃入阵位，身体伴随已有雷光Shader。0.65秒和弧高是纯美术表现，不改变占格时间。</summary>
        /// <summary>敌近战接敌跳跃；模型已原子Move，Tween只画0.28秒弧线。复用已有回收停止契约。</summary>
        public void JumpTo(Vector3 position)
        {
            StopFormationMove();
            _moveTarget = position;
            _formationTween = transform.DOJump(position, .26f, 1, .28f).SetEase(Ease.OutQuad);
            Tween tween = _formationTween;
            tween.OnKill(() =>
            {
                if (ReferenceEquals(_formationTween, tween))
                {
                    _formationTween = null;
                }
            });
        }
        public void MoveFormationTo(Vector3 position)
        {
            StopFormationMove();
            _moving = false;
            _hasPosition = true;
            _moveTarget = position;
            Vector3 start = transform.position;
            float progress = 0f;
            PlayUpgradeStorm();
            Tween tween = DOTween.To(() => progress, value =>
            {
                progress = value;
                transform.position = Vector3.Lerp(start, position, value) + Vector3.up * (Mathf.Sin(value * Mathf.PI) * Consts.CellSize * 0.65f);
            }, 1f, 0.65f).SetEase(Ease.InOutCubic);
            _formationTween = tween;
            tween.OnComplete(() => { transform.position = position; });
            tween.OnKill(() =>
            {
                if (ReferenceEquals(_formationTween, tween))
                {
                    _formationTween = null;
                }
            });
        }
        private void StopFormationMove()
        {
            Tween previous = _formationTween;
            _formationTween = null;
            previous?.Kill(false);
        }
        private void AdvanceMove()
        {
            // 与平滑移动共享既有更新回调；站桩远程也需要推进攻击姿态，故必须放在_moving判断之前。
            AdvanceAttackPulse();

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
            if (PieceId == pieceId && _entry != null && _boundKind == kind && _boundStar == star && _boundSide == side)
            {
                return;
            }
            RestoreBossArt();
            StopAttackPulse();
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
            _hpVisualValid = false;
            SetHp(1f);
        }
        /// <summary>
        /// 设置血量比例（0~1）。只改血条长度，不改棋子本体缩放。
        /// </summary>
        // 比例输入为0～1，只改血条子节点。先比较比例/图片/可见性，完全没变才跳过原生Transform写入。
        // 演出曾隐藏血条时enabled不同，必须重新落屏；模型生命仍由BattleService/Piece维护。
        public void SetHp(float ratio)
        {
            float next = Mathf.Clamp01(ratio);

            if (hpFillRenderer == null)
            {
                _hpRatio = next;
                return;
            }
            // 固定48棋每次同步都会调用这里；血量和Sprite未变时跳过原生Transform写入。
            // enabled也参与校验，演出曾隐藏血条时不能命中旧结果而一直看不到血条。
            // 同时核对可见性：上一演出禁用了Renderer，即使血量没变也需要恢复；仅缓存比例会留下消失血条。
            bool visible = next > 0.001f;

            if (_hpVisualValid && _hpRatio == next && _hpVisualSprite == hpFillRenderer.sprite && hpFillRenderer.enabled == visible)
            {
                return;
            }
            _hpRatio = next;
            _hpVisualSprite = hpFillRenderer.sprite;
            _hpVisualValid = true;

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
            IsKingVisual = boss && king;
            transform.localScale = Vector3.one * (0.72f * size * Consts.CellSize);

            // Boss关键帧在BindBossArt中绑定；尺寸刷新绝不覆盖当前Sprite或重置帧索引。

            if (crownRenderer != null)
            {
                crownRenderer.enabled = king && (_bossArt == null || !_bossArt.BuiltInDecorations);
            }
            if (haloRenderer != null)
            {
                // 章节Boss只显示专属演出，禁用通用圆形底座；普通大棋保留原标记。
                haloRenderer.enabled = !boss && size > 1;
            }
        }

        /// <summary>
        /// 设置零气围困标识：显示原有受击风险，不负责判断攻击资格；我方仍可反击，敌方锁死才停攻。
        /// </summary>
        public void SetLocked(bool locked)
        {
            // HUD每次刷新都会再次传入同一状态；只在状态切换时归零，否则永远停在第一帧。
            if (_locked != locked)
            {
                _lockedFrameIndex = 0;
                _lockedFrameTimer = 0f;
            }
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
            ClearBossDeath();
            RestoreBossArt();
            SetEyeGarrison(false);
            PieceId = 0;
            _entry = null;
            _frames = null;
            _frameIndex = 0;
            _frameTimer = 0f;
            _hpRatio = 1f;
            _hpVisualValid = false;
            _hpVisualSprite = null;
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
            // 被围标识和身体分别换帧；燃烧/雷暴只改变当前帧的颜色，不能把活棋变成静止立绘。
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
            // 死亡会取消本计时器并清空帧列表；存活棋子即使正在燃烧，也必须继续播放序列帧。
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
            AdvanceAnimationIndex();

            if (bodyRenderer != null)
            {
                bodyRenderer.sprite = _frames[_frameIndex];

                if (_birthProgress > 0f || _stormProgress > 0f || _eyeGarrison > 0f || _bossCharge > 0f || _bossIce > 0f)
                {
                    // 一张Atlas里的不同帧通常位于不同矩形。先换图再同步UV/纹理，
                    // 保留当前效果进度，确保火舌始终贴在“此刻正在显示”的身体上。
                    SetVisualUv(bodyRenderer.sprite);
                    bodyRenderer.SetPropertyBlock(_visualProperties);
                }
            }
        }
    }
}

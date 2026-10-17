// ============================================================
// PieceView.cs
// 棋子视图可反复回池，绑定模型和启动动画分开；每次停用取消 owner 计时器，防止旧动画继续跑。
// ============================================================
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 一颗棋子的**表现**组件（挂在 GameRes/棋子/棋子.prefab 的根节点上）。
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
    [RevPoolPrefab("Pieces/", "棋子", Group = RevResGroup.Battle)]
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
        private Sprite bossSmallSprite;
        [SerializeField]
        private Sprite bossLargeSprite;
        [SerializeField]
        private SpriteRenderer crownRenderer;
        [SerializeField]
        private SpriteRenderer haloRenderer;
        private Sprite _bossSprite;
        [Header("动画节奏")]
        [Tooltip("动画推进的检查间隔（秒）。50ms 足够，别用每帧")]
        [SerializeField]
        private float tickInterval = 0.05f;
        private PieceArtConfig.Entry _entry;
        private Sprite[] _frames;
        private float _frameInterval = 0.2f;
        private int _frameIndex;
        private float _frameTimer;
        private bool _locked;
        private float _hpRatio = 1f;
        private Vector3 _hpFillScale = Vector3.one;
        private Vector3 _hpFillBasePosition = Vector3.zero;
        private Vector3 _moveStart;
        private Vector3 _moveTarget;
        private float _moveElapsed;
        private bool _hasPosition;
        private bool _moving;
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
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
        }

        public void OnPoolGet()
        {
            _hasPosition = false;
            _moving = false;
            // 出池后的第一次定位直接落到出生格，不能从上一颗棋子的旧位置飞过来。
        }

        public void OnPoolReturn()
        {
            Clear();
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
            PieceId = pieceId;
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
            _bossSprite = boss ? (size >= 3 ? bossLargeSprite : bossSmallSprite) : null;
            if (_bossSprite != null && bodyRenderer != null)
            {
                bodyRenderer.sprite = _bossSprite;
            }

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
            lockedRenderer.sprite = _entry.lockedFrames[0];
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
            _bossSprite = null;
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
            if (bodyRenderer != null && _bossSprite == null)
            {
                bodyRenderer.sprite = _frames[_frameIndex];
            }
        }
    }
}

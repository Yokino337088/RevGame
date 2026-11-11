using Revolution;
using RevGame.Pvp.Duel;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>每席独立的表现状态机。它只消费快照，不通过动画事件触发伤害或跳跃。</summary>
    [RequireComponent(typeof(Animation), typeof(Image))]
    public sealed class DuelActorAnimator : MonoBehaviour, RevIHeavyFSMOwner
    {
        // 表现姿态槽：待机、跑动、腾空、三招式与倒地。名称对应场景里的 Legacy Clip。
        private enum Pose
        {
            // 站立循环，没有移动和招式时使用。
            Idle,
            // 跑动循环，由当前绘制区间的位移方向驱动。
            Run,
            // 离地姿态，本机可以由沙盒预览。
            Jump,
            // 权威普攻片段。
            Attack1,
            // 权威技能片段。
            Attack2,
            // 权威大招片段。
            Attack3,
            // 已确认倒地，优先于所有动作。
            Down,
            // 权威技能二片段。
            Attack4,
            // 命中取消后的普攻二段。
            Attack5,
            // 命中取消后的普攻三段。
            Attack6,
            // 原生受击硬直期间的反应片段。
            Hit
        }
        // Unity 不能直接序列化交错数组，用一层对象保存每个姿态的帧序列。
        [System.Serializable]
        private sealed class SpriteSequence
        {
            // 编辑器按播放顺序写入，每个元素对应一张实际绘制的图片。
            public Sprite[] frames;
            // 与原生判伤年龄对应的接触关键帧下标。非攻击姿态忽略。
            public int contactIndex;
        }

        // 与 Pose 枚举一一对应，由决斗场景配方预先绑定。
        [SerializeField] private SpriteSequence[] spriteSequences;
        // 屏幕上的角色图片。由本组件按片段时钟显式换帧。
        private Image _image;
        // 旧姿态的最后一张图只在过渡期间保留。两个图层复用整局，不逐帧创建对象。
        private Image _outgoingImage;
        // 两层图片交叠时间，单位为表现秒数。
        private const float PoseFadeSeconds = .09f;
        // 本次过渡开始时的非缩放时间。
        private float _fadeStarted;
        // 当前图层的朝向，用于下一次过渡建立基线。
        private int _paintedFacing = 1;
        // 旧图层保留的朝向，换向时不能让旧图一起镜像。
        private int _outgoingFacing = 1;
        // 首次落屏后才允许采集旧图。
        private bool _hasPainted;
        // 本角色的 Legacy Animation 组件，只执行画面播放。
        private Animation _animation;
        // 本角色的重量级表现状态机，离场时释放。
        private RevHeavyFSM<Pose, DuelActorAnimator> _machine;
        // 当前片段和帧序列。只在切换姿态时查找，普通渲染帧直接复用。
        private Sprite[] _playingFrames;
        // 当前攻击片段的接触关键帧，由场景预先绑定。
        private int _contactIndex;
        // Legacy 资源中每秒的采样张数。
        private float _sampleRate;
        // Legacy 资源定义的片段长度，单位秒。
        private float _clipLength;
        // 当前普通姿态已播放的非缩放秒数，切换时归零。
        private float _poseSeconds;
        // 同一招式可连续出现。用逻辑起始帧识别每一次，而不是只看招式编号。
        private int _attackStart = -1;
        // 待机与跑动循环，其余片段播到最后一张时保持该姿势。
        private bool _loop;
        // 只读播放检查，供验收判断片段确实在运行。
        public bool IsPlaying => _animation != null && _animation.isPlaying;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 上一帧实际可见的 Sprite，完全透明时清空。
        private Sprite _observedSprite;
        // 上一帧的姿态，切姿态时只建立新基线。
        private Pose _observedPose;
        // 攻击以逻辑起始帧区分实例。上一次攻击的结果保留到下一次攻击开始，方便测试异步读取。
        private int _lastObservedAttackStart = -1;
        // 当前招式同一姿态内真正换了几次图。
        private int _attackSpriteChanges;
        // 当前招式有多少帧同时看得见新旧两层。
        private int _attackFadeSamples;
        // 每个姿态占一位。同一个姿态至少绘制过两张不同的图片才设置对应位。
        private int _animatedPoses;
        // 至少有一种姿态真正落屏换过图。
        public bool HasSpriteMotion => _animatedPoses != 0;
        // 待机片段是否实际换图。
        public bool IdleSpriteMotion => (_animatedPoses & (1 << (int)Pose.Idle)) != 0;
        // 跳跃片段是否实际换图。
        public bool JumpSpriteMotion => (_animatedPoses & (1 << (int)Pose.Jump)) != 0;
        // 跑动片段是否实际换图。
        public bool RunSpriteMotion => (_animatedPoses & (1 << (int)Pose.Run)) != 0;
        // 任一权威招式片段是否实际换图。
        public bool AttackSpriteMotion => (_animatedPoses & ((7 << (int)Pose.Attack1) | (7 << (int)Pose.Attack4))) != 0;
        // 最近观察到的招式起始逻辑帧。
        internal int LastObservedAttackStart => _lastObservedAttackStart;
        // 验收读取该招式的实际换帧次数。
        internal int AttackSpriteChanges => _attackSpriteChanges;
        // 验收读取该招式的新旧图叠加帧数。
        internal int AttackFadeSamples => _attackFadeSamples;
        // 旧图层启用意味着本次过渡还未结束。
        internal bool IsPoseFadingForCheck => _outgoingImage != null && _outgoingImage.enabled;

        // 比较实际落屏图片，排除切换姿态时仅替换第一帧导致的误报。
        internal void ObserveSprite(Sprite sprite)
        {
            Pose pose = _machine.CurrentStateType;
            bool newAttack = _attackStart >= 0 && _attackStart != _lastObservedAttackStart;
            if (newAttack)
            {
                _lastObservedAttackStart = _attackStart;
                _attackSpriteChanges = 0;
                _attackFadeSamples = 0;
            }
            float alpha = _image.canvasRenderer.GetAlpha();
            if (_attackStart >= 0 && IsPoseFadingForCheck && alpha > 0 && alpha < 1 &&
                _outgoingImage.canvasRenderer.GetAlpha() > 0)
            {
                _attackFadeSamples++;
            }
            if (alpha <= 0)
            {
                // 透明的新图还没进入画面，不能作为真实换帧的观察基线。
                _observedSprite = null;
                _observedPose = pose;
                return;
            }
            if (!newAttack && sprite != null && _observedSprite != null && pose == _observedPose && sprite != _observedSprite)
            {
                _animatedPoses |= 1 << (int)pose;
                if (_attackStart >= 0)
                {
                    _attackSpriteChanges++;
                }
            }
            _observedSprite = sprite;
            _observedPose = pose;
        }
#endif

        // 某个姿态的播放状态，入场时选择一个固定片段。
        private sealed class ClipState : RevHeavyFSMState<Pose, DuelActorAnimator>
        {
            // Unity Animation 上登记的片段名。
            private readonly string _clip;
            // 框架注册与切换该状态所用的唯一枚举键。
            public override Pose StateType { get; }

            // 绑定状态机和姿态，将枚举转换为片段名。
            public ClipState(RevHeavyFSM<Pose, DuelActorAnimator> machine, Pose pose) : base(machine)
            {
                StateType = pose;
                _clip = pose.ToString();
            }

            // 只在进入姿态时播放，避免每次刷新重置动画进度。
            public override void OnEnter()
            {
                // 同一状态的每个快照只更新位置。真正切换状态才从头播片段。
                Owner.PlayPose(StateType, _clip);
            }
        }

        // 注册场景配方里的姿态片段，从待机开始。
        private void Awake()
        {
            _animation = GetComponent<Animation>();
            _image = GetComponent<Image>();
            CreateTransitionImage();
            _machine = new RevHeavyFSM<Pose, DuelActorAnimator>(this);
            foreach (Pose pose in System.Enum.GetValues(typeof(Pose)))
            {
                _machine.AddState(pose, new ClipState(_machine, pose));
            }
            _machine.ChangeState(Pose.Idle, "场景就绪");
        }

        // 按生命、招式、离地和移动确定表现姿态。没有状态变化时继续原片段。
        public void Paint(int health, int altitude, int action, int stun, bool moving, int frame, int age, float frameFraction)
        {
            Pose next = health == 0 ? Pose.Down : stun > 0 ? Pose.Hit : action switch
            {
                1 => Pose.Attack1,
                2 => Pose.Attack2,
                3 => Pose.Attack3,
                4 => Pose.Attack4,
                5 => Pose.Attack5,
                6 => Pose.Attack6,
                _ => altitude > 0 ? Pose.Jump : moving ? Pose.Run : Pose.Idle
            };
            int attackStart = action != 0 && health > 0 ? frame - age : -1;
            if (_machine.CurrentStateType != next)
            {
                _machine.ChangeState(next, "收到决斗快照");
            }
            else if (attackStart >= 0 && attackStart != _attackStart)
            {
                // 连续普攻可能在同一逻辑帧结束旧招并开始新招，状态没有机会经过 Idle。
                PlayPose(next, next.ToString());
            }
            _attackStart = attackStart;
            _machine.UpdateState(Time.unscaledDeltaTime);
            PaintSpriteFrame(attackStart >= 0 ? action : 0, age, frameFraction);
            PaintPoseFade();
            _poseSeconds += Time.unscaledDeltaTime;
        }

        // 重启 Legacy 片段并读取资源采样率，UGUI 图片仍由表现层显式换帧。
        private void PlayPose(Pose pose, string clip)
        {
            BeginPoseFade();
            _animation.Stop(clip);
            _animation.Play(clip);
            AnimationClip asset = _animation.GetClip(clip);
            _sampleRate = asset == null ? 0 : asset.frameRate;
            _clipLength = asset == null ? 0 : asset.length;
            _poseSeconds = 0;
            int index = (int)pose;
            SpriteSequence sequence = spriteSequences != null && index < spriteSequences.Length ? spriteSequences[index] : null;
            _playingFrames = sequence?.frames;
            _contactIndex = sequence?.contactIndex ?? 0;
            _loop = pose == Pose.Idle || pose == Pose.Run;
        }

        // 旧图层是当前角色的表现子节点，随场景一起退出；关闭射线检测，避免覆盖 HUD 的触屏输入。
        private void CreateTransitionImage()
        {
            var layer = new GameObject("PreviousPose", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)layer.transform;
            rect.SetParent(_image.rectTransform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            _outgoingImage = layer.GetComponent<Image>();
            _outgoingImage.preserveAspect = _image.preserveAspect;
            _outgoingImage.useSpriteMesh = _image.useSpriteMesh;
            _outgoingImage.raycastTarget = false;
            _outgoingImage.enabled = false;
        }

        // 只在权威姿态或动作实例切换时采样旧画面。过渡中再次切换，沿用当时更清楚的图层。
        private void BeginPoseFade()
        {
            if (!_hasPainted || _image.sprite == null)
            {
                return;
            }
            if (!_outgoingImage.enabled || _outgoingImage.canvasRenderer.GetAlpha() <= _image.canvasRenderer.GetAlpha())
            {
                _outgoingImage.sprite = _image.sprite;
                _outgoingImage.color = _image.color;
                _outgoingFacing = _paintedFacing;
            }
            _outgoingImage.enabled = true;
            _fadeStarted = Time.unscaledTime;
        }

        // 用渲染时钟交叠两个图层。逻辑帧照常驱动动作年龄，受击颜色仍由 View 控制。
        private void PaintPoseFade()
        {
            _paintedFacing = _image.rectTransform.localScale.x < 0 ? -1 : 1;
            _hasPainted = true;
            if (!_outgoingImage.enabled)
            {
                return;
            }
            float progress = Mathf.Clamp01((Time.unscaledTime - _fadeStarted) / PoseFadeSeconds);
            if (progress >= 1)
            {
                FinishPoseFade();
                return;
            }
            float blend = progress * progress * (3 - 2 * progress);
            _outgoingImage.rectTransform.localScale = new Vector3(_outgoingFacing * _paintedFacing, 1, 1);
            _image.canvasRenderer.SetAlpha(blend);
            _outgoingImage.canvasRenderer.SetAlpha(1 - blend);
        }

        // 离场、对象停用或过渡结束时清掉临时图层，重新启用不出现上一局的残影。
        private void FinishPoseFade()
        {
            _image.canvasRenderer.SetAlpha(1);
            _outgoingImage.enabled = false;
            _outgoingImage.sprite = null;
        }

        private void OnDisable()
        {
            if (_outgoingImage != null)
            {
                FinishPoseFade();
            }
            _hasPainted = false;
        }

        // 攻击分为接触前和接触后两段。权威命中帧进入接触姿势，渲染按逻辑年龄插值。
        private void PaintSpriteFrame(int action, int age, float frameFraction)
        {
            if (_playingFrames == null || _playingFrames.Length == 0)
            {
                return;
            }

            float seconds = _loop && _clipLength > 0 ? Mathf.Repeat(_poseSeconds, _clipLength) : _poseSeconds;
            float sample = seconds * _sampleRate;
            if (action > 0)
            {
                int hit = DuelConfig.ActionHitFrame(action);
                float elapsed = age + frameFraction;
                sample = elapsed < hit
                    ? elapsed * _contactIndex / hit
                    : _contactIndex + (elapsed - hit) * (_playingFrames.Length - _contactIndex) /
                        (DuelConfig.ActionDuration(action) + 1 - hit);
            }
            int index = Mathf.Clamp(Mathf.FloorToInt(sample), 0, _playingFrames.Length - 1);
            Sprite frame = _playingFrames[index];
            if (_image.sprite != frame)
            {
                _image.sprite = frame;
            }
        }

        // 销毁角色时清理状态机及其状态引用。
        private void OnDestroy()
        {
            _machine?.Dispose();
        }
    }
}

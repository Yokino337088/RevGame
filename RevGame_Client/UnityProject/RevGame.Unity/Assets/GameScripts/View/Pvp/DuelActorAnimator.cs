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
        private enum Pose { Idle, Run, Jump, Attack1, Attack2, Attack3, Down }
        // Unity 不能直接序列化交错数组，用一层对象保存每个姿态的帧序列。
        [System.Serializable]
        private sealed class SpriteSequence
        {
            // 编辑器按播放顺序写入，每个元素对应一张实际绘制的图片。
            public Sprite[] frames;
        }

        // 与 Pose 枚举一一对应，由决斗场景配方预先绑定。
        [SerializeField] private SpriteSequence[] spriteSequences;
        // 屏幕上的角色图片。由本组件按片段时钟显式换帧。
        private Image _image;
        // 本角色的 Legacy Animation 组件，只执行画面播放。
        private Animation _animation;
        // 本角色的重量级表现状态机，离场时释放。
        private RevHeavyFSM<Pose, DuelActorAnimator> _machine;
        // 当前片段和帧序列。只在切换姿态时查找，普通渲染帧直接复用。
        private Sprite[] _playingFrames;
        // 片段采样率和长度来自既有 Legacy 资源。普通姿态用独立的表现秒数。
        private float _sampleRate, _clipLength, _poseSeconds;
        // 同一招式可连续出现。用逻辑起始帧识别每一次，而不是只看招式编号。
        private int _attackStart = -1;
        // 待机与跑动循环，其余片段播到最后一张时保持该姿势。
        private bool _loop;
        // 只读播放检查，供验收判断片段确实在运行。
        public bool IsPlaying => _animation != null && _animation.isPlaying;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 上一帧真正交给 Image 绘制的 Sprite 和姿态。状态切换时只建立新基线。
        private Sprite _observedSprite;
        private Pose _observedPose;
        // 每个姿态占一位。同一个姿态至少绘制过两张不同的图片才设置对应位。
        private int _animatedPoses;
        public bool HasSpriteMotion => _animatedPoses != 0;
        public bool IdleSpriteMotion => (_animatedPoses & (1 << (int)Pose.Idle)) != 0;
        public bool JumpSpriteMotion => (_animatedPoses & (1 << (int)Pose.Jump)) != 0;
        public bool RunSpriteMotion => (_animatedPoses & (1 << (int)Pose.Run)) != 0;
        public bool AttackSpriteMotion => (_animatedPoses & (7 << (int)Pose.Attack1)) != 0;

        // 比较实际落屏图片，排除切换姿态时仅替换第一帧导致的误报。
        internal void ObserveSprite(Sprite sprite)
        {
            Pose pose = _machine.CurrentStateType;
            if (sprite != null && _observedSprite != null && pose == _observedPose && sprite != _observedSprite)
            {
                _animatedPoses |= 1 << (int)pose;
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

        // 注册七个姿态及其片段，从待机开始。
        private void Awake()
        {
            _animation = GetComponent<Animation>();
            _image = GetComponent<Image>();
            _machine = new RevHeavyFSM<Pose, DuelActorAnimator>(this);
            foreach (Pose pose in System.Enum.GetValues(typeof(Pose)))
            {
                _machine.AddState(pose, new ClipState(_machine, pose));
            }
            _machine.ChangeState(Pose.Idle, "场景就绪");
        }

        // 按生命、招式、离地和移动确定表现姿态。没有状态变化时继续原片段。
        public void Paint(int health, int altitude, int action, bool moving, int frame, int age, float frameFraction)
        {
            Pose next = health == 0 ? Pose.Down : action == 1 ? Pose.Attack1 :
                action == 2 ? Pose.Attack2 : action == 3 ? Pose.Attack3 :
                altitude > 0 ? Pose.Jump : moving ? Pose.Run : Pose.Idle;
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
            PaintSpriteFrame(attackStart >= 0 ? DuelConfig.ActionDuration(action) : 0, age, frameFraction);
            _poseSeconds += Time.unscaledDeltaTime;
        }

        // 重启 Legacy 片段并读取资源采样率，UGUI 图片仍由表现层显式换帧。
        private void PlayPose(Pose pose, string clip)
        {
            _animation.Stop(clip);
            _animation.Play(clip);
            AnimationClip asset = _animation.GetClip(clip);
            _sampleRate = asset == null ? 0 : asset.frameRate;
            _clipLength = asset == null ? 0 : asset.length;
            _poseSeconds = 0;
            int index = (int)pose;
            _playingFrames = spriteSequences != null && index < spriteSequences.Length ? spriteSequences[index]?.frames : null;
            _loop = pose == Pose.Idle || pose == Pose.Run;
        }

        // 招式图按逻辑年龄铺满整段动作。渲染只在当前逻辑帧内插值，断流时不会提前播完。
        private void PaintSpriteFrame(int duration, int age, float frameFraction)
        {
            if (_playingFrames == null || _playingFrames.Length == 0)
            {
                return;
            }

            float seconds = _loop && _clipLength > 0 ? Mathf.Repeat(_poseSeconds, _clipLength) : _poseSeconds;
            float sample = duration > 0
                ? (age + frameFraction) * _playingFrames.Length / (duration + 1)
                : seconds * _sampleRate;
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

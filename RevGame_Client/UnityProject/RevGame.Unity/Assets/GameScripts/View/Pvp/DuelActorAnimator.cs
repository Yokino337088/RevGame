using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>每席独立的表现状态机。它只消费快照，不通过动画事件触发伤害或跳跃。</summary>
    [RequireComponent(typeof(Animation))]
    public sealed class DuelActorAnimator : MonoBehaviour, RevIHeavyFSMOwner
    {
        // 表现姿态槽：待机、跑动、腾空、三招式与倒地。名称对应场景里的 Legacy Clip。
        private enum Pose { Idle, Run, Jump, Attack1, Attack2, Attack3, Down }
        // 本角色的 Legacy Animation 组件，只执行画面播放。
        private Animation _animation;
        // 本角色的重量级表现状态机，离场时释放。
        private RevHeavyFSM<Pose, DuelActorAnimator> _machine;
        // 只读播放检查，供验收判断片段确实在运行。
        public bool IsPlaying => _animation != null && _animation.isPlaying;

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
                Owner._animation.Play(_clip);
            }
        }

        // 注册七个姿态及其片段，从待机开始。
        private void Awake()
        {
            _animation = GetComponent<Animation>();
            _machine = new RevHeavyFSM<Pose, DuelActorAnimator>(this);
            foreach (Pose pose in System.Enum.GetValues(typeof(Pose)))
            {
                _machine.AddState(pose, new ClipState(_machine, pose));
            }
            _machine.ChangeState(Pose.Idle, "场景就绪");
        }

        // 按生命、招式、离地和移动确定表现姿态。没有状态变化时继续原片段。
        public void Paint(int health, int altitude, int action, bool moving)
        {
            Pose next = health == 0 ? Pose.Down : action == 1 ? Pose.Attack1 :
                action == 2 ? Pose.Attack2 : action == 3 ? Pose.Attack3 :
                altitude > 0 ? Pose.Jump : moving ? Pose.Run : Pose.Idle;
            if (_machine.CurrentStateType != next)
            {
                _machine.ChangeState(next, "收到决斗快照");
            }
            _machine.UpdateState(Time.unscaledDeltaTime);
        }

        // 销毁角色时清理状态机及其状态引用。
        private void OnDestroy()
        {
            _machine?.Dispose();
        }
    }
}

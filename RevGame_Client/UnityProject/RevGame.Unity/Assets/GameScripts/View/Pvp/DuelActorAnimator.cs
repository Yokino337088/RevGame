using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>每席独立的表现状态机。它只消费快照，不通过动画事件触发伤害或跳跃。</summary>
    [RequireComponent(typeof(Animation))]
    public sealed class DuelActorAnimator : MonoBehaviour, RevIHeavyFSMOwner
    {
        private enum Pose { Idle, Run, Jump, Attack1, Attack2, Attack3, Down }
        private Animation _animation;
        private RevHeavyFSM<Pose, DuelActorAnimator> _machine;
        public bool IsPlaying => _animation != null && _animation.isPlaying;

        private sealed class ClipState : RevHeavyFSMState<Pose, DuelActorAnimator>
        {
            private readonly string _clip;
            public override Pose StateType { get; }

            public ClipState(RevHeavyFSM<Pose, DuelActorAnimator> machine, Pose pose) : base(machine)
            {
                StateType = pose;
                _clip = pose.ToString();
            }

            public override void OnEnter()
            {
                // 同一状态的每个快照只更新位置。真正切换状态才从头播片段。
                Owner._animation.Play(_clip);
            }
        }

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

        private void OnDestroy()
        {
            _machine?.Dispose();
        }
    }
}

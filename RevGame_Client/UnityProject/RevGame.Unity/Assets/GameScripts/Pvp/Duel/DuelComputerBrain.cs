using System;

namespace RevGame.Pvp.Duel
{
    /// <summary>本地白方的连招决策。只读已封帧快照，按真实起手推进连段，最后仍由双方完整输入帧判定。</summary>
    internal sealed class DuelComputerBrain
    {
        private enum Step
        {
            Approach,
            FollowUp,
            Skill,
            Finish
        }

        // 相同起手帧可能被多次读取。每个已执行招式只推进一次连招。
        private int _lastStart = -1;
        private Step _step;
        // 两轮交替选不同技能，能量和命中距离不合适时允许降级。
        private int _preferredSkill = 2;

        /// <summary>距离单位与原生判定相同。先走近起手，普攻接段后接技能，有命中且能量足够才收大招。</summary>
        internal int Choose(DuelSnapshot state, bool yaya)
        {
            ObserveAction(state);
            int offset = state.FirstX - state.SecondX;
            int gap = Math.Abs(offset);
            bool inReach = gap <= DuelConfig.ActionRange(yaya, 1);
            if (state.Result != 0 || state.SecondStun > 0)
            {
                return 0;
            }
            // 取消窗口由原生掩码决定。技能被打断或落空时回到普攻，避免远处反复丢大招。
            if (_step == Step.Finish && state.SecondAction is 2 or 4 && state.SecondCombo > 0 &&
                CanCast(state, yaya, 3, gap, DuelConfig.UltimateCost))
            {
                return 3 << 2;
            }
            if (_step == Step.Skill || (_step == Step.FollowUp && !inReach))
            {
                int first = _preferredSkill;
                int second = first == 2 ? 4 : 2;
                if (CanCast(state, yaya, first, gap, first == 2 ? DuelConfig.SkillCost : DuelConfig.SkillTwoCost))
                {
                    return first << 2;
                }
                if (CanCast(state, yaya, second, gap, second == 2 ? DuelConfig.SkillCost : DuelConfig.SkillTwoCost))
                {
                    return second << 2;
                }
            }
            // 技能或大招的收势结束才重新近身。能量不足时继续打普攻来恢复。
            return inReach ? (state.CanTakeAction(false, 1) ? 1 << 2 : 0) : offset < 0 ? 1 : 2;
        }

        private static bool CanCast(DuelSnapshot state, bool yaya, int action, int distance, int cost)
        {
            return distance <= DuelConfig.ActionRange(yaya, action) && state.SecondEnergy >= cost &&
                state.CanTakeAction(false, action);
        }

        // 读取原生真正接受的招式编号。二段、三段都由普攻输入派生。
        private void ObserveAction(DuelSnapshot state)
        {
            int start = state.Frame - state.SecondAge;
            if (state.SecondAction == 0 || start <= _lastStart)
            {
                return;
            }
            _lastStart = start;
            _step = state.SecondAction switch
            {
                1 => Step.FollowUp,
                5 or 6 => Step.Skill,
                2 or 4 => Step.Finish,
                _ => Step.Approach
            };
            if (state.SecondAction is 2 or 4)
            {
                _preferredSkill = state.SecondAction == 2 ? 4 : 2;
            }
        }

        /// <summary>重新建局时清除上一局观察到的帧号和出招顺序。</summary>
        internal void Reset()
        {
            _lastStart = -1;
            _step = Step.Approach;
            _preferredSkill = 2;
        }
    }
}

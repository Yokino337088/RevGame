using System;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        /// <summary>只在服务Poll推进AI。延迟仅用于表现思考，不是策划难度；复用独立候选缓冲，不修改实盘试算。</summary>
        private void PollComputer()
        {
            if (UseCloudOpponent || !IsComputerGame || Core == null || !CanAct || _clock.Elapsed.TotalSeconds < _computerAt)
            {
                return;
            }
            if (Core.Phase == GoPhase.Scoring)
            {
                if (!Core.HasConfirmed(GoSide.White))
                {
                    ApplyComputer(GoAction.ConfirmScore, -1, -1);
                }
                return;
            }
            if (Core.Phase != GoPhase.Playing || Core.Turn != GoSide.White)
            {
                _computerAt = _clock.Elapsed.TotalSeconds + .45;
                return;
            }
            bool playerPassed = _moves.Count > 0 && _moves[_moves.Count - 1].kind == (int)GoAction.Pass;
            int count = playerPassed ? 0 : Core.GetHints(GoSide.White, _computerHints);
            ApplyComputer(count > 0 ? GoAction.Play : GoAction.Pass, count > 0 ? _computerHints[0].X : -1, count > 0 ? _computerHints[0].Y : -1);
        }

        private void ApplyComputer(GoAction action, int x, int y)
        {
            // 与玩家使用同一个提交入口，时钟、规则、棋谱只在该入口结算一次。
            SubmitActor(action, GoSide.White, x, y);
            _computerAt = _clock.Elapsed.TotalSeconds + .45;
        }
    }
}

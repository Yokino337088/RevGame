namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        // 这是棋面表现的时长。时钟仍由 SubmitActor 核验，回放也只记录真正落下的一手。
        private const double PreviewSeconds = .8;
        internal const float LandingSeconds = .24f;
        private GoTacticalSearch _tacticalSearch;
        private readonly GoBoardAnalysis _computerAnalysis = new GoBoardAnalysis();
        private GoCore _previewPosition, _landingPosition;
        private int _previewPoint = -1, _landingPoint = -1;
        private double _previewDue, _landedAt;
        public int ComputerPreviewPoint => ReferenceEquals(_previewPosition, Core) ? _previewPoint : -1;
        public int ComputerLandingPoint => ReferenceEquals(_landingPosition, Core) ? _landingPoint : -1;
        public float ComputerLandingAge => (float)(_clock.Elapsed.TotalSeconds - _landedAt);

        /// <summary>电脑只从当前快照选点。加了盘面高亮也仍走和玩家相同的提交、计时和提子入口。</summary>
        private void PollComputer()
        {
            if (UseCloudOpponent || !IsComputerGame || Core == null || !CanAct || ComputerPreviewPoint >= 0 ||
                _clock.Elapsed.TotalSeconds < _computerAt)
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
                _computerAt = _clock.Elapsed.TotalSeconds + .35;
                return;
            }
            QueueBestComputerMove();
        }

        private int RankComputerMoves(GoHint[] destination, out GoHint bait)
        {
            _tacticalSearch ??= new GoTacticalSearch(Core);
            return _tacticalSearch.Rank(Core, destination, out bait);
        }

        private bool ComputerShouldPass()
        {
            if (Core.ConsecutivePasses == 0)
            {
                return false;
            }
            Core.Analyze(_computerAnalysis);
            return _computerAnalysis.Score.Winner == GoSide.White;
        }

        private void QueueBestComputerMove()
        {
            int count = ComputerShouldPass() ? 0 : RankComputerMoves(_computerHints, out _);
            if (count == 0)
            {
                ApplyComputer(GoAction.Pass, -1, -1);
                return;
            }
            QueueComputerPlacement(_computerHints[0].X, _computerHints[0].Y);
        }

        /// <summary>标记只在发起时的棋面有效。换局、认输或超时后自然失效，表现无法写进下一盘棋。</summary>
        private void QueueComputerPlacement(int x, int y)
        {
            _previewPosition = Core;
            _previewPoint = y * GoCore.BoardSize + x;
            _previewDue = _clock.Elapsed.TotalSeconds + PreviewSeconds;
            Revision++;
        }

        private void PollComputerPlacement()
        {
            int point = ComputerPreviewPoint;
            if (point < 0 || !CanAct || _clock.Elapsed.TotalSeconds < _previewDue)
            {
                return;
            }
            _previewPosition = null;
            _previewPoint = -1;
            ApplyComputer(GoAction.Play, point % GoCore.BoardSize, point / GoCore.BoardSize);
        }

        private void ApplyComputer(GoAction action, int x, int y)
        {
            int placementCount = Core.PlacementCount;
            if (SubmitActor(action, GoSide.White, x, y))
            {
                if (Core.PlacementCount != placementCount)
                {
                    _landingPosition = Core;
                    _landingPoint = y * GoCore.BoardSize + x;
                    _landedAt = _clock.Elapsed.TotalSeconds;
                }
                if (Core.Phase == GoPhase.Playing)
                {
                    SpeakAgent(action == GoAction.Play ? "OpponentPlay" : "OpponentPass", x, y);
                }
            }
            _computerAt = _clock.Elapsed.TotalSeconds + .35;
        }

        private void ResetComputerPresentation()
        {
            _previewPosition = _landingPosition = null;
            _previewPoint = _landingPoint = -1;
            _tacticalSearch = null;
            _hintRequests = 0;
        }
    }
}

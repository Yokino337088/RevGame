#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
using Stopwatch = System.Diagnostics.Stopwatch;
#endif

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        // 这是棋面表现的时长。时钟仍由 SubmitActor 核验，回放也只记录真正落下的一手。
        private const double PreviewSeconds = .8;
        internal const float LandingSeconds = .24f;
        private GoTacticalSearch _tacticalSearch;
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
        /// <summary>本局安卓试验档位。仅开发态隔离入口创建，换局释放。</summary>
        internal PvpAndroidTacticalTrial AndroidTacticalTrial { get; private set; }
        /// <summary>最近一次实战搜索墙钟时间，毫秒。零表示本局试验尚未搜索。</summary>
        internal double AndroidTacticalSearchMs { get; private set; }
#endif
        private readonly GoBoardAnalysis _computerAnalysis = new GoBoardAnalysis();
        private GoCore _previewPosition, _landingPosition;
        private int _previewPoint = -1, _landingPoint = -1;
        private double _previewDue, _landedAt;
        public int ComputerPreviewPoint => ReferenceEquals(_previewPosition, Core) ? _previewPoint : -1;
        /// <summary>本局最近一次本地试算使用的组数，零表示本局尚未调用或已退出。</summary>
        internal int TacticalSearchPartitions => _tacticalSearch?.LastPartitionCount ?? 0;
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

        /// <summary>本局共用搜索工作区。等待全部候选分数后才交给预告或提示，实盘提交仍走原入口。</summary>
        private int RankComputerMoves(GoHint[] destination, out GoHint bait)
        {
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
            if (AndroidTacticalTrial is { } trial)
            {
                int limit = trial.Limit(trial.RequestedGroups);
                _tacticalSearch ??= new GoTacticalSearch(Core, maxPartitions: 4);
                long start = Stopwatch.GetTimestamp();
                int count = _tacticalSearch.Rank(Core, destination, out bait, partitionLimit: limit);
                AndroidTacticalSearchMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                return count;
            }
#endif
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

#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
        /// <summary>隔离调试入口为本局指定组数。零归还试验资源，后续搜索恢复平台默认。</summary>
        internal void SetAndroidTacticalGroups(int groups)
        {
            if (groups == 0)
            {
                ClearAndroidTacticalTrial();
                return;
            }
            if (AndroidTacticalTrial == null)
            {
                _tacticalSearch = null;
                AndroidTacticalTrial = new PvpAndroidTacticalTrial();
            }
            AndroidTacticalTrial.RequestedGroups = groups;
            AndroidTacticalTrial.Refresh(beforeSearch: true);
        }

        /// <summary>退场、重开或撤销试验时释放工作区、热状态和上次计时。</summary>
        private void ClearAndroidTacticalTrial()
        {
            if (AndroidTacticalTrial != null)
            {
                AndroidTacticalTrial.Dispose();
                AndroidTacticalTrial = null;
                _tacticalSearch = null;
            }
            AndroidTacticalSearchMs = 0;
        }
#endif

        /// <summary>释放上一局的预告、落子表现和战术搜索工作区及试验状态。</summary>
        private void ResetComputerPresentation()
        {
            _previewPosition = _landingPosition = null;
            _previewPoint = _landingPoint = -1;
            _tacticalSearch = null;
#if UNITY_EDITOR || (UNITY_ANDROID && UNITY_5_3_OR_NEWER && (DEVELOPMENT_BUILD || REV_AUTOMATION))
            ClearAndroidTacticalTrial();
#endif
        }
    }
}

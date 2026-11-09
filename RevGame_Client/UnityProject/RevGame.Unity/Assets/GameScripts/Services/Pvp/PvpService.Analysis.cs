using System;
using RevGame.Pvp.Agent;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        // 独立于落子和手动建议。低频、单请求，避免快棋或云端异常反复消耗额度。
        private const double AnalysisQuietSeconds = .45;
        private const double AnalysisMinInterval = 5;
        private const double AnalysisFailureInterval = 30;
        private const double AnalysisDeadline = 12;
        private IGoAgentRequest _analysisRequest;
        private GoAgentPosition _analysisPosition;
        private GoCore _analysisAttempt, _analysisBoard;
        private double _analysisStarted, _analysisNext;
        internal Func<PvpAgentSettings, GoAgentPosition, IGoAgentRequest> AnalysisRequestFactory = DeepSeekGoRequest.StartAnalysis;
        internal GoPositionAnalysis PositionAnalysis { get; private set; }
        internal bool AnalysisFresh => PositionAnalysis != null && ReferenceEquals(Core, _analysisBoard) && CanShowAnalysis && !_awaiting;
        /// <summary>只公开当前棋谱的落点索引。停一手、断线、终局和过期回复均没有格子可画。</summary>
        internal int AnalysisPoint
        {
            get
            {
                GoAgentAction suggestion = AnalysisFresh ? PositionAnalysis.Suggestion : null;
                return suggestion?.Kind == GoAction.Play ? suggestion.Y * GoCore.BoardSize + suggestion.X : -1;
            }
        }
        internal bool AnalysisPending => _analysisRequest != null;
        internal string AnalysisFailure { get; private set; } = "";
        internal bool AnalysisMode => Mode != PvpConnectionMode.Local || IsComputerGame;
        private bool CanShowAnalysis => AnalysisMode && Core?.Phase == GoPhase.Playing && DuelId.Length == 0 &&
            !_leaving && ReplayStep < 0 && (Mode == PvpConnectionMode.Local || Connected && !_needsServerSync && _syncMoves == null);

        /// <summary>仅分析轮到自己的已同步棋面；请求结果始终只读，永远不进 SubmitActor。</summary>
        private void PollAnalysis()
        {
            if (!CanShowAnalysis || !AgentSettings.Ready || AgentControlsHumanSeat || AnalysisRequestFactory == null)
            {
                if (_analysisRequest != null || PositionAnalysis != null)
                {
                    ClearAnalysis();
                }
                return;
            }

            if (_analysisRequest != null && (!ReferenceEquals(_analysisAttempt, Core) || _awaiting))
            {
                ReleaseAnalysisRequest();
            }
            double now = _clock.Elapsed.TotalSeconds;
            try
            {
                if (_analysisRequest == null)
                {
                    if (Core.Turn != MySide || _awaiting || HintPending || ReferenceEquals(_analysisAttempt, Core) ||
                        now < _analysisNext || now - _turnStarted < AnalysisQuietSeconds)
                    {
                        return;
                    }

                    _analysisAttempt = Core;
                    _analysisStarted = now;
                    _analysisNext = now + AnalysisMinInterval;
                    _analysisPosition = GoAgentPosition.Create(Core, MySide, analysis: true);
                    _analysisRequest = AnalysisRequestFactory(AgentSettings, _analysisPosition);
                }
                if (!_analysisRequest.IsDone)
                {
                    if (now - _analysisStarted >= AnalysisDeadline)
                    {
                        throw new GoAgentRequestException("局势分析超时");
                    }
                    return;
                }
                if (!_analysisPosition.Matches(Core) || !GoPositionAnalysis.TryRead(_analysisRequest.ReadContent(), _analysisPosition, out GoPositionAnalysis result))
                {
                    throw new GoAgentRequestException("局势分析响应无效");
                }

                _analysisBoard = Core;
                PositionAnalysis = result;
                AnalysisFailure = "";
                ReleaseAnalysisRequest();
            }
            catch (Exception error)
            {
                // 只记录本地分类，不回传模型响应、请求正文或 Key。
                AnalysisFailure = error is GoAgentRequestException ? error.Message : "局势分析请求失败";
                PositionAnalysis = null;
                _analysisBoard = null;
                _analysisNext = now + AnalysisFailureInterval;
                ReleaseAnalysisRequest();
            }
        }

        private void ReleaseAnalysisRequest()
        {
            _analysisRequest?.Dispose();
            _analysisRequest = null;
            _analysisPosition = null;
        }

        private void ClearAnalysis()
        {
            ReleaseAnalysisRequest();
            _analysisBoard = _analysisAttempt = null;
            PositionAnalysis = null;
            AnalysisFailure = "";
            _analysisNext = 0;
        }
    }
}

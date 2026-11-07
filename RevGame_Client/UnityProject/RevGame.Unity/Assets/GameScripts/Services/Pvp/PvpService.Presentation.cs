using System;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        private readonly GoHint[] _suggestions = new GoHint[3];
        private int _suggestionCount;
        private GoCore _replay;
        public int FeedbackVersion
        {
            get;
            private set;
        }
        public string Feedback
        {
            get;
            private set;
        } = "";
        public int SpeechVersion
        {
            get;
            private set;
        }
        public string SpeechCue
        {
            get;
            private set;
        } = "";
        public string SpeechPoint
        {
            get;
            private set;
        } = "";

        /// <summary>只发布确实发生的对局事件，鼠标移过棋盘不会不断重播弹幕。</summary>
        private void NotifyBoard(string text)
        {
            Feedback = text;
            FeedbackVersion++;
        }

        private void SpeakAgent(string cue, int x = -1, int y = -1)
        {
            SpeechCue = cue;
            SpeechPoint = x < 0 ? "" : FormatPoint(x, y);
            SpeechVersion++;
        }

        /// <summary>落点坐标给旁白用。列从左到右 A–H、J，行号从下往上 1–9。</summary>
        internal static string FormatPoint(int x, int y)
        {
            return "ABCDEFGHJ"[x].ToString() + (y + 1);
        }
        public int ReplayStep
        {
            get;
            private set;
        } = -1;
        public GoCore DisplayCore { get { return _replay ?? Core; } }
        public GoHint[] Suggestions { get { return _suggestions; } }
        public int SuggestionCount { get { return _suggestionCount; } }
        public bool CanAct { get { return Core != null && !_awaiting && !_leaving && ReplayStep < 0 && (Mode == PvpConnectionMode.Local || Connected) && (Mode != PvpConnectionMode.Server || (_server != null && !_server.Busy)); } }

        /// <summary>对局普通状态留在HUD，横幅只接收菜单提示和需要玩家处理的异常。</summary>
        public bool ShouldShowStatusNotice
        {
            get
            {
                return Status != "服务器已连接" && (Core == null || Status.StartsWith("连接不可用") || Status.StartsWith("操作未被接受"));
            }
        }

        /// <summary>建议是独立的只读请求。玩家落子、离开或改变授权后取消，绝不代替玩家提交。</summary>
        private Agent.IGoAgentRequest _hintRequest;
        private GoCore _hintCore;
        private Agent.GoAgentPosition _hintPosition;
        private double _hintDeadline;
        public bool HintPending => _hintRequest != null;
        public string HintSource
        {
            get;
            private set;
        } = "";
        public int HintRevision
        {
            get;
            private set;
        }

        public bool CanRequestHint => CanAct && !AgentControlsHumanSeat && Core.Phase == GoPhase.Playing &&
            (Mode == PvpConnectionMode.Local && !IsComputerGame || Core.Turn == MySide);

        public void RequestHint()
        {
            if (!CanRequestHint || HintPending)
            {
                return;
            }
            if (AgentSettings.Ready && _clock.Elapsed.TotalSeconds >= _cooldownUntil)
            {
                try
                {
                    _hintCore = Core;
                    _hintPosition = Agent.GoAgentPosition.Create(Core, Core.Turn, advice: true);
                    _hintDeadline = _clock.Elapsed.TotalSeconds + DecisionDeadlineSeconds;
                    _hintRequest = AgentRequestFactory(AgentSettings, _hintPosition);
                    PublishHint("HintThinking", 0);
                    return;
                }
                catch (Exception)
                {
                    CancelHint();
                    // 不把供应商响应、请求地址或凭据写到玩家台词里。
                }
            }
            PublishLocalHint();
        }

        private void PollHint()
        {
            if (!HintPending)
            {
                return;
            }
            // 同一快照只对同一手有效。离开或收到远端新盘后不会发布旧落点。
            if (!ReferenceEquals(_hintCore, Core) || !CanRequestHint)
            {
                CancelHint();
                return;
            }
            try
            {
                if (_clock.Elapsed.TotalSeconds < _hintDeadline)
                {
                    if (!_hintRequest.IsDone)
                    {
                        return;
                    }
                    if (_hintPosition.TryReadAction(_hintRequest.ReadContent(), out Agent.GoAgentAction action))
                    {
                        CancelHint();
                        if (action.Kind == GoAction.Play)
                        {
                            _suggestions[0] = new GoHint { X = action.X, Y = action.Y };
                            PublishHint("HintCloud", 1);
                        }
                        else
                        {
                            PublishHint("HintPass", 0);
                        }
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // 超时、响应格式错误与网络故障统一使用同一份当前盘面的本地候选。
            }
            CancelHint();
            PublishLocalHint();
        }

        private void PublishLocalHint()
        {
            int count = Core.GetHints(Core.Turn, _suggestions);
            PublishHint(count > 0 ? "HintLocal" : "HintPass", count);
        }

        private void PublishHint(string source, int count)
        {
            HintSource = source;
            HintRevision++;
            _suggestionCount = count;
            if (count > 0)
            {
                HintsVisible = true;
            }
            Revision++;
        }

        private void CancelHint()
        {
            _hintRequest?.Dispose();
            _hintRequest = null;
            _hintCore = null;
            _hintPosition = null;
            HintSource = "";
        }

        public void SetRookie(bool rookie)
        {
            if (Core != null || Queued || Connected)
            {
                Status = "请先离开房间再切换场次";
                return;
            }
            Config = PvpConfig.Read(rookie);
            Revision++;
        }
        /// <summary>显示时钟只做无副作用估算；每次真实落子用ElapsedMs重放，读秒不会因UI刷新而重复扣减。</summary>
        public string ClockText(GoSide side)
        {
            if (_goClock == null || Core == null)
            {
                return "--:--";
            }
            double elapsed = Core.Phase == GoPhase.Playing && Core.Turn == side ? Math.Max(0, _clock.Elapsed.TotalSeconds - _turnStarted) : 0;
            double remaining = _goClock.PeekRemaining(side, elapsed);
            int seconds = (int)Math.Ceiling(Math.Max(0, remaining));
            int periods = _goClock.PeekPeriods(side, elapsed);
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00")
                + (periods > 0 && _goClock.MainRemaining(side) <= elapsed ? " · 读秒 " + periods : "");
        }
        public void Replay(int step)
        {
            if (Core == null || Core.Phase != GoPhase.Finished)
            {
                Status = "对局结束后可复盘";
                return;
            }
            int requestedStep = Math.Max(0, Math.Min(_moves.Count, step));
            var replay = new GoCore(Config.komiHalfPoints);
            for (int i = 0; i < requestedStep; i++)
            {
                PvpMove move = _moves[i];
                if (!replay.TryApply((GoAction)move.kind, (GoSide)move.side, move.x, move.y, out GoPlayResult result))
                {
                    Status = "复盘失败：" + result.Error;
                    return;
                }
            }
            ReplayStep = requestedStep;
            _replay = replay;
            Status = "复盘 " + ReplayStep + " / " + _moves.Count;
            Revision++;
        }
        public void ExitReplay()
        {
            _replay = null;
            ReplayStep = -1;
            Revision++;
        }
        public string ResultText()
        {
            if (Core == null)
            {
                return "";
            }
            if (Core.Phase == GoPhase.Scoring)
            {
                return "确认死子 · 黑" + (Core.HasConfirmed(GoSide.Black) ? "已确认" : "未确认")
                    + " / 白" + (Core.HasConfirmed(GoSide.White) ? "已确认" : "未确认");
            }
            if (Core.Phase != GoPhase.Finished)
            {
                return (Core.Turn == GoSide.Black ? "黑方" : "白方") + "落子 · 第 " + (Core.MoveNumber + 1) + " 手";
            }
            string winner = Core.Winner == GoSide.Black ? "黑方胜" : Core.Winner == GoSide.White ? "白方胜" : "和棋";
            if (Core.EndReason == GoEndReason.Scored)
            {
                GoScore s = Core.Score;
                return winner + " " + (Math.Abs(s.DifferenceHalfPoints) / 2.0).ToString("0.0") + " 目\n"
                    + "黑：" + s.BlackStones + "子 + " + s.BlackTerritory + "空   白：" + s.WhiteStones + "子 + " + s.WhiteTerritory
                    + "空\n白贴 " + (s.KomiHalfPoints / 2.0).ToString("0.0") + " 目 · 中立 " + s.Neutral;
            }
            return winner + (Core.EndReason == GoEndReason.Resigned ? " · 对方认输" : Core.EndReason == GoEndReason.Timeout ? " · 对方超时" : " · 对方离开");
        }
        private void ResetPresentation()
        {
            _replay = null;
            ReplayStep = -1;
            _suggestionCount = 0;
            Feedback = "";
            SpeechCue = "";
            CancelHint();
        }
    }
}

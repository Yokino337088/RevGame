using System;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        private readonly GoHint[] _suggestions = new GoHint[3];
        private int _suggestionCount;
        private GoCore _replay;
        public int ReplayStep { get; private set; } = -1;
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

        /// <summary>只消耗本地建议次数；三候选是启发式而非胜率预测，与两端规则裁判完全分开。</summary>
        public void RequestHint()
        {
            if (!CanAct || Core.Phase != GoPhase.Playing || HintsRemaining <= 0 || (Mode != PvpConnectionMode.Local && Core.Turn != MySide))
            {
                Status = "当前不能请求建议，或建议次数已经用完";
                return;
            }
            _suggestionCount = Core.GetHints(Core.Turn, _suggestions);
            if (_suggestionCount > 0)
            {
                HintsRemaining--;
                HintsVisible = true;
                Status = "建议：" + _suggestions[0].Reason + "（启发式参考，并非必胜下法）";
                Revision++;
            }
        }
        public void SetRookie(bool rookie)
        {
            if (Core != null || Queued || Connected)
            {
                Status = "请先离开房间再切换场次";
                return;
            }
            Config = PvpConfig.Read(rookie);
            HintsRemaining = Config.hintCount;
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
        }
    }
}

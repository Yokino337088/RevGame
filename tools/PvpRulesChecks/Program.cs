// 可重现的纯规则回归。直接链接游戏真实源码，不使用 Unity 桩，不以“测试命令成功”代替业务断言。
using System;
using System.Collections.Generic;
using RevGame.Pvp;

internal static class Program
{
    private static int _assertions;
    private static int _accepted;
    private static int _rejected;

    /// <summary>依次执行生产棋规的纯逻辑回归，遇到首个断言失败则终止。</summary>
    private static void Main()
    {
        OpeningAndAtomicRejection();
        CaptureAndSuicide();
        SuperkoAcrossScoring();
        ScoringNegotiation();
        GuidedTutorial();
        AnalysisAndHints();
        DuelForecastAtMoveFifty();
        ClockBoundaries();
        DeterministicGames(500);
        Console.WriteLine("PASS: " + _assertions + " assertions; 500 fixed-seed games; " + _accepted + " accepted commands; " + _rejected + " rejected attempts.");
    }

    /// <summary>按玩家可操作步骤跑完真实规则课，覆盖禁着回滚、盘面缓存换代和重试恢复。</summary>
    private static void GuidedTutorial()
    {
        var lesson = new PvpTutorial(15);
        var analysis = new GoBoardAnalysis();
        var liberties = new int[GoCore.PointCount];
        for (int step = 1; step <= lesson.StepCount; step++)
        {
            // 错点与重试不应丢掉当前课，也不能误消耗一手。
            Check(lesson.StepNumber == step && !lesson.Completed, "tutorial step stays pending");
            ulong start = lesson.Core.StateHash;
            if (lesson.HintPoint >= 0)
            {
                Check(!lesson.Click(-1, 0) && lesson.Core.StateHash == start && !lesson.CanContinue, "wrong input cannot advance lesson");
                int point = lesson.HintPoint;
                Check(lesson.Click(point % 9, point / 9), "guided point accepted " + lesson.Cue + " " + lesson.Feedback);
            }
            else if (lesson.CanPass)
            {
                Check(lesson.Core.Turn == GoSide.Black && lesson.Pass() && lesson.Core.Phase == GoPhase.Scoring, "two real passes enter scoring");
            }
            else if (lesson.CanConfirmScore)
            {
                Check(lesson.Core.DeadStones.Contains(0) && lesson.ConfirmScore(), "both sides confirm actual dead stones");
            }

            switch (lesson.Cue)
            {
                case "TutorialPlace":
                    Check(lesson.Core.GetStone(4, 4) == GoSide.Black && lesson.Core.GetStone(5, 4) == GoSide.White, "black and white take turns");
                    break;
                case "TutorialLiberty":
                    Check(lesson.FocusPoint == 40 && lesson.Core.CopyLiberties(lesson.FocusPoint, liberties) == 4 &&
                        Array.IndexOf(liberties, 49, 0, 4) >= 0, "highlight points to a real liberty");
                    break;
                case "TutorialConnect":
                    lesson.Core.Analyze(analysis);
                    Check(lesson.FocusPoint == 39 && analysis.Group[39] >= 0 && analysis.Group[39] == analysis.Group[41],
                        "played stone connects groups");
                    break;
                case "TutorialEscape":
                    Check(lesson.Core.CopyLiberties(30, liberties) > 1, "extension saves the threatened group");
                    break;
                case "TutorialCapture":
                    Check(lesson.Core.GetStone(3, 3) == GoSide.Empty && lesson.Core.BlackCaptures == 1, "capture removed actual opponent stone");
                    break;
                case "TutorialSuicide":
                    Check(lesson.Core.StateHash == start && lesson.Feedback.Contains("没有气"), "suicide lesson uses genuine rule rejection");
                    break;
                case "TutorialKo":
                    Check(lesson.Core.StateHash == start && lesson.Feedback.Contains("全局同形"), "ko lesson preserves the prior position");
                    break;
                case "TutorialKoAway":
                    Check(lesson.Core.GetStone(5, 4) == GoSide.White, "white can play elsewhere despite ko");
                    break;
                case "TutorialEyes":
                    lesson.Core.Analyze(analysis);
                    Check(analysis.Eye[0] == 1 && analysis.Eye[2] == 1, "both example eyes exist in production analysis");
                    break;
                case "TutorialTerritory":
                    lesson.Core.Analyze(analysis);
                    Check(analysis.Territory[80] == (byte)GoSide.White && analysis.Territory[40] == 0, "territory demo contains white and neutral points");
                    break;
                case "TutorialDead":
                    Check(lesson.Core.DeadStones.Contains(0) && lesson.Core.GetStone(0) == GoSide.White, "scoring marks instead of removing a dead chain early");
                    break;
                case "TutorialScore":
                    Check(lesson.Core.Phase == GoPhase.Finished && lesson.Core.Score.BlackTerritory == 2 &&
                        lesson.Core.Score.WhiteTerritory == 1 && lesson.Core.Score.KomiHalfPoints == 15, "confirmed outcome uses production scoring and configured komi");
                    break;
            }
            Check(lesson.CanContinue, "completed exercise unlocks next lesson");

            // 计分阶段重试必须恢复死子草稿。随后重新确认，才能推进到结课。
            if (lesson.Cue == "TutorialScore")
            {
                Check(lesson.Retry() && lesson.Core.StateHash == start && lesson.CanConfirmScore &&
                    !lesson.CanContinue && lesson.ConfirmScore(), "retry restores the genuine scoring state");
            }
            Check(lesson.Continue(), "advance after completing exercise");
        }
        Check(lesson.Completed && !lesson.Continue() && !lesson.Retry(), "completed tutorial is terminal");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Play(GoCore game, int x, int y)
    {
        Check(game.TryPlay(x, y, out GoPlayResult result), "fixture move " + x + "," + y + ": " + result.Error);
    }

    /// <summary>相同镜像棋面覆盖第 49 手、第 50 手平估及贴目分出劣势，继续落子不重算第 50 手。</summary>
    private static void DuelForecastAtMoveFifty()
    {
        var equal = new GoCore(0);
        var whiteLead = new GoCore(15);
        var moves = new List<GoCommand>();
        for (int turn = 0; turn < GoCore.DuelForecastMove; turn++)
        {
            int point = turn / 2;
            int x = turn % 2 == 0 ? point % 3 : 8 - point % 3;
            var command = new GoCommand { Action = GoAction.Play, Side = equal.Turn, X = x, Y = point / 3 };
            moves.Add(command);
            Play(equal, command.X, command.Y);
            Play(whiteLead, command.X, command.Y);
            if (turn < 49)
            {
                Check(equal.DuelBlackWinPercent == 50 && whiteLead.DuelBlackWinPercent == 50, "第 50 手前不结算决斗资格");
            }
        }
        int milestone = whiteLead.DuelBlackWinPercent;
        Check(equal.DuelBlackWinPercent == 50 && milestone < 50, "第 50 手平估不触发，贴目落后方低于 50%");
        Check(GoCore.TryReplay(15, moves, out GoCore replay, out _) && replay.DuelBlackWinPercent == milestone,
            "重连棋谱重放得到相同的第 50 手胜率");
        Check(whiteLead.Clone().DuelBlackWinPercent == milestone, "克隆继承已锁定的第 50 手胜率");
        Play(whiteLead, 4, 4);
        Check(whiteLead.DuelBlackWinPercent == milestone, "后续围棋落子不反复发放资格");
    }

    private static void OpeningAndAtomicRejection()
    {
        GoCore game = new GoCore(15);
        Check(game.Turn == GoSide.Black && game.Phase == GoPhase.Playing, "black opens");
        ulong before = game.StateHash;
        Check(!game.TryPlay(GoSide.White, 4, 4, out _), "wrong turn denied");
        Check(!game.TryPlay(GoSide.Black, -1, 2, out _), "outside denied");
        Check(!game.TryApply((GoAction)999, GoSide.Black, 0, 0, out _), "unknown action denied");
        Check(game.StateHash == before && game.Sequence == 0, "invalid commands are atomic");
        Play(game, 0, 0);
        before = game.StateHash;
        Check(!game.TryPlay(0, 0, out _), "occupied denied");
        Check(game.StateHash == before, "occupied rollback");
        byte[] copy = new byte[81];
        game.CopyBoard(copy);
        copy[0] = 2;
        Check(game.GetStone(0) == GoSide.Black, "board copy cannot mutate original");
        GoCore clone = game.Clone();
        Check(clone.StateHash == game.StateHash, "clone complete state");
        Play(clone, 8, 8);
        Check(game.GetStone(8, 8) == GoSide.Empty, "clone independent");
    }

    private static void CaptureAndSuicide()
    {
        GoCore game = new GoCore(15);
        Play(game, 1, 0);
        Play(game, 0, 0);
        Check(game.TryPlay(0, 1, out GoPlayResult capture) && capture.CapturedCount == 1 && capture.Captured.Contains(0), "corner capture");
        Check(game.GetStone(0, 0) == GoSide.Empty && game.BlackCaptures == 1, "capture submitted once");

        game = new GoCore(15);
        Play(game, 1, 0); Play(game, 0, 0); Play(game, 8, 8); Play(game, 0, 1);
        Play(game, 1, 1); Play(game, 8, 7);
        Check(game.TryPlay(0, 2, out capture) && capture.CapturedCount == 2, "whole connected group captured");
        Check(capture.Captured.Contains(0) && capture.Captured.Contains(9), "capture mask stable");
        Play(game, 7, 7);
        Check(capture.Captured.Contains(0) && capture.Captured.Contains(9), "later moves cannot change prior result");

        game = new GoCore(15);
        Play(game, 0, 1); Play(game, 8, 8); Play(game, 1, 0); Play(game, 8, 7);
        Play(game, 2, 1); Play(game, 7, 8); Play(game, 1, 2);
        ulong before = game.StateHash;
        Check(!game.TryPlay(1, 1, out _), "suicide denied");
        Check(game.StateHash == before && game.GetStone(1, 1) == GoSide.Empty, "suicide leaves board and history untouched");
    }

    private static void SuperkoAcrossScoring()
    {
        GoCore game = new GoCore(15);
        Play(game, 0, 1); Play(game, 1, 1); Play(game, 1, 0); Play(game, 2, 0);
        Play(game, 1, 2); Play(game, 3, 1); Play(game, 8, 8); Play(game, 2, 2);
        Check(game.TryPlay(2, 1, out GoPlayResult capture) && capture.CapturedCount == 1, "capture into apparent suicide is legal");
        ulong before = game.StateHash;
        Check(!game.TryPlay(1, 1, out _), "immediate ko denied");
        Check(game.StateHash == before, "ko rejection atomic");
        Check(game.TryPass(GoSide.White, out _) && game.TryPass(GoSide.Black, out _), "passes exempt position repetition");
        Check(game.Phase == GoPhase.Scoring && game.Turn == GoSide.White, "double pass enters scoring");
        Check(game.TryResume(GoSide.Black, out _), "either party can dispute and resume");
        Check(!game.TryPlay(1, 1, out _), "positional superko history survives scoring and pass");
        Play(game, 7, 7); Play(game, 7, 8);
        Check(game.TryPlay(1, 1, out capture) && capture.CapturedCount == 1, "ko recapture after actual board change allowed");
    }

    private static void ScoringNegotiation()
    {
        GoCore game = new GoCore(15);
        Play(game, 0, 0); Play(game, 8, 8); Play(game, 0, 1); Play(game, 8, 7);
        Check(game.TryPass(GoSide.Black, out _) && game.TryPass(GoSide.White, out _), "score fixture pass");
        Check(game.TryConfirmScore(GoSide.Black, out _), "first confirmation recorded");
        Check(game.Phase == GoPhase.Scoring && game.HasConfirmed(GoSide.Black), "one confirmation does not finish");
        Check(!game.TryConfirmScore(GoSide.Black, out _), "duplicate confirmation denied");
        Check(game.TryMarkDead(GoSide.White, 0, 0, out _), "dead proposal accepted");
        Check(game.DeadStones.Contains(0) && game.DeadStones.Contains(9), "whole group marked");
        Check(!game.HasConfirmed(GoSide.Black) && !game.HasConfirmed(GoSide.White), "new proposal invalidates all consent");
        Check(game.GetStone(0, 0) == GoSide.Black, "dead draft does not change played position");
        Check(game.TryConfirmScore(GoSide.Black, out _) && game.TryConfirmScore(GoSide.White, out _), "both agree same proposal");
        Check(game.Phase == GoPhase.Finished && game.EndReason == GoEndReason.Scored && game.Score.BlackStones == 0, "agreed dead group excluded from final area");
        Check(game.Score.BlackArea + game.Score.WhiteArea + game.Score.Neutral == 81, "area partitions board");
        ulong finished = game.StateHash;
        Check(!game.TryResume(GoSide.Black, out _) && !game.TryResign(GoSide.White, out _), "finished immutable");
        Check(game.StateHash == finished, "rejected postgame no mutation");

        GoScore documentedMistake = new GoScore { BlackStones = 43, WhiteStones = 38, KomiHalfPoints = 15 };
        Check(documentedMistake.DifferenceHalfPoints == -5 && documentedMistake.Winner == GoSide.White, "3.75 stones means 7.5 points, not 3.75 points");
        GoScore narrowBlack = new GoScore { BlackStones = 45, WhiteStones = 36, KomiHalfPoints = 15 };
        Check(narrowBlack.DifferenceHalfPoints == 3 && narrowBlack.Winner == GoSide.Black, "45 to 36 wins 1.5 points");
        game = new GoCore(0);
        game.TryPass(GoSide.Black, out _); game.TryPass(GoSide.White, out _);
        game.TryConfirmScore(GoSide.Black, out _); game.TryConfirmScore(GoSide.White, out _);
        Check(game.Score.Neutral == 81 && game.Winner == GoSide.Empty, "empty board is neutral, zero komi draws");
        game = new GoCore(15);
        Check(game.TryResign(GoSide.White, out _) && game.Winner == GoSide.Black && game.EndReason == GoEndReason.Resigned, "resignation winner");
    }

    private static void AnalysisAndHints()
    {
        GoCore game = new GoCore(15);
        Play(game, 1, 1); Play(game, 8, 8); Play(game, 2, 1); Play(game, 8, 7);
        GoBoardAnalysis analysis = new GoBoardAnalysis();
        game.Analyze(analysis);
        Check(analysis.Group[10] == analysis.Group[11] && analysis.Liberties[10] == 6, "connected stones share six unique liberties");
        int[] liberties = new int[81];
        Check(game.CopyLiberties(10, liberties) == 6, "liberty positions agree with count");
        ulong before = game.StateHash;
        Check(game.TryPreview(GoSide.Black, 3, 1, out _) && game.StateHash == before, "preview no side effect");
        GoHint[] hints = new GoHint[3];
        Check(game.GetHints(GoSide.Black, hints) == 3, "three suggestions");
        Check(game.StateHash == before, "suggestions do not mutate history");
        for (int i = 0; i < hints.Length; i++)
        {
            Check(game.TryPreview(GoSide.Black, hints[i].X, hints[i].Y, out _), "suggestion legal");
        }
        game = new GoCore(15);
        for (int y = 0; y < 9; y++)
        {
            Play(game, 4, y); Play(game, 8, y);
        }
        game.Analyze(analysis);
        Check(analysis.Score.BlackTerritory == 36 && analysis.Score.WhiteTerritory == 0 && analysis.Score.Neutral == 27, "connected regions ownership, mixed boundary neutral");

        game = new GoCore(15);
        int[] ring = { 20, 21, 22, 31, 40, 39, 38, 29 };
        for (int i = 0; i < ring.Length; i++)
        {
            Play(game, ring[i] % 9, ring[i] / 9);
            Play(game, 8, i);
        }
        before = game.StateHash;
        game.Analyze(analysis);
        Check(analysis.Eye[30] == 1 && analysis.EyeOwner[30] == 1, "closed single-point eye shape");
        Check(game.StateHash == before, "analysis cannot change rules state");
    }

    private static void ClockBoundaries()
    {
        GoClock clock = new GoClock(600, 30, 3);
        Check(clock.Advance(GoSide.Black, 590) && clock.MainRemaining(GoSide.Black) == 10, "main time consumed");
        Check(clock.PeekRemaining(GoSide.Black, 15) == 25 && clock.PeekPeriods(GoSide.Black, 15) == 3, "cross main to byoyomi preview");
        Check(clock.MainRemaining(GoSide.Black) == 10, "peek never consumes clock");
        Check(clock.Advance(GoSide.Black, 15) && clock.PeekRemaining(GoSide.Black) == 30, "legal move resets last byoyomi period");
        Check(clock.Advance(GoSide.Black, 65) && clock.PeekPeriods(GoSide.Black) == 1, "two complete periods consumed");
        Check(clock.PeekRemaining(GoSide.Black, 29.999) > 0 && !clock.IsTimedOut(GoSide.Black, 29.999), "just before final period expiry");
        Check(clock.IsTimedOut(GoSide.Black, 30), "exact final period expiry");
        GoClock clone = clock.Clone();
        Check(clone.Advance(GoSide.Black, 30) && clone.IsTimedOut(GoSide.Black), "timeout commit");
        Check(!clock.IsTimedOut(GoSide.Black) && clock.MainRemaining(GoSide.White) == 600, "clone and opponent isolated");
        Check(!clock.Advance(GoSide.Black, -1) && !clock.Advance(GoSide.Black, double.NaN), "malformed elapsed denied");
        Check(!clock.Advance(GoSide.Empty, 1), "invalid clock identity denied");
        GoClockSnapshot malformed = clock.Capture();
        malformed.BlackPeriods = -1;
        Check(!clock.TryRestore(malformed) && clock.PeekPeriods(GoSide.Black) == 1, "malformed snapshot atomic");
        GoClock replay = new GoClock(600, 30, 3);
        Check(replay.Advance(GoSide.Black, 590) && replay.Advance(GoSide.Black, 15) && replay.Advance(GoSide.Black, 65), "elapsed sequence replay");
        Check(replay.PeekRemaining(GoSide.Black) == clock.PeekRemaining(GoSide.Black) && replay.PeekPeriods(GoSide.Black) == clock.PeekPeriods(GoSide.Black), "clock replay identical");
        Check(replay.Advance(GoSide.Black, double.MaxValue) && replay.IsTimedOut(GoSide.Black), "huge finite elapsed bounded calculation");
    }

    /// <summary>固定种子逐手对照独立棋规，验证合法性、棋盘和按旧公式全量算出的联机指纹。</summary>
    private static void DeterministicGames(int games)
    {
        Random random = new Random(20261106);
        for (int gameIndex = 0; gameIndex < games; gameIndex++)
        {
            GoCore left = new GoCore(15);
            GoCore right = new GoCore(15);
            SlowOracle oracle = new SlowOracle();
            List<GoCommand> history = new List<GoCommand>();
            GoBoardAnalysis analysis = new GoBoardAnalysis();
            for (int attempt = 0; attempt < 500 && left.Phase == GoPhase.Playing; attempt++)
            {
                int point = random.Next(81);
                GoCommand command = new GoCommand { Action = attempt > 350 || random.Next(80) == 0 ? GoAction.Pass : GoAction.Play, Side = left.Turn, X = point % 9, Y = point / 9 };
                ulong beforeLeft = left.StateHash;
                ulong beforeRight = right.StateHash;
                bool acceptedLeft = left.TryApply(command.Action, command.Side, command.X, command.Y, out GoPlayResult a);
                bool acceptedRight = right.TryApply(command.Action, command.Side, command.X, command.Y, out GoPlayResult b);
                bool acceptedOracle = oracle.Apply(command);
                Check(acceptedLeft == acceptedRight && a.CapturedCount == b.CapturedCount && left.StateHash == right.StateHash, "dual source deterministic game " + gameIndex);
                Check(acceptedLeft == acceptedOracle, "independent full-board oracle agrees on legality");
                if (!acceptedLeft)
                {
                    _rejected++;
                    Check(left.StateHash == beforeLeft && right.StateHash == beforeRight, "every illegal attempt atomic");
                    continue;
                }
                history.Add(command);
                _accepted++;
                Check(left.PositionHash == ReferencePositionHash(oracle.Board) && a.PositionHash == left.PositionHash,
                    "incremental hash agrees with the original full-board computation");
                left.Analyze(analysis);
                for (int cell = 0; cell < 81; cell++)
                {
                    Check(left.GetStone(cell) == right.GetStone(cell), "dual board agrees");
                    Check((int)left.GetStone(cell) == oracle.Board[cell], "independent reference board agrees");
                    if (left.GetStone(cell) != GoSide.Empty)
                    {
                        Check(analysis.Liberties[cell] > 0, "no dead zero-liberty group survives accepted move");
                    }
                }
                Check(analysis.Score.BlackArea + analysis.Score.WhiteArea + analysis.Score.Neutral == 81, "area partition invariant");
            }
            Check(left.Phase == GoPhase.Scoring, "bounded random game reaches double pass");
            for (int color = 1; color <= 2; color++)
            {
                GoCommand confirm = new GoCommand { Action = GoAction.ConfirmScore, Side = (GoSide)color };
                Check(left.TryApply(confirm.Action, confirm.Side, 0, 0, out _) && right.TryApply(confirm.Action, confirm.Side, 0, 0, out _), "finish random game");
                history.Add(confirm);
            }
            Check(GoCore.TryReplay(15, history, out GoCore replay, out string error), "random game replay: " + error);
            Check(replay.StateHash == left.StateHash && replay.Score.DifferenceHalfPoints == left.Score.DifferenceHalfPoints, "final replay agrees");
            history.Add(new GoCommand { Action = GoAction.Play, Side = GoSide.Black, X = 0, Y = 0 });
            Check(!GoCore.TryReplay(15, history, out GoCore invalid, out _) && invalid == null, "invalid replay never publishes partial board");
        }
    }

    /// <summary>直接遍历参考棋盘，复算优化前的 Zobrist 指纹，避免测试和生产代码共用增量更新。</summary>
    private static ulong ReferencePositionHash(int[] board)
    {
        ulong hash = 0;
        for (int point = 0; point < board.Length; point++)
        {
            if (board[point] == 0)
            {
                continue;
            }
            unchecked
            {
                ulong value = 0x72657667616D6500UL + (ulong)(point * 2 + board[point]);
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                hash ^= value ^ (value >> 31);
            }
        }
        return hash;
    }

    /// <summary>
    /// 故意采用低效但直观的独立参考：每步扫描全盘敌块、字符串存历史，不调用 GoCore 的任何算法。
    /// 它只在测试中存在，用来发现“双端同源，所以双方恰好都算错”的问题，不进入游戏或服务器。
    /// </summary>
    private sealed class SlowOracle
    {
        internal int[] Board = new int[81];
        private readonly HashSet<string> _history = new HashSet<string>();
        private int _turn = 1;
        private int _passes;

        internal SlowOracle()
        {
            _history.Add(string.Join(",", Board));
        }

        internal bool Apply(GoCommand command)
        {
            if ((int)command.Side != _turn || _passes == 2)
            {
                return false;
            }
            if (command.Action == GoAction.Pass)
            {
                _turn = 3 - _turn;
                _passes++;
                return true;
            }
            int point = command.Y * 9 + command.X;
            if (command.X < 0 || command.X > 8 || command.Y < 0 || command.Y > 8 || Board[point] != 0)
            {
                return false;
            }
            int[] candidate = (int[])Board.Clone();
            candidate[point] = _turn;
            HashSet<int> handled = new HashSet<int>();
            for (int i = 0; i < 81; i++)
            {
                if (candidate[i] != 3 - _turn || handled.Contains(i))
                {
                    continue;
                }
                List<int> group = Group(candidate, i, out bool hasLiberty);
                handled.UnionWith(group);
                if (!hasLiberty)
                {
                    foreach (int removed in group)
                    {
                        candidate[removed] = 0;
                    }
                }
            }
            Group(candidate, point, out bool ownLiberty);
            string key = string.Join(",", candidate);
            if (!ownLiberty || _history.Contains(key))
            {
                return false;
            }
            Board = candidate;
            _history.Add(key);
            _turn = 3 - _turn;
            _passes = 0;
            return true;
        }

        private static List<int> Group(int[] board, int start, out bool hasLiberty)
        {
            List<int> members = new List<int> { start };
            hasLiberty = false;
            for (int i = 0; i < members.Count; i++)
            {
                int x = members[i] % 9;
                int y = members[i] / 9;
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(8, y + 1); ny++)
                {
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(8, x + 1); nx++)
                    {
                        if (Math.Abs(nx - x) + Math.Abs(ny - y) != 1)
                        {
                            continue;
                        }
                        int next = ny * 9 + nx;
                        if (board[next] == 0)
                        {
                            hasLiberty = true;
                        }
                        else if (board[next] == board[start] && !members.Contains(next))
                        {
                            members.Add(next);
                        }
                    }
                }
            }
            return members;
        }
    }
}

using System;

namespace RevGame.Pvp
{
    /// <summary>只属于一轮本地教学的练习盘和进度。入场者创建，离场直接丢弃，所有落子、停一手和计分由 GoCore 判定。</summary>
    public sealed class PvpTutorial
    {
        /// <summary>区分点击落子、观察棋面、尝试禁着和使用计分按钮，确保读对白不能完成实操。</summary>
        private enum Exercise
        {
            /// <summary>读完说明后允许直接继续。</summary>
            Read,
            /// <summary>必须提交一个符合题意的合法落点。</summary>
            Play,
            /// <summary>必须指出相应现象，点击不吃掉一手棋。</summary>
            Inspect,
            /// <summary>必须亲手尝试禁着并收到真实规则拒绝。</summary>
            Reject,
            /// <summary>必须按停一手按钮进入计分。</summary>
            Pass,
            /// <summary>必须点中死子并改变计分草稿。</summary>
            MarkDead,
            /// <summary>必须主动确认数子，随后演示双方同意的结果。</summary>
            Confirm
        }

        /// <summary>一课对应一个 Excel 事件和一种玩家动作，交叉点缺省为无。</summary>
        private readonly struct Lesson
        {
            /// <summary>供既有鸦鸦气泡从 Excel 查找教学对白的稳定事件名。</summary>
            public readonly string Cue;
            /// <summary>当前课程明确要求玩家执行的动作。</summary>
            public readonly Exercise Action;
            /// <summary>需要点击的交叉点。-1 表示课程不要求点击棋盘。</summary>
            public readonly int Point;

            /// <summary>将从零开始的棋盘坐标换成线性索引。说明页和按钮课使用缺省坐标。</summary>
            public Lesson(string cue, Exercise action, int x = -1, int y = -1)
            {
                Cue = cue;
                Action = action;
                Point = x < 0 ? -1 : y * GoCore.BoardSize + x;
            }
        }

        /// <summary>教学从一手棋开始，逐步引入棋块、禁着、做活与比赛终局。</summary>
        private static readonly Lesson[] Lessons =
        {
            new Lesson("TutorialPlace", Exercise.Play, 4, 4),
            new Lesson("TutorialLiberty", Exercise.Inspect, 4, 5),
            new Lesson("TutorialConnect", Exercise.Play, 4, 4),
            new Lesson("TutorialEscape", Exercise.Play, 3, 4),
            new Lesson("TutorialCapture", Exercise.Play, 3, 4),
            new Lesson("TutorialSuicide", Exercise.Reject, 4, 4),
            new Lesson("TutorialKo", Exercise.Reject, 2, 1),
            new Lesson("TutorialKoAway", Exercise.Play, 5, 4),
            new Lesson("TutorialEyes", Exercise.Inspect, 0, 0),
            new Lesson("TutorialTerritory", Exercise.Inspect, 8, 8),
            new Lesson("TutorialPass", Exercise.Pass),
            new Lesson("TutorialDead", Exercise.MarkDead, 0, 0),
            new Lesson("TutorialScore", Exercise.Confirm),
            new Lesson("TutorialModes", Exercise.Read),
            new Lesson("TutorialFinish", Exercise.Read)
        };

        /// <summary>沿用游戏当前配置的白方贴目，单位半目。</summary>
        private readonly int _komiHalfPoints;
        /// <summary>从零开始的课程下标，结课后仍停在最后一课。</summary>
        private int _index;
        /// <summary>当前实操是否已作答。切课和重试会清空。</summary>
        private bool _answered;
        /// <summary>当前课程的只读定义。</summary>
        private Lesson Current => Lessons[_index];

        /// <summary>给实景棋盘和数子面板读取。教学动作在独立草稿上判定，成功后才交付新实例。</summary>
        public GoCore Core { get; private set; }
        /// <summary>当前课程的 Excel 台词键，答题后保持到玩家继续。</summary>
        public string Cue => Current.Cue;
        /// <summary>界面显示的当前课程编号，从 1 开始。</summary>
        public int StepNumber => _index + 1;
        /// <summary>课程总数，用于显示当前进度。</summary>
        public int StepCount => Lessons.Length;
        /// <summary>未答时的练习高亮交叉点。按钮课程、说明页和已答课程返回 -1。</summary>
        public int HintPoint => _answered || Completed ? -1 : Current.Point;
        /// <summary>气和连接两课主动展示这块棋的气口。其它课使用正常提示，返回 -1。</summary>
        public int FocusPoint => Cue switch
        {
            "TutorialLiberty" => 4 * GoCore.BoardSize + 4,
            "TutorialConnect" => 4 * GoCore.BoardSize + 3,
            _ => -1
        };
        /// <summary>最近实际落子的索引，供棋盘复用末手标记。未落子时为 -1。</summary>
        public int LastPoint { get; private set; } = -1;
        /// <summary>玩家最近动作的提示或规则回执，进入新课程和重试时清空。</summary>
        public string Feedback { get; private set; } = string.Empty;
        /// <summary>只有完成当前实操或读当前说明时允许切到下一课。</summary>
        public bool CanContinue => !Completed && (_answered || Current.Action == Exercise.Read);
        /// <summary>当前要求玩家停一手时才启用按钮。</summary>
        public bool CanPass => !Completed && !_answered && Current.Action == Exercise.Pass;
        /// <summary>当前要求玩家确认计分时才启用按钮。</summary>
        public bool CanConfirmScore => !Completed && !_answered && Current.Action == Exercise.Confirm;
        /// <summary>最后一课已主动继续。调用方据此结束会话并记录完成。</summary>
        public bool Completed { get; private set; }

        /// <summary>为新教学会话建立第一课的空盘。</summary>
        public PvpTutorial(int komiHalfPoints)
        {
            _komiHalfPoints = komiHalfPoints;
            LoadLesson();
        }

        /// <summary>处理真实棋盘交叉点点击。成功表示本课作答完成，规则拒绝课须真的触发禁着才算答对。</summary>
        public bool Click(int x, int y)
        {
            if (Completed || _answered)
            {
                return false;
            }
            if (Current.Point < 0)
            {
                return Reject(CanPass ? "这一步请点“停一手”。" : CanConfirmScore ? "这一步请确认数子。" : "读完说明后继续。");
            }
            if (x < 0 || x >= GoCore.BoardSize || y < 0 || y >= GoCore.BoardSize ||
                y * GoCore.BoardSize + x != Current.Point)
            {
                return Reject("先点棋盘上亮起的交叉点。");
            }
            if (Current.Action == Exercise.Inspect)
            {
                return Accept("找到了。看清棋盘后就能继续。");
            }

            // 棋盘分析以 GoCore 实例缓存。提交成功后换一份规则盘，气、眼和圈地提示同一帧更新。
            GoCore board = Core.Clone();
            if (Current.Action == Exercise.MarkDead)
            {
                if (!board.TryMarkDead(GoSide.Black, x, y, out GoPlayResult mark))
                {
                    return Reject(mark.Error);
                }
                Core = board;
                return Accept("这块棋已标作死子，计分草稿更新。");
            }
            bool legal = board.TryPlay(x, y, out GoPlayResult move);
            if (Current.Action == Exercise.Reject)
            {
                return legal ? Reject("这个落点没有触发本课的禁着。") : Accept(move.Error);
            }
            if (!legal)
            {
                return Reject(move.Error);
            }
            LastPoint = Current.Point;
            if (_index == 0)
            {
                // 首课让玩家看一遍黑白轮流。白棋的示例手也必须由真实规则接受。
                PlayFixture(board, GoSide.White, 5, 4);
                LastPoint = 4 * GoCore.BoardSize + 5;
            }
            Core = board;
            return Accept(move.CapturedCount > 0 ? $"提走了 {move.CapturedCount} 颗棋子。" : "落子成功。");
        }

        /// <summary>玩家选择停一手后，演示白方也停一手。两次真实停手才会进入数子。</summary>
        public bool Pass()
        {
            if (!CanPass)
            {
                return false;
            }
            GoCore board = Core.Clone();
            PassBoth(board);
            Core = board;
            return Accept("双方连续停一手，进入数子。");
        }

        /// <summary>记录玩家的确认，并演示另一方同意死子草稿，交付真实终局分数。</summary>
        public bool ConfirmScore()
        {
            if (!CanConfirmScore)
            {
                return false;
            }
            GoCore board = Core.Clone();
            ConfirmBoth(board);
            Core = board;
            return Accept("双方确认完毕。棋盘上显示的是最终结果。");
        }

        /// <summary>已完成指定动作才能继续。计分系列保留刚才提交的盘面，结课返回真实完成标志。</summary>
        public bool Continue()
        {
            if (!CanContinue)
            {
                return false;
            }
            if (_index == Lessons.Length - 1)
            {
                Completed = true;
                return true;
            }
            _index++;
            switch (Cue)
            {
                case "TutorialKoAway":
                case "TutorialPass":
                case "TutorialDead":
                case "TutorialScore":
                case "TutorialModes":
                case "TutorialFinish":
                    ResetAnswer();
                    return true;
                default:
                    LoadLesson();
                    return true;
            }
        }

        /// <summary>重建当前课程的规则盘和前置步骤，保留课程编号。已结束的会话拒绝重试。</summary>
        public bool Retry()
        {
            if (Completed)
            {
                return false;
            }
            LoadLesson();
            return true;
        }

        /// <summary>保留玩家能理解的结果，停在当前棋面等玩家继续。</summary>
        private bool Accept(string message)
        {
            _answered = true;
            Feedback = message;
            return true;
        }

        /// <summary>解释怎么重试，当前课程与已提交棋面保持原状态。</summary>
        private bool Reject(string message)
        {
            Feedback = message;
            return false;
        }

        /// <summary>清空上一课的作答与提示，调用方决定是否同步重建棋面。</summary>
        private void ResetAnswer()
        {
            _answered = false;
            Feedback = string.Empty;
        }

        /// <summary>通过正式规则调用回放课程前置，含劫前历史、停手阶段和计分草稿。</summary>
        private void LoadLesson()
        {
            ResetAnswer();
            LastPoint = -1;
            var board = new GoCore(_komiHalfPoints);
            switch (Cue)
            {
                case "TutorialLiberty":
                    PlayFixture(board, GoSide.Black, 4, 4);
                    PlayFixture(board, GoSide.White, 0, 0);
                    break;
                case "TutorialConnect":
                    PlaceGroup(board, GoSide.Black, (3, 4), (5, 4));
                    PlayFixture(board, GoSide.White, 0, 0);
                    break;
                case "TutorialEscape":
                    PlayFixture(board, GoSide.Black, 3, 3);
                    PlaceGroup(board, GoSide.White, (2, 3), (3, 2), (4, 3));
                    break;
                case "TutorialCapture":
                    PlaceGroup(board, GoSide.Black, (2, 3), (3, 2), (4, 3));
                    PlayFixture(board, GoSide.White, 3, 3);
                    break;
                case "TutorialSuicide":
                    PlaceGroup(board, GoSide.White, (3, 4), (5, 4), (4, 3), (4, 5));
                    break;
                case "TutorialKo":
                case "TutorialKoAway":
                    // 先放被提的白棋，再用黑棋在底线提掉它。直接写棋面会丢掉打劫所需的前一盘。
                    PlaceGroup(board, GoSide.Black, (1, 1), (3, 1), (2, 2));
                    PlaceGroup(board, GoSide.White, (2, 1), (1, 0), (3, 0));
                    PlayFixture(board, GoSide.Black, 2, 0);
                    LastPoint = 2;
                    break;
                case "TutorialEyes":
                    PlaceGroup(board, GoSide.Black, (1, 0), (0, 1), (1, 1), (2, 1), (3, 1), (3, 0));
                    PlayFixture(board, GoSide.White, 8, 8);
                    break;
                case "TutorialTerritory":
                case "TutorialPass":
                case "TutorialDead":
                case "TutorialScore":
                case "TutorialModes":
                case "TutorialFinish":
                    // 同一结尾棋谱展示两边围地、中立空点和左下角的白方死子。
                    PlaceGroup(board, GoSide.Black, (0, 1), (1, 1), (2, 0));
                    PlaceGroup(board, GoSide.White, (0, 0), (8, 7), (7, 8));
                    if (Current.Action == Exercise.MarkDead || Current.Action == Exercise.Confirm || Current.Action == Exercise.Read)
                    {
                        PassBoth(board);
                    }
                    if (Current.Action == Exercise.Confirm || Current.Action == Exercise.Read)
                    {
                        Require(board.TryMarkDead(GoSide.Black, 0, 0, out GoPlayResult mark), mark);
                    }
                    if (Current.Action == Exercise.Read)
                    {
                        ConfirmBoth(board);
                    }
                    break;
            }
            Core = board;
        }

        /// <summary>为一块练习形状依次落指定一方的棋子，轮次由正式停一手换边。</summary>
        private static void PlaceGroup(GoCore board, GoSide side, params (int X, int Y)[] points)
        {
            foreach ((int x, int y) in points)
            {
                PlayFixture(board, side, x, y);
            }
        }

        /// <summary>合法回放一手示例棋。若轮次不同，先让对方停一手，保留真实规则历史。</summary>
        private static void PlayFixture(GoCore board, GoSide side, int x, int y)
        {
            if (board.Turn != side)
            {
                Require(board.TryPass(board.Turn, out GoPlayResult pass), pass);
            }
            Require(board.TryPlay(side, x, y, out GoPlayResult move), move);
        }

        /// <summary>通过现行的连续停一手规则从落子阶段切到共同数子。</summary>
        private static void PassBoth(GoCore board)
        {
            Require(board.TryPass(board.Turn, out GoPlayResult first), first);
            Require(board.TryPass(board.Turn, out GoPlayResult second), second);
        }

        /// <summary>保持双方确认分别提交，使用 GoCore 最终计算双方面积和贴目。</summary>
        private static void ConfirmBoth(GoCore board)
        {
            Require(board.TryConfirmScore(GoSide.Black, out GoPlayResult black), black);
            Require(board.TryConfirmScore(GoSide.White, out GoPlayResult white), white);
        }

        /// <summary>固定教学棋谱不合法属于配置错误，立即暴露具体拒绝原因。</summary>
        private static void Require(bool success, GoPlayResult result)
        {
            if (!success)
            {
                throw new InvalidOperationException("围棋教学棋谱无效：" + result.Error);
            }
        }
    }
}

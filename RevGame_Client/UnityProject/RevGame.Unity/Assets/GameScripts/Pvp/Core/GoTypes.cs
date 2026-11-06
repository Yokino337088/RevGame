// 九路围棋的数据契约。客户端、回放和离线断言直接编译同一份源码，不引用 Unity 或网络框架。

namespace RevGame.Pvp
{
    /// <summary>协议稳定编号：空点为 0，黑白为 1/2；它们与单机的玩家/敌人阵营完全独立。</summary>
    public enum GoSide : byte
    {
        Empty = 0,
        Black = 1,
        White = 2
    }

    public enum GoPhase
    {
        Playing = 0,
        Scoring = 1,
        Finished = 2
    }

    /// <summary>完整命令序列的动作编号；改动编号属于不兼容协议更新，必须同步提升 RulesVersion。</summary>
    public enum GoAction
    {
        Play = 1,
        Pass = 2,
        Resign = 3,
        ToggleDead = 4,
        ConfirmScore = 5,
        Resume = 6,
        Timeout = 7,
        Disconnect = 8
    }

    public enum GoEndReason
    {
        None = 0,
        Scored = 1,
        Resigned = 2,
        Timeout = 3,
        Disconnected = 4
    }

    /// <summary>两个整数容纳 81 个交叉点；按值复制，提子结果不会被下一次落子的工作缓冲覆盖。</summary>
    public struct GoPointSet
    {
        public ulong Low;
        public ulong High;

        public bool Contains(int index)
        {
            if (index < 0 || index >= GoCore.PointCount)
            {
                return false;
            }
            return index < 64 ? (Low & (1UL << index)) != 0 : (High & (1UL << (index - 64))) != 0;
        }

        public void Add(int index)
        {
            if (index < 0 || index >= GoCore.PointCount)
            {
                return;
            }
            if (index < 64)
            {
                Low |= 1UL << index;
            }
            else
            {
                High |= 1UL << (index - 64);
            }
        }

        public void Remove(int index)
        {
            if (index < 0 || index >= GoCore.PointCount)
            {
                return;
            }
            if (index < 64)
            {
                Low &= ~(1UL << index);
            }
            else
            {
                High &= ~(1UL << (index - 64));
            }
        }
    }

    /// <summary>值类型动作回执。失败只提供原因，不消耗回合、不修改棋盘、历史、确认状态或序号。</summary>
    public struct GoPlayResult
    {
        public bool Success;
        public string Error;
        public GoPointSet Captured;
        public int CapturedCount;
        public int Sequence;
        public ulong PositionHash;
        public ulong StateHash;
    }

    /// <summary>
    /// 中国规则面积计分。面积是盘上活子加独占空点；提子计数仅供展示，不再重复加分。
    /// HalfPoints 的单位为半目，例如 15 表示 7.5 目，也就是 3.75 子；子差等于目差除以二。
    /// </summary>
    public struct GoScore
    {
        public int BlackStones;
        public int WhiteStones;
        public int BlackTerritory;
        public int WhiteTerritory;
        public int Neutral;
        public int KomiHalfPoints;

        public int BlackArea
        {
            get
            {
                return BlackStones + BlackTerritory;
            }
        }

        public int WhiteArea
        {
            get
            {
                return WhiteStones + WhiteTerritory;
            }
        }

        public int DifferenceHalfPoints
        {
            get
            {
                return (BlackArea - WhiteArea) * 2 - KomiHalfPoints;
            }
        }

        public GoSide Winner
        {
            get
            {
                return DifferenceHalfPoints > 0 ? GoSide.Black : DifferenceHalfPoints < 0 ? GoSide.White : GoSide.Empty;
            }
        }
    }

    /// <summary>供视图重复填充的分析结果。只在棋局或选中点变化时计算，禁止每个棋子每帧单独遍历。</summary>
    public sealed class GoBoardAnalysis
    {
        /// <summary>每颗棋子所在块的代表点编号；空点为 -1。</summary>
        public readonly int[] Group = new int[GoCore.PointCount];
        /// <summary>每颗棋子所属块的共享气数；不是逐颗棋子各自的气数。</summary>
        public readonly int[] Liberties = new int[GoCore.PointCount];
        /// <summary>0 中立 / 1 黑 / 2 白；仅拓扑独占区域估计，不自动判死活。</summary>
        public readonly byte[] Territory = new byte[GoCore.PointCount];
        /// <summary>0 无 / 1 眼形 / 2 假眼风险；眼形不等于证明活棋，视图不得据此显示“必活”。</summary>
        public readonly byte[] Eye = new byte[GoCore.PointCount];
        public readonly byte[] EyeOwner = new byte[GoCore.PointCount];
        public GoScore Score;
    }

    /// <summary>单条回放命令；网络序号/重传归连接层，规则只消费已经排好序的意图。</summary>
    public struct GoCommand
    {
        public GoAction Action;
        public GoSide Side;
        public int X;
        public int Y;
    }

    /// <summary>候选仅是局部启发式；它不承诺胜率，不属于裁判，也不会修改当前棋局。</summary>
    public struct GoHint
    {
        public int X;
        public int Y;
        public int Score;
        public string Reason;
    }
}

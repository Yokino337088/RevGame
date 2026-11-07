using UnityEngine;

namespace RevGame.Pvp
{
    public sealed partial class PvpBoardView
    {
        // 计分演出从终局只读快照生成一次，序号、归属、面板小计都共享这张表。
        private GoCore _scoreCore;
        private readonly int[] _scoreOrder = new int[GoCore.PointCount];
        private readonly GoScore[] _prefix = new GoScore[GoCore.PointCount + 1];
        private float _scoreProgress = -1;
        internal int CountStoneTotal
        {
            get;
            private set;
        }
        internal GoScore CountedScore => _prefix[Mathf.Clamp(Mathf.FloorToInt(_scoreProgress), 0, GoCore.PointCount)];

        internal void BeginScoreCount(GoCore core)
        {
            _scoreCore = core;
            _scoreProgress = 0;
            AnalyzeBoard(core, false);
            int order = 0;
            _prefix[0] = default;
            // 第一轮盘上活子，第二轮围住的空点（含移除死子），第三轮中立格。
            // 每轮逐行蛇形扫过棋盘；近邻连续亮起比随机排序更容易追踪。
            for (int stage = 0; stage < 3; stage++)
            {
                for (int i = 0; i < GoCore.PointCount; i++)
                {
                    int x = i % GoCore.BoardSize;
                    int y = i / GoCore.BoardSize;
                    int index = y * GoCore.BoardSize + (y % 2 == 0 ? x : GoCore.BoardSize - x - 1);
                    GoSide stone = core.DeadStones.Contains(index) ? GoSide.Empty : core.GetStone(index);
                    GoSide territory = (GoSide)_analysis.Territory[index];
                    int category = stone != GoSide.Empty ? 0 : territory != GoSide.Empty ? 1 : 2;
                    if (stage != category)
                    {
                        continue;
                    }
                    GoScore tally = _prefix[order];
                    if (stage == 0)
                    {
                        tally.BlackStones += stone == GoSide.Black ? 1 : 0;
                        tally.WhiteStones += stone == GoSide.White ? 1 : 0;
                    }
                    else if (stage == 1)
                    {
                        tally.BlackTerritory += territory == GoSide.Black ? 1 : 0;
                        tally.WhiteTerritory += territory == GoSide.White ? 1 : 0;
                    }
                    else
                    {
                        tally.Neutral++;
                    }
                    _scoreOrder[index] = ++order;
                    _prefix[order] = tally;
                }
                if (stage == 0)
                {
                    CountStoneTotal = order;
                }
            }
            _revision = -1;
        }

        internal void SetScoreProgress(float value)
        {
            _scoreProgress = value;
        }

        internal void ResetScoreCount()
        {
            _scoreCore = null;
            _scoreProgress = -1;
            CountStoneTotal = 0;
            _revision = -1;
        }
    }
}

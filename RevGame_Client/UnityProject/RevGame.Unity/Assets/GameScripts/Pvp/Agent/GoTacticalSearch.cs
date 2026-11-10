using System;

namespace RevGame.Pvp
{
    /// <summary>九路人机的有界两手搜索。先挑当前看起来好的着法，再逐一让对手走出最强的一手。</summary>
    internal sealed class GoTacticalSearch
    {
        // 搜索宽度和评分系数属于算法内部预算，不改变规则或配置表里的对局上限。
        private readonly GoHint[] _ranked = new GoHint[10];
        private readonly int[] _replyScores = new int[10];
        private readonly GoBoardAnalysis _analysis = new GoBoardAnalysis();
        private readonly int[] _influence = new int[GoCore.PointCount];
        private readonly GoCore _move;
        private readonly GoCore _reply;

        internal GoTacticalSearch(GoCore position)
        {
            _move = position.Clone();
            _reply = position.Clone();
        }

        /// <summary>返回最佳着法；试探建议在短名单里找有明显反制的合法点，找不到便沿用正常建议。</summary>
        internal int Rank(GoCore position, GoHint[] best, out GoHint bait)
        {
            int count = 0;
            bait = default;
            GoSide side = position.Turn;
            for (int i = 0; i < GoCore.PointCount; i++)
            {
                if (position.GetStone(i) != GoSide.Empty || !PlayOn(_move, position, i))
                {
                    continue;
                }
                int score = Evaluate(_move, side);
                int insert = count;
                while (insert > 0 && score > _ranked[insert - 1].Score)
                {
                    insert--;
                }
                if (insert == _ranked.Length)
                {
                    continue;
                }
                int last = Math.Min(count, _ranked.Length - 1);
                for (int j = last; j > insert; j--)
                {
                    _ranked[j] = _ranked[j - 1];
                }
                _ranked[insert] = new GoHint { X = i % GoCore.BoardSize, Y = i / GoCore.BoardSize, Score = score };
                count = Math.Min(count + 1, _ranked.Length);
            }

            int written = 0;
            for (int i = 0; i < count; i++)
            {
                GoHint candidate = _ranked[i];
                PlayOn(_move, position, candidate.Y * GoCore.BoardSize + candidate.X);
                int worst = int.MaxValue;
                if (_move.Phase == GoPhase.Playing)
                {
                    // 枚举对手全部合法应手。一个看似能提子的点，如果下一手被反吃，就会在此降序。
                    for (int j = 0; j < GoCore.PointCount; j++)
                    {
                        if (_move.GetStone(j) == GoSide.Empty && PlayOn(_reply, _move, j))
                        {
                            worst = Math.Min(worst, Evaluate(_reply, side));
                        }
                    }
                }
                candidate.Score = worst == int.MaxValue ? candidate.Score : worst;
                _replyScores[i] = candidate.Score;
                int insert = written;
                while (insert > 0 && candidate.Score > best[insert - 1].Score)
                {
                    insert--;
                }
                if (insert < best.Length)
                {
                    for (int j = Math.Min(written, best.Length - 1); j > insert; j--)
                    {
                        best[j] = best[j - 1];
                    }
                    best[insert] = candidate;
                    written = Math.Min(written + 1, best.Length);
                }
            }
            for (int i = 0; i < count; i++)
            {
                // 只从单手排名靠前的点挑陷阱，且必须真的比最强应对差。
                if (best[0].Score - _replyScores[i] >= 12)
                {
                    bait = _ranked[i];
                    bait.Score = _replyScores[i];
                    bait.Reason = "有反制";
                    break;
                }
            }
            return written;
        }

        private static bool PlayOn(GoCore target, GoCore source, int index)
        {
            target.CopyPositionFrom(source);
            return target.TryPlay(index % GoCore.BoardSize, index / GoCore.BoardSize, out _);
        }

        private int Evaluate(GoCore position, GoSide side)
        {
            if (position.Phase == GoPhase.Scoring || position.EndReason == GoEndReason.Scored)
            {
                int points = position.Score.DifferenceHalfPoints;
                return (side == GoSide.Black ? points : -points) * 16;
            }

            position.Analyze(_analysis);
            Array.Clear(_influence, 0, _influence.Length);
            int score = 0;
            for (int i = 0; i < GoCore.PointCount; i++)
            {
                GoSide stone = position.GetStone(i);
                if (stone == GoSide.Empty)
                {
                    continue;
                }
                int sign = stone == side ? 1 : -1;
                int liberties = _analysis.Liberties[i];
                // 每颗被叫吃的棋子都扣分，因此一整条大龙只剩一口气比单颗棋危险得多。
                score += sign * (14 + Math.Min(liberties, 5) * 2 - (liberties == 1 ? 27 : 0));
                int x = i % GoCore.BoardSize;
                int y = i / GoCore.BoardSize;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        int weight = 3 - Math.Abs(dx) - Math.Abs(dy);
                        if (weight > 0 && nx >= 0 && ny >= 0 && nx < GoCore.BoardSize && ny < GoCore.BoardSize)
                        {
                            _influence[ny * GoCore.BoardSize + nx] += sign * weight;
                        }
                    }
                }
            }
            for (int i = 0; i < GoCore.PointCount; i++)
            {
                if (position.GetStone(i) != GoSide.Empty)
                {
                    continue;
                }
                GoSide owner = (GoSide)_analysis.Territory[i];
                if (owner != GoSide.Empty && !_analysis.OpenTerritory[i])
                {
                    score += (owner == side ? 1 : -1) * (_analysis.Eye[i] == 1 ? 19 : 9);
                }
                else
                {
                    score += Math.Sign(_influence[i]) * Math.Min(Math.Abs(_influence[i]), 3);
                }
            }
            return score;
        }
    }
}

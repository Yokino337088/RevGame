// 棋盘可视化分析与启发式建议。它们只读取已提交事实；死活不是局部几何能证明的，禁止自动宣布死棋。
using System;

namespace RevGame.Pvp
{
    public sealed partial class GoCore
    {
        /// <summary>一次填完整盘。调用方持有结果缓冲，避免 81 个棋子各自遍历棋盘，也不暴露内核草稿。</summary>
        public void Analyze(GoBoardAnalysis destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            for (int i = 0; i < PointCount; i++)
            {
                destination.Group[i] = -1;
                destination.Liberties[i] = 0;
                destination.Eye[i] = 0;
                destination.EyeOwner[i] = 0;
            }
            for (int i = 0; i < PointCount; i++)
            {
                if (_board[i] == 0 || destination.Group[i] >= 0)
                {
                    continue;
                }
                int count = CollectGroup(_board, i, out int liberties);
                for (int member = 0; member < count; member++)
                {
                    destination.Group[_group[member]] = i;
                    destination.Liberties[_group[member]] = liberties;
                }
            }
            for (int i = 0; i < PointCount; i++)
            {
                AnalyzeEye(i, destination);
            }
            bool removeDead = Phase == GoPhase.Scoring || (Phase == GoPhase.Finished && EndReason == GoEndReason.Scored);
            destination.Score = CalculateScore(removeDead, destination.Territory, destination.OpenTerritory);
        }

        /// <summary>返回指定棋块的气坐标，按固定下标升序。目标数组由调用方复用，容量至少为 81。</summary>
        public int CopyLiberties(int index, int[] destination)
        {
            if (destination == null || destination.Length < PointCount)
            {
                throw new ArgumentException("气口缓冲区至少需要 81 项。", nameof(destination));
            }
            if (index < 0 || index >= PointCount || _board[index] == 0)
            {
                return 0;
            }
            CollectGroup(_board, index, out _);
            int count = 0;
            for (int i = 0; i < PointCount; i++)
            {
                if (_liberty[i])
                {
                    destination[count++] = i;
                }
            }
            return count;
        }

        /// <summary>用于悬停或首次点选的提子预演。完整克隆含劫历史，预览非法与实际提交用同一规则。</summary>
        public bool TryPreview(GoSide side, int x, int y, out GoPlayResult result)
        {
            return Clone().TryPlay(side, x, y, out result);
        }

        /// <summary>
        /// 显式请求时计算少量候选，不每帧调用。次数由本局客户端服务管理，不伪称服务端反作弊。
        /// 内部权重只决定启发式排序，不是玩法平衡或奖励数值；同分按坐标固定排序，双端可复现。
        /// </summary>
        public int GetHints(GoSide side, GoHint[] destination)
        {
            if (destination == null || destination.Length == 0 || Phase != GoPhase.Playing || side != Turn)
            {
                return 0;
            }
            int count = 0;
            GoBoardAnalysis before = new GoBoardAnalysis();
            Analyze(before);
            for (int i = 0; i < PointCount; i++)
            {
                if (_board[i] != 0)
                {
                    continue;
                }
                GoCore preview = Clone();
                int x = i % BoardSize;
                int y = i / BoardSize;
                if (!preview.TryPlay(side, x, y, out GoPlayResult play))
                {
                    continue;
                }
                preview.CollectGroup(preview._board, i, out int liberties);
                int saved = 0;
                GoPointSet neighboringGroups = new GoPointSet();
                int connections = 0;
                for (int direction = 0; direction < 4; direction++)
                {
                    int next = Neighbor(i, direction);
                    if (next < 0 || _board[next] != (byte)side)
                    {
                        continue;
                    }
                    int representative = before.Group[next];
                    if (neighboringGroups.Contains(representative))
                    {
                        continue;
                    }
                    neighboringGroups.Add(representative);
                    connections++;
                    if (before.Liberties[next] == 1 && liberties > 1)
                    {
                        saved++;
                    }
                }
                int cornerDistance = Math.Min(Math.Abs(x - 2), Math.Abs(x - 6)) + Math.Min(Math.Abs(y - 2), Math.Abs(y - 6));
                int value = play.CapturedCount * 100 + saved * 70 + Math.Max(0, connections - 1) * 12 + Math.Min(liberties, 5) * 2;
                value += MoveNumber < 12 ? Math.Max(0, 12 - cornerDistance * 3) : 0;
                if (liberties == 1)
                {
                    value -= 60;
                }
                string reason = play.CapturedCount > 0 ? "提走对方棋子，获得新的气口" : saved > 0 ? "给被叫吃的棋块增加气" : connections > 1 ? "连接己方棋块，共享更多气" : "靠近空角与星位，保留发展空间";
                GoHint hint = new GoHint { X = x, Y = y, Score = value, Reason = reason };
                int insert = count;
                for (int current = 0; current < count; current++)
                {
                    if (value > destination[current].Score)
                    {
                        insert = current;
                        break;
                    }
                }
                if (insert >= destination.Length)
                {
                    continue;
                }
                int last = Math.Min(count, destination.Length - 1);
                for (int move = last; move > insert; move--)
                {
                    destination[move] = destination[move - 1];
                }
                destination[insert] = hint;
                count = Math.Min(count + 1, destination.Length);
            }
            return count;
        }

        /// <summary>
        /// 面积法按空点连通块找相邻颜色：只接触黑或只接触白才归属，混合/完全空盘为中立。
        /// 不是“最近棋子占地”，也不把提子再加一次分；未协商死子前只是当前拓扑的估算。
        /// </summary>
        private GoScore CalculateScore(bool removeDead, byte[] territory, bool[] openTerritory = null)
        {
            Array.Copy(_board, _working, PointCount);
            Array.Clear(_visited, 0, PointCount);
            GoScore score = new GoScore { KomiHalfPoints = KomiHalfPoints };
            if (openTerritory != null)
            {
                Array.Clear(openTerritory, 0, PointCount);
            }
            for (int i = 0; i < PointCount; i++)
            {
                if (removeDead && _dead.Contains(i))
                {
                    _working[i] = 0;
                }
                if (territory != null)
                {
                    territory[i] = 0;
                }
                if (_working[i] == (byte)GoSide.Black)
                {
                    score.BlackStones++;
                }
                else if (_working[i] == (byte)GoSide.White)
                {
                    score.WhiteStones++;
                }
            }
            for (int start = 0; start < PointCount; start++)
            {
                if (_working[start] != 0 || _visited[start])
                {
                    continue;
                }
                int count = 1;
                int borders = 0;
                int edges = 0;
                _group[0] = start;
                _visited[start] = true;
                for (int head = 0; head < count; head++)
                {
                    int point = _group[head];
                    if (openTerritory != null)
                    {
                        int x = point % BoardSize;
                        int y = point / BoardSize;
                        edges |= (x == 0 ? 1 : x == BoardSize - 1 ? 2 : 0) | (y == 0 ? 4 : y == BoardSize - 1 ? 8 : 0);
                    }
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int next = Neighbor(point, direction);
                        if (next < 0)
                        {
                            continue;
                        }
                        if (_working[next] == 0)
                        {
                            if (!_visited[next])
                            {
                                _visited[next] = true;
                                _group[count++] = next;
                            }
                        }
                        else
                        {
                            borders |= 1 << _working[next];
                        }
                    }
                }
                byte owner = borders == (1 << (int)GoSide.Black) ? (byte)GoSide.Black : borders == (1 << (int)GoSide.White) ? (byte)GoSide.White : (byte)0;
                if (owner == (byte)GoSide.Black)
                {
                    score.BlackTerritory += count;
                }
                else if (owner == (byte)GoSide.White)
                {
                    score.WhiteTerritory += count;
                }
                else
                {
                    score.Neutral += count;
                }
                if (territory != null)
                {
                    for (int member = 0; member < count; member++)
                    {
                        int point = _group[member];
                        territory[point] = owner;
                        if (openTerritory != null)
                        {
                            // 三位或四位为1代表空区还沿棋盘外缘敞开。只压制预览，终局仍按边界棋色计。
                            openTerritory[point] = edges == 7 || edges == 11 || edges >= 13;
                        }
                    }
                }
            }
            return score;
        }

        /// <summary>保守的单点眼形提示：邻边同块，且内点至少三枚同色对角、边角所有实际对角同色。</summary>
        private void AnalyzeEye(int index, GoBoardAnalysis destination)
        {
            if (_board[index] != 0)
            {
                return;
            }
            byte color = 0;
            int group = -1;
            for (int direction = 0; direction < 4; direction++)
            {
                int next = Neighbor(index, direction);
                if (next < 0)
                {
                    continue;
                }
                if (_board[next] == 0 || (color != 0 && color != _board[next]))
                {
                    return;
                }
                color = _board[next];
                if (group >= 0 && group != destination.Group[next])
                {
                    return;
                }
                group = destination.Group[next];
            }
            int x = index % BoardSize;
            int y = index / BoardSize;
            int total = 0;
            int friendly = 0;
            for (int dy = -1; dy <= 1; dy += 2)
            {
                for (int dx = -1; dx <= 1; dx += 2)
                {
                    if (!IsInside(x + dx, y + dy))
                    {
                        continue;
                    }
                    total++;
                    if (_board[(y + dy) * BoardSize + x + dx] == color)
                    {
                        friendly++;
                    }
                }
            }
            destination.EyeOwner[index] = color;
            destination.Eye[index] = (byte)(friendly >= (total == 4 ? 3 : total) ? 1 : 2);
        }
    }
}

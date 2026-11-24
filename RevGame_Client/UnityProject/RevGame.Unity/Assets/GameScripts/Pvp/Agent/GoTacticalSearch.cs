using System;
using System.Collections.Generic;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System.Runtime.ExceptionServices;
using System.Threading;
#endif

namespace RevGame.Pvp
{
    /// <summary>九路人机的有界两手搜索。先挑当前看起来好的着法，再逐一让对手走出最强的一手。</summary>
    internal sealed class GoTacticalSearch
    {
        /// <summary>首轮评分保留的候选数上限，沿用原两手搜索宽度。</summary>
        private const int ShortlistSize = 10;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        /// <summary>Windows 单次至多分四组，给渲染和游戏其他运行线程留出空间。</summary>
        private const int MaxPartitions = 4;
        /// <summary>Windows 至少报告八个逻辑处理器时才默认用并行试算。</summary>
        private const int MinimumProcessorCount = 8;
        /// <summary>最少六个可分的候选点，避免少量工作也等待调度。</summary>
        private const int MinimumParallelCandidates = 6;
        /// <summary>局面少于十八个空位时直接顺算。空位只用来估计试算量，不决定合法点。</summary>
        private const int MinimumParallelEmptyPoints = 18;
#endif
        /// <summary>每格的影响力覆盖和权重只算一次，初始化完成后所有试算组只读。</summary>
        private static readonly (int Point, int Weight)[][] InfluenceNeighbors = BuildInfluenceNeighbors();
        /// <summary>按第一手评分稳定排序的短名单，同分沿用原棋格扫描顺序。</summary>
        private readonly GoHint[] _ranked = new GoHint[ShortlistSize];
        /// <summary>各组只写自己负责的原下标，全部完成后主线程按短名单顺序读。</summary>
        private readonly int[] _replyScores = new int[ShortlistSize];
        /// <summary>当前查询的完整棋面和超级劫历史，开算前复制，工作组只读。</summary>
        private readonly GoCore _source;
        /// <summary>每组独占首手盘、应手盘和分析草稿。组 0 同时用于首轮和串行路径。</summary>
        private readonly SearchWorkspace[] _workspaces;
        /// <summary>最近一次查询启用的试算组数，用于核对平台和负载分支。</summary>
        internal int LastPartitionCount { get; private set; }

        /// <summary>绑定同一局的规则配置。0 使用平台默认分组，显式设置供同输入对照。</summary>
        internal GoTacticalSearch(GoCore position, int maxPartitions = 0)
        {
            _source = position.Clone();
            int partitions = 1;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            partitions = maxPartitions > 0 ? Math.Min(maxPartitions, MaxPartitions)
                : Environment.ProcessorCount >= MinimumProcessorCount ? MaxPartitions : 1;
#endif
            _workspaces = new SearchWorkspace[partitions];
            _workspaces[0] = new SearchWorkspace(position);
        }

        /// <summary>同一搜索实例一次处理一份盘面。各组试算完成后按原短名单顺序挑建议和反制，返回实际写入数。</summary>
        internal int Rank(GoCore position, GoHint[] best, out GoHint bait)
        {
            bait = default;
            LastPartitionCount = 1;
            if (best.Length == 0 || position.Phase != GoPhase.Playing)
            {
                return 0;
            }

            // 先复制一次真实棋局，所有后续试放访问的都是本次查询的棋盘。
            _source.CopyPositionFrom(position);
            GoSide side = _source.Turn;
            SearchWorkspace main = _workspaces[0];
            int count = 0;
            int emptyPoints = 0;
            for (int i = 0; i < GoCore.PointCount; i++)
            {
                if (_source.GetStone(i) != GoSide.Empty)
                {
                    continue;
                }
                emptyPoints++;
                if (!main.TryMove(_source, i, out int score))
                {
                    continue;
                }
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

            ScoreReplies(side, count, emptyPoints);

            // 保持原有同分与诱敌顺序。各组完成的时间完全不参与排位。
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                GoHint candidate = _ranked[i];
                candidate.Score = _replyScores[i];
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

        /// <summary>足量时按连续下标分组并在本次调用内等待全部结果。小局、终局前一手和其他平台仍用组 0。</summary>
        private void ScoreReplies(GoSide side, int count, int emptyPoints)
        {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            bool oneMoveToScoring = _source.PlacementLimit > 0 && _source.PlacementCount >= _source.PlacementLimit - 1;
            if (_workspaces.Length > 1 && count >= MinimumParallelCandidates && emptyPoints >= MinimumParallelEmptyPoints && !oneMoveToScoring)
            {
                LastPartitionCount = _workspaces.Length;
                // 每局首次达到门槛才准备额外试盘。退出本局随原搜索实例一起释放。
                for (int i = 1; i < _workspaces.Length; i++)
                {
                    _workspaces[i] ??= new SearchWorkspace(_source);
                }
                ScoreInParallel(side, count);
                return;
            }
#endif
            ScoreRange(_workspaces[0], side, 0, count);
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        /// <summary>调用线程自己算一组，其余组交给线程池。成功或异常都等本次任务退出，保证下次查询可复用草稿。</summary>
        private void ScoreInParallel(GoSide side, int count)
        {
            var gate = new object();
            int queued = 0;
            int finished = 0;
            ExceptionDispatchInfo failure = null;

            // 同样的分段同时用于调用线程、后台线程和线程池拒绝入队的回退。
            void ScorePartition(int partition)
            {
                int start = count * partition / _workspaces.Length;
                int end = count * (partition + 1) / _workspaces.Length;
                ScoreRange(_workspaces[partition], side, start, end);
            }

            // 每组写完自己的分数才报告完成。后台异常交回发起者，避免拿到不完整排名。
            WaitCallback run = state =>
            {
                try
                {
                    ScorePartition((int)state);
                }
                catch (Exception error)
                {
                    lock (gate)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(error);
                    }
                }
                finally
                {
                    lock (gate)
                    {
                        finished++;
                        Monitor.Pulse(gate);
                    }
                }
            };

            try
            {
                for (int partition = 1; partition < _workspaces.Length; partition++)
                {
                    if (ThreadPool.QueueUserWorkItem(run, partition))
                    {
                        queued++;
                    }
                    else
                    {
                        ScorePartition(partition);
                    }
                }
                ScorePartition(0);
            }
            finally
            {
                // 仅等待真正入队的组，异常退出也不会把仍在写入的草稿交给下次查询。
                lock (gate)
                {
                    while (finished < queued)
                    {
                        Monitor.Wait(gate);
                    }
                }
            }
            failure?.Throw();
        }
#endif

        /// <summary>从不可变的本轮短名单读取，写入该组独占的前闭后开下标范围。</summary>
        private void ScoreRange(SearchWorkspace workspace, GoSide side, int first, int end)
        {
            for (int i = first; i < end; i++)
            {
                _replyScores[i] = workspace.ScoreReply(_source, side, _ranked[i]);
            }
        }

        /// <summary>建出原评估的菱形影响范围。缓存格号和整数权重，省掉每颗棋每次重复检查边界。</summary>
        private static (int Point, int Weight)[][] BuildInfluenceNeighbors()
        {
            var neighbors = new (int Point, int Weight)[GoCore.PointCount][];
            var points = new List<(int Point, int Weight)>(13);
            for (int i = 0; i < neighbors.Length; i++)
            {
                points.Clear();
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
                            points.Add((ny * GoCore.BoardSize + nx, weight));
                        }
                    }
                }
                neighbors[i] = points.ToArray();
            }
            return neighbors;
        }

        /// <summary>每个试算组的私有棋盘与分析缓存。按正式规则验证落子，不持有任何表现对象。</summary>
        private sealed class SearchWorkspace
        {
            /// <summary>前一手候选的完整试盘，也是试对方应手时的历史来源。</summary>
            private readonly GoCore _move;
            /// <summary>逐个尝试对方应手的独立试盘，每一步重新复制首手盘。</summary>
            private readonly GoCore _reply;
            /// <summary>当前组逐盘复用的气、眼和地盘分析结果。</summary>
            private readonly GoBoardAnalysis _analysis = new GoBoardAnalysis();
            /// <summary>当前被评估棋盘逐格累计的影响分数，评价下一张盘前清空。</summary>
            private readonly int[] _influence = new int[GoCore.PointCount];

            /// <summary>复制贴目、落子上限和自动数子约定。后续换候选只复用容量并覆盖盘面。</summary>
            internal SearchWorkspace(GoCore source)
            {
                _move = source.CreateEmptyReplay();
                _reply = source.CreateEmptyReplay();
            }

            /// <summary>以当前走棋方试一手。非法点不参与首轮短名单；合法点返回这手走完的评分。</summary>
            internal bool TryMove(GoCore source, int index, out int score)
            {
                if (!PlayOn(_move, source, index))
                {
                    score = 0;
                    return false;
                }
                score = Evaluate(_move, source.Turn);
                return true;
            }

            /// <summary>用对手全部合法应手中的最低分作为候选得分。无应手或已结束时沿用首轮评分。</summary>
            internal int ScoreReply(GoCore source, GoSide side, GoHint candidate)
            {
                PlayOn(_move, source, candidate.Y * GoCore.BoardSize + candidate.X);
                int worst = int.MaxValue;
                if (_move.Phase == GoPhase.Playing)
                {
                    for (int i = 0; i < GoCore.PointCount; i++)
                    {
                        if (_move.GetStone(i) == GoSide.Empty && PlayOn(_reply, _move, i))
                        {
                            worst = Math.Min(worst, Evaluate(_reply, side));
                        }
                    }
                }
                return worst == int.MaxValue ? candidate.Score : worst;
            }

            /// <summary>重填私有试盘再落子，保留整局的超级劫约束，也保留合法手触发的终局规则。</summary>
            private static bool PlayOn(GoCore target, GoCore source, int index)
            {
                target.CopyPositionFrom(source);
                return target.TryPlay(index % GoCore.BoardSize, index / GoCore.BoardSize, out _);
            }

            /// <summary>从固定执棋方计算原有气眼、叫吃与影响分。终局直接沿用正式数子结果。</summary>
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
                    foreach (var neighbor in InfluenceNeighbors[i])
                    {
                        _influence[neighbor.Point] += sign * neighbor.Weight;
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
}

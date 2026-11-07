// 正规九路围棋的唯一规则入口。每局持有一份实例；不依赖单机 BoardModel、Unity、时钟或随机数。
// 先在私有草稿盘完整试算，确认提子、自杀与超级劫都合法后一次提交，网络拒绝不会留下半步棋。
using System;
using System.Collections.Generic;

namespace RevGame.Pvp
{
    public sealed partial class GoCore
    {
        // 9 和 81 属于本模式棋盘/协议的固定结构，不是策划平衡数值；变更尺寸须升级协议及界面。
        public const int BoardSize = 9;
        public const int PointCount = BoardSize * BoardSize;
        public const int RulesVersion = 1;

        private readonly byte[] _board = new byte[PointCount];
        private readonly byte[] _working = new byte[PointCount];
        private readonly int[] _group = new int[PointCount];
        private readonly bool[] _visited = new bool[PointCount];
        private readonly bool[] _liberty = new bool[PointCount];
        private readonly HashSet<PositionKey> _positions = new HashSet<PositionKey>();
        private GoPointSet _dead;
        private byte _confirmed;
        private int _passes;
        private ulong _historyHash;

        public GoSide Turn
        {
            get;
            private set;
        }
        public GoPhase Phase
        {
            get;
            private set;
        }
        public GoSide Winner
        {
            get;
            private set;
        }
        public GoEndReason EndReason
        {
            get;
            private set;
        }
        public int MoveNumber
        {
            get;
            private set;
        }
        /// <summary>只数实际放到盘上的棋子，停一手和死子确认都不占预算。0 上限表示标准对局。</summary>
        public int PlacementCount
        {
            get;
            private set;
        }
        public int PlacementLimit
        {
            get;
        }
        public bool PlacementLimitReached => PlacementLimit > 0 && PlacementCount >= PlacementLimit;

        public int Sequence
        {
            get;
            private set;
        }
        public int BlackCaptures
        {
            get;
            private set;
        }
        public int WhiteCaptures
        {
            get;
            private set;
        }
        public int KomiHalfPoints
        {
            get;
            private set;
        }
        public ulong PositionHash
        {
            get;
            private set;
        }
        public GoScore Score
        {
            get;
            private set;
        }

        /// <summary>只读共享死子草稿。标记不会真的提子；双方同意后才用这份草稿计算最终面积。</summary>
        public GoPointSet DeadStones
        {
            get
            {
                return _dead;
            }
        }

        public int ConsecutivePasses
        {
            get
            {
                return _passes;
            }
        }

        /// <summary>联机对账同时覆盖棋面、规则、轮次、历史、死子草稿与确认；只比棋面会漏掉错回合。</summary>
        public ulong StateHash
        {
            get
            {
                ulong hash = Mix(PositionHash ^ _historyHash);
                hash = Mix(hash ^ (ulong)KomiHalfPoints ^ ((ulong)RulesVersion << 32));
                hash = Mix(hash ^ (ulong)Sequence ^ ((ulong)MoveNumber << 32));
                hash = Mix(hash ^ (ulong)Turn ^ ((ulong)Phase << 8) ^ ((ulong)_confirmed << 16) ^ ((ulong)_passes << 24));
                hash = Mix(hash ^ (ulong)Winner ^ ((ulong)EndReason << 8));
                hash = Mix(hash ^ (ulong)BlackCaptures ^ ((ulong)WhiteCaptures << 32));
                if (PlacementLimit > 0)
                {
                    hash = Mix(hash ^ (ulong)PlacementLimit ^ ((ulong)PlacementCount << 32));
                }
                return Mix(hash ^ _dead.Low ^ Mix(_dead.High));
            }
        }

        /// <summary>贴目由导表配置传入，单位半目；构造参数非法属于接线错误，不偷偷改成默认数值。</summary>
        public GoCore(int komiHalfPoints, int placementLimit = 0)
        {
            if (komiHalfPoints < 0 || komiHalfPoints > PointCount * 2)
            {
                throw new ArgumentOutOfRangeException(nameof(komiHalfPoints), "贴目须在 0 至棋盘总面积之间，单位为半目。");
            }
            PlacementLimit = placementLimit;
            KomiHalfPoints = komiHalfPoints;
            Turn = GoSide.Black;
            _positions.Add(new PositionKey(_board));
        }

        public static GoSide Opponent(GoSide side)
        {
            return side == GoSide.Black ? GoSide.White : side == GoSide.White ? GoSide.Black : GoSide.Empty;
        }

        public GoSide GetStone(int x, int y)
        {
            return IsInside(x, y) ? (GoSide)_board[y * BoardSize + x] : GoSide.Empty;
        }

        public GoSide GetStone(int index)
        {
            return index >= 0 && index < PointCount ? (GoSide)_board[index] : GoSide.Empty;
        }

        /// <summary>复制给调用方自己的缓冲区，永不暴露可写棋盘数组，防止视图绕过规则改占格。</summary>
        public void CopyBoard(byte[] destination)
        {
            if (destination == null || destination.Length < PointCount)
            {
                throw new ArgumentException("棋盘缓冲区至少需要 81 项。", nameof(destination));
            }
            Array.Copy(_board, destination, PointCount);
        }

        public bool HasConfirmed(GoSide side)
        {
            return IsPlayer(side) && (_confirmed & (1 << (int)side)) != 0;
        }

        /// <summary>网络、UI、回放共用的命令分派。断线/超时的时间判断归连接层，本类只记录其确定结果。</summary>
        public bool TryApply(GoAction action, GoSide side, int x, int y, out GoPlayResult result)
        {
            switch (action)
            {
                case GoAction.Play:
                    return TryPlay(side, x, y, out result);
                case GoAction.Pass:
                    return TryPass(side, out result);
                case GoAction.Resign:
                    return TryResign(side, out result);
                case GoAction.ToggleDead:
                    return TryMarkDead(side, x, y, out result);
                case GoAction.ConfirmScore:
                    return TryConfirmScore(side, out result);
                case GoAction.Resume:
                    return TryResume(side, out result);
                case GoAction.Timeout:
                    return TryForfeit(side, GoEndReason.Timeout, out result);
                case GoAction.Disconnect:
                    return TryForfeit(side, GoEndReason.Disconnected, out result);
                default:
                    return Reject("无法识别的围棋动作。", out result);
            }
        }

        public bool TryPlay(int x, int y, out GoPlayResult result)
        {
            return TryPlay(Turn, x, y, out result);
        }

        public bool TryPlay(GoSide side, int x, int y, out GoPlayResult result)
        {
            if (!CanTakeTurn(side, out string error))
            {
                return Reject(error, out result);
            }
            if (!IsInside(x, y))
            {
                return Reject("请落在棋盘的交叉点上。", out result);
            }
            int index = y * BoardSize + x;
            if (_board[index] != 0)
            {
                return Reject("这里已经有子。", out result);
            }

            Array.Copy(_board, _working, PointCount);
            _working[index] = (byte)side;
            GoPointSet captured = new GoPointSet();
            int captureCount = 0;
            // 先提敌子再检查自己是否有气：看似无气但能提走敌人的落子是合法着法。
            for (int direction = 0; direction < 4; direction++)
            {
                int next = Neighbor(index, direction);
                if (next < 0 || _working[next] != (byte)Opponent(side))
                {
                    continue;
                }
                int count = CollectGroup(_working, next, out int liberties);
                if (liberties != 0)
                {
                    continue;
                }
                for (int member = 0; member < count; member++)
                {
                    int removed = _group[member];
                    _working[removed] = 0;
                    captured.Add(removed);
                    captureCount++;
                }
            }
            CollectGroup(_working, index, out int ownLiberties);
            if (ownLiberties == 0)
            {
                return Reject("这里没有气，且不能提走对方棋子。", out result);
            }

            PositionKey candidate = new PositionKey(_working);
            // 精确的 162 位棋面键做位置超级劫；公开 Zobrist 哈希仅作联机快速对账，不用碰撞概率判输赢。
            if (_positions.Contains(candidate))
            {
                return Reject("此着会使全局同形再现，请先在别处落子。", out result);
            }
            Array.Copy(_working, _board, PointCount);
            _positions.Add(candidate);
            PositionHash = ComputePositionHash(_board);
            _historyHash = Mix(_historyHash ^ PositionHash);
            if (side == GoSide.Black)
            {
                BlackCaptures += captureCount;
            }
            else
            {
                WhiteCaptures += captureCount;
            }
            Turn = Opponent(side);
            MoveNumber++;
            PlacementCount++;
            _passes = 0;
            if (PlacementLimitReached)
            {
                BeginScoring();
            }
            return Accept(captured, captureCount, out result);
        }

        /// <summary>停一手不改变棋面，所以明确豁免超级劫检查；连续双方停一手进入共同数子。</summary>
        public bool TryPass(GoSide side, out GoPlayResult result)
        {
            if (!CanTakeTurn(side, out string error))
            {
                return Reject(error, out result);
            }
            Turn = Opponent(side);
            MoveNumber++;
            _passes++;
            if (_passes == 2)
            {
                BeginScoring();
            }
            return Accept(new GoPointSet(), 0, out result);
        }

        private void BeginScoring()
        {
            Phase = GoPhase.Scoring;
            _dead = default;
            _confirmed = 0;
            Score = CalculateScore(false, null);
        }

        public bool TryResign(GoSide side, out GoPlayResult result)
        {
            return TryForfeit(side, GoEndReason.Resigned, out result);
        }

        /// <summary>side 是认输/超时/断线的一方，绝不是获胜方。双方仍通过同一序列重放该事实。</summary>
        public bool TryForfeit(GoSide side, GoEndReason reason, out GoPlayResult result)
        {
            if (!IsPlayer(side) || Phase == GoPhase.Finished)
            {
                return Reject("对局已结束，或玩家身份无效。", out result);
            }
            if (reason != GoEndReason.Resigned && reason != GoEndReason.Timeout && reason != GoEndReason.Disconnected)
            {
                return Reject("结束原因无效。", out result);
            }
            Winner = Opponent(side);
            EndReason = reason;
            Phase = GoPhase.Finished;
            Score = CalculateScore(false, null);
            return Accept(new GoPointSet(), 0, out result);
        }

        /// <summary>任一玩家可整块切换死子标记；每次修改都清掉双方旧确认，不能拿旧同意结算新方案。</summary>
        public bool TryMarkDead(GoSide side, int x, int y, out GoPlayResult result)
        {
            if (!CanScore(side, out string error))
            {
                return Reject(error, out result);
            }
            if (!IsInside(x, y) || GetStone(x, y) == GoSide.Empty)
            {
                return Reject("请点选一块棋子来标记或取消死子。", out result);
            }
            int index = y * BoardSize + x;
            bool remove = _dead.Contains(index);
            int count = CollectGroup(_board, index, out _);
            for (int member = 0; member < count; member++)
            {
                if (remove)
                {
                    _dead.Remove(_group[member]);
                }
                else
                {
                    _dead.Add(_group[member]);
                }
            }
            _confirmed = 0;
            Score = CalculateScore(true, null);
            return Accept(new GoPointSet(), 0, out result);
        }

        public bool TryConfirmScore(GoSide side, out GoPlayResult result)
        {
            if (!CanScore(side, out string error))
            {
                return Reject(error, out result);
            }
            if (HasConfirmed(side))
            {
                return Reject("你已确认当前死子方案，正在等待对手。", out result);
            }
            _confirmed |= (byte)(1 << (int)side);
            if (HasConfirmed(GoSide.Black) && HasConfirmed(GoSide.White))
            {
                Score = CalculateScore(true, null);
                Winner = Score.Winner;
                EndReason = GoEndReason.Scored;
                Phase = GoPhase.Finished;
            }
            return Accept(new GoPointSet(), 0, out result);
        }

        /// <summary>死活有争议时继续下，保留全部超级劫历史，不能借数子流程绕过全局同形禁令。</summary>
        public bool TryResume(GoSide side, out GoPlayResult result)
        {
            if (!CanScore(side, out string error))
            {
                return Reject(error, out result);
            }
            if (PlacementLimitReached)
            {
                return Reject("已达到本局落子上限，请确认死子并数子。", out result);
            }
            Phase = GoPhase.Playing;
            _passes = 0;
            _dead = new GoPointSet();
            _confirmed = 0;
            Score = new GoScore();
            return Accept(new GoPointSet(), 0, out result);
        }

        /// <summary>深复制只用于显式悬停预览、建议和测试；复制全部劫历史，绝不在实盘上试着落下再撤销。</summary>
        public GoCore Clone()
        {
            var clone = new GoCore(KomiHalfPoints, PlacementLimit);
            clone.CopyPositionFrom(this);
            return clone;
        }

        /// <summary>搜索同一局时反复重填私有棋盘，保留完整劫历史和规则预算。禁止用在正在进行的实盘上。</summary>
        internal void CopyPositionFrom(GoCore source)
        {
            Array.Copy(source._board, _board, PointCount);
            _positions.Clear();
            _positions.UnionWith(source._positions);
            _dead = source._dead;
            _confirmed = source._confirmed;
            _passes = source._passes;
            _historyHash = source._historyHash;
            Turn = source.Turn;
            Phase = source.Phase;
            Winner = source.Winner;
            EndReason = source.EndReason;
            MoveNumber = source.MoveNumber;
            PlacementCount = source.PlacementCount;
            Sequence = source.Sequence;
            BlackCaptures = source.BlackCaptures;
            WhiteCaptures = source.WhiteCaptures;
            PositionHash = source.PositionHash;
            Score = source.Score;
        }

        /// <summary>回放在新实例中执行。任一命令非法即返回失败及准确索引，不发布半份“已同步”棋盘。</summary>
        public static bool TryReplay(int komiHalfPoints, IReadOnlyList<GoCommand> commands, out GoCore replay, out string error)
        {
            replay = null;
            error = string.Empty;
            if (commands == null)
            {
                error = "回放命令为空。";
                return false;
            }
            GoCore candidate = new GoCore(komiHalfPoints);
            for (int i = 0; i < commands.Count; i++)
            {
                GoCommand command = commands[i];
                if (!candidate.TryApply(command.Action, command.Side, command.X, command.Y, out GoPlayResult result))
                {
                    error = "第 " + (i + 1) + " 条命令无效：" + result.Error;
                    return false;
                }
            }
            replay = candidate;
            return true;
        }

        private bool CanTakeTurn(GoSide side, out string error)
        {
            error = string.Empty;
            if (Phase != GoPhase.Playing)
            {
                error = Phase == GoPhase.Scoring ? "双方正在确认死子；有争议可继续对弈。" : "对局已经结束。";
                return false;
            }
            if (!IsPlayer(side) || side != Turn)
            {
                error = "还没有轮到你落子。";
                return false;
            }
            return true;
        }

        private bool CanScore(GoSide side, out string error)
        {
            error = string.Empty;
            if (Phase != GoPhase.Scoring || !IsPlayer(side))
            {
                error = "当前不在双方数子阶段。";
                return false;
            }
            return true;
        }

        private bool Accept(GoPointSet captured, int count, out GoPlayResult result)
        {
            Sequence++;
            result = new GoPlayResult
            {
                Success = true,
                Error = string.Empty,
                Captured = captured,
                CapturedCount = count,
                Sequence = Sequence,
                PositionHash = PositionHash,
                StateHash = StateHash
            };
            return true;
        }

        private bool Reject(string error, out GoPlayResult result)
        {
            result = new GoPlayResult
            {
                Success = false,
                Error = error,
                Sequence = Sequence,
                PositionHash = PositionHash,
                StateHash = StateHash
            };
            return false;
        }

        private static bool IsPlayer(GoSide side)
        {
            return side == GoSide.Black || side == GoSide.White;
        }

        private static bool IsInside(int x, int y)
        {
            return x >= 0 && x < BoardSize && y >= 0 && y < BoardSize;
        }

        /// <summary>只取上下左右；对角相连不属于同一块。越界返回 -1，边角不会“绕行”到另一边。</summary>
        private static int Neighbor(int index, int direction)
        {
            int x = index % BoardSize;
            int y = index / BoardSize;
            switch (direction)
            {
                case 0:
                    return x > 0 ? index - 1 : -1;
                case 1:
                    return x + 1 < BoardSize ? index + 1 : -1;
                case 2:
                    return y > 0 ? index - BoardSize : -1;
                default:
                    return y + 1 < BoardSize ? index + BoardSize : -1;
            }
        }

        /// <summary>广度优先找棋块，同时按交叉点去重其气；同一口气邻接多个己子仍只算一次。</summary>
        private int CollectGroup(byte[] board, int start, out int liberties)
        {
            Array.Clear(_visited, 0, PointCount);
            Array.Clear(_liberty, 0, PointCount);
            byte color = board[start];
            int count = 1;
            _group[0] = start;
            _visited[start] = true;
            liberties = 0;
            for (int head = 0; head < count; head++)
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    int next = Neighbor(_group[head], direction);
                    if (next < 0)
                    {
                        continue;
                    }
                    if (board[next] == 0)
                    {
                        if (!_liberty[next])
                        {
                            _liberty[next] = true;
                            liberties++;
                        }
                    }
                    else if (board[next] == color && !_visited[next])
                    {
                        _visited[next] = true;
                        _group[count++] = next;
                    }
                }
            }
            return count;
        }

        /// <summary>固定整数混合生成 Zobrist 键；不依赖系统 Random、进程种子或容器枚举顺序。</summary>
        private static ulong ComputePositionHash(byte[] board)
        {
            ulong hash = 0;
            for (int i = 0; i < PointCount; i++)
            {
                if (board[i] != 0)
                {
                    hash ^= Mix((ulong)(i * 2 + board[i]) + 0x72657667616D6500UL);
                }
            }
            return hash;
        }

        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }

        /// <summary>每点两位，三个 ulong 完整保存 81 点，避免以“哈希碰撞很少”代替规则正确性。</summary>
        private struct PositionKey : IEquatable<PositionKey>
        {
            private readonly ulong _a;
            private readonly ulong _b;
            private readonly ulong _c;

            internal PositionKey(byte[] board)
            {
                _a = 0;
                _b = 0;
                _c = 0;
                for (int i = 0; i < PointCount; i++)
                {
                    if (i < 32)
                    {
                        _a |= (ulong)board[i] << (i * 2);
                    }
                    else if (i < 64)
                    {
                        _b |= (ulong)board[i] << ((i - 32) * 2);
                    }
                    else
                    {
                        _c |= (ulong)board[i] << ((i - 64) * 2);
                    }
                }
            }

            public bool Equals(PositionKey other)
            {
                return _a == other._a && _b == other._b && _c == other._c;
            }

            public override bool Equals(object other)
            {
                return other is PositionKey && Equals((PositionKey)other);
            }

            public override int GetHashCode()
            {
                ulong mixed = Mix(_a ^ Mix(_b) ^ Mix(_c));
                return unchecked((int)(mixed ^ (mixed >> 32)));
            }
        }
    }
}

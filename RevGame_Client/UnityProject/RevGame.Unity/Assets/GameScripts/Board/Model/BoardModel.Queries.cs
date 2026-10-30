// ============================================================
// BoardModel.Queries.cs —— 减少同一盘面上的重复查询与AI短命副本。
// 高频工作区归一张棋盘所有：同一战斗规则在主线程串行查询，实战与试算棋盘永不共享可变名单。
// 几何缓存只保存有序坐标，不缓存生命威胁评分；缓存命中后目标优先级仍按当前血量计算。
// Reset断开旧棋引用，保留有界容器容量；它是项目模型的一部分，不是新的框架系统。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Board
{
    public sealed partial class BoardModel
    {
        private sealed class AttackEntry
        {
            // Topology/Targets是计算时的版本，Range单位为格；-1强制首次重算。
            internal int Topology = -1, Targets = -1, Range;
            // Def是只读图鉴引用；原位升星更换Def会失效。Allowed捕捉冻结/围困/禁攻变化。
            internal PieceDef Definition;
            internal bool Allowed;
            // 只存坐标而非Piece引用；调用方获得复制的有序结果，无权修改缓存本体。
            internal readonly List<BoardPos> Cells = new List<BoardPos>(81);
        }
        private readonly Dictionary<int, AttackEntry> _attackCache = new Dictionary<int, AttackEntry>(81);
        // 每张棋盘最多81颗在场棋。移动与AI试盘复位时复用条目及其列表，不能因失效又每次new。
        private readonly Stack<AttackEntry> _freeAttackEntries = new Stack<AttackEntry>(81);
        /// <summary>累计命中/重算计数供摘要使用，不在热循环格式化字符串；Reset清零。</summary>
        public long AttackCacheHits { get; private set; }
        /// <summary>累计几何重算次数；可见/存活或拓扑变化后首次查询计一次，供实际命中率核对。</summary>
        public long AttackCacheMisses { get; private set; }
        /// <summary>独立试盘完整刷新次数，不等于候选个数；只在调用Trial时累计。</summary>
        public long TacticalSnapshotCopies { get; private set; }
        /// <summary>已存在的深副本完整覆盖次数；首次构造不计复用，避免虚报缓存收益。</summary>
        public long TacticalPieceReuses { get; private set; }
        private readonly List<List<Piece>> _tacticalGroups = new List<List<Piece>>(81);
        private readonly Stack<List<Piece>> _freeGroups = new Stack<List<Piece>>(81);
        private readonly HashSet<Piece> _groupVisited = new HashSet<Piece>();
        private int _groupsTopology = -1;
        /// <summary>只供同步战术评分读取。调用者不得改名单；下一次拓扑变化重建，但列表对象和容量复用。</summary>
        internal List<List<Piece>> TacticalGroups()
        {
            if (_groupsTopology == TopologyVersion) { return _tacticalGroups; }
            foreach (List<Piece> group in _tacticalGroups) { group.Clear(); _freeGroups.Push(group); }
            _tacticalGroups.Clear(); _groupVisited.Clear();
            foreach (Piece root in Pieces)
            {
                if (!_groupVisited.Add(root)) { continue; }
                List<Piece> group = _freeGroups.Count > 0 ? _freeGroups.Pop() : new List<Piece>(81);
                group.Add(root);
                // group也作为BFS队列，避免每个棋群新建Queue与yield邻格枚举器。
                for (int i = 0; i < group.Count; i++)
                {
                    Piece p = group[i];
                    for (int edge = 0; edge < p.Size; edge++)
                    {
                        for (int direction = 0; direction < 4; direction++)
                        {
                            BoardPos pos = direction == 0 ? new BoardPos(p.Pos.X - 1, p.Pos.Y + edge)
                                : direction == 1 ? new BoardPos(p.Pos.X + p.Size, p.Pos.Y + edge)
                                : direction == 2 ? new BoardPos(p.Pos.X + edge, p.Pos.Y - 1) : new BoardPos(p.Pos.X + edge, p.Pos.Y + p.Size);
                            Piece other = GetPiece(pos);
                            if (other != null && other.Side == p.Side && _groupVisited.Add(other)) { group.Add(other); }
                        }
                    }
                }
                _tacticalGroups.Add(group);
            }
            _groupVisited.Clear(); _groupsTopology = TopologyVersion; return _tacticalGroups;
        }
        private TacticsWorkspace _tactics;
        // 惰性创建：完全不做试算的盘面不支付深试盘与辅助数组开销。
        internal TacticsWorkspace Tactics => _tactics ?? (_tactics = new TacticsWorkspace(Size));
        internal void ForgetQuery(int id)
        {
            if (!_attackCache.TryGetValue(id, out AttackEntry entry)) { return; }
            _attackCache.Remove(id); entry.Cells.Clear(); entry.Definition = null; entry.Topology = -1;
            _freeAttackEntries.Push(entry);
        }
        private void ClearQueryCache()
        {
            foreach (AttackEntry entry in _attackCache.Values)
            {
                entry.Cells.Clear(); entry.Definition = null; entry.Topology = -1; _freeAttackEntries.Push(entry);
            }
            _attackCache.Clear(); AttackCacheHits = AttackCacheMisses = 0; TargetVersion++;
            // 清旧棋引用但保留容量：新回合第一次查询会按新Topology重建，长期试盘也不会持有已离场棋子。
            foreach (List<Piece> group in _tacticalGroups) { group.Clear(); _freeGroups.Push(group); }
            _tacticalGroups.Clear(); _groupVisited.Clear(); _groupsTopology = -1;
            _tactics?.Clear();
        }
        internal int CountSide(GameSide side)
        {
            int n = 0; foreach (Piece p in Pieces) { if (p.Side == side) { n++; } }
            return n;
        }
        internal void ReadAttackTargets(Piece actor, List<BoardPos> result)
        {
            // 临时尚未入盘棋子不可污染Id缓存，供布阵评分等兼容查询使用。
            if (FindPiece(actor.Id) != actor) { MoveRules.CalculateAttackTargets(this, actor, result); return; }
            if (!_attackCache.TryGetValue(actor.Id, out AttackEntry entry))
            { entry = _freeAttackEntries.Count > 0 ? _freeAttackEntries.Pop() : new AttackEntry(); _attackCache.Add(actor.Id, entry); }
            bool allowed = MoveRules.CanAttack(actor); int range = Balance.EnemyArcherRange;
            if (entry.Topology == TopologyVersion && entry.Targets == TargetVersion && entry.Definition == actor.Def && entry.Allowed == allowed && entry.Range == range)
            { AttackCacheHits++; }
            else
            {
                AttackCacheMisses++;
                MoveRules.CalculateAttackTargets(this, actor, entry.Cells);
                entry.Topology = TopologyVersion; entry.Targets = TargetVersion; entry.Definition = actor.Def; entry.Allowed = allowed; entry.Range = range;
            }
            result.Clear(); result.AddRange(entry.Cells);
        }

        /// <summary>每张源棋盘单独持有一份深试盘与列表，查询结束副本可变，下一轮必须全字段覆盖。</summary>
        internal sealed class TacticsWorkspace
        {
            internal readonly List<BoardPos> Hits = new List<BoardPos>(81);
            internal readonly List<Piece> Allies = new List<Piece>(81), Players = new List<Piece>(81), BossVictims = new List<Piece>(81);
            // 整队名单与移动/补子评分名单分开，嵌套辅助查询不会覆盖正在遍历的候选。
            internal readonly List<Piece> FormationRanged = new List<Piece>(81);
            internal readonly List<Piece> FormationCopies = new List<Piece>(81);
            internal readonly HashSet<int> FormationConnected = new HashSet<int>();
            internal readonly Queue<BoardPos> Queue = new Queue<BoardPos>(81);
            internal readonly int[,] Distances;
            private readonly int _size;
            // _trial与_copies只能由当前源盘使用；不交给动画/异步回调，不在候选评估中访问Unity对象。
            private BoardModel _trial;
            private readonly Dictionary<int, Piece> _copies = new Dictionary<int, Piece>(81);
            private readonly List<int> _obsolete = new List<int>(81);
            private Piece _added;
            internal TacticsWorkspace(int size) { _size = size; Distances = new int[size, size]; }
            /// <summary>源棋盘回合重置/离场时断开全部旧棋引用，保留容器容量与试盘本身，防止串局。</summary>
            internal void Clear()
            {
                _trial?.Reset();
                _copies.Clear(); _obsolete.Clear(); _added = null;
                Hits.Clear(); Allies.Clear(); Players.Clear(); BossVictims.Clear(); Queue.Clear();
                FormationRanged.Clear(); FormationCopies.Clear(); FormationConnected.Clear();
            }
            internal BoardModel Trial(BoardModel source)
            {
                if (_trial == null) { _trial = new BoardModel(_size); }
                source.TacticalSnapshotCopies++;
                // 先撤销上一候选占格，再覆盖棋子，最后Evaluate；反过来会让旧眼身份或旧位置污染新评分。
                _trial.Reset(); _obsolete.Clear();
                // 只保留当前在场身份，历史死棋不能在长期缓存中积累；使用Id字典O(1)检查。
                foreach (int id in _copies.Keys) { if (source.FindPiece(id) == null) { _obsolete.Add(id); } }
                foreach (int id in _obsolete) { _copies.Remove(id); }
                foreach (Piece original in source.Pieces)
                {
                    if (!_copies.TryGetValue(original.Id, out Piece copy) || copy.Side != original.Side || copy.Size != original.Size || copy.IsBoss != original.IsBoss || copy.IsKing != original.IsKing)
                    { copy = original.CreateSnapshot(); _copies[original.Id] = copy; }
                    else { copy.CopySnapshotState(original); source.TacticalPieceReuses++; }
                    if (!_trial.Place(copy, original.Pos, respectEnemyEyes: false)) { throw new InvalidOperationException("深快照占格复制失败"); }
                }
                for (int y = 0; y < _size; y++)
                {
                    for (int x = 0; x < _size; x++) { _trial._cells[x, y].EyeGarrisonId = source._cells[x, y].EyeGarrisonId; }
                }
                GoRules.Evaluate(_trial); return _trial;
            }
            internal Piece Added(Piece original)
            {
                if (_added == null || _added.Id != original.Id || _added.Side != original.Side || _added.Size != original.Size || _added.IsBoss != original.IsBoss || _added.IsKing != original.IsKing)
                { _added = original.CreateSnapshot(); }
                else { _added.CopySnapshotState(original); }
                return _added;
            }
        }
    }
}

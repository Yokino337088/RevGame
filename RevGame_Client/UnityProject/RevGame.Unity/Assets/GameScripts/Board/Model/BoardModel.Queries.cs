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
    // 本文件仍是BoardModel同一个类（partial表示拆文件），由战斗服务持有，随棋盘Reset清旧内容。
    // 职责是保存可复用查询工作区；不保存选敌评分、不替实战扣血，也不引入第二套棋盘规则。
    public sealed partial class BoardModel
    {
        // 一颗已入盘棋子的射界“计算收据”。除了有序坐标，还保存当时的输入版本。
        // 只有输入仍一致才允许复用；若只按Id保存结果，敌人移走后就会继续攻击旧位置。
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
        // 当前棋盘独占的Id→射界条目索引。最多对应在场身份，不与另一局或深试盘共享。
        private readonly Dictionary<int, AttackEntry> _attackCache = new Dictionary<int, AttackEntry>(81);
        // 每张棋盘最多81颗在场棋。移动与AI试盘复位时复用条目及其列表，不能因失效又每次new。
        private readonly Stack<AttackEntry> _freeAttackEntries = new Stack<AttackEntry>(81);
        /// <summary>累计命中/重算计数供摘要使用，不在热循环格式化字符串；Reset清零。</summary>
        public long AttackCacheHits
        {
            get; private set;
        }
        /// <summary>累计几何重算次数；可见/存活或拓扑变化后首次查询计一次，供实际命中率核对。</summary>
        public long AttackCacheMisses
        {
            get; private set;
        }
        /// <summary>独立试盘完整刷新次数，不等于候选个数；只在调用Trial时累计。</summary>
        public long TacticalSnapshotCopies
        {
            get; private set;
        }
        /// <summary>已存在的深副本完整覆盖次数；首次构造不计复用，避免虚报缓存收益。</summary>
        public long TacticalPieceReuses
        {
            get; private set;
        }
        // 外层是一盘的棋群，内层是同阵营四邻相连的棋子。只在同步评分期间借给AI读取。
        private readonly List<List<Piece>> _tacticalGroups = new List<List<Piece>>(81);
        // 旧棋群清空后存放在这里，复用List容量；入栈前Clear，避免池里仍引用死棋。
        private readonly Stack<List<Piece>> _freeGroups = new Stack<List<Piece>>(81);
        // 本次广度遍历的“已登记棋子”。大棋占多格，但同一个Piece只能进入棋群一次。
        private readonly HashSet<Piece> _groupVisited = new HashSet<Piece>();
        // 棋群建成时的占格版本；-1表示还没建。只有位置/成员变化才改变连通关系，掉血不用重建。
        private int _groupsTopology = -1;
        /// <summary>只供同步战术评分读取。调用者不得改名单；下一次拓扑变化重建，但列表对象和容量复用。</summary>
        // 调用者只借用结果，不能Add/Remove或保留到异步回调。拓扑变化后同一List会装入新棋群。
        // 返回顺序保持旧BuildGroups的根节点与四邻BFS顺序，避免AI同分候选因优化而换了决策。
        internal List<List<Piece>> TacticalGroups()
        {
            // 步骤1：盘面版本没变，直接使用已经建好的棋群，不再重新分配队列和成员列表。
            if (_groupsTopology == TopologyVersion)
            {
                return _tacticalGroups;
            }
            foreach (List<Piece> group in _tacticalGroups)
            {
                group.Clear();
                _freeGroups.Push(group);
            }
            _tacticalGroups.Clear();
            _groupVisited.Clear();
            // 步骤2：每颗未访问棋子当一个起点，使用group本身作为BFS队列。
            // 循环索引i逐个处理，新发现的同阵营邻居追加到末尾，随后也会被访问。
            foreach (Piece root in Pieces)
            {
                if (!_groupVisited.Add(root))
                {
                    continue;
                }
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
                            // 大棋不能只检查左下角：依次检查每段身体外沿的左、右、下、上格。
                            // 同一大棋的多个占格都指向同一个对象，HashSet去重保证成员不重复。
                            BoardPos pos = direction == 0 ? new BoardPos(p.Pos.X - 1, p.Pos.Y + edge)
                                : direction == 1 ? new BoardPos(p.Pos.X + p.Size, p.Pos.Y + edge)
                                : direction == 2 ? new BoardPos(p.Pos.X + edge, p.Pos.Y - 1) : new BoardPos(p.Pos.X + edge, p.Pos.Y + p.Size);
                            Piece other = GetPiece(pos);

                            if (other != null && other.Side == p.Side && _groupVisited.Add(other))
                            {
                                group.Add(other);
                            }
                        }
                    }
                }
                _tacticalGroups.Add(group);
            }
            _groupVisited.Clear();
            _groupsTopology = TopologyVersion;
            return _tacticalGroups;
        }
        // 试算工作区归源棋盘持有；首次需要才创建。实盘与试盘是两个对象，不是共享可变草稿。
        private TacticsWorkspace _tactics;
        // 惰性创建：完全不做试算的盘面不支付深试盘与辅助数组开销。
        internal TacticsWorkspace Tactics => _tactics ?? (_tactics = new TacticsWorkspace(Size));
        // 移除某颗棋时只撤销它自己的索引，并把清空的条目归还缓存池。
        // 其它条目不必逐一删除：下一次读取会因为TopologyVersion变化而重算。
        internal void ForgetQuery(int id)
        {
            if (!_attackCache.TryGetValue(id, out AttackEntry entry))
            {
                return;
            }
            _attackCache.Remove(id);
            entry.Cells.Clear();
            entry.Definition = null;
            entry.Topology = -1;
            _freeAttackEntries.Push(entry);
        }
        // 回合重置时清名单和旧资源引用，但保留容器容量；这样既不串局，也减少下局重新扩容。
        // _tactics.Clear还会清试盘成员；它不能只清计数，否则旧棋仍可能被引用。
        private void ClearQueryCache()
        {
            foreach (AttackEntry entry in _attackCache.Values)
            {
                entry.Cells.Clear();
                entry.Definition = null;
                entry.Topology = -1;
                _freeAttackEntries.Push(entry);
            }
            _attackCache.Clear();
            AttackCacheHits = AttackCacheMisses = 0;
            TargetVersion++;
            // 清旧棋引用但保留容量：新回合第一次查询会按新Topology重建，长期试盘也不会持有已离场棋子。
            foreach (List<Piece> group in _tacticalGroups)
            {
                group.Clear();
                _freeGroups.Push(group);
            }
            _tacticalGroups.Clear();
            _groupVisited.Clear();
            _groupsTopology = -1;
            _tactics?.Clear();
        }
        // 需要“有几颗”时只累计，不先new List再取Count。保持原GetPiecesOf语义，包含尚未撤盘的死亡棋。
        internal int CountSide(GameSide side)
        {
            int n = 0;
            foreach (Piece p in Pieces)
            {
                if (p.Side == side)
                {
                    n++;
                }
            }
            return n;
        }
        // 输入actor必须来自当前盘，result由调用者预留并独占。未入盘临时棋走原计算，避免相同Id覆盖实棋缓存。
        // 命中后也复制坐标到result，调用者可排序或删候选，但不能改到缓存本体。
        internal void ReadAttackTargets(Piece actor, List<BoardPos> result)
        {
            // 临时尚未入盘棋子不可污染Id缓存，供布阵评分等兼容查询使用。
            if (FindPiece(actor.Id) != actor)
            {
                MoveRules.CalculateAttackTargets(this, actor, result);
                return;
            }
            if (!_attackCache.TryGetValue(actor.Id, out AttackEntry entry))
            {
                entry = _freeAttackEntries.Count > 0 ? _freeAttackEntries.Pop() : new AttackEntry();
                _attackCache.Add(actor.Id, entry);
            }
            // 步骤1：收集本次查询真正影响射界的输入。CanAttack包括存活/隐藏/冻结/敌方锁死；Def更换代表升星。
            // 普通非致死扣血不改变几何，故不使所有射界重算；威胁和伤害评分由调用方读取当前血量。
            bool allowed = MoveRules.CanAttack(actor);
            int range = Balance.EnemyArcherRange;

            // 步骤2：完整输入相同才计命中。只缓存几何和顺序，不缓存“最值得攻击谁”。
            // 任何一个版本或资格不同都执行原算法，再更新条目；不能为了命中率故意漏掉失效条件。
            if (entry.Topology == TopologyVersion && entry.Targets == TargetVersion && entry.Definition == actor.Def && entry.Allowed == allowed && entry.Range == range)
            {
                AttackCacheHits++;
            }
            else
            {
                AttackCacheMisses++;
                MoveRules.CalculateAttackTargets(this, actor, entry.Cells);
                entry.Topology = TopologyVersion;
                entry.Targets = TargetVersion;
                entry.Definition = actor.Def;
                entry.Allowed = allowed;
                entry.Range = range;
            }
            // 步骤3：清结果的旧内容，复用它已分配的容量，再复制有序坐标。Clear不会把List容量释放。
            result.Clear();
            result.AddRange(entry.Cells);
        }
        /// <summary>每张源棋盘单独持有一份深试盘与列表，查询结束副本可变，下一轮必须全字段覆盖。</summary>
        // 单张源棋盘的同步试算工具箱：深试盘、棋子副本和多个职责缓冲。
        // 深复制表示位置/血量/护盾等可变字段独立；Def是只读图鉴，允许共享。禁止将本工作区传给异步表现。
        internal sealed class TacticsWorkspace
        {
            // 试算攻击候选坐标缓冲，容量预留81格；每次算法主动清空，不跨候选累加。
            internal readonly List<BoardPos> Hits = new List<BoardPos>(81);
            // Allies存敌方友军，Players存玩家，BossVictims存一次Boss评分目标。
            // 拆成三个列表是防止辅助查询把外层仍在遍历的名单清掉，而不是三份全局缓存。
            internal readonly List<Piece> Allies = new List<Piece>(81), Players = new List<Piece>(81), BossVictims = new List<Piece>(81);
            // 整队名单与移动/补子评分名单分开，嵌套辅助查询不会覆盖正在遍历的候选。
            // 远程整队的实盘候选名单，按Id稳定排序；与移动评分的Allies/Hits分开。
            internal readonly List<Piece> FormationRanged = new List<Piece>(81);
            // 上面远程名单对应的试盘副本；移动副本不会改实棋，最终只输出Id和目标格。
            internal readonly List<Piece> FormationCopies = new List<Piece>(81);
            // 本次已与阵型相连的Id集合；GrowConnected迭代扩展，下一次Plan必须Clear。
            internal readonly HashSet<int> FormationConnected = new HashSet<int>();
            // 追击距离场BFS队列，容量预留整盘格数；每次搜索先Clear，距离单位为可走的格步。
            internal readonly Queue<BoardPos> Queue = new Queue<BoardPos>(81);
            // 二维最短路径步数表；int.MaxValue表示不可达，0表示已到目标身体外沿。
            // 数组复用，但每次BuildPursuitDistances会覆盖旧值，不能把上次路径当当前路径。
            internal readonly int[,] Distances;
            // 源盘边长（格），构造后不变；决定试盘和距离数组尺寸，不是玩家攻击范围参数。
            private readonly int _size;
            // _trial与_copies只能由当前源盘使用；不交给动画/异步回调，不在候选评估中访问Unity对象。
            private BoardModel _trial;
            // 源棋Id→深副本。只保留当前源盘身份，旧死棋在下一次Trial中删除，Reset时全部清掉。
            private readonly Dictionary<int, Piece> _copies = new Dictionary<int, Piece>(81);
            // 遍历_copies时先登记失效Id，遍历结束再Remove；直接遍历中修改Dictionary会抛异常。
            private readonly List<int> _obsolete = new List<int>(81);
            // 一次补子评分的临时新棋副本。Id/阵营/大小等身份不同时重建，不能复用成另一颗棋。
            private Piece _added;
            // 构造仅创建辅助数组和容器；真正深试盘仍延后到首次Trial，避免完全不试算的战斗额外占内存。
            internal TacticsWorkspace(int size)
            {
                _size = size;
                Distances = new int[size, size];
            }
            /// <summary>源棋盘回合重置/离场时断开全部旧棋引用，保留容器容量与试盘本身，防止串局。</summary>
            internal void Clear()
            {
                _trial?.Reset();
                _copies.Clear();
                _obsolete.Clear();
                _added = null;
                Hits.Clear();
                Allies.Clear();
                Players.Clear();
                BossVictims.Clear();
                Queue.Clear();
                FormationRanged.Clear();
                FormationCopies.Clear();
                FormationConnected.Clear();
            }
            // 每轮试算入口：把源盘当前状态完整覆盖到独立试盘，返回可修改副本。
            // 身份不匹配重建Piece，匹配就CopySnapshotState；任何占格复制失败立即抛错，不能给AI残缺盘面。
            internal BoardModel Trial(BoardModel source)
            {
                if (_trial == null)
                {
                    _trial = new BoardModel(_size);
                }
                source.TacticalSnapshotCopies++;
                // 先撤销上一候选占格，再覆盖棋子，最后Evaluate；反过来会让旧眼身份或旧位置污染新评分。
                _trial.Reset();
                _obsolete.Clear();
                // 只保留当前在场身份，历史死棋不能在长期缓存中积累；使用Id字典O(1)检查。
                // 先剔除已经离开实盘的身份。只保留容量和有效副本，不允许“为了缓存”让每个历史棋子永久常驻。
                foreach (int id in _copies.Keys)
                {
                    if (source.FindPiece(id) == null)
                    {
                        _obsolete.Add(id);
                    }
                }
                foreach (int id in _obsolete)
                {
                    _copies.Remove(id);
                }
                // 再逐棋覆盖全部可变状态并按原顺序Place；不能只覆盖Pos/Hp，护盾、冻结、驻眼和行动进度也影响评分。
                foreach (Piece original in source.Pieces)
                {
                    if (!_copies.TryGetValue(original.Id, out Piece copy) || copy.Side != original.Side || copy.Size != original.Size || copy.IsBoss != original.IsBoss || copy.IsKing != original.IsKing)
                    {
                        copy = original.CreateSnapshot();
                        _copies[original.Id] = copy;
                    }
                    else
                    {
                        copy.CopySnapshotState(original);
                        source.TacticalPieceReuses++;
                    }
                    if (!_trial.Place(copy, original.Pos, respectEnemyEyes: false))
                    {
                        throw new InvalidOperationException("深快照占格复制失败");
                    }
                }
                // 最后复制每格驻眼身份，再完整Evaluate气眼。驻眼Id不能按落子顺序重新猜，否则试盘规则与实盘不同。
                for (int y = 0; y < _size; y++)
                {
                    for (int x = 0; x < _size; x++)
                    {
                        _trial._cells[x, y].EyeGarrisonId = source._cells[x, y].EyeGarrisonId;
                    }
                }
                GoRules.Evaluate(_trial);
                return _trial;
            }
            // 补子候选也必须深复制；候选循环可多次Place/Remove这个副本，但不能将original入试盘来试放。
            internal Piece Added(Piece original)
            {
                if (_added == null || _added.Id != original.Id || _added.Side != original.Side || _added.Size != original.Size || _added.IsBoss != original.IsBoss || _added.IsKing != original.IsKing)
                {
                    _added = original.CreateSnapshot();
                }
                else
                {
                    _added.CopySnapshotState(original);
                }
                return _added;
            }
        }
    }
}

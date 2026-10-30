// ============================================================
// BoardModel.cs
// 棋盘是占格数据的唯一真源。大棋所有身体格指向同一对象，遍历名单时只算一次。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Board
{
    /// <summary>
    /// 棋盘数据模型。
    ///
    /// 【为什么这么设计】（这是相对原项目最重要的修正之一）
    /// 原项目用一维数组 + 世界坐标反算格子（`Mathf.RoundToInt((pos - offset) / cellSize)`），
    /// 每次查格子都要浮点运算，还容易因为"0.5 格"产生错位；所有逻辑又全塞在一个 900 行的管理器里。
    ///
    /// 这里改成：
    /// - **二维数组** `_cells[X, Y]` 作为唯一真源 —— 查格子就是 `_cells[x, y]`，O(1) 且无浮点；
    /// - **字典** `_piecesByPos` 做"坐标 → 棋子"的快速索引（规则层做连通块/气计算时高频用）；
    /// - 世界坐标只在**表现层**用一次（摆 GameObject），逻辑层永远只认格子坐标。
    ///
    /// 一句话：**数据（本类）与规则（GoRules）与表现（Battle 层的 View）三层分开**。
    /// </summary>
    public sealed partial class BoardModel
    {
        private readonly int _size;
        private readonly Cell[, ] _cells;
        private readonly Dictionary<BoardPos, Piece> _piecesByPos = new Dictionary<BoardPos, Piece>();
        // 今日新增的身份索引：把Id映射到当前在场Piece，攻击/治疗/AI不必每次线性扫描所有棋子。
        // 索引只是查找加速，真实占格仍由_cells和Place/Move/Remove/Reset维护；这些入口必须一起更新。
        private readonly Dictionary<int, Piece> _piecesById = new Dictionary<int, Piece>(81);
        /// <summary>目标可见/存活版本；普通非致死扣血不影响射界，隐藏/致死即时失效。</summary>
        internal int TargetVersion { get; private set; }
        // 隐藏、死亡或复活通知射界缓存输入发生变化；只增加版本号，不在伤害回调里立刻重算全盘。
        internal void TargetStateChanged() { TargetVersion++; }
        /// <summary>占格版本：只有落子、移走、重置才改变。普通掉血不改变气眼，服务据此跳过重复计算。</summary>
        public int TopologyVersion { get; private set; }
        // 每张棋盘独占草稿区，AI快照不能和实战共用，否则试算会覆盖实战的遍历状态。
        internal readonly GoRules.Workspace RuleWorkspace;
        internal Dictionary<BoardPos, Piece>.ValueCollection Pieces => _piecesByPos.Values;
        public int Size
        {
            get
            {
                return _size;
            }
        }

        public BoardModel(int size)
        {
            _size = size;
            _cells = new Cell[size, size];
            RuleWorkspace = new GoRules.Workspace(size * size);
            BuildCells();
        }

        /// <summary>9×9 棋盘的 5 个星位（四角小目 + 天元）。</summary>
        private static bool IsStarPoint(int x, int y, int size)
        {
            if (size != 9)
            {
                return x == size / 2 && y == size / 2;
            }

            bool edge = (x == 2 || x == 6);
            bool mid = x == 4;
            return (edge && (y == 2 || y == 6)) || (mid && y == 4);
        }

        private void BuildCells()
        {
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    _cells[x, y] = new Cell(new BoardPos(x, y), IsStarPoint(x, y, _size));
                }
            }
        }

        /// <summary>清空棋盘（新的一局用）。</summary>
        // 生命周期入口：先断开Piece.QueryBoard归属，再清位置/Id索引和缓存；最后撤去所有格子占用与眼标记。
        // 清缓存保留容量，清棋子引用防串局。Reset不表示玩家通关，也不能代替退出流程的事件/动画回收。
        public void Reset()
        {
            foreach (Piece piece in _piecesByPos.Values) { piece.IsEyeGarrison = false; piece.HasEyeGuardAura = false; }
            foreach (Piece piece in _piecesByPos.Values) { piece.QueryBoard = null; }
            _piecesByPos.Clear(); _piecesById.Clear(); ClearQueryCache();
            TopologyVersion++;
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    _cells[x, y].Occupant = null;
                    _cells[x, y].IsEyeMarked = false;
                    _cells[x, y].EyeSide = null;
                    _cells[x, y].EyeGarrisonId = 0;
                }
            }
        }

        /// <summary>按坐标取格子；越界返回 null（调用方判空，不抛异常）。</summary>
        public Cell GetCell(BoardPos pos)
        {
            if (!pos.IsInside(_size))
            {
                return null;
            }

            return _cells[pos.X, pos.Y];
        }

        /// <summary>取格子上的棋子（空/越界都返回 null）。</summary>
        public Piece GetPiece(BoardPos pos)
        {
            Cell cell = GetCell(pos);
            return cell == null ? null : cell.Occupant;
        }

        /// <summary>这一格能不能落子。</summary>
        public bool CanPlace(BoardPos pos)
        {
            Cell cell = GetCell(pos);
            return cell != null && cell.IsEmpty;
        }

        /// <summary>落子。成功返回 true；越界或已被占返回 false（**并且不改任何状态**）。</summary>
        public bool Place(Piece piece, BoardPos pos, bool respectEnemyEyes = true)
        {
            if (piece == null || _piecesById.ContainsKey(piece.Id) || !CanFit(pos, piece.Size, piece.Side, respectEnemyEyes: respectEnemyEyes))
            {
                return false;
            }

            for (int y = 0; y < piece.Size; y++)
            {
                for (int x = 0; x < piece.Size; x++)
                {
                    Cell cell = _cells[pos.X + x, pos.Y + y];
                    // 记录落子时的空眼来源供旧快照兼容；有效驻眼由GoRules全盘按当前几何重算，不再依赖此历史标记。
                    cell.EyeGarrisonId = piece.Size == 1 && GoRules.IsEye(this, cell.Pos, piece.Side) ? piece.Id : 0;
                    cell.Occupant = piece;
                }
            }

            _piecesByPos[pos] = piece;
            // 完整占格预检通过后才登记Id与归属。若允许同Id两棋，FindPiece会指错目标，故上方先拒绝重复Id。
            _piecesById.Add(piece.Id, piece); piece.QueryBoard = this;
            piece.Pos = pos;
            TopologyVersion++;
            return true;
        }

        /// <summary>把棋子从棋盘上拿走（死亡/收回手牌）。成功返回 true。</summary>
        public bool Remove(Piece piece)
        {
            if (piece == null)
            {
                return false;
            }

            Cell cell = GetCell(piece.Pos);
            if (cell == null || cell.Occupant != piece)
            {
                return false;
            }

            for (int y = 0; y < piece.Size; y++)
            {
                for (int x = 0; x < piece.Size; x++)
                {
                    _cells[piece.Pos.X + x, piece.Pos.Y + y].Occupant = null;
                    _cells[piece.Pos.X + x, piece.Pos.Y + y].EyeGarrisonId = 0;
                }
            }

            _piecesByPos.Remove(piece.Pos);
            // 格子撤销后同步删除Id；QueryBoard置空让离盘棋子的后续状态变化不能污染当前盘缓存。
            _piecesById.Remove(piece.Id); piece.QueryBoard = null;
            ForgetQuery(piece.Id);
            piece.IsEyeGarrison = false; piece.HasEyeGuardAura = false;
            TopologyVersion++;
            return true;
        }

        /// <summary>移动棋子（目标格必须为空）。成功返回 true。</summary>
        public bool Move(Piece piece, BoardPos to, bool respectEnemyEyes = true)
        {
            if (piece == null)
            {
                return false;
            }

            if (!CanFit(to, piece.Size, piece.Side, piece, respectEnemyEyes: respectEnemyEyes))
            {
                return false;
            }

            Cell from = GetCell(piece.Pos);
            if (from == null || from.Occupant != piece)
            {
                return false;
            }
            // 原位操作没有拓扑变化，也不应丢失暂时破眼期间保留的驻守身份。
            if (to == piece.Pos) { return true; }

            // ★ 先把完整目标区域检查完，再整体搬移，不能先搬第一格、失败后留下半个身体。
            Remove(piece);
            return Place(piece, to, respectEnemyEyes);
        }

        /// <summary>检查整个占格；移动时忽略自己，离线预检也可显式忽略待替换对象。原位升星不改变占格。</summary>
        public bool CanFit(BoardPos pos, int size, GameSide side, Piece ignore = null, HashSet<Piece> consumed = null, bool respectEnemyEyes = true)
        {
            if (size < 1)
            {
                return false;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Cell cell = GetCell(new BoardPos(pos.X + x, pos.Y + y));
                    if (cell == null)
                    {
                        return false;
                    }

                    if (cell.Occupant != null && cell.Occupant != ignore && (consumed == null || !consumed.Contains(cell.Occupant)))
                    {
                        return false;
                    }

                    // 准备期按用户规则允许任何空格；战斗自动移动仍不能闯入对方眼位。
                    if (respectEnemyEyes && cell.EyeSide.HasValue && cell.EyeSide.Value != side)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>枚举身体外一圈的正交邻格，2×2 是 8 格、3×3 是 12 格；不会把身体内部当成气。</summary>
        public static IEnumerable<BoardPos> Boundary(Piece piece)
        {
            for (int i = 0; i < piece.Size; i++)
            {
                yield return new BoardPos(piece.Pos.X - 1, piece.Pos.Y + i);
                yield return new BoardPos(piece.Pos.X + piece.Size, piece.Pos.Y + i);
                yield return new BoardPos(piece.Pos.X + i, piece.Pos.Y - 1);
                yield return new BoardPos(piece.Pos.X + i, piece.Pos.Y + piece.Size);
            }
        }

        /// <summary>遍历所有棋子（规则层做连通块计算用）。</summary>
        public List<Piece> GetAllPieces()
        {
            List<Piece> list = new List<Piece>(_piecesByPos.Count);
            foreach (KeyValuePair<BoardPos, Piece> pair in _piecesByPos)
            {
                list.Add(pair.Value);
            }

            return list;
        }

        /// <summary>给布阵和走法评分使用的深快照；每颗棋子也独立复制，试算不会改变实战数据。</summary>
        public BoardModel CreateSnapshot()
        {
            var snapshot = new BoardModel(_size);
            foreach (Piece piece in _piecesByPos.Values)
            {
                snapshot.Place(piece.CreateSnapshot(), piece.Pos, respectEnemyEyes: false);
            }
            // 驻眼是落子时取得的身份，不取决于字典枚举顺序；试盘必须逐格复制，不能重新猜测。
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    snapshot._cells[x, y].EyeGarrisonId = _cells[x, y].EyeGarrisonId;
                }
            }
            GoRules.Evaluate(snapshot);
            return snapshot;
        }

        /// <summary>按编号找在场棋子，不临时创建全盘名单；高频受击、UI刷新和AI快照都走此入口。</summary>
        // 按身份字典直接查当前棋子，不再GetAllPieces后逐个比Id；不存在返回null，调用者要判空。
        // Move不会改变身份，升星仍是同一Piece，死亡到Remove之间也保留索引，与原规则生命周期一致。
        public Piece FindPiece(int id)
        {
            _piecesById.TryGetValue(id, out Piece piece);
            return piece;
        }

        /// <summary>把名单填进调用方复用的缓冲区。先清空，调用方不可在遍历时让同一缓冲区被重入覆盖。</summary>
        public void CopyPieces(List<Piece> destination, GameSide? side = null)
        {
            destination.Clear();
            foreach (Piece piece in _piecesByPos.Values)
            {
                if (!side.HasValue || piece.Side == side.Value)
                {
                    destination.Add(piece);
                }
            }
        }

        /// <summary>取某一方所有棋子。</summary>
        public List<Piece> GetPiecesOf(GameSide side)
        {
            List<Piece> list = new List<Piece>();
            foreach (KeyValuePair<BoardPos, Piece> pair in _piecesByPos)
            {
                if (pair.Value.Side == side)
                {
                    list.Add(pair.Value);
                }
            }

            return list;
        }

        /// <summary>某一方是否已经全灭（胜负判定用）。</summary>
        public bool IsSideWiped(GameSide side)
        {
            foreach (KeyValuePair<BoardPos, Piece> pair in _piecesByPos)
            {
                if (pair.Value.Side == side && pair.Value.IsAlive)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>导出成"字符画"，调试与日志用（比一堆断点直观）。</summary>
        public string Dump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int y = _size - 1; y >= 0; y--)
            {
                sb.Append("  ");
                for (int x = 0; x < _size; x++)
                {
                    Piece p = _cells[x, y].Occupant;
                    if (p == null)
                    {
                        sb.Append(_cells[x, y].IsStarPoint ? "+ " : ". ");
                    }
                    else
                    {
                        sb.Append(p.Side == GameSide.Player ? "P " : "E ");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}

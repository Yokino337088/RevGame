// ============================================================
// BoardView.cs —— 把棋盘数据画成玩家看得见、点得到的棋盘。
// BoardModel 是唯一棋盘：这里的字典仅保存「编号 → 图像」，不复制游戏规则。
// 棋子死亡、移动或换回合都重新对照模型，因此晚加载、漏过开局事件也不会显示旧棋子。
// ============================================================
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView : MonoBehaviour
    {
        private readonly Dictionary<int, float> _attackTextAt = new Dictionary<int, float>();
        private readonly List<BoardCellView> _cells = new List<BoardCellView>();
        // 长按范围要按坐标反查格子；81 格就算线性找也不慢，但波纹每圈要查很多次，字典更直接。
        private readonly Dictionary<BoardPos, BoardCellView> _cellByPos = new Dictionary<BoardPos, BoardCellView>();
        private readonly Dictionary<int, PieceView> _pieces = new Dictionary<int, PieceView>();
        // ---------------- 长按攻击范围（纯表现，不改任何规则数据） ----------------
        /// <summary>每向外扩一圈的间隔；圈数就是 BFS 距离，所以近处先亮、远处后亮。</summary>
        private const float RangeRingSeconds = 0.05f;
        private static readonly int[] RingDx =
        {
            1,
            -1,
            0,
            0
        };
        private static readonly int[] RingDy =
        {
            0,
            0,
            1,
            -1
        };
        private readonly List<BoardPos> _attackCoverage = new List<BoardPos>(Consts.BoardSize * Consts.BoardSize);
        private readonly List<BoardCellView> _rangeCells = new List<BoardCellView>();
        private readonly List<RevTimerHandle> _rangeTimers = new List<RevTimerHandle>();
        private readonly int[] _ringDistance = new int[Consts.BoardSize * Consts.BoardSize];
        private readonly Queue<BoardPos> _ringQueue = new Queue<BoardPos>(Consts.BoardSize * Consts.BoardSize);
        private bool _attackRangeVisible;
        // 规则层死亡后即从 _pieces 移除；原 PieceView 只在此列表多留一小段表现寿命，溶解完才回池。
        private readonly HashSet<PieceView> _dissolvingPieces = new HashSet<PieceView>();
        private readonly HashSet<int> _pendingPieces = new HashSet<int>();
        // 事件可能先于异步池对象到达；仅存棋子编号，不保存旧视图。升级优先于同帧的入场火焰。
        private readonly HashSet<int> _pendingBirths = new HashSet<int>();
        private readonly HashSet<int> _pendingUpgrades = new HashSet<int>();
        // 准备增益可能先派升星再派落子；前者若已在同步池命中时播过，后者不能又切回入场火焰。
        private readonly HashSet<int> _upgradeBeforeBirth = new HashSet<int>();
        private readonly List<int> _removedIds = new List<int>();
        private readonly Dictionary<int, int> _lastHp = new Dictionary<int, int>();
        private readonly HashSet<BattleEffectView> _effects = new HashSet<BattleEffectView>();
        private Transform _viewRoot;
        private bool _released;
        private int _generation;
        // 棋盘格跟随整场战斗，弹幕只属于当前阶段，必须分开编号。
        // 若复用 _generation，清弹幕时会误拦仍在加载的 81 个棋盘格，造成缺格。
        private int _effectGeneration;
        /// <summary>过场读取的装配状态；不是玩法阶段。失败必须显示原因，不能用“格子少几个”悄悄继续。</summary>
        public string LoadError { get; private set; }
        private bool _cellsReady;
        public bool IsReady { get { return _surfaceReady && _cellsReady && _pendingPieces.Count == 0 && _pieces.Count > 0; } }
        public int ActiveEffectCount { get { return _effects.Count; } }
        /// <summary>攻击范围是否正在显示；编辑器/自动化验收直接读，不看渲染器状态。</summary>
        public bool AttackRangeVisible { get { return _attackRangeVisible; } }
        /// <summary>本次已亮起的范围格数（波纹没走完时会小于最终覆盖数）。</summary>
        public int AttackRangeCellCount { get { return _rangeCells.Count; } }
        public int CellCount
        {
            get
            {
                return _cells.Count;
            }
        }

        public int PieceCount
        {
            get
            {
                return _pieces.Count;
            }
        }

        private void OnEnable()
        {
            _released = false;
            LoadError = null;
            _cellsReady = false;
            int generation = ++_generation;
            // ★ 池中实例放在独立容器，不挂到本组件的父链下。
            // Unity 停用 BoardView 时会调用 OnDisable；如果子棋子此时回池换父节点，Unity 会拒绝并报警。
            // 独立容器的子物体不参与那次停用遍历，便可先完整回池，再销毁这个空容器。
            _viewRoot = new GameObject("BattleViews").transform;
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnRound, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnRound, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPlaced, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMoved, OnPieceChanged, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceFormationMoved, OnFormationMoved, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceDied, OnDied, priority: 10, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceHpChanged, OnHpChanged, owner: this);
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, OnAttack, owner: this);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, OnDamage, priority: 10, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnMerged, owner: this);
            RevEvent.AddEventListener(Events.GoStateRefreshed, RefreshBoard, owner: this);
            RevEvent.AddEventListener(Events.SelectionChanged, RefreshCells, owner: this);
            RevEvent.AddEventListener(Events.HandChanged, RefreshCells, owner: this);
            ListenBossSkills();
            ListenCombatPacing();
            RequestBoardSurface();
            BuildCellsAsync(generation);
            RefreshBoard();
        }

        /// <summary>
        /// 一次创建81格会把Instantiate、Collider、Sprite装配堆在一帧，异步加载预制体也无法消除这个尖峰。
        /// 这里按行遍历，每批最多4格或耗时3毫秒就Yield；每格只刷新自己，末尾统一刷新全盘。
        /// generation保证离场后晚到结果回池，不会混进下一场的棋盘列表。
        /// </summary>
        private async void BuildCellsAsync(int generation)
        {
            try
            {
                float sliceStart = Time.realtimeSinceStartup;
                for (int index = 0; index < Consts.BoardSize * Consts.BoardSize; index++)
                {
                    if (_released || this == null || generation != _generation)
                    {
                        return;
                    }
                    BoardPos pos = new BoardPos(index % Consts.BoardSize, index / Consts.BoardSize);
                    var completion = new RevTaskCompletionSource<BoardCellView>();
                    GamePoolRequests.GetAsync<BoardCellView>(cell => completion.SetResult(cell), _viewRoot);
                    BoardCellView view = await completion.Task;
                    OnCellLoaded(view, pos, generation);
                    if (view == null)
                    {
                        return;
                    }
                    if (index % 4 == 3 || Time.realtimeSinceStartup - sliceStart >= 0.003f)
                    {
                        await RevTask.Yield();
                        sliceStart = Time.realtimeSinceStartup;
                    }
                }
                if (_released || this == null || generation != _generation)
                {
                    return;
                }
                _cellsReady = true;
                RefreshCells();
            }
            catch (System.Exception error)
            {
                LoadError = "棋盘分帧创建失败：" + error.Message;
                RevLog.Exception(error, LoadError, "RevGame");
            }
        }

        private void OnCellLoaded(BoardCellView view, BoardPos pos, int generation)
        {
            if (view == null)
            {
                LoadError = "棋盘格加载失败，请检查 BoardCell 预制体";
                RevLog.Error(LoadError, "RevGame");
                return;
            }

            if (_released || this == null || generation != _generation)
            {
                RevPool.Return(view, 0);
                return;
            }

            var cell = RevServices.GetRequired<IBattleService>().Board.GetCell(pos);
            view.Bind(pos, cell.IsStarPoint, OnCellClicked, OnCellLongPressed);
            if (_surfaceMaterial != null) { view.ApplySurface(_surfaceMaterial); }
            view.name = "格子_" + pos.X + "_" + pos.Y;
            _cells.Add(view);
            _cellByPos[pos] = view;
            // 构建期间没有玩家输入，最后统一RefreshCells；避免1+2+…+81次重复遍历。
        }

        /// <summary>真实鼠标/触屏点击和自动验收走同一个入口，表现层不直接改棋盘。</summary>
        public void OnCellClicked(BoardPos pos)
        {
            // 全屏UI遮挡鼠标/触屏，业务门禁再保护GM与物理射线调用，不能在半成品棋盘上提前落子。
            if (RevServices.GetRequired<Flow.GameFlow>().IsGameplayBlocked)
            {
                return;
            }
            // ★ 范围显示期间的这一次点击只负责收起：否则"点一下取消"会顺手把牌落下去/把棋子换位。
            if (_attackRangeVisible)
            {
                HideAttackRange();
                return;
            }

            Flow.BattleLogic logic = RevLogic.Get<Flow.BattleLogic>();
            if (logic == null)
            {
                return;
            }

            logic.ClickBoard(pos);
        }

        /// <summary>
        /// 长按棋盘格 = 查看站在这一格上的棋子能打到哪。
        /// 只读规则、只画表现，不改变选中、不消耗出牌额度。
        /// </summary>
        private void OnCellLongPressed(BoardPos pos)
        {
            if (RevServices.GetRequired<Flow.GameFlow>().IsGameplayBlocked)
            {
                return;
            }
            // 已经显示时再长按只收起，不切换到另一颗棋子的范围（"任意点击都取消"的同一语义）。
            if (_attackRangeVisible)
            {
                HideAttackRange();
                return;
            }

            Piece piece = RevServices.GetRequired<IBattleService>().Board.GetPiece(pos);
            if (piece == null || !piece.IsAlive)
            {
                return;
            }
            ShowAttackRange(piece.Id);
        }

        /// <summary>点击棋盘之外的任意位置（背景/HUD）也收起范围，和点击棋盘格保持同一手感。</summary>
        private void Update()
        {
            AnimateBoardEyes();
            if (!_attackRangeVisible || !Input.GetMouseButtonDown(0))
            {
                return;
            }

            // 点在棋盘格上时交给 BoardCellView → OnCellClicked 处理；这里再收一次会变成"先收起、随后又落子"。
            if (IsPointerOnBoardCell())
            {
                return;
            }
            HideAttackRange();
        }

        private static bool IsPointerOnBoardCell()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return false;
            }
            Vector3 world = camera.ScreenToWorldPoint(Input.mousePosition);
            Collider2D hit = Physics2D.OverlapPoint(world);
            return hit != null && hit.GetComponentInParent<BoardCellView>() != null;
        }

        /// <summary>
        /// 显示某颗棋子的攻击覆盖格。返回 false 表示没有可显示的内容（棋子不存在/敌方被锁死/射界为空），
        /// 由调用方决定是否给玩家提示——表现层不吞掉"为什么没反应"。
        /// </summary>
        public bool ShowAttackRange(int pieceId)
        {
            if (_released)
            {
                return false;
            }
            BoardModel board = RevServices.GetRequired<IBattleService>().Board;
            Piece piece = board.FindPiece(pieceId);
            if (piece == null || !piece.IsAlive)
            {
                RevLog.Debug("攻击范围：棋子不存在或已死亡 #" + pieceId, "RevGame");
                return false;
            }

            HideAttackRange();
            MoveRules.GetAttackCoverage(board, piece, _attackCoverage);
            if (_attackCoverage.Count == 0)
            {
                RevLog.Debug("攻击范围：棋子 #" + pieceId + " 没有可覆盖的格子（可能是敌方被锁死或射界被挡）", "RevGame");
                return false;
            }

            BuildRingDistances(piece);
            int size = Consts.BoardSize;
            int maxRing = 0;
            for (int i = 0; i < _attackCoverage.Count; i++)
            {
                int distance = _ringDistance[_attackCoverage[i].ToIndex(size)];
                if (distance > maxRing)
                {
                    maxRing = distance;
                }
            }

            // 先置为显示，再按圈排队：即使后面的圈还在等计时器，这期间的点击也应该只收起范围。
            _attackRangeVisible = true;
            for (int ring = 1; ring <= maxRing; ring++)
            {
                List<BoardCellView> views = CollectRangeRing(ring);
                if (views.Count == 0)
                {
                    continue;
                }
                float delay = (ring - 1) * RangeRingSeconds;
                if (delay <= 0f)
                {
                    PlayRangeRing(views);
                }
                else
                {
                    // owner: this → 退出战斗时随 OnDisable 一起停，不会在下一场棋盘上突然亮格。
                    _rangeTimers.Add(RevTimer.After(delay, () => PlayRangeRing(views), owner: this));
                }
            }
            RevLog.Debug("攻击范围：棋子 #" + pieceId + " 覆盖 " + _attackCoverage.Count + " 格，共 " + maxRing + " 圈", "RevGame");
            return true;
        }

        public void HideAttackRange()
        {
            for (int i = 0; i < _rangeTimers.Count; i++) { _rangeTimers[i].Stop(); }
            _rangeTimers.Clear();
            for (int i = 0; i < _rangeCells.Count; i++)
            {
                if (_rangeCells[i] != null)
                {
                    _rangeCells[i].HideRange();
                }
            }
            _rangeCells.Clear();
            _attackRangeVisible = false;
        }

        /// <summary>
        /// 以棋子身体为起点做一次 4 邻域 BFS，得到每格到棋子的最短步数 —— 这就是"水波扩散"的圈号。
        /// 不用曼哈顿距离而是真 BFS：以后若棋盘出现障碍/异形区域，圈号仍然正确。
        /// 遍历不看棋子占格：波纹是视觉层次，不应该被别的棋子切断。
        /// </summary>
        private void BuildRingDistances(Piece piece)
        {
            int size = Consts.BoardSize;
            for (int i = 0; i < _ringDistance.Length; i++) { _ringDistance[i] = -1; }
            _ringQueue.Clear();
            for (int y = 0; y < piece.Size; y++)
            {
                for (int x = 0; x < piece.Size; x++)
                {
                    var pos = new BoardPos(piece.Pos.X + x, piece.Pos.Y + y);
                    if (!pos.IsInside(size))
                    {
                        continue;
                    }
                    int index = pos.ToIndex(size);
                    if (_ringDistance[index] >= 0)
                    {
                        continue;
                    }
                    _ringDistance[index] = 0;
                    _ringQueue.Enqueue(pos);
                }
            }

            while (_ringQueue.Count > 0)
            {
                BoardPos current = _ringQueue.Dequeue();
                int next = _ringDistance[current.ToIndex(size)] + 1;
                for (int i = 0; i < 4; i++)
                {
                    var neighbor = new BoardPos(current.X + RingDx[i], current.Y + RingDy[i]);
                    if (!neighbor.IsInside(size))
                    {
                        continue;
                    }
                    int index = neighbor.ToIndex(size);
                    if (_ringDistance[index] >= 0)
                    {
                        continue;
                    }
                    _ringDistance[index] = next;
                    _ringQueue.Enqueue(neighbor);
                }
            }
        }

        /// <summary>取出覆盖格里距离正好等于 ring 的那些格；格子还没加载完时直接跳过。</summary>
        private List<BoardCellView> CollectRangeRing(int ring)
        {
            int size = Consts.BoardSize;
            var views = new List<BoardCellView>();
            for (int i = 0; i < _attackCoverage.Count; i++)
            {
                BoardPos pos = _attackCoverage[i];
                if (_ringDistance[pos.ToIndex(size)] != ring)
                {
                    continue;
                }
                BoardCellView view;
                if (_cellByPos.TryGetValue(pos, out view) && view != null)
                {
                    views.Add(view);
                }
            }
            return views;
        }

        private void PlayRangeRing(List<BoardCellView> views)
        {
            for (int i = 0; i < views.Count; i++)
            {
                BoardCellView view = views[i];
                if (view == null)
                {
                    continue;
                }
                view.SetRange(true);
                view.PlayRangePulse();
                _rangeCells.Add(view);
            }
        }

        private Piece FindPiece(int id)
        {
            var board = RevServices.GetRequired<IBattleService>().Board;
            // 受击高频触发；按Id查询不再临时复制一整张棋盘名单。
            Piece piece = board.FindPiece(id);
            return piece != null && piece.IsAlive ? piece : null;
        }

        private void RefreshBoard()
        {
            if (_released)
            {
                return;
            }

            _removedIds.Clear();
            foreach (var pair in _pieces)
            {
                if (FindPiece(pair.Key) == null)
                {
                    _removedIds.Add(pair.Key);
                }
            }

            foreach (int id in _removedIds)
            {
                RevPool.Return(_pieces[id], 0);
                _pieces.Remove(id);
                _lastHp.Remove(id);
            }

            foreach (Piece piece in RevServices.GetRequired<IBattleService>().Board.Pieces)
            {
                PieceView view;
                if (_pieces.TryGetValue(piece.Id, out view))
                {
                    RefreshPiece(view, piece);
                    _lastHp[piece.Id] = piece.Hp;
                }
                else if (_pendingPieces.Add(piece.Id))
                {
                    int id = piece.Id;
                    int generation = _generation;
                    GamePoolRequests.GetAsync<PieceView>(loaded => OnPieceLoaded(loaded, id, generation), _viewRoot);
                }
            }

            RefreshCells();
        }

        private void OnPieceLoaded(PieceView view, int id, int generation)
        {
            if (view == null)
            {
                LoadError = "棋子预制体加载失败，编号 " + id;
                _pendingPieces.Remove(id);
                RevLog.Error(LoadError, "RevGame");
                return;
            }

            // ★ 加载结束前可能已经死亡、换章或退出场景。此时归还对象，不能把过期棋子画回来。
            if (_released || this == null || generation != _generation)
            {
                RevPool.Return(view, 0);
                return;
            }

            _pendingPieces.Remove(id);
            Piece piece = FindPiece(id);
            if (piece == null)
            {
                _pendingBirths.Remove(id);
                _pendingUpgrades.Remove(id);
                RevPool.Return(view, 0);
                return;
            }

            _pieces.Add(id, view);
            _lastHp[id] = piece.Hp;
            view.Bind(id, piece.Def.Kind, piece.Def.Star, piece.Side);
            RefreshPiece(view, piece);
            PlayPendingPieceVisual(id, view);
        }

        private void PlayPendingPieceVisual(int id, PieceView view)
        {
            // 星级升级可能在落子事件之前触发；只有新帧绑定好、尺寸落定后才播，不能闪回旧星级。
            if (_pendingUpgrades.Remove(id))
            {
                _pendingBirths.Remove(id);
                view.PlayUpgradeStorm();
            }
            else if (_pendingBirths.Remove(id))
            {
                view.PlayBirthFire();
            }
        }

        private static void RefreshPiece(PieceView view, Piece piece)
        {
            Vector3 position = piece.Pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            // Pos 指身体左下格，图像要放在整个占格的中心，而不是压在左下角那一格。
            position += new Vector3(piece.Size - 1, piece.Size - 1, 0f) * (Consts.CellSize * 0.5f);
            view.MoveTo(position);
            // 原位升级保留Id，所以每次同步检查外观；Bind内部只在种类/星级真的变化时重绑。
            view.Bind(piece.Id, piece.Def.Kind, piece.Def.Star, piece.Side);
            view.SetBodySize(piece.Size, piece.IsBoss, piece.IsKing);
            view.SetHp(piece.HpRatio);
            view.SetLocked(piece.IsLocked);
            view.SetEyeGarrison(piece.IsEyeGarrison && piece.IsAlive);
        }

        private void RefreshCells()
        {
            if (_released)
            {
                return;
            }

            IBattleService battle = RevServices.GetRequired<IBattleService>();
            foreach (BoardCellView view in _cells)
            {
                // 教学也点真实BoardCellView；只收窄已有高亮，不画第二张UI棋盘。
                var lesson = RevLogic.Get<RevGame.Flow.TutorialLogic>();
                bool guided = lesson != null && lesson.Active;
                // 教学只显示浮层的柔和魔法边框，避免原落点贴图与边框叠加变亮。
                view.SetHighlight(guided ? false :
                    battle.CanPlaySelectedAt(view.Position) || (battle.SelectedPieceId != 0 && battle.Board.GetPiece(view.Position)?.Id == battle.SelectedPieceId));
                view.SetEye(battle.Board.GetCell(view.Position).IsEyeMarked);
            }
        }

        private void OnRound(int round)
        {
            RequestBoardSurface();
            // 换阶段后旧棋子的射界已经过期，必须先收起再清弹幕。
            HideAttackRange();
            ClearRoundEffects();
            RefreshBoard();
        }

        private void OnSettle(GameSide side)
        {
            HideAttackRange();
            ClearRoundEffects();
            RefreshBoard();
        }

        /// <summary>阶段交接时立即收走旧弹幕，不等待动画自然结束；暂停的动画也必须清除。</summary>
        private void ClearRoundEffects()
        {
            // 先让旧异步回调失效，再清伤害合并队列、显示预算和所属延时操作。
            // 仅归还已显示的物体还不够：已发出的对象池加载请求稍后返回时也必须核对批次。
            _effectGeneration++;
            RevTimer.CancelAllOf(this);
            ResetFeedbackBudget();
            foreach (PieceView view in _pieces.Values)
            {
                if (view != null)
                {
                    view.StopTransientVisuals();
                }
            }
            // 死亡可能是本阶段最后一击：阶段交接同帧清旧弹幕，但不能把原棋子的溶解也截掉。
            // 死棋早已脱离规则棋盘和 _pieces；仅离开战斗场景时 CancelDissolvingPieces 负责强制收尾。
            foreach (BattleEffectView effect in _effects)
            {
                if (effect != null)
                {
                    RevPool.Return(effect, 0);
                }
            }
            // OnPoolReturn 使用 Kill(false)，不会执行旧完成回调，也不会在遍历中修改本集合。
            _effects.Clear();
            ClearBossFx();
            ClearCombatPacing();
        }

        private void OnPlaced(int id)
        {
            if (_released)
            {
                return;
            }
            // 落子=占格变了，旧范围可能指向已经打不到的格子。
            HideAttackRange();
            if (!_upgradeBeforeBirth.Remove(id))
            {
                _pendingBirths.Add(id);
            }
            RefreshBoard();
            if (_pieces.TryGetValue(id, out PieceView view))
            {
                PlayPendingPieceVisual(id, view);
            }
        }

        private void OnPieceChanged(int id)
        {
            HideAttackRange();
            RefreshBoard();
        }

        /// <summary>服务已经提交完整阵型；先启动专用位移，随后的常规刷新看到同一目标不会重启动画。</summary>
        private void OnFormationMoved(int id)
        {
            Piece piece = FindPiece(id);
            if (_released || piece == null || !_pieces.TryGetValue(id, out PieceView view)) { return; }
            Vector3 destination = piece.Pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            destination += new Vector3(piece.Size - 1, piece.Size - 1, 0) * Consts.CellSize * 0.5f;
            view.MoveFormationTo(destination);
        }

        private void OnHpChanged(int id)
        {
            Piece p = FindPiece(id);
            PieceView view;
            if (_pieces.TryGetValue(id, out view))
            {
                view.SetHp(p == null ? 0f : p.HpRatio);
                if (p != null)
                {
                    _lastHp[id] = p.Hp;
                }
            }
        }

        private void OnAttack(PieceAttackInfo attack)
        {
            // ★ 口号和子弹受全场预算限制，但每颗真实出手的棋子仍要有本地动作。
            // 否则后排/新落的一星棋即使已经造成伤害，也会被玩家误认为“整场不攻击”。
            // 这里只播身体姿态，不移动根节点、不改占格、不再结算一次伤害。
            PieceView view;
            if (_pieces.TryGetValue(attack.PieceId, out view))
            {
                view.PlayAttackPulse();
            }
            QueueAttackText(attack);
        }

        /// <summary>预算预约成功后再取池对象；多个真实攻击口号错开播放，仍不更改任何模型伤害。</summary>
        private void ShowAttackText(PieceAttackInfo attack)
        {
            BattleText config = GameTextConfig.Require("Attack_" + attack.Kind);
            Vector3 position = attack.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            position += new Vector3(attack.Size - 1, attack.Size - 1, 0f) * Consts.CellSize * 0.5f;
            int generation = _effectGeneration;
            GamePoolRequests.GetAsync<BattleEffectView>(effect =>
            {
                if (_released || generation != _effectGeneration)
                {
                    if (effect != null)
                    {
                        RevPool.Return(effect, 0);
                    }
                    return;
                }
                _pendingTextLoads--;
                if (effect == null)
                {
                    RevLog.Warn("攻击文字加载失败", "RevGame"); return;
                }
                _effects.Add(effect);
                _textEffects.Add(effect);
                effect.ShowAttack(config, position, OnEffectFinished, ArrangeFloatingText);
            }, _viewRoot);
        }

        private void OnDamage(BattleHitInfo hit)
        {
            Vector3 target = hit.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            target += Vector3.one * ((hit.TargetSize - 1) * Consts.CellSize * 0.5f);
            target.z = 0f;
            Vector3 source = hit.HasAttacker ? hit.Source.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset) : target + Vector3.up * 2f;
            source += new Vector3(hit.SourceSize - 1, hit.SourceSize - 1, 0f) * Consts.CellSize * 0.5f;
            // 只为真实远程普攻发射职业弹道；近战和自带特效的Boss技能仅反馈真实伤害。
            if (hit.HasAttacker && hit.SourceIsRanged && !hit.FromSkill)
            {
                QueueProjectile(source, target, hit.TargetSide, hit.SourceId, hit.SourceKind);
            }
            else if (hit.HasAttacker && !hit.FromSkill && _meleeEffects.Count + _pendingMeleeLoads < MaxMeleeEffects)
            {
                _pendingMeleeLoads++;
                ShowEffect(BattleEffectKind.Melee, source, 0, hit.TargetSide, target, hit.SourceKind);
            }
            // ★ 伤害取自扣血当刻的快照，即使受击棋子已经死亡回池，这个数字仍能正常显示。
            // 只在同一画面帧内合并同目标连击；弹道和数字各自受显示预算约束，都不再创建每次命中独立的Timer。
            QueueDamageText(hit, target);
        }

        private void OnDied(int id)
        {
            HideAttackRange();
            _pendingBirths.Remove(id);
            _pendingUpgrades.Remove(id);
            _upgradeBeforeBirth.Remove(id);
            PieceView view;
            if (_pieces.TryGetValue(id, out view))
            {
                // 规则层已在发事件前移除占格；这里也立即摘出存活视图字典，但让原物体留场消融。
                // 切阶段保留这段短动画；完成或独立期限到达后归池，离场则由 CancelDissolvingPieces 收尾。
                _pieces.Remove(id);
                _lastHp.Remove(id);
                _dissolvingPieces.Add(view);
                if (!view.BeginDeathDissolve(OnDeathDissolved))
                {
                    _dissolvingPieces.Remove(view);
                    RevPool.Return(view, 0);
                    RevLog.Warn("棋子死亡溶解缺少身体帧或材质，编号 " + id, "RevGame");
                }
            }

            RefreshBoard();
        }

        private void OnDeathDissolved(PieceView view)
        {
            // 动画自然完成或表现期限到达都走这里；中途离场会取消两者，不允许旧回调归还新棋子。
            if (_dissolvingPieces.Remove(view))
            {
                RevPool.Return(view, 0);
            }
        }

        private void CancelDissolvingPieces()
        {
            foreach (PieceView view in _dissolvingPieces)
            {
                if (view != null)
                {
                    RevPool.Return(view, 0);
                }
            }
            // OnPoolReturn Kill(false)，不会重入 OnDeathDissolved 修改这个集合。
            _dissolvingPieces.Clear();
        }

        private void OnMerged(int id)
        {
            if (_released)
            {
                return;
            }
            // 升星会换攻击/射程，旧范围不再可信。
            HideAttackRange();
            // 若尚未见到落子事件，记下这次更高优先级的升级；同步池命中时可能在落子前已播完待请求。
            if (!_pendingBirths.Contains(id))
            {
                _upgradeBeforeBirth.Add(id);
            }
            // 升级改用原棋子的身体 Shader；BattleEffect 仍供伤害数字、攻击口号和弹道使用，不能删除整个预制体。
            _pendingUpgrades.Add(id);
            RefreshBoard();
            if (_pieces.TryGetValue(id, out PieceView view))
            {
                PlayPendingPieceVisual(id, view);
            }
        }

        private void ShowEffect(BattleEffectKind kind, Vector3 position, int damage, GameSide targetSide = GameSide.Enemy, Vector3 destination = default(Vector3), PieceKind sourceKind = PieceKind.Archer)
        {
            int generation = _effectGeneration;
            GamePoolRequests.GetAsync<BattleEffectView>(effect =>
            {
                if (effect == null)
                {
                    if (generation == _effectGeneration && kind == BattleEffectKind.Projectile)
                    {
                        _pendingProjectileLoads--;
                    }
                    if (generation == _effectGeneration && kind == BattleEffectKind.Melee) { _pendingMeleeLoads--; }
                    RevLog.Warn("战斗特效加载失败", "RevGame");
                    return;
                }

                if (_released || generation != _effectGeneration)
                {
                    RevPool.Return(effect, 0);
                    return;
                }

                _effects.Add(effect);
                if (kind == BattleEffectKind.Projectile)
                {
                    _pendingProjectileLoads--;
                    _projectileEffects.Add(effect);
                    effect.ShowProjectile(position, destination, OnEffectFinished, targetSide, sourceKind);
                }
                else if (kind == BattleEffectKind.Melee)
                {
                    _pendingMeleeLoads--;
                    _meleeEffects.Add(effect);
                    effect.ShowMelee(position, destination, sourceKind, OnEffectFinished);
                }
                else
                {
                    effect.Show(kind, position, damage, OnEffectFinished, targetSide);
                }
            }, _viewRoot);
        }

        private void OnEffectFinished(BattleEffectView effect)
        {
            _effects.Remove(effect);
            _textEffects.Remove(effect);
            _projectileEffects.Remove(effect);
            _meleeEffects.Remove(effect);
        }

        private void OnDisable()
        {
            _released = true;
            // 先收起范围（停波纹计时器、清黄格），再把棋子/格子归池；顺序反了会对已回池的对象调 Hide。
            HideAttackRange();
            RevEvent.RemoveAllByOwner(this);
            RevTimer.CancelAllOf(this);
            // Boss 技能特效先回池：它们挂在 _viewRoot 下，容器稍后销毁前必须全部归还。
            ClearBossFx();
            ClearCombatPacing();
            foreach (var view in _pieces.Values)
            {
                if (view != null)
                {
                    RevPool.Return(view, 0);
                }
            }

            foreach (var cell in _cells)
            {
                if (cell != null)
                {
                    RevPool.Return(cell, 0);
                }
            }

            ReleaseBoardSurface();
            _pieces.Clear();
            _cells.Clear();
            _cellByPos.Clear();
            _pendingPieces.Clear();
            _pendingBirths.Clear();
            _pendingUpgrades.Clear();
            _upgradeBeforeBirth.Clear();
            _lastHp.Clear();
            ClearRoundEffects();
            CancelDissolvingPieces();
            _generation++;
            RevPool.DestroyPool<PieceView>();
            RevPool.DestroyPool<BoardCellView>();
            RevPool.DestroyPool<BattleEffectView>();
            if (_viewRoot != null)
            {
                Destroy(_viewRoot.gameObject);
                _viewRoot = null;
            }
        }
    }
}

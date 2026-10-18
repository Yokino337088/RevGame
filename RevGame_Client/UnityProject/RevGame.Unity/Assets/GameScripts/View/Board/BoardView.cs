// ============================================================
// BoardView.cs —— 把棋盘数据画成玩家看得见、点得到的棋盘。
// BoardModel 是唯一棋盘：这里的字典仅保存「编号 → 图像」，不复制游戏规则。
// 棋子死亡、移动或换回合都重新对照模型，因此晚加载、漏过开局事件也不会显示旧棋子。
// ============================================================
using System.Collections.Generic;
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
        private readonly Dictionary<int, PieceView> _pieces = new Dictionary<int, PieceView>();
        private readonly HashSet<int> _pendingPieces = new HashSet<int>();
        private readonly List<int> _removedIds = new List<int>();
        private readonly Dictionary<int, int> _lastHp = new Dictionary<int, int>();
        private readonly HashSet<BattleEffectView> _effects = new HashSet<BattleEffectView>();
        private Transform _viewRoot;
        private bool _released;
        private int _generation;
        // 棋盘格跟随整场战斗，弹幕只属于当前阶段，必须分开编号。
        // 若复用 _generation，清弹幕时会误拦仍在加载的 81 个棋盘格，造成缺格。
        private int _effectGeneration;
        public int ActiveEffectCount { get { return _effects.Count; } }
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
            int generation = ++_generation;
            // ★ 池中实例放在独立容器，不挂到本组件的父链下。
            // Unity 停用 BoardView 时会调用 OnDisable；如果子棋子此时回池换父节点，Unity 会拒绝并报警。
            // 独立容器的子物体不参与那次停用遍历，便可先完整回池，再销毁这个空容器。
            _viewRoot = new GameObject("BattleViews").transform;
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnRound, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnRound, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPieceChanged, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMoved, OnPieceChanged, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceDied, OnDied, priority: 10, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceHpChanged, OnHpChanged, owner: this);
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, OnAttack, owner: this);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, OnDamage, priority: 10, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnMerged, owner: this);
            RevEvent.AddEventListener(Events.GoStateRefreshed, RefreshBoard, owner: this);
            RevEvent.AddEventListener(Events.SelectionChanged, RefreshCells, owner: this);
            RevEvent.AddEventListener(Events.HandChanged, RefreshCells, owner: this);
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    // ★ 每次循环保存一份坐标；异步回调执行时，循环变量早已走到末尾。
                    BoardPos pos = new BoardPos(x, y);
                    RevPool.GetAsync<BoardCellView>(cell => OnCellLoaded(cell, pos, generation), _viewRoot);
                }
            }

            RefreshBoard();
        }

        private void OnCellLoaded(BoardCellView view, BoardPos pos, int generation)
        {
            if (view == null)
            {
                RevLog.Error("棋盘格加载失败，请检查 BoardCell 预制体", "RevGame");
                return;
            }

            if (_released || this == null || generation != _generation)
            {
                RevPool.Return(view, 0);
                return;
            }

            var cell = RevServices.GetRequired<IBattleService>().Board.GetCell(pos);
            view.Bind(pos, cell.IsStarPoint, OnCellClicked);
            view.name = "格子_" + pos.X + "_" + pos.Y;
            _cells.Add(view);
            RefreshCells();
        }

        /// <summary>真实鼠标/触屏点击和自动验收走同一个入口，表现层不直接改棋盘。</summary>
        public void OnCellClicked(BoardPos pos)
        {
            Flow.BattleLogic logic = RevLogic.Get<Flow.BattleLogic>();
            if (logic == null)
            {
                return;
            }

            logic.ClickBoard(pos);
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
                    RevPool.GetAsync<PieceView>(loaded => OnPieceLoaded(loaded, id, generation), _viewRoot);
                }
            }

            RefreshCells();
        }

        private void OnPieceLoaded(PieceView view, int id, int generation)
        {
            if (view == null)
            {
                RevLog.Error("棋子预制体加载失败，编号 " + id, "RevGame");
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
                RevPool.Return(view, 0);
                return;
            }

            _pieces.Add(id, view);
            _lastHp[id] = piece.Hp;
            view.Bind(id, piece.Def.Kind, piece.Def.Star, piece.Side);
            RefreshPiece(view, piece);
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
                view.SetHighlight(battle.CanPlaySelectedAt(view.Position) || (battle.SelectedPieceId != 0 && battle.Board.GetPiece(view.Position)?.Id == battle.SelectedPieceId));
                view.SetEye(battle.Board.GetCell(view.Position).IsEyeMarked);
            }
        }

        private void OnRound(int round)
        {
            ClearRoundEffects();
            RefreshBoard();
        }

        private void OnSettle(GameSide side)
        {
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
            foreach (BattleEffectView effect in _effects)
            {
                if (effect != null) { RevPool.Return(effect, 0); }
            }
            // OnPoolReturn 使用 Kill(false)，不会执行旧完成回调，也不会在遍历中修改本集合。
            _effects.Clear();
        }

        private void OnPieceChanged(int id)
        {
            RefreshBoard();
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
            QueueAttackText(attack);
        }

        /// <summary>预算预约成功后再取池对象；多个真实攻击口号错开播放，仍不更改任何模型伤害。</summary>
        private void ShowAttackText(PieceAttackInfo attack)
        {
            BattleText config = GameTextConfig.Require("Attack_" + attack.Kind);
            Vector3 position = attack.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            position += new Vector3(attack.Size - 1, attack.Size - 1, 0f) * Consts.CellSize * 0.5f;
            int generation = _effectGeneration;
            RevPool.GetAsync<BattleEffectView>(effect =>
            {
                if (_released || generation != _effectGeneration) { if (effect != null) { RevPool.Return(effect, 0); } return; }
                _pendingTextLoads--;
                if (effect == null) { RevLog.Warn("攻击文字加载失败", "RevGame"); return; }
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
            QueueProjectile(source, target, hit.TargetSide);
            // ★ 伤害取自扣血当刻的快照，即使受击棋子已经死亡回池，这个数字仍能正常显示。
            // 用短窗口合并同目标连击；弹道和数字各自受显示预算约束，都不再创建每次命中独立的Timer。
            QueueDamageText(hit, target);
        }

        private void OnDied(int id)
        {
            PieceView view;
            if (_pieces.TryGetValue(id, out view))
            {
                ShowEffect(BattleEffectKind.Death, view.transform.position, 0);
            }

            RefreshBoard();
        }

        private void OnMerged(int id)
        {
            Piece p = FindPiece(id);
            if (p != null)
            {
                ShowEffect(BattleEffectKind.Merge, p.Pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset), 0);
            }

            RefreshBoard();
        }

        private void ShowEffect(BattleEffectKind kind, Vector3 position, int damage, GameSide targetSide = GameSide.Enemy, Vector3 destination = default(Vector3))
        {
            int generation = _effectGeneration;
            RevPool.GetAsync<BattleEffectView>(effect =>
            {
                if (effect == null)
                {
                    if (generation == _effectGeneration && kind == BattleEffectKind.Projectile) { _pendingProjectileLoads--; }
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
                    effect.ShowProjectile(position, destination, OnEffectFinished, targetSide);
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
        }

        private void OnDisable()
        {
            _released = true;
            RevEvent.RemoveAllByOwner(this);
            RevTimer.CancelAllOf(this);
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

            _pieces.Clear();
            _cells.Clear();
            _pendingPieces.Clear();
            _lastHp.Clear();
            ClearRoundEffects();
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

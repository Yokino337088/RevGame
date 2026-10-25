// 玩家战术移动：独立冷却、单轮独立深快照；只有最终选定的格子通过BoardModel.Move提交到实战。
using System;
using System.Collections.Generic;
using System.Text;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>近战靠近落单敌军/封气，远程贴敌时后撤。守眼者与零气棋不自动拆阵，仍能攻击。</summary>
        private bool TryPlanPlayerMove(Piece piece, out BoardPos best, out bool breaksEye)
        {
            best = piece.Pos;
            breaksEye = false;
            if (piece.IsLocked || piece.EyeCount > 0 || _battleElapsed < Math.Max(Balance.PlayerMoveSeconds, piece.NextMoveAt)) { return false; }
            int oldDistance = NearestEnemyDistance(piece.Pos, piece.Size);
            if (piece.Def.IsRanged && oldDistance > Balance.PlayerRetreatDistance) { return false; }
            piece.NextMoveAt = _battleElapsed + Balance.PlayerMoveSeconds;
            if (_board.IsSideWiped(GameSide.Enemy)) { return false; }
            BoardModel trial = _board.CreateSnapshot();
            Piece copy = trial.FindPiece(piece.Id);
            // best初始为原位；只有严格更优且通过硬门槛的候选才会被采用。
            int bestScore = PlayerPositionScore(trial, piece, piece.Pos, oldDistance);
            for (int y = 0; y <= _board.Size - piece.Size; y++)
            {
                for (int x = 0; x <= _board.Size - piece.Size; x++)
                {
                    BoardPos pos = new BoardPos(x, y);
                    if (pos == piece.Pos || !_board.CanFit(pos, piece.Size, GameSide.Player, piece, respectEnemyEyes: false)) { continue; }
                    if (!EyeTactics.CanEnter(_board, pos, piece.Size, GameSide.Player, piece.Def.IsRanged)) { continue; }
                    int distance = NearestEnemyDistance(pos, piece.Size);
                    if (piece.Def.IsRanged ? distance <= oldDistance : (oldDistance > 1 && distance > 1)) { continue; }
                    // 试算棋盘每个候选先恢复；绝不能借用实战棋子进行Evaluate。
                    if (copy.Pos != piece.Pos) { trial.Move(copy, piece.Pos, respectEnemyEyes: false); }
                    GoRules.Evaluate(trial);
                    if (!trial.Move(copy, pos, respectEnemyEyes: false)) { continue; }
                    GoRules.Evaluate(trial);
                    bool finalEnemyEye = _board.GetCell(pos).EyeSide == GameSide.Enemy;
                    // 最后一眼中的突入棋天然零气；我方零气仍可反击，不能用通用安全筛选把明确授权的破眼永远挡掉。
                    if ((copy.IsLocked && !finalEnemyEye) || !PreservesPlayerFormation(trial, finalEnemyEye ? piece : null)) { continue; }
                    int score = PlayerPositionScore(trial, piece, pos, distance);
                    if (score > bestScore) { best = pos; bestScore = score; }
                }
            }
            if (best == piece.Pos) { return false; }
            breaksEye = _board.GetCell(best).EyeSide == GameSide.Enemy;
            return true;
        }

        /// <summary>每颗棋一台机器；回合结束统一释放，不持有场景对象。</summary>
        private readonly Dictionary<int, PlayerAI> _playerAI = new Dictionary<int, PlayerAI>();
        public int PlayerAICount => _playerAI.Count;

        private sealed class PlayerOwner : IPlayerAIOwner
        {
            private readonly BattleService _service;
            public Piece Actor { get; }
            public GamePhase Phase => _service.Phase;
            public PlayerOwner(BattleService service, Piece actor) { _service = service; Actor = actor; }
            public bool PlanMove(out BoardPos position, out bool breaksEye) { return _service.TryPlanPlayerMove(Actor, out position, out breaksEye); }
            public void Move(BoardPos position) { _service.CommitPlayerMove(Actor, position); }
            public void Attack(float seconds) { _service.StepPlayerAttack(Actor, seconds / Balance.BattleTickSeconds); }
        }

        private PlayerAI EnsurePlayerAI(Piece piece)
        {
            if (!_playerAI.TryGetValue(piece.Id, out PlayerAI ai))
            {
                ai = new PlayerAI(new PlayerOwner(this, piece));
                _playerAI.Add(piece.Id, ai);
            }
            return ai;
        }

        /// <summary>按真实占格提交；最后再检查眼位硬规则，不能因先前试算合法而跳过提交门槛。</summary>
        private void CommitPlayerMove(Piece piece, BoardPos position)
        {
            if (!EyeTactics.CanEnter(_board, position, piece.Size, GameSide.Player, piece.Def.IsRanged)
                || !_board.Move(piece, position, respectEnemyEyes: false))
            {
                throw new InvalidOperationException("玩家AI移动预检与提交不一致");
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMoved, piece.Id);
        }

        /// <summary>原有规则夹具复用此入口；规划和提交与状态机一致，不另造测试用移动算法。</summary>
        private void TryPlayerTacticalMove(Piece piece)
        {
            if (TryPlanPlayerMove(piece, out BoardPos position, out bool breaksEye)) { CommitPlayerMove(piece, position); }
        }

        private void StopPlayerAI()
        {
            foreach (PlayerAI ai in _playerAI.Values) { ai.Dispose(); }
            _playerAI.Clear();
        }

        /// <summary>开发态观察真实状态及最近切换原因；只查询，不允许强制修改AI状态。</summary>
        public string PlayerAIStatus()
        {
            var result = new StringBuilder();
            foreach (KeyValuePair<int, PlayerAI> entry in _playerAI)
            {
                var history = entry.Value.History;
                result.AppendLine("#" + entry.Key + " " + entry.Value.State + " " + (history.Length == 0 ? "" : history[history.Length - 1].Reason));
            }
            return result.ToString();
        }

        private int NearestEnemyDistance(BoardPos pos, int size)
        {
            int nearest = _board.Size * 2;
            foreach (Piece enemy in _enemyPieces)
            {
                if (enemy.IsAlive) { nearest = Math.Min(nearest, BodyDistance(pos, size, enemy)); }
            }
            return nearest;
        }

        /// <summary>以下权重仅是算法相对排序，不是伤害/距离/冷却数值。围住整块的收益高于追击落单者。</summary>
        private int PlayerPositionScore(BoardModel trial, Piece actor, BoardPos pos, int enemyDistance)
        {
            if (actor.Def.IsRanged)
            {
                int friendlyDistance = _board.Size;
                foreach (Piece ally in _playerPieces)
                {
                    if (ally.IsAlive && ally != actor) { friendlyDistance = Math.Min(friendlyDistance, BodyDistance(pos, actor.Size, ally)); }
                }
                return enemyDistance * 100 - friendlyDistance * 8;
            }
            int score = -enemyDistance * 60;
            foreach (Piece enemy in _enemyPieces)
            {
                if (!enemy.IsAlive) { continue; }
                Piece candidate = trial.FindPiece(enemy.Id);
                if (candidate == null) { continue; }
                if (candidate.IsLocked && !enemy.IsLocked) { score += 2000; }
                score += Math.Max(0, enemy.Liberties - candidate.Liberties) * 100;
                if (BodyDistance(pos, actor.Size, enemy) == 1)
                {
                    score += 150 + (enemy.ConnectedCells <= enemy.Size * enemy.Size ? 180 : 0);
                }
            }
            return score;
        }
    }
}

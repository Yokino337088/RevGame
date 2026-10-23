// 玩家战术移动：独立冷却、单轮独立深快照；只有最终选定的格子通过BoardModel.Move提交到实战。
using System;
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
        private void TryPlayerTacticalMove(Piece piece)
        {
            if (piece.IsLocked || piece.EyeCount > 0 || _battleElapsed < Math.Max(Balance.PlayerMoveSeconds, piece.NextMoveAt)) { return; }
            piece.NextMoveAt = _battleElapsed + Balance.PlayerMoveSeconds;
            int oldDistance = NearestEnemyDistance(piece.Pos, piece.Size);
            if (piece.Def.IsRanged && oldDistance > Balance.PlayerRetreatDistance) { return; }
            if (_board.IsSideWiped(GameSide.Enemy)) { return; }
            BoardModel trial = _board.CreateSnapshot();
            Piece copy = trial.FindPiece(piece.Id);
            BoardPos best = piece.Pos;
            int bestScore = PlayerPositionScore(trial, piece, piece.Pos, oldDistance);
            for (int y = 0; y <= _board.Size - piece.Size; y++)
            {
                for (int x = 0; x <= _board.Size - piece.Size; x++)
                {
                    BoardPos pos = new BoardPos(x, y);
                    if (pos == piece.Pos || !_board.CanFit(pos, piece.Size, GameSide.Player, piece, respectEnemyEyes: false)) { continue; }
                    int distance = NearestEnemyDistance(pos, piece.Size);
                    if (piece.Def.IsRanged ? distance <= oldDistance : (oldDistance > 1 && distance > 1)) { continue; }
                    // 试算棋盘每个候选先恢复；绝不能借用实战棋子进行Evaluate。
                    if (copy.Pos != piece.Pos) { trial.Move(copy, piece.Pos, respectEnemyEyes: false); }
                    GoRules.Evaluate(trial);
                    if (!trial.Move(copy, pos, respectEnemyEyes: false)) { continue; }
                    GoRules.Evaluate(trial);
                    if (copy.IsLocked || !PreservesPlayerFormation(trial)) { continue; }
                    int score = PlayerPositionScore(trial, piece, pos, distance);
                    if (score > bestScore) { best = pos; bestScore = score; }
                }
            }
            if (best == piece.Pos) { return; }
            if (!_board.Move(piece, best, respectEnemyEyes: false)) { throw new InvalidOperationException("玩家AI移动预检与提交不一致"); }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMoved, piece.Id);
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

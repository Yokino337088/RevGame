// ============================================================
// EnemyTactics.cs —— 敌人的目标选择与保眼试走；不直接搬动实战棋子。
// 先保证阵型与气，再比较射界和距离，同分按编号/遍历顺序稳定选择，方便复现。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    public static class EnemyTactics
    {
        /// <summary>在已经满足射程的目标中，优先收残血，再压制远程；同分选编号小的目标。</summary>
        public static Piece ChooseTarget(BoardModel board, Piece attacker, List<BoardPos> targets)
        {
            Piece best = null;
            int bestScore = int.MinValue;
            foreach (BoardPos pos in targets)
            {
                Piece target = board.GetPiece(pos);
                if (target == null || !target.IsAlive) { continue; }
                // 分数分层拉开：优先可能一击击败的目标，其次远程威胁，最后距离。
                // 这里只选择目标；真正扣血仍由服务计算锁死、连通、增益等效果，不能在 AI 中另扣一次。
                int score = (target.Hp <= attacker.Def.Attack * Consts.CombatDamageScale ? 10000 : 0);
                score += target.Def.IsRanged ? 1000 : 0;
                score -= Distance(attacker.Pos, target.Pos) * 10;
                if (best == null || score > bestScore || (score == bestScore && target.Id < best.Id))
                {
                    best = target;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// 只有服务确认“没有目标且移动冷却完成”才调用。每个候选都在深快照试走，最多四个普通格/八个马步。
        /// 不填自己的眼，不减少任何原有守眼棋群的眼数，也不把有气的同伴堵成零气。
        /// </summary>
        public static BoardPos ChooseSafeMove(BoardModel board, Piece piece, List<BoardPos> moves)
        {
            BoardPos best = piece.Pos;
            int oldDistance = NearestPlayerDistance(board, piece.Pos);
            int bestScore = int.MinValue;
            foreach (BoardPos pos in moves)
            {
                Cell cell = board.GetCell(pos);
                if (cell == null || cell.IsEyeMarked) { continue; }
                BoardModel trial = board.CreateSnapshot();
                Piece moved = EnemyFormationPlanner.Find(trial, piece.Id);
                if (!trial.Move(moved, pos)) { continue; }
                GoRules.Evaluate(trial);
                if (moved.IsLocked || !PreservesFormation(board, trial)) { continue; }
                int targets = MoveRules.GetAttackTargets(trial, moved).Count;
                int distance = NearestPlayerDistance(trial, pos);
                // 只接受能打开射界或真正接近的格子，堵路时等待，避免左右横跳。
                if (targets == 0 && distance >= oldDistance) { continue; }
                int score = targets * 1000 - distance * 20 + System.Math.Min(moved.Liberties, 12);
                if (score > bestScore) { bestScore = score; best = pos; }
            }
            return best;
        }

        private static bool PreservesFormation(BoardModel before, BoardModel after)
        {
            foreach (Piece ally in before.GetPiecesOf(GameSide.Enemy))
            {
                Piece copy = EnemyFormationPlanner.Find(after, ally.Id);
                if (copy == null || copy.EyeCount < ally.EyeCount || (ally.Liberties > 0 && copy.Liberties == 0)) { return false; }
            }
            return true;
        }

        private static int NearestPlayerDistance(BoardModel board, BoardPos from)
        {
            int nearest = int.MaxValue;
            foreach (Piece player in board.GetPiecesOf(GameSide.Player)) { nearest = System.Math.Min(nearest, Distance(from, player.Pos)); }
            return nearest;
        }

        private static int Distance(BoardPos a, BoardPos b)
        {
            return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
        }
    }
}

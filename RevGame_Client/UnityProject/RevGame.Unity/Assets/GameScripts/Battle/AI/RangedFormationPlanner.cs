// 远程整队纯规划：只在一份深快照中试排。已有相连火力阵不移动；守眼、零气、手动冷却中的棋子保持原位。
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>一次顺序提交的落点，不保存可变Piece引用；表现层只消费已提交的棋子Id。</summary>
    public readonly struct FormationMove
    {
        /// <summary>战斗内稳定Id；提交时重新查找真实棋子，避免持有试算快照里的对象。</summary>
        public readonly int PieceId;
        /// <summary>目标身体左下格，单位为棋盘格；规划已检查整个身体占格。</summary>
        public readonly BoardPos Position;
        public FormationMove(int pieceId, BoardPos position)
        {
            PieceId = pieceId;
            Position = position;
        }
    }

    /// <summary>无资源和动画依赖的整队算法；只产生计划，不修改实战棋盘，不持有跨轮次缓存。</summary>
    public static class RangedFormationPlanner
    {
        /// <summary>
        /// 将散落远程并入一个相连阵型。result归调用者持有，方法开始清空。
        /// 每个落点都要求整个身体可放、远离贴身敌军，并保留所有玩家棋的眼和生存气。
        /// 不强行交换棋子或穿插占格；空间不足时只返回安全的部分整队，下一窗口再判断。
        /// 这是有界贪心整理，不是全局最优阵型搜索；评分只是候选偏好，不是可调战斗数值。
        /// </summary>
        public static void Plan(BoardModel board, float elapsed, int minimumCount, int safeDistance, List<FormationMove> result)
        {
            result.Clear();
            var ranged = new List<Piece>();
            foreach (Piece piece in board.Pieces)
            {
                if (piece.Side == GameSide.Player && piece.Def.IsRanged && piece.IsAlive) { ranged.Add(piece); }
            }
            if (ranged.Count < minimumCount) { return; }
            ranged.Sort((a,b) => a.Id.CompareTo(b.Id));
            // 锚点优先选已有最多相邻同伴的远程，减少移动人数；同分保留最早入场的棋。
            Piece anchor = ranged[0];
            int bestLinks = -1;
            foreach (Piece piece in ranged)
            {
                int links = 0;
                foreach (Piece other in ranged) { if (piece != other && Distance(piece.Pos, piece.Size, other) == 1) { links++; } }
                if (links > bestLinks) { bestLinks = links; anchor = piece; }
            }
            var connected = new HashSet<int> { anchor.Id };
            GrowConnected(ranged, connected);
            if (connected.Count == ranged.Count) { return; }
            BoardModel trial = board.CreateSnapshot();
            var copies = new List<Piece>();
            foreach (Piece piece in ranged) { copies.Add(trial.FindPiece(piece.Id)); }
            foreach (Piece original in ranged)
            {
                if (connected.Contains(original.Id) || original.IsLocked || original.EyeCount > 0 || elapsed < original.NextMoveAt) { continue; }
                Piece moving = trial.FindPiece(original.Id);
                BoardPos start = moving.Pos;
                BoardPos best = start;
                int bestScore = int.MinValue;
                for (int y = 0; y <= board.Size - moving.Size; y++)
                {
                    for (int x = 0; x <= board.Size - moving.Size; x++)
                    {
                        var pos = new BoardPos(x,y);
                        if (moving.Pos != start) { trial.Move(moving, start, respectEnemyEyes: false); }
                        GoRules.Evaluate(trial);
                        if (pos == start || !trial.CanFit(pos, moving.Size, moving.Side, moving)) { continue; }
                        int links = 0;
                        int nearestEnemy = board.Size * 2;
                        foreach (Piece other in trial.Pieces)
                        {
                            int distance = Distance(pos, moving.Size, other);
                            if (other.Side == GameSide.Enemy) { nearestEnemy = Math.Min(nearestEnemy, distance); }
                            else if (connected.Contains(other.Id) && distance == 1) { links++; }
                        }
                        if (links == 0 || nearestEnemy <= safeDistance) { continue; }
                        if (!trial.Move(moving, pos)) { continue; }
                        GoRules.Evaluate(trial);
                        if (!PreservesPlayers(board, trial)) { continue; }
                        int travel = Math.Abs(pos.X - start.X) + Math.Abs(pos.Y - start.Y);
                        int anchorDistance = Distance(pos, moving.Size, trial.FindPiece(anchor.Id));
                        int score = links * 200 - anchorDistance * 40 + Math.Min(nearestEnemy, 5) * 20 - travel;
                        if (score > bestScore) { best = pos; bestScore = score; }
                    }
                }
                if (moving.Pos != start) { trial.Move(moving, start, respectEnemyEyes: false); }
                GoRules.Evaluate(trial);
                if (best == start) { continue; }
                if (!trial.Move(moving, best)) { throw new InvalidOperationException("整队快照落点失效"); }
                GoRules.Evaluate(trial);
                result.Add(new FormationMove(original.Id, best));
                connected.Add(original.Id);
                GrowConnected(copies, connected);
            }
        }

        /// <summary>四邻身体连接就是有效火力阵，不强制视觉对称，不拆玩家已经摆好的队形。</summary>
        private static void GrowConnected(List<Piece> pieces, HashSet<int> connected)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (Piece piece in pieces)
                {
                    if (connected.Contains(piece.Id)) { continue; }
                    foreach (Piece other in pieces)
                    {
                        if (connected.Contains(other.Id) && Distance(piece.Pos, piece.Size, other) == 1)
                        { connected.Add(piece.Id); changed = true; break; }
                    }
                }
            } while (changed);
        }

        /// <summary>以本次规划开始时的实战盘为底线：不能拆眼，也不能让原本有气的友军变成零气。</summary>
        private static bool PreservesPlayers(BoardModel before, BoardModel after)
        {
            foreach (Piece piece in before.Pieces)
            {
                if (piece.Side != GameSide.Player) { continue; }
                Piece copy = after.FindPiece(piece.Id);
                if (copy.EyeCount < piece.EyeCount || (piece.Liberties > 0 && copy.Liberties == 0)) { return false; }
            }
            return true;
        }

        /// <summary>按身体边缘计算格距，二阶/三阶大棋也遵守同一占格规则。</summary>
        private static int Distance(BoardPos pos, int size, Piece other)
        {
            return Math.Max(0, Math.Max(other.Pos.X - (pos.X + size - 1), pos.X - (other.Pos.X + other.Size - 1)))
                + Math.Max(0, Math.Max(other.Pos.Y - (pos.Y + size - 1), pos.Y - (other.Pos.Y + other.Size - 1)));
        }
    }
}

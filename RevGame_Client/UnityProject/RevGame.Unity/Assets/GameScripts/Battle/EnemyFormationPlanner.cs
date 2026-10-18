// ============================================================
// EnemyFormationPlanner.cs —— 用真实棋盘规则验证敌方眼阵，再一次性提交落点。
// 这是游戏业务算法，不依赖 Unity 对象，也不修改 Revolution 框架。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    public static class EnemyFormationPlanner
    {
        // 模板贴着棋盘边缘。第一排的空洞才是眼，第二排把守子连成同一个棋群。
        // 数组前几项留给远程输出，后几项连接骨架；两眼需要八颗，而不是把一个两格空洞当两只眼。
        private static readonly BoardPos[] OneEye =
        {
            new BoardPos(1, 1), new BoardPos(0, 1), new BoardPos(2, 1),
            new BoardPos(0, 0), new BoardPos(2, 0)
        };
        private static readonly BoardPos[] TwoEyes =
        {
            new BoardPos(1, 1), new BoardPos(3, 1), new BoardPos(2, 1),
            new BoardPos(0, 1), new BoardPos(4, 1),
            new BoardPos(0, 0), new BoardPos(2, 0), new BoardPos(4, 0)
        };

        /// <summary>
        /// 只在成功后把最终坐标写入尚未出生的敌棋。玩家原对象始终只读，失败不会丢残局或少生 Boss。
        /// 搜索有固定上限：两种模板 × 四条边 × 最多七个偏移，不进行无穷递归。
        /// </summary>
        public static bool TryArrange(List<Piece> roster, IReadOnlyList<Piece> players, bool newBoard, out string report, int intelligence = 4)
        {
            var baseline = new BoardModel(Consts.BoardSize);
            if (!newBoard)
            {
                foreach (Piece player in players)
                {
                    if (player.IsAlive && !baseline.Place(player.CreateSnapshot(), player.Pos, respectEnemyEyes: false))
                    {
                        report = "保留残局存在重叠，拒绝生成敌阵";
                        return false;
                    }
                }
            }
            GoRules.Evaluate(baseline);
            var guards = roster.FindAll(p => !p.IsBoss && p.Size == 1);
            int requested = guards.Count >= TwoEyes.Length ? 2 : 1;
            for (int eyes = requested; eyes >= 1; eyes--)
            {
                BoardPos[] template = eyes == 2 ? TwoEyes : OneEye;
                if (guards.Count < template.Length) { continue; }
                BoardModel best = null;
                int bestScore = int.MinValue;
                int width = eyes == 2 ? 5 : 3;
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    for (int offset = 0; offset <= baseline.Size - width; offset++)
                    {
                        BoardModel trial = baseline.CreateSnapshot();
                        HashSet<BoardPos> reserved = ProtectedEyes(trial);
                        reserved.Add(Transform(new BoardPos(1 + offset, 0), rotation, trial.Size));
                        if (eyes == 2) { reserved.Add(Transform(new BoardPos(3 + offset, 0), rotation, trial.Size)); }
                        bool fits = true;
                        for (int i = 0; i < template.Length; i++)
                        {
                            BoardPos pos = Transform(new BoardPos(template[i].X + offset, template[i].Y), rotation, trial.Size);
                            if (reserved.Contains(pos) || !trial.Place(guards[i].CreateSnapshot(), pos))
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (!fits || !PlaceRemaining(trial, roster, reserved)) { continue; }
                        GoRules.Evaluate(trial);
                        Piece root = Find(trial, guards[0].Id);
                        if (root == null || root.EyeCount < eyes || !PreservesPlayers(baseline, trial)) { continue; }
                        if (trial.GetPiecesOf(GameSide.Enemy).Exists(p => p.IsLocked)) { continue; }
                        int score = Score(trial, intelligence);
                        if (score > bestScore) { best = trial; bestScore = score; }
                    }
                }
                if (best != null)
                {
                    CopyPositions(best, roster);
                    report = (eyes == 2 ? "两眼活棋" : "一眼守阵") + (eyes < requested ? "（空间受限，已降级）" : "");
                    return true;
                }
            }

            // 玩家可能已经占据全部边缘：允许完整合法散阵，但绝不偷偷删掉放不下的护卫或主将。
            BoardModel fallback = baseline.CreateSnapshot();
            if (PlaceRemaining(fallback, roster, ProtectedEyes(fallback)))
            {
                GoRules.Evaluate(fallback);
                if (!fallback.GetPiecesOf(GameSide.Enemy).Exists(p => p.IsLocked) && PreservesPlayers(baseline, fallback))
                {
                    CopyPositions(fallback, roster);
                    report = "合法散阵（残局无完整眼阵空间）";
                    return true;
                }
            }
            report = "棋盘没有完整敌阵及 Boss 身体的出生空间；本回合保持不变，可返回主界面重新布阵";
            return false;
        }

        /// <summary>补齐模板之外的完整名单；reserved 是必须留空的眼位，任何大棋身体都不能覆盖它。</summary>
        private static bool PlaceRemaining(BoardModel board, List<Piece> roster, HashSet<BoardPos> reserved)
        {
            // 大身体先摆，避免小兵占掉最后一块 3×3；已有模板棋子按 Id 跳过。
            var ordered = new List<Piece>(roster);
            ordered.Sort((a, b) => a.Size == b.Size ? a.Id.CompareTo(b.Id) : b.Size.CompareTo(a.Size));
            foreach (Piece source in ordered)
            {
                if (Find(board, source.Id) != null) { continue; }
                // 眼阵之外的普通棋是机动前排，优先留一格间隔摆到中线；贴住骨架会继承眼数而全队站死。
                if (!source.IsBoss && board.GetPiecesOf(GameSide.Enemy).Count > 0 && TryPlaceMobile(board, source, reserved)) { continue; }
                bool placed = false;
                for (int y = board.Size - source.Size; y >= 0 && !placed; y--)
                {
                    for (int x = board.Size - source.Size; x >= 0 && !placed; x--)
                    {
                        var pos = new BoardPos(x, y);
                        if (!board.CanFit(pos, source.Size, source.Side) || CoversReserved(pos, source.Size, reserved)) { continue; }
                        placed = board.Place(source.CreateSnapshot(), pos);
                    }
                }
                if (!placed) { return false; }
            }
            return true;
        }

        private static bool TryPlaceMobile(BoardModel board, Piece source, HashSet<BoardPos> reserved)
        {
            BoardPos best = new BoardPos(-1, -1);
            int bestScore = int.MinValue;
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (reserved.Contains(pos) || !board.CanFit(pos, source.Size, source.Side)) { continue; }
                    bool linked = false;
                    foreach (BoardPos neighbor in pos.GetOrthogonalNeighbors())
                    {
                        Piece ally = board.GetPiece(neighbor);
                        if (ally != null && ally.Side == GameSide.Enemy) { linked = true; break; }
                    }
                    if (linked) { continue; }
                    // 不直接贴脸出生，中线施压需要数次低频接近，给玩家观察与射击的窗口。
                    int score = -System.Math.Abs(y - board.Size / 2) * 10 - System.Math.Abs(x - board.Size / 2);
                    if (score > bestScore) { bestScore = score; best = pos; }
                }
            }
            return best.X >= 0 && board.Place(source.CreateSnapshot(), best);
        }

        private static bool CoversReserved(BoardPos pos, int size, HashSet<BoardPos> reserved)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (reserved.Contains(new BoardPos(pos.X + x, pos.Y + y))) { return true; }
                }
            }
            return false;
        }

        /// <summary>收集规则已经识别的眼位，生成敌人时也不能直接占掉玩家现成的眼。</summary>
        private static HashSet<BoardPos> ProtectedEyes(BoardModel board)
        {
            var result = new HashSet<BoardPos>();
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (board.GetCell(pos).IsEyeMarked) { result.Add(pos); }
                }
            }
            return result;
        }

        /// <summary>敌人出生可以增加压力，但不能靠生成操作拆掉玩家的眼，或把原本有气的玩家直接锁死。</summary>
        private static bool PreservesPlayers(BoardModel before, BoardModel after)
        {
            foreach (Piece player in before.GetPiecesOf(GameSide.Player))
            {
                Piece copy = Find(after, player.Id);
                if (copy == null || copy.EyeCount < player.EyeCount || (player.Liberties > 0 && copy.Liberties == 0)) { return false; }
            }
            return true;
        }

        private static int Score(BoardModel board, int intelligence)
        {
            int score = 0;
            foreach (Piece enemy in board.GetPiecesOf(GameSide.Enemy))
            {
                score += enemy.Pos.Y * 3 + System.Math.Min(enemy.Liberties, 12);
                // 使用真实射程查询评分，能开火的远程位置优先，不靠“职业叫射手”假定一定有输出。
                if (intelligence >= 2 && enemy.Def.IsRanged) { score += MoveRules.GetAttackTargets(board, enemy).Count * 100; }
                if (intelligence >= 3) { score += enemy.EyeCount * 30; }
                if (intelligence >= 4)
                {
                    // 高阶布阵同时看对方火力，主将和远程尽量不在出生点遭到集火。
                    foreach (Piece player in board.GetPiecesOf(GameSide.Player))
                    {
                        foreach (BoardPos hit in MoveRules.GetAttackTargets(board, player))
                        {
                            if (board.GetPiece(hit) == enemy) { score -= enemy.IsKing ? 180 : 60; break; }
                        }
                    }
                }
            }
            return score;
        }

        /// <summary>同一个边缘模板每次旋转九十度，就能尝试四条边；size - 1 把坐标限制在棋盘内。</summary>
        private static BoardPos Transform(BoardPos pos, int rotation, int size)
        {
            for (int i = 0; i < rotation; i++) { pos = new BoardPos(size - 1 - pos.Y, pos.X); }
            return pos;
        }

        /// <summary>按稳定编号在快照中找对应棋子；不能用原对象引用查快照，因为深复制后已是不同对象。</summary>
        public static Piece Find(BoardModel board, int id)
        {
            return board.GetAllPieces().Find(p => p.Id == id);
        }

        private static void CopyPositions(BoardModel board, List<Piece> roster)
        {
            foreach (Piece piece in roster) { piece.Pos = Find(board, piece.Id).Pos; }
        }
    }
}

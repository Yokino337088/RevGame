// ============================================================
// EnemyFormationPlanner.cs —— 用真实棋盘规则验证敌方眼阵，再一次性提交落点。
// 这是游戏业务算法，不依赖 Unity 对象，也不修改 Revolution 框架。
//
// 【它在敌方AI里的位置】
// 回合开始时由服务调用；规划好后交给状态机的 Prepare 状态提交（见 EnemyStates.cs）。
// 与 EnemyTactics 的区别：这里管“整队初始阵型”，那里管“战斗中的一次增援落点”；
// 两者都只在深快照里试算，失败不改真实棋盘。
//
// 【为什么先规划再提交】
// 敌阵必须整套放得下：Boss 的 2×2/3×3 身体、护卫眼阵、额外小兵都算在内。
// 边摆边改会让“摆到一半放不下”留下半套敌阵，所以本类只输出坐标，提交由服务一次完成。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>
    /// 回合开始前的布阵规划器。输入“必须出生的敌军名单”和“上一轮玩家残局”，输出整套合法落点。
    /// 分三步：复制玩家棋盘→枚举边缘眼阵并补齐名单→挑最高分方案一次性提交坐标。
    /// 它不拥有计时器、资源和Unity对象；开战后的低频补子由EnemyTactics计算，不能混用两个时机。
    /// 所有试算使用独立Piece副本，失败保留输入；否则下一回合出生失败会把玩家残局一起破坏。
    /// </summary>
    public static class EnemyFormationPlanner
    {
        // 模板贴着棋盘边缘。第一排的空洞才是眼，第二排把守子连成同一个棋群。
        // 数组前几项留给远程输出，后几项连接骨架；两眼需要八颗，而不是把一个两格空洞当两只眼。
        /// <summary>五子边缘单眼模板，(1,0)必须留空；坐标先按下边缘定义，再统一旋转。</summary>
        private static readonly BoardPos[] OneEye =
        {
            new BoardPos(1, 1), new BoardPos(0, 1), new BoardPos(2, 1),
            new BoardPos(0, 0), new BoardPos(2, 0)
        };
        /// <summary>八子双眼模板，(1,0)/(3,0)为两个独立空点；中间的连接子把两眼归入同一棋群。</summary>
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
        /// <param name="roster">完整敌军名单，不能为了“放得下”偷偷删除Boss或护卫。</param>
        /// <param name="players">保留残局的玩家名单，本方法不会修改其中的对象。</param>
        /// <param name="newBoard">true代表新开局，false代表保留玩家原有阵型。</param>
        /// <param name="report">成功返回实际眼阵/降级说明，失败返回明确的空间不足原因。</param>
        /// <param name="intelligence">1～4档评分因素，所有档位都遵守不重叠、不破眼的安全检查。</param>
        public static bool TryArrange(List<Piece> roster, IReadOnlyList<Piece> players, bool newBoard, out string report, int intelligence = 4, int variation = 0)
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
            // 靠内侧的模板点先安排近战，边缘后排安排远程；职业排序每轮稳定，坐标由变化编号决定。
            guards.Sort((a, b) => a.Def.IsRanged == b.Def.IsRanged ? a.Id.CompareTo(b.Id) : (a.Def.IsRanged ? 1 : -1));
            // 查询内共享只读名单与射界缓冲，不在每个候选重新创建、排序同一份名册。
            var ordered = new List<Piece>(roster);
            ordered.Sort((a, b) => a.Size == b.Size ? a.Id.CompareTo(b.Id) : b.Size.CompareTo(a.Size));
            var hits = new List<BoardPos>(Consts.BoardSize * Consts.BoardSize);
            var hitIds = new HashSet<int>();
            int preferredEdge = 1 + variation % 3; // 左、上、右轮换，优先给玩家下方准备区留空间。
            // 先争取两眼；空间不够才退一眼，再退散阵。降级的是阵型，不是必需出生的棋子数量。
            int requested = guards.Count >= TwoEyes.Length ? 2 : 1;
            for (int eyes = requested; eyes >= 1; eyes--)
            {
                BoardPos[] template = eyes == 2 ? TwoEyes : OneEye;
                if (guards.Count < template.Length)
                {
                    continue;
                }
                BoardModel best = null;
                int bestScore = int.MinValue;
                int width = eyes == 2 ? 5 : 3;
                for (int edgeIndex = 0; edgeIndex < 4; edgeIndex++)
                {
                    int rotation = (preferredEdge + edgeIndex) % 4;
                    int preferredOffset = (variation / 3) % (baseline.Size - width + 1);
                    if (newBoard && rotation == 1) { preferredOffset = 2 + (variation / 3) % (baseline.Size - width - 1); }
                    if (newBoard && rotation == 3) { preferredOffset = (variation / 3) % (baseline.Size - width - 1); }
                    for (int offsetIndex = 0; offsetIndex <= baseline.Size - width; offsetIndex++)
                    {
                        int offset = (preferredOffset + offsetIndex) % (baseline.Size - width + 1);
                        BoardModel trial = baseline.CreateSnapshot();
                        HashSet<BoardPos> reserved = ProtectedEyes(trial);
                        // 新挑战保留最下两行准备区，让首轮手牌有连续落点；结构坐标由9×9棋盘决定，不是人数/伤害参数。
                        // 保留残局时只保护真实玩家眼位，由后面的安全验收决定其它空格能否出生。
                        if (newBoard) { ReserveOpening(reserved, trial.Size); }
                        // 模板的眼还没有形成，规则暂时识别不到，必须提前保留空点以防补名单时被填上。
                        reserved.Add(Transform(new BoardPos(1 + offset, 0), rotation, trial.Size));
                        if (eyes == 2)
                        {
                            reserved.Add(Transform(new BoardPos(3 + offset, 0), rotation, trial.Size));
                        }
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
                        if (!fits || !PlaceRemaining(trial, ordered, reserved, variation))
                        {
                            continue;
                        }
                        // 模板看起来像眼不等于真的成立；最终以同一套GoRules验证，UI/AI/实战没有三种眼算法。
                        GoRules.Evaluate(trial);
                        Piece root = Find(trial, guards[0].Id);
                        if (root == null || root.EyeCount < eyes || !PreservesPlayers(baseline, trial))
                        {
                            continue;
                        }
                        if (trial.GetPiecesOf(GameSide.Enemy).Exists(p => p.IsLocked))
                        {
                            continue;
                        }
                        // 安全门槛通过后才考虑回合风格。边缘/偏移偏好大于一般射界分，避免每轮都退化成同一最高分布局。
                        // 首选构图已通过完整气眼/占格/残局保护时直接提交，不继续创建二十多份等价布局快照。
                        // 只有被残局阻挡时才进入后面的全候选评分；不能以节省搜索为理由绕过前面的安全断言。
                        if (edgeIndex == 0 && offsetIndex == 0)
                        {
                            CopyPositions(trial, roster);
                            report = Describe(variation, eyes, requested);
                            return true;
                        }
                        int score = Score(trial, intelligence, hits, hitIds)
                            + (rotation == preferredEdge ? 10000 : 0) - System.Math.Abs(offset - preferredOffset) * 1000;
                        if (score > bestScore)
                        {
                            best = trial; bestScore = score;
                        }
                    }
                }
                if (best != null)
                {
                    CopyPositions(best, roster);
                    report = Describe(variation, eyes, requested);
                    return true;
                }
            }

            // 玩家可能已经占据全部边缘：允许完整合法散阵，但绝不偷偷删掉放不下的护卫或主将。
            BoardModel fallback = baseline.CreateSnapshot();
            HashSet<BoardPos> fallbackReserved = ProtectedEyes(fallback);
            if (newBoard) { ReserveOpening(fallbackReserved, fallback.Size); }
            if (PlaceRemaining(fallback, ordered, fallbackReserved, variation))
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
        private static bool PlaceRemaining(BoardModel board, List<Piece> ordered, HashSet<BoardPos> reserved, int variation)
        {
            // 大身体先摆，避免小兵占掉最后一块 3×3；已有模板棋子按 Id 跳过。
            foreach (Piece source in ordered)
            {
                if (Find(board, source.Id) != null)
                {
                    continue;
                }
                // 眼阵之外的普通棋是机动前排，优先留一格间隔摆到中线；贴住骨架会继承眼数而全队站死。
                if (!source.IsBoss && HasEnemy(board) && TryPlaceMobile(board, source, reserved, variation))
                {
                    continue;
                }
                bool placed = false;
                for (int y = board.Size - source.Size; y >= 0 && !placed; y--)
                {
                    for (int x = board.Size - source.Size; x >= 0 && !placed; x--)
                    {
                        var pos = new BoardPos(x, y);
                        if (!board.CanFit(pos, source.Size, source.Side) || CoversReserved(pos, source.Size, reserved))
                        {
                            continue;
                        }
                        placed = board.Place(source.CreateSnapshot(), pos);
                    }
                }
                if (!placed)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>给眼阵外的普通棋找中线机动点；不紧贴守眼骨架，避免共享眼数后全军停止移动。</summary>
        private static bool TryPlaceMobile(BoardModel board, Piece source, HashSet<BoardPos> reserved, int variation)
        {
            BoardPos best = new BoardPos(-1, -1);
            int bestScore = int.MinValue;
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (reserved.Contains(pos) || !board.CanFit(pos, source.Size, source.Side))
                    {
                        continue;
                    }
                    bool linked = false;
                    foreach (BoardPos neighbor in pos.GetOrthogonalNeighbors())
                    {
                        Piece ally = board.GetPiece(neighbor);
                        if (ally != null && ally.Side == GameSide.Enemy)
                        {
                            linked = true; break;
                        }
                    }
                    if (linked)
                    {
                        continue;
                    }
                    // 不直接贴脸出生，中线施压需要数次低频接近，给玩家观察与射击的窗口。
                    int row = source.Def.IsRanged ? board.Size - 3 : board.Size / 2;
                    int flank = 2 + variation % 3 * 2;
                    int score = -System.Math.Abs(y - row) * 10 - System.Math.Abs(x - flank);
                    // 同一合法射界下前排更近、后排更远；不靠“所有兵种都堆中线”制造压力。
                    if (score > bestScore)
                    {
                        bestScore = score; best = pos;
                    }
                }
            }
            return best.X >= 0 && board.Place(source.CreateSnapshot(), best);
        }

        /// <summary>检查整个Size×Size身体是否压到眼位；只检查左下锚点会让Boss右上角把眼盖住。</summary>
        private static bool CoversReserved(BoardPos pos, int size, HashSet<BoardPos> reserved)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (reserved.Contains(new BoardPos(pos.X + x, pos.Y + y)))
                    {
                        return true;
                    }
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
                    if (board.GetCell(pos).IsEyeMarked)
                    {
                        result.Add(pos);
                    }
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
                if (copy == null || copy.EyeCount < player.EyeCount || (player.Liberties > 0 && copy.Liberties == 0))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 对已经合法的整套敌阵评分。低档偏向后排与有气；高档再看射界、眼数、玩家可反击的位置。
        /// 分数越大越优先，同分保留扫描时先遇到的方案，因此固定输入可以稳定回放。
        /// </summary>
        private static int Score(BoardModel board, int intelligence, List<BoardPos> hits, HashSet<int> hitIds)
        {
            int score = 0;
            foreach (Piece enemy in board.Pieces)
            {
                if (enemy.Side != GameSide.Enemy) { continue; }
                score += enemy.Pos.Y * 3 + System.Math.Min(enemy.Liberties, 12);
                if (intelligence >= 2 && enemy.Def.IsRanged)
                {
                    MoveRules.GetAttackTargets(board, enemy, hits);
                    score += hits.Count * 100;
                }
                if (intelligence >= 3) { score += enemy.EyeCount * 30; }
            }
            // 旧实现对每颗敌人重复计算每颗玩家的射界。现在每个玩家只查一次，按敌人Id去重后扣分。
            // 这与旧评分等价，大身体多个占格命中也只算一次；缓冲归本次查询独占，不共享可变全局草稿。
            if (intelligence >= 4)
            {
                foreach (Piece player in board.Pieces)
                {
                    if (player.Side != GameSide.Player) { continue; }
                    hitIds.Clear();
                    MoveRules.GetAttackTargets(board, player, hits);
                    foreach (BoardPos hit in hits)
                    {
                        Piece enemy = board.GetPiece(hit);
                        if (enemy != null && enemy.Side == GameSide.Enemy && hitIds.Add(enemy.Id)) { score -= enemy.IsKing ? 180 : 60; }
                    }
                }
            }
            return score;
        }

        private static bool HasEnemy(BoardModel board)
        {
            foreach (Piece piece in board.Pieces) { if (piece.Side == GameSide.Enemy) { return true; } }
            return false;
        }

        private static void ReserveOpening(HashSet<BoardPos> reserved, int size)
        {
            for (int x = 0; x < size; x++) { reserved.Add(new BoardPos(x, 0)); reserved.Add(new BoardPos(x, 1)); }
        }

        private static string Describe(int variation, int eyes, int requested)
        {
            return (variation % 3 == 0 ? "左翼推进·" : variation % 3 == 1 ? "纵深守阵·" : "右翼夹击·")
                + (eyes == 2 ? "两眼活棋" : "一眼守阵") + (eyes < requested ? "（空间受限，已降级）" : "");
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
            return board.FindPiece(id);
        }

        /// <summary>唯一提交点：方案全部验证成功才写真实待出生名单的Pos，不复制快照中的临时气眼或血量。</summary>
        private static void CopyPositions(BoardModel board, List<Piece> roster)
        {
            foreach (Piece piece in roster) { piece.Pos = Find(board, piece.Id).Pos; }
        }
    }
}

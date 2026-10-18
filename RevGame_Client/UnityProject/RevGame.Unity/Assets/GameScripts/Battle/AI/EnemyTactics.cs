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
    /// <summary>
    /// 敌方战术计算器：回答“打谁、走哪、补在哪”，不负责时间、不生成GameObject、不派事件。
    /// 初学者阅读顺序：ChooseTarget（只打分）→ ChooseSafeMove（先试走）→ TryChooseDeployment（试落子）。
    /// intelligence是1～4档观察能力，不是行动速度；冷却、数量上限和玩家通关辅助由BattleService控制。
    /// 这里采用一手启发式：看下一步后的气眼和射界，不进行职业围棋的多层搜索，也不保证数学最优。
    /// 每次查询有独立的深快照和临时缓冲区；快照里的棋子可写，真实board里的棋子只能读。
    /// </summary>
    public static class EnemyTactics
    {
        /// <summary>
        /// 从合法射程列表选择一个目标。第1档就近，第2档识别远程，第3档收残血，第4档还看气与连接。
        /// 返回null表示候选已经死亡/消失；调用方不可假设查到的每个坐标仍有棋子。
        /// 评分仅比较优先级，没有伤害单位；10000让预计可击杀通常优先，但真实扣血仍必须走服务。
        /// </summary>
        /// <param name="board">当前棋盘，只读取占格和规则结果。</param>
        /// <param name="attacker">当前行动者，不会在这里扣攻击次数。</param>
        /// <param name="targets">MoveRules算出的合法射程坐标；本方法不会把射程外敌人塞进来。</param>
        /// <param name="intelligence">关卡控制的1～4档；档位只增加评分因素。</param>
        public static Piece ChooseTarget(BoardModel board, Piece attacker, List<BoardPos> targets, int intelligence = 4)
        {
            Piece best = null;
            int bestScore = int.MinValue;
            foreach (BoardPos pos in targets)
            {
                Piece target = board.GetPiece(pos);
                if (target == null || !target.IsAlive) { continue; }
                // 分数分层拉开：优先可能一击击败的目标，其次远程威胁，最后距离。
                // 这里只选择目标；真正扣血仍由服务计算锁死、连通、增益等效果，不能在 AI 中另扣一次。
                // 第一档就近交战；第二档识别远程威胁；第三档会收残血；第四档优先断开脆弱连接。
                // 规则安全检查不分档：低级AI也不能故意拆自己眼阵，只是看问题没有后期全面。
                int score = -Distance(attacker.Pos, target.Pos) * 10;
                if (intelligence >= 2 && target.Def.IsRanged) { score += 1000; }
                if (intelligence >= 3)
                {
                    score += (int)((1f - target.HpRatio) * 500f);
                    if (target.Hp <= attacker.Def.Attack * attacker.AttackScale * Consts.CombatDamageScale) { score += 10000; }
                }
                if (intelligence >= 4)
                {
                    score += target.EyeCount == 0 ? 300 : 0;
                    score += System.Math.Max(0, 4 - target.Liberties) * 180;
                    score += target.Def.IsRanged ? (int)(target.Def.Attack * target.AttackScale) : 0;
                }
                if (best == null || score > bestScore || (score == bestScore && target.Id < best.Id))
                {
                    best = target;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// 只有服务确认“没有目标且移动冷却完成”才调用。普通候选最多四格，马步最多八格。
        /// 不填自己的眼，不减少任何原有守眼棋群的眼数，也不把有气的同伴堵成零气。
        /// 查询只复制一次棋盘，每次试走前恢复位置并重算规则；返回原坐标代表原地等候，不是失败异常。
        /// </summary>
        public static BoardPos ChooseSafeMove(BoardModel board, Piece piece, List<BoardPos> moves, int intelligence = 4)
        {
            BoardPos best = piece.Pos;
            int oldDistance = NearestPlayerDistance(board, piece.Pos);
            int bestScore = int.MinValue;
            BoardModel trial = board.CreateSnapshot();
            Piece moved = trial.FindPiece(piece.Id);
            var hits = new List<BoardPos>(board.Size * board.Size);
            foreach (BoardPos pos in moves)
            {
                Cell cell = board.GetCell(pos);
                if (cell == null || cell.IsEyeMarked) { continue; }
                // ★ 恢复的是“试算棋盘”，绝不能临时移动真实棋子再移回去，那会让表现/事件读到假状态。
                // 上一次试走可能改变眼标记，所以还原时不受旧眼限制，重算后才校验新候选。
                if (moved.Pos != piece.Pos) { trial.Move(moved, piece.Pos, respectEnemyEyes: false); }
                GoRules.Evaluate(trial);
                if (!trial.Move(moved, pos)) { continue; }
                GoRules.Evaluate(trial);
                if (moved.IsLocked || !PreservesFormation(board, trial)) { continue; }
                MoveRules.GetAttackTargets(trial, moved, hits);
                int targets = hits.Count;
                int distance = NearestPlayerDistance(trial, pos);
                // 只接受能打开射界或真正接近的格子，堵路时等待，避免左右横跳。
                if (targets == 0 && distance >= oldDistance) { continue; }
                int score = targets * 1000 - distance * 20 + System.Math.Min(moved.Liberties, 12);
                if (intelligence >= 3)
                {
                    // 前后棋盘都是深快照，比较压气成果不回写真实棋子的气与眼。
                    foreach (Piece player in board.Pieces)
                    {
                        if (player.Side != GameSide.Player) { continue; }
                        Piece copy = EnemyFormationPlanner.Find(trial, player.Id);
                        score += System.Math.Max(0, player.Liberties - copy.Liberties) * 70;
                    }
                }
                if (intelligence >= 4)
                {
                    // 后期AI会估计走到该格后有多少玩家能立即反击，避开集火区；不提高移动频率。
                    foreach (Piece player in trial.Pieces)
                    {
                        if (player.Side != GameSide.Player) { continue; }
                        MoveRules.GetAttackTargets(trial, player, hits);
                        foreach (BoardPos hit in hits)
                        {
                            if (trial.GetPiece(hit) == moved) { score -= 220; break; }
                        }
                    }
                }
                if (score > bestScore) { bestScore = score; best = pos; }
            }
            return best;
        }

        /// <summary>
        /// 安全门槛优先于分数：逐个Id找到同一颗棋子的副本，比较它原先与现在的气眼。
        /// 眼数减少或把原本有气的友军堵成零气，立即否决；再高的攻击评分也不能越过此门槛。
        /// </summary>
        private static bool PreservesFormation(BoardModel before, BoardModel after)
        {
            foreach (Piece ally in before.Pieces)
            {
                if (ally.Side != GameSide.Enemy) { continue; }
                Piece copy = EnemyFormationPlanner.Find(after, ally.Id);
                if (copy == null || copy.EyeCount < ally.EyeCount || (ally.Liberties > 0 && copy.Liberties == 0)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// 按“补活自己的棋、连接断点、压缩对手的气、形成射界”给空格评分。
        /// 这是可解释的一手启发式，不是职业围棋搜索：最多81个候选，只在十几秒一次的落子窗口运行。
        /// 一次查询只创建一张深快照，候选之间先撤掉上一颗试落子再重算气眼；不累计前一个候选的影响。
        /// 无安全点返回false，best保持(-1,-1)，调用方照常进入冷却，不允许在下帧无限重试。
        /// </summary>
        /// <param name="board">已经刷新气眼的真实棋盘；整个查询只读它。</param>
        /// <param name="newcomer">尚未入场的敌棋；这里只把副本放入快照，不改变它的位置和血量。</param>
        /// <param name="intelligence">控制连接距离及压气/避火因素，不影响落子间隔。</param>
        /// <param name="best">成功时的合法落点；提交仍由服务调用Board.Place。</param>
        /// <param name="intention">解释这一手主要用途，供战斗日志展示，不能当作额外增益执行。</param>
        public static bool TryChooseDeployment(BoardModel board, Piece newcomer, int intelligence, out BoardPos best, out string intention)
        {
            best = new BoardPos(-1, -1);
            intention = "连接补阵";
            int bestScore = int.MinValue;
            List<Piece> allies = board.GetPiecesOf(GameSide.Enemy);
            List<Piece> players = board.GetPiecesOf(GameSide.Player);
            if (allies.Count == 0 || players.Count == 0) { return false; }
            BoardModel trial = board.CreateSnapshot();
            Piece added = newcomer.CreateSnapshot();
            var hits = new List<BoardPos>(board.Size * board.Size);
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    Cell cell = board.GetCell(pos);
                    // 真眼受现有围棋规则保护，敌方不能直接塞进已成立的玩家眼位；压气要从外围进行。
                    if (!cell.IsEmpty || cell.IsEyeMarked) { continue; }
                    int allyDistance = int.MaxValue;
                    int neighbors = 0;
                    foreach (Piece ally in allies)
                    {
                        // 大棋按整个身体边界计算距离，不能只认左下角，漏掉Boss右上方的连接机会。
                        int dx = System.Math.Max(0, System.Math.Max(ally.Pos.X - x, x - (ally.Pos.X + ally.Size - 1)));
                        int dy = System.Math.Max(0, System.Math.Max(ally.Pos.Y - y, y - (ally.Pos.Y + ally.Size - 1)));
                        int distance = dx + dy;
                        allyDistance = System.Math.Min(allyDistance, distance);
                        if (distance == 1) { neighbors++; }
                    }
                    // 从既有阵线延伸，避免敌人在玩家后排凭空刷出一颗贴脸棋。
                    if (allyDistance > (intelligence >= 3 ? 3 : 2)) { continue; }
                    // 上一个候选无论通过还是被continue否决，都在这里撤回；不创建81份棋盘/棋子。
                    trial.Remove(added);
                    GoRules.Evaluate(trial);
                    if (!trial.Place(added, pos)) { continue; }
                    GoRules.Evaluate(trial);
                    if (added.IsLocked || !PreservesFormation(board, trial)) { continue; }
                    int eyeGain = 0;
                    int rescue = 0;
                    foreach (Piece ally in allies)
                    {
                        Piece copy = EnemyFormationPlanner.Find(trial, ally.Id);
                        eyeGain += System.Math.Max(0, copy.EyeCount - ally.EyeCount);
                        if (ally.Liberties <= 2) { rescue += System.Math.Max(0, copy.Liberties - ally.Liberties); }
                    }
                    int pressure = 0;
                    foreach (Piece player in players)
                    {
                        Piece copy = EnemyFormationPlanner.Find(trial, player.Id);
                        // 本游戏“气”按连通块共享；压缩大块会影响多颗棋，因此按受影响棋子累计是有意的。
                        pressure += System.Math.Max(0, player.Liberties - copy.Liberties) * (copy.Liberties <= 2 ? 3 : 1);
                    }
                    MoveRules.GetAttackTargets(trial, added, hits);
                    int targets = hits.Count;
                    // 分数单位只是偏好：做眼500 > 解围100 > 连接65 > 射界35。
                    // “气”按整块共享，救回五颗同一块的棋，会累计五颗的收益，刻意鼓励保护主体阵型。
                    int score = eyeGain * 500 + rescue * 100 + neighbors * 65 + targets * 35;
                    score += pressure * (intelligence >= 2 ? 65 : 25);
                    score += System.Math.Min(added.Liberties, 8) * 8 - NearestPlayerDistance(trial, pos) * 8;
                    if (intelligence >= 4)
                    {
                        foreach (Piece player in trial.Pieces)
                        {
                            if (player.Side != GameSide.Player) { continue; }
                            MoveRules.GetAttackTargets(trial, player, hits);
                            if (hits.Contains(pos)) { score -= 24; }
                        }
                    }
                    // 同分保持首次扫描结果，固定种子回放与不同帧率运行才会落在同一个位置。
                    if (score <= bestScore) { continue; }
                    bestScore = score;
                    best = pos;
                    intention = eyeGain > 0 ? "补眼做活" : (rescue > 0 ? "接应解围" : (pressure > 0 ? "压气封堵" : "连接补阵"));
                }
            }
            return best.X >= 0;
        }

        /// <summary>与最近玩家锚点的曼哈顿距离，用来鼓励接近。这里只作评分，合法攻击仍由MoveRules确定。</summary>
        private static int NearestPlayerDistance(BoardModel board, BoardPos from)
        {
            int nearest = int.MaxValue;
            foreach (Piece player in board.Pieces)
            {
                if (player.Side == GameSide.Player) { nearest = System.Math.Min(nearest, Distance(from, player.Pos)); }
            }
            return nearest;
        }

        /// <summary>横向格数+纵向格数；棋盘按四邻连接，所以不用屏幕像素或欧氏距离。</summary>
        private static int Distance(BoardPos a, BoardPos b)
        {
            return System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);
        }
    }
}

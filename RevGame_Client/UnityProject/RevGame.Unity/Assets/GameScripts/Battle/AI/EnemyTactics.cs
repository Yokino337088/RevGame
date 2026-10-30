// ============================================================
// EnemyTactics.cs —— 敌方战术“纯算法”：打谁、走哪、补在哪；不直接搬动实战棋子。
//
// 【它在敌方AI里的位置】
// EnemyStates.cs 决定“什么时候该攻击/推进/增援”，本文件只回答“具体选哪个目标/落点”。
// 分成两层的好处：状态机处理时序，算法处理评分；调整权重不会影响状态流转，反之亦然。
//
// 【三条铁律】
// 1) 只读真实棋盘：所有试走/试落子都在 CreateSnapshot() 的深拷贝里完成，
//    真实棋子的位置、气、眼、血量在查询期间绝不会被改动；
// 2) 安全检查优先于分数：不填真眼、不减少友军眼数、不把有气的友军堵成零气，
//    任何权重都不能越过这道门槛；
// 3) 结果可复现：同分时保留先扫描到的候选，不依赖字典顺序或时间。
//
// 【为什么不做多层搜索】
// 这是可解释的一手启发式（看落子后的气眼与射界），不是 AlphaGo 类搜索：
// 每次只在配置指定的落子窗口运行，最多 81 个候选，不做递归博弈树。
// 好处是手机端可控、每手都有中文意图可解释；代价是算不到多步之后的劫争。
// 评分权重是算法内部相对偏好，不是策划可调数值，因此不放进 Excel。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Battle
{
    /// <summary>
    /// 敌方战术计算器（静态类，无状态，可离线单测）。
    ///
    /// 【三个公开入口，对应三个时机】
    /// · ChooseTarget        ：已有射界时，选这一发打谁；
    /// · ChooseSafeMove      ：没有射界且移动冷却到，选靠近哪一格；
    /// · TryChooseDeployment ：回合布阵后的战术增援，选补在哪一格。
    ///
    /// 【调用者是谁】
    /// 只有 EnemyStates.cs 的状态会调用（棋子状态与指挥状态），BattleService 不直接调，
    /// 这样“AI 什么时候思考”始终由状态机决定，不会出现某处绕过状态偷偷落子。
    ///
    /// intelligence 是 1～4 档观察能力（看得多远、考虑多少因素），**不是行动速度**；
    /// 出手频率、移动冷却、增援数量上限由 Excel 与服务控制。
    /// 每次查询独立创建快照与临时缓冲；快照里的棋子可写，真实 board 里的棋子只读。
    /// </summary>
    /// <summary>AI只依赖只读预测契约；由当前战斗服务提供，绝不另外写一套伤害公式。</summary>
    public interface IAttackPrediction
    {
        bool PredictAttackDefeat(Piece attacker, Piece target);
    }

    // 纯同步战术计算：接收BoardModel和真实棋子，输出目标/落点；不碰Unity视图或直接提交实战移动。
    // 今天复用board.Tactics工具箱，持有者是一张棋盘，禁止改为全局static可变名单。
    public static class EnemyTactics
    {
        /// <summary>
        /// 从合法射程列表选择目标。远程单位先集火玩家远程；其余情况才按距离、残血和气眼评分。
        /// 返回null表示候选已经死亡/消失；调用方不可假设查到的每个坐标仍有棋子。
        /// 评分仅比较优先级，没有伤害单位；10000让预计可击杀通常优先，但真实扣血仍必须走服务。
        /// </summary>
        /// <param name="board">当前棋盘，只读取占格和规则结果。</param>
        /// <param name="attacker">当前行动者，不会在这里扣攻击次数。</param>
        /// <param name="targets">MoveRules算出的合法射程坐标；本方法不会把射程外敌人塞进来。</param>
        /// <param name="intelligence">关卡控制的1～4档；档位只增加评分因素。</param>
        public static Piece ChooseTarget(BoardModel board, Piece attacker, List<BoardPos> targets, int intelligence = 4, IAttackPrediction prediction = null)
        {
            // 集火采用所有远程共用的排序（当前生命、Id），不掺入各自距离或攻击力。
            // 因而有共同射界的射手会持续压同一颗残血远程；目标死亡后下一次攻击立即重选。
            // 只遍历调用方给出的合法射界，不能为了集火穿墙、越射程或击中友军。
            if (attacker.Def.IsRanged)
            {
                Piece focus = null;
                foreach (BoardPos pos in targets)
                {
                    Piece candidate = board.GetPiece(pos);
                    if (candidate == null || !candidate.IsAlive || candidate.BossHidden || candidate.Side == attacker.Side || !candidate.Def.IsRanged) { continue; }
                    if (focus == null || candidate.Hp < focus.Hp || (candidate.Hp == focus.Hp && candidate.Id < focus.Id))
                    {
                        focus = candidate;
                    }
                }
                if (focus != null) { return focus; }
            }
            Piece best = null;
            int bestScore = int.MinValue;
            foreach (BoardPos pos in targets)
            {
                Piece target = board.GetPiece(pos);
                if (target == null || !target.IsAlive || target.BossHidden || target.Side == attacker.Side)
                {
                    continue;
                }
                // 分数分层拉开：优先可能一击击败的目标，其次远程威胁，最后距离。
                // 这里只选择目标；真正扣血仍由服务计算锁死、连通、增益等效果，不能在 AI 中另扣一次。
                // 第一档就近交战；第二档识别远程威胁；第三档会收残血；第四档优先断开脆弱连接。
                // 规则安全检查不分档：低级AI也不能故意拆自己眼阵，只是看问题没有后期全面。
                int score = -Distance(attacker.Pos, target.Pos) * 10;
                if (target.Def.IsRanged)
                {
                    score += 1000;
                }
                if (intelligence >= 3)
                {
                    score += (int)((1f - target.HpRatio) * 500f);
                    if (prediction != null && prediction.PredictAttackDefeat(attacker, target))
                    {
                        score += 10000;
                    }
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
        /// 没有射界且移动冷却完成时，从合法走法里选一个安全落点。
        ///
        /// 【怎么做】
        /// 1) 复制一次真实棋盘；每个候选都“先恢复原位 → 再试走 → 重算气眼”，
        ///    所以候选之间不会累积上一次的影响；
        /// 2) 硬门槛：不能让自己零气、不能让任何友军掉眼或从有气变零气（PreservesFormation）；
        /// 3) 软评分：新射界数 ×1000、距离更近、自身气数；3 档起加“压缩玩家气”，
        ///    4 档再减去“走进去会被多少玩家集火”的风险。
        ///
        /// 【为什么返回原坐标】
        /// 表示“原地等待”，不是失败：可能所有候选都被门槛否决，或都不比现在更接近。
        /// 调用方会把移动冷却推到下一次，堵路时不会每个小步重新全盘搜索。
        /// </summary>
        public static BoardPos ChooseSafeMove(BoardModel board, Piece piece, List<BoardPos> moves, int intelligence = 4)
        {
            // 用户授权近战直接跳至射界附近：只扩大候选位置，下面的保眼/占格/零气门禁仍逐一验证。
            if (!piece.Def.IsRanged && !piece.IsBoss && piece.EyeCount == 0)
            {
                moves.Clear();
                for (int y = 0; y < board.Size; y++)
                {
                    for (int x = 0; x < board.Size; x++)
                    {
                        BoardPos candidate = new BoardPos(x, y);
                        if (board.CanFit(candidate, piece.Size, piece.Side, piece)) { moves.Add(candidate); }
                    }
                }
            }
            BoardPos best = piece.Pos;
            // 无眼近战优先选后排；距离场会绕开占格，不能只比曼哈顿距离而在墙边左右横跳。
            Piece prey = !piece.Def.IsRanged ? FindRangedPrey(board, piece) : null;
            int[,] pursuit = prey != null ? BuildPursuitDistances(board, piece, prey) : null;
            int oldDistance = pursuit != null ? pursuit[piece.Pos.X, piece.Pos.Y] : NearestPlayerDistance(board, piece.Pos);
            // 后排完全不可达时恢复原有接敌策略，不让无穷距离参与乘法评分。
            if (oldDistance == int.MaxValue) { pursuit = null; oldDistance = NearestPlayerDistance(board, piece.Pos); }
            int bestScore = int.MinValue;
            // 本次移动查询只刷新一张独立深试盘，不为每个候选new一整盘。
            // 后续候选仍要恢复副本位置并Evaluate，缓存优化不能省略气眼/安全门槛。
            BoardModel trial = board.Tactics.Trial(board);
            Piece moved = trial.FindPiece(piece.Id);
            List<BoardPos> hits = board.Tactics.Hits; hits.Clear();
            foreach (BoardPos pos in moves)
            {
                Cell cell = board.GetCell(pos);
                if (cell == null || cell.IsEyeMarked)
                {
                    continue;
                }
                // ★ 恢复的是“试算棋盘”，绝不能临时移动真实棋子再移回去，那会让表现/事件读到假状态。
                // 上一次试走可能改变眼标记，所以还原时不受旧眼限制，重算后才校验新候选。
                if (moved.Pos != piece.Pos)
                {
                    trial.Move(moved, piece.Pos, respectEnemyEyes: false);
                }
                GoRules.Evaluate(trial);
                if (!trial.Move(moved, pos))
                {
                    continue;
                }
                GoRules.Evaluate(trial);
                if (moved.IsLocked || !PreservesFormation(board, trial))
                {
                    continue;
                }
                MoveRules.GetAttackTargets(trial, moved, hits);
                int targets = hits.Count;
                int distance = pursuit != null ? pursuit[pos.X, pos.Y] : NearestPlayerDistance(trial, pos);
                // 只接受能打开射界或真正接近的格子，堵路时等待，避免左右横跳。
                if (distance == int.MaxValue || (targets == 0 && distance >= oldDistance))
                {
                    continue;
                }
                // 追后排时，接近目标比偶然打到前排更重要；贴到后排立即获得最高接敌评分。
                int score = pursuit != null ? (oldDistance - distance) * 450 + (distance == 0 ? 2600 : 0) + targets * 80
                    : targets * 1000 - distance * 20;
                score += System.Math.Min(moved.Liberties, 12);
                if (intelligence >= 3)
                {
                    // 前后棋盘都是深快照，比较压气成果不回写真实棋子的气与眼。
                    foreach (Piece player in board.Pieces)
                    {
                        if (player.Side != GameSide.Player)
                        {
                            continue;
                        }
                        Piece copy = EnemyFormationPlanner.Find(trial, player.Id);
                        score += System.Math.Max(0, player.Liberties - copy.Liberties) * 70;
                    }
                }
                if (intelligence >= 4)
                {
                    // 后期AI会估计走到该格后有多少玩家能立即反击，避开集火区；不提高移动频率。
                    foreach (Piece player in trial.Pieces)
                    {
                        if (player.Side != GameSide.Player)
                        {
                            continue;
                        }
                        MoveRules.GetAttackTargets(trial, player, hits);
                        foreach (BoardPos hit in hits)
                        {
                            if (trial.GetPiece(hit) == moved)
                            {
                                score -= 220; break;
                            }
                        }
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score; best = pos;
                }
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
                if (ally.Side != GameSide.Enemy)
                {
                    continue;
                }
                Piece copy = EnemyFormationPlanner.Find(after, ally.Id);
                if (copy == null || copy.EyeCount < ally.EyeCount || (ally.Liberties > 0 && copy.Liberties == 0))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 按“补活自己的棋、连接断点、压缩对手的气、形成射界”给空格评分。
        /// 这是可解释的一手启发式，不是职业围棋搜索：最多81个候选，只在配置指定的落子窗口运行，不在每个渲染帧运行。
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
            // 先清并填入调用方持有的友军/玩家缓冲；它们保存当前同步查询的成员，不能带入下一帧或异步回调。
            List<Piece> allies = board.Tactics.Allies; board.CopyPieces(allies, GameSide.Enemy);
            List<Piece> players = board.Tactics.Players; board.CopyPieces(players, GameSide.Player);
            if (allies.Count == 0 || players.Count == 0)
            {
                return false;
            }
            // 远程本方驻眼优先于所有相对评分；眼的骨架完整且空格可容纳时，不会封死同伴。
            if (newcomer.Def.IsRanged && EyeTactics.TryFindOwnEye(board, GameSide.Enemy, newcomer.Size, out best))
            {
                intention = "远程驻守眼位";
                return true;
            }
            // 补子试放复用独立试盘和新棋副本；每个候选用副本Place/Remove，不把实棋移动到候选格来算。
            BoardModel trial = board.Tactics.Trial(board);
            Piece added = board.Tactics.Added(newcomer);
            List<BoardPos> hits = board.Tactics.Hits; hits.Clear();
            // 连通块只构建一次。共享一口气的五颗棋是一块，不应把打吃奖励算五遍。
            // 下面仍在同一张快照试落子；全盘最多81个候选，只在落子时钟到期时运行。
            // 棋群连通关系只依赖占格，当前实盘未变就复用棋群；候选减气仍从before/after真实气数比较。
            List<List<Piece>> groups = board.TacticalGroups();
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    var pos = new BoardPos(x, y);
                    Cell cell = board.GetCell(pos);
                    // 真眼受现有围棋规则保护，敌方不能直接塞进已成立的玩家眼位；压气要从外围进行。
                    if (!cell.IsEmpty || cell.IsEyeMarked || !board.CanFit(pos, newcomer.Size, newcomer.Side))
                    {
                        continue;
                    }
                    int allyDistance = int.MaxValue;
                    int neighbors = 0;
                    foreach (Piece ally in allies)
                    {
                        // 大棋按整个身体边界计算距离，不能只认左下角，漏掉Boss右上方的连接机会。
                        int dx = System.Math.Max(0, System.Math.Max(ally.Pos.X - x, x - (ally.Pos.X + ally.Size - 1)));
                        int dy = System.Math.Max(0, System.Math.Max(ally.Pos.Y - y, y - (ally.Pos.Y + ally.Size - 1)));
                        int distance = dx + dy;
                        allyDistance = System.Math.Min(allyDistance, distance);
                        if (distance == 1)
                        {
                            neighbors++;
                        }
                    }
                    // 从既有阵线延伸，避免敌人在玩家后排凭空刷出一颗贴脸棋。
                    if (allyDistance > (intelligence >= 3 ? 3 : 2))
                    {
                        continue;
                    }
                    // 上一个候选无论通过还是被continue否决，都在这里撤回；不创建81份棋盘/棋子。
                    trial.Remove(added);
                    // 候选在原棋盘已通过整个身体的占格/眼位检查，撤回上个候选后占格正好恢复原状。
                    // 忽略试盘残留眼标记再放入，随后仍完整重算气眼；省去每候选一次重复Evaluate，不省安全验收。
                    if (!trial.Place(added, pos, respectEnemyEyes: false))
                    {
                        continue;
                    }
                    GoRules.Evaluate(trial);
                    if (added.IsLocked || !PreservesFormation(board, trial))
                    {
                        continue;
                    }
                    int eyeGain = 0;
                    int rescue = 0;
                    foreach (Piece ally in allies)
                    {
                        Piece copy = EnemyFormationPlanner.Find(trial, ally.Id);
                        eyeGain += System.Math.Max(0, copy.EyeCount - ally.EyeCount);
                        if (ally.Liberties <= 2)
                        {
                            rescue += System.Math.Max(0, copy.Liberties - ally.Liberties);
                        }
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
                    // 战术优先、阵型兜底：实际封住一口气，优先于在后排无目的地堆棋。
                    // 权重没有血量/时间单位，是算法内部的相对偏好，因此不作为策划数值进表。
                    //
                    // 评分构成（数值越大越优先，仅用于候选之间比较）：
                    //   eyeGain ×500      ：做眼活棋，最多计 2 只（min），防止大块补眼淹没进攻评分
                    //   rescue  ×100      ：给只剩 1~2 气的友军续气
                    //   neighbors×65      ：与友军相连，形成一整块
                    //   targets ×35       ：落下即有合法射界
                    //   pressure×45/65    ：压缩玩家气（2 档起加权，靠近吃更值钱）
                    //   attackScore       ：围困/打吃/断点/封逃路，见 ScoreEncirclement
                    //   自身气 ×8、距离 ×8：同等条件下选更安全、更靠近战线
                    string attackPlan;
                    int attackScore = ScoreEncirclement(board, trial, groups, pos, out attackPlan);
                    int score = System.Math.Min(eyeGain, 2) * 500 + rescue * 100 + neighbors * 65 + targets * 35;
                    score += attackScore + pressure * (intelligence >= 2 ? 65 : 45);
                    // 追击兵接近远程后，增援优先沿其阵线封掉该远程棋群的气；眼位仍由前面的门槛保护。
                    foreach (Piece player in players)
                    {
                        if (!player.Def.IsRanged) { continue; }
                        Piece copy = trial.FindPiece(player.Id);
                        int lost = System.Math.Max(0, player.Liberties - copy.Liberties);
                        score += lost * 900 + (lost > 0 && copy.Liberties == 0 ? 2200 : 0);
                    }
                    score += System.Math.Min(added.Liberties, 8) * 8 - NearestPlayerDistance(trial, pos) * 8;
                    if (intelligence >= 4)
                    {
                        foreach (Piece player in trial.Pieces)
                        {
                            if (player.Side != GameSide.Player)
                            {
                                continue;
                            }
                            MoveRules.GetAttackTargets(trial, player, hits);
                            if (hits.Contains(pos))
                            {
                                score -= 24;
                            }
                        }
                    }
                    // 同分保持首次扫描结果（严格大于才替换）：固定输入必然得到同一落点，
                    // 不同帧率、不同机器上的回放才不会漂移；不要为了“更随机”改成 >=。
                    if (score <= bestScore)
                    {
                        continue;
                    }
                    bestScore = score;
                    best = pos;
                    // 意图只用于战斗日志/弹幕解释“这一手在干什么”，不产生任何额外效果；
                    // 优先级与实际评分一致：先报战术，再报做眼/解围/连接。
                    intention = attackScore > 0 ? attackPlan : (eyeGain > 0 ? "补眼做活" : (rescue > 0 ? "接应解围" : "连接补阵"));
                }
            }
            return best.X >= 0;
        }

        /// <summary>
        /// 围追堵截评分：先看真实减气，再看打吃、封逃路和断点。
        ///
        /// 【评分场景表】（数值只是候选排序，不是伤害）
        ///   对方从有气变 0 气（收网围困）：+3200，最快让目标锁死、挨双倍伤害
        ///   对方从多气变 1 气（打吃逼应）  ：+1800，逼对方补气或救援
        ///   减到 ≤3 气（夹击压气）          ：+600，持续施压
        ///   每封掉 1 口气                    ：+180，基础收益
        ///   该口气通向的空点越多              ：每个 +90（越像出头通道越值得先封）
        ///   同时接触两个玩家棋群（切断接应）  ：额外 +(块数−1)×1100
        ///
        /// 打吃=对方只剩一口气；本游戏零气是锁死、不是围棋立即提子，仍由战斗伤害击败它。
        /// 切断=占住两个不同玩家棋群共同的连接点；并非假称落子能拆开已经连接的棋群。
        /// 所有权重只是候选之间的优先级，不是伤害、概率或冷却，不需要进入数值表。
        /// </summary>
        private static int ScoreEncirclement(BoardModel before, BoardModel after, List<List<Piece>> groups, BoardPos pos, out string plan)
        {
            int score = 0;
            int touchedGroups = 0;
            int strongest = 0;
            plan = "压气封堵";
            foreach (List<Piece> group in groups)
            {
                if (group[0].Side != GameSide.Player)
                {
                    continue;
                }
                bool touched = false;
                foreach (Piece player in group)
                {
                    foreach (BoardPos boundary in BoardModel.Boundary(player))
                    {
                        if (boundary == pos)
                        {
                            touched = true; break;
                        }
                    }
                    if (touched)
                    {
                        break;
                    }
                }
                if (!touched)
                {
                    continue;
                }
                touchedGroups++;
                Piece old = group[0];
                Piece copy = after.FindPiece(old.Id);
                int lost = System.Math.Max(0, old.Liberties - copy.Liberties);
                if (lost == 0)
                {
                    continue;
                }
                int benefit = lost * 180;
                string reason = "压气封堵";
                if (old.Liberties > 0 && copy.Liberties == 0)
                {
                    benefit += 3200; reason = "收网围困";
                }
                else if (old.Liberties > 1 && copy.Liberties == 1)
                {
                    benefit += 1800; reason = "打吃逼应";
                }
                else if (copy.Liberties <= 3)
                {
                    benefit += 600; reason = "夹击压气";
                }
                // 一口气若通向许多空格，往往是出头通道；先封它比填后方狭窄空点更有效。
                // 这里只观察一层邻居，是有界的一手启发式，不能冒称AlphaGo多层搜索。
                int exits = 0;
                foreach (BoardPos next in pos.GetOrthogonalNeighbors())
                {
                    Cell cell = before.GetCell(next);
                    if (cell != null && cell.IsEmpty && !cell.IsEyeMarked)
                    {
                        exits++;
                    }
                }
                benefit += exits * 90;
                score += benefit;
                if (benefit > strongest)
                {
                    strongest = benefit; plan = reason;
                }
            }
            if (touchedGroups >= 2)
            {
                score += (touchedGroups - 1) * 1100;
                if (strongest < 1800)
                {
                    plan = "切断接应";
                }
            }
            return score;
        }

        /// <summary>
        /// 查询一次推进的合法候选：无眼近战追远程时允许配置长度的直线冲锋，逐格检查占用；
        /// 没有远程目标时过滤普通棋跨多格落点，马步保留原有跳法，守眼棋不主动离阵。
        /// 这是通用候选计算，不决定何时移动、不消费冷却、不提交位置。
        /// result 由调用方复用，本方法会重填它；倒序删除避免下标前移漏掉候选。
        /// </summary>
        public static void GetAdvanceCandidates(BoardModel board, Piece actor, List<BoardPos> result)
        {
            if (actor.EyeCount > 0) { result.Clear(); return; }
            if (!actor.Def.IsRanged && actor.Def.Pattern != MovePattern.Knight && FindRangedPrey(board, actor) != null)
            {
                // 冲锋沿四个直线方向连续走空格，显示层直线滑动也不会斜穿拐角里的棋子。
                // 需要绕路时由反向距离场选择下一个拐点，分两次行动完成；整个大棋身体都必须能放下。
                result.Clear();
                foreach (BoardPos adjacent in actor.Pos.GetOrthogonalNeighbors())
                {
                    int dx = adjacent.X - actor.Pos.X;
                    int dy = adjacent.Y - actor.Pos.Y;
                    for (int step = 1; step <= Balance.EnemyChaseSteps; step++)
                    {
                        var next = new BoardPos(actor.Pos.X + dx * step, actor.Pos.Y + dy * step);
                        if (!board.CanFit(next, actor.Size, actor.Side, actor)) { break; }
                        result.Add(next);
                    }
                }
                return;
            }
            MoveRules.GetMoveTargets(board, actor, result);
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if (actor.Def.Pattern != MovePattern.Knight && Distance(result[i], actor.Pos) > 1)
                {
                    result.RemoveAt(i);
                }
            }
        }

        /// <summary>先按曼哈顿距离选最近玩家远程，同距按Id稳定选择；调用者再检查可达性，不可达时回退常规接敌。</summary>
        private static Piece FindRangedPrey(BoardModel board, Piece actor)
        {
            Piece best = null;
            int nearest = int.MaxValue;
            foreach (Piece player in board.Pieces)
            {
                if (player.Side != GameSide.Player || !player.IsAlive || !player.Def.IsRanged) { continue; }
                int distance = Distance(actor.Pos, player.Pos);
                if (distance < nearest || (distance == nearest && (best == null || player.Id < best.Id)))
                {
                    best = player; nearest = distance;
                }
            }
            return best;
        }

        /// <summary>从目标身体外沿反向广度搜索；路径距离0表示已贴身，不会穿过第三方棋子或受保护眼位。</summary>
        private static int[,] BuildPursuitDistances(BoardModel board, Piece actor, Piece prey)
        {
            // 距离数组和BFS队列复用，但下面会完整重置每格为不可达，随后从目标边界扩散。
            // 省的是数组/队列创建，不是跳过障碍、眼位保护或大棋CanFit校验。
            var distances = board.Tactics.Distances;
            var queue = board.Tactics.Queue; queue.Clear();
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    distances[x,y] = int.MaxValue;
                    var pos = new BoardPos(x,y);
                    int dx = System.Math.Max(0, System.Math.Max(prey.Pos.X - (x + actor.Size - 1), x - (prey.Pos.X + prey.Size - 1)));
                    int dy = System.Math.Max(0, System.Math.Max(prey.Pos.Y - (y + actor.Size - 1), y - (prey.Pos.Y + prey.Size - 1)));
                    if (dx + dy != 1 || !board.CanFit(pos, actor.Size, actor.Side, actor)) { continue; }
                    distances[x,y] = 0; queue.Enqueue(pos);
                }
            }
            while (queue.Count > 0)
            {
                BoardPos from = queue.Dequeue();
                for (int direction = 0; direction < 4; direction++)
                {
                    // 这里用四个整数分支替代GetOrthogonalNeighbors创建的小数组。
                    // 顺序也必须与原数组一致：距离相同的路径先后可能影响AI，性能优化不能暗改同分偏好。
                    // 保持原四邻数组的右、左、上、下顺序，省掉数组分配且不改同分路径偏好。
                    BoardPos next = new BoardPos(from.X + (direction == 0 ? 1 : direction == 1 ? -1 : 0), from.Y + (direction == 2 ? 1 : direction == 3 ? -1 : 0));
                    if (!board.CanFit(next, actor.Size, actor.Side, actor) || distances[next.X,next.Y] != int.MaxValue) { continue; }
                    distances[next.X,next.Y] = distances[from.X,from.Y] + 1; queue.Enqueue(next);
                }
            }
            return distances;
        }

        /// <summary>
        /// 统计真正未占用的格子，不等于合法增援点数：眼位保护、气眼安全仍由选点算法检查。
        /// 此查询只返回数字，不读增援预算、不改模型，也不决定是否停援。
        /// </summary>
        public static int CountEmptyCells(BoardModel board)
        {
            int count = 0;
            for (int y = 0; y < board.Size; y++)
            {
                for (int x = 0; x < board.Size; x++)
                {
                    if (board.CanPlace(new BoardPos(x, y)))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>与最近玩家锚点的曼哈顿距离，用来鼓励接近。这里只作评分，合法攻击仍由MoveRules确定。</summary>
        private static int NearestPlayerDistance(BoardModel board, BoardPos from)
        {
            int nearest = int.MaxValue;
            foreach (Piece player in board.Pieces)
            {
                if (player.Side == GameSide.Player)
                {
                    nearest = System.Math.Min(nearest, Distance(from, player.Pos));
                }
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

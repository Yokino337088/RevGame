// ============================================================
// GameTacticsChecks.cs —— 眼阵、试算隔离、射界和实际战斗时长的业务验收。
// 在 Editor 外层统一运行；只检查公开模型/服务的真实结果，不复制气眼或伤害公式。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

namespace RevGame.Editor
{
    public static class GameTacticsChecks
    {
        private static int _count;
        public static string LastResult { get; private set; } = "未运行";

        /// <summary>非 Play 运行，防止独立服务的广播被当前 UI 当成实战事件。</summary>
        public static string Run()
        {
            if (UnityEngine.Application.isPlaying) { return "请先退出 Play 再运行战术断言"; }
            _count = 0;
            var report = new StringBuilder();
            RoundDef previous = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
            {
                for (int round = 1; round <= Consts.RoundsPerChapter; round++)
                {
                    var battle = new BattleService(true, 1024);
                    Check(battle.StartRound(chapter, round), "眼阵完整生成：" + chapter + "/" + round);
                    RoundDef current = LevelCatalog.GetRound(chapter, round);
                    if (previous != null)
                    {
                        Check(current.HpScale > previous.HpScale && current.AttackScale > previous.AttackScale, "敌方生命与攻击基础倍率逐轮增长，跨章不倒退");
                        Check(current.EnemyKinds.Count >= previous.EnemyKinds.Count && current.Intelligence >= previous.Intelligence, "数量与AI档位不倒退");
                    }
                    previous = current;
                    List<Piece> guards = new List<Piece>(battle.EnemyPieces).FindAll(p => !p.IsBoss);
                    Check(guards.Count >= 6 && guards.Count <= 19, "普通敌棋六至十九名，Boss另计");
                    Check(guards.Exists(p => p.Def.Kind == PieceKind.Crossbow) && guards.Exists(p => p.Def.Kind == PieceKind.Archer) && guards.Exists(p => p.Def.Kind == PieceKind.MageFire), "重弩/弓箭/火法均存在");
                    int expectedEyes = guards.Count >= 8 ? 2 : 1;
                    Check(guards.FindAll(p => p.EyeCount >= expectedEyes).Count >= (expectedEyes == 2 ? 8 : 5), "真实规则确认完整守眼骨架");
                    Check(guards.TrueForAll(p => !p.IsLocked), "敌棋出生不锁死");
                    Check(battle.Board.GetAllPieces().Count == battle.EnemyPieces.Count, "全部名单都有完整棋盘占格");
                }
            }
            watch.Stop();
            report.AppendLine("66个敌阵生成耗时合计 " + watch.ElapsedMilliseconds + "ms（编辑器单次测量，不代表手机性能）");
            VerifySnapshotAndFallback();
            VerifyRangedAndMovement();
            VerifyBattleDeployment();
            VerifyReplay(report);
            VerifyCampaign(report);
            LastResult = "通过 " + _count + " 条战术断言\n" + report;
            return LastResult;
        }

        private static void VerifySnapshotAndFallback()
        {
            var board = new BoardModel(9);
            var original = new Piece(1, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player, hpScale: 1.65f);
            board.Place(original, new BoardPos(3, 3));
            GoRules.Evaluate(board);
            BoardModel copy = board.CreateSnapshot();
            Piece copied = copy.GetPiece(original.Pos);
            int hp = original.Hp;
            int liberties = original.Liberties;
            copied.TakeDamage(10);
            copy.Move(copied, new BoardPos(0, 0));
            GoRules.Evaluate(copy);
            Check(original.Hp == hp && original.Pos == new BoardPos(3, 3) && original.Liberties == liberties, "试走和扣血不污染实战棋子");
            Check(copied.MaxHp == original.MaxHp && !ReferenceEquals(copied, original), "深复制精确保留最大生命，图鉴以外没有共享可变对象");

            var players = new List<Piece>();
            var playerPositions = new BoardModel(9);
            int id = 1;
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    if (x != 0 && x != 8 && y != 0 && y != 8) { continue; }
                    var player = new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
                    playerPositions.Place(player, new BoardPos(x, y));
                    players.Add(player);
                }
            }
            List<Piece> enemies = MakeEnemies();
            string reason;
            Check(EnemyFormationPlanner.TryArrange(enemies, players, false, out reason) && reason.Contains("散阵"), "边缘被占时完整降级为合法散阵");
            Check(enemies.Count == 8 && players.TrueForAll(p => p.Hp == p.MaxHp), "降级不减员、不修改玩家血量");
            for (int y = 1; y < 8; y++)
            {
                for (int x = 1; x < 8; x++)
                {
                    var player = new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
                    playerPositions.Place(player, new BoardPos(x, y), respectEnemyEyes: false);
                    players.Add(player);
                }
            }
            enemies = MakeEnemies();
            Check(!EnemyFormationPlanner.TryArrange(enemies, players, false, out reason) && reason.Contains("保持不变"), "满盘明确拒绝，不吞掉关键敌棋");
            Check(enemies.TrueForAll(p => p.Pos == default(BoardPos)), "布阵失败不提交部分落点");
        }

        /// <summary>推进真实战斗，记录每次人数增长的时刻；不能只验证一个“AI开启”开关。</summary>
        private static void VerifyBattleDeployment()
        {
            int total = 0;
            foreach (int chapter in new[] { 1, 6 })
            {
                foreach (int round in new[] { 1, 9, 11 })
                {
                    var battle = new BattleService(true, 381);
                    Check(battle.StartRound(chapter, round), "战斗落子夹具初始化");
                    // 同一角落的单兵故意制造弱势，验证AI施压和通关保障能够同时成立。
                    Check(battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(0, 0)), "玩家保留一颗合法棋子");
                    battle.Tick(3f);
                    Check(battle.EnemyDeploymentCount == 0, "准备期不会偷偷给敌人落子");
                    Check(battle.StartBattle(), "落子AI夹具真实开战");
                    int previous = 0;
                    float lastTime = 0f;
                    while (battle.Phase == GamePhase.Battle && battle.BattleElapsed < 92f)
                    {
                        battle.Tick(Consts.BattleSimulationStepSeconds);
                        if (battle.EnemyDeploymentCount == previous) { continue; }
                        Check(battle.EnemyDeploymentCount == previous + 1, "每个落子窗口只增加一颗敌棋");
                        Check(battle.BattleElapsed - lastTime >= 12f && battle.BattleElapsed < 60f, "至少间隔12秒且60秒后停援");
                        Check(battle.Board.GetPiecesOf(GameSide.Enemy).Count <= 26, "敌军存活数量遵守上限");
                        Piece newest = battle.EnemyPieces[battle.EnemyPieces.Count - 1];
                        Check(battle.Board.GetPiece(newest.Pos) == newest && !newest.IsLocked, "实际落点合法且新棋不会无气出生");
                        previous = battle.EnemyDeploymentCount;
                        lastTime = battle.BattleElapsed;
                        total++;
                    }
                    Check(battle.LastWinner == GameSide.Player && battle.BattleElapsed <= 87f, "加入敌方增援后弱势玩家仍由真实支援取胜");
                    Check(previous <= 2 + battle.EnemyIntelligence, "章节AI档位的落子总预算未超限");
                    int count = battle.EnemyDeploymentCount;
                    battle.Tick(30f);
                    Check(battle.EnemyDeploymentCount == count, "结算期不会出现迟到落子");
                    Check(battle.StartRound(chapter, 1) && battle.EnemyDeploymentCount == 0, "重开归零落子计数和冷却");
                }
            }
            Check(total > 0, "真实战斗中确实触发过敌方落子");

            var probe = new BattleService(false, 3);
            probe.StartRound(1, 9);
            probe.TryPlayCard(probe.Hand[0].Id, new BoardPos(0, 0));
            string original = probe.Board.Dump();
            var reinforcement = new Piece(9000, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Enemy);
            BoardPos chosen;
            string purpose;
            Check(EnemyTactics.TryChooseDeployment(probe.Board, reinforcement, 4, out chosen, out purpose), "战术算法能找到有效的接应位置");
            Check(probe.Board.Dump() == original && probe.Board.GetPiece(chosen) == null, "候选试算没有把棋落进实盘");
            Check(!probe.Board.GetCell(chosen).IsEyeMarked, "自动落子不填任何已成立的眼位");
        }

        private static List<Piece> MakeEnemies()
        {
            var result = new List<Piece>();
            foreach (PieceKind kind in LevelCatalog.GetRound(1, 11).EnemyKinds)
            {
                result.Add(new Piece(1000 + result.Count, PieceCatalog.Get(kind, StarTier.One), GameSide.Enemy));
            }
            return result;
        }

        private static void VerifyRangedAndMovement()
        {
            var board = new BoardModel(9);
            var target = new Piece(1, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
            var crossbow = new Piece(2, PieceCatalog.Get(PieceKind.Crossbow, StarTier.One), GameSide.Enemy);
            board.Place(target, new BoardPos(0, 0));
            board.Place(crossbow, new BoardPos(8, 8));
            GoRules.Evaluate(board);
            Check(MoveRules.GetAttackTargets(board, crossbow).Contains(target.Pos), "敌方重弩可以跨盘施压，守眼也有真实射界");
            board.Remove(crossbow);
            var archer = new Piece(3, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Enemy);
            board.Place(archer, new BoardPos(0, 6));
            Check(MoveRules.GetAttackTargets(board, archer).Contains(target.Pos), "弓箭六格直线可命中");
            var screen = new Piece(4, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy);
            board.Place(screen, new BoardPos(0, 3));
            Check(!MoveRules.GetAttackTargets(board, archer).Contains(target.Pos), "弓箭仍受阻挡，不与重弩混用规则");
            board.Remove(archer);
            var mage = new Piece(5, PieceCatalog.Get(PieceKind.MageFire, StarTier.One), GameSide.Enemy);
            board.Place(mage, new BoardPos(0, 6));
            Check(MoveRules.GetAttackTargets(board, mage).Contains(target.Pos), "火法仍通过一颗炮架攻击");
            board.Remove(screen);
            Check(!MoveRules.GetAttackTargets(board, mage).Contains(target.Pos), "火法无炮架不可凭空开火");

            var battle = new BattleService(false, 12);
            battle.StartRound(1, 9);
            foreach (Piece enemy in battle.EnemyPieces)
            {
                if (enemy.EyeCount > 0) { Check(MoveRules.GetMoveTargets(battle.Board, enemy).Count == 0, "两眼守子不自动拆阵"); }
            }
            var aim = new BoardModel(9);
            var sniper = new Piece(100, PieceCatalog.Get(PieceKind.Crossbow, StarTier.One), GameSide.Enemy);
            var near = new Piece(101, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
            var threat = new Piece(102, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player);
            aim.Place(sniper, new BoardPos(4, 4));
            aim.Place(near, new BoardPos(4, 3));
            aim.Place(threat, new BoardPos(0, 0));
            GoRules.Evaluate(aim);
            List<BoardPos> shots = MoveRules.GetAttackTargets(aim, sniper);
            Check(EnemyTactics.ChooseTarget(aim, sniper, shots, 1) == near, "一档AI优先就近目标");
            Check(EnemyTactics.ChooseTarget(aim, sniper, shots, 2) == threat, "二档AI真实改为优先远程威胁");
            near.TakeDamage(near.Hp - 1);
            Check(EnemyTactics.ChooseTarget(aim, sniper, shots, 3) == near, "三档AI真实改为优先一击收残血");
            BoardModel snapshot = battle.Board.CreateSnapshot();
            Piece bone = snapshot.GetPiecesOf(GameSide.Enemy)[0];
            string before = snapshot.Dump();
            BoardPos result = EnemyTactics.ChooseSafeMove(snapshot, bone, new List<BoardPos> { new BoardPos(5, 5) });
            Check(result == bone.Pos && before == snapshot.Dump(), "错误候选也不能损伤原有眼阵或污染输入棋盘");
        }

        private static void VerifyReplay(StringBuilder report)
        {
            foreach (int chapter in new[] { 1, 3, 6 })
            {
                foreach (int round in new[] { 1, 9 })
                {
                    var battle = new BattleService(true, 701 + chapter * 10 + round);
                    battle.StartRound(chapter, round);
                    for (int card = 0; card < Consts.MaxHandSize; card++)
                    {
                        bool placed = false;
                        for (int y = 0; y < 9 && !placed; y++)
                        {
                            for (int x = 0; x < 9 && !placed; x++)
                            {
                                placed = battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(x, y));
                            }
                        }
                        Check(placed, "固定种子六牌实际布置");
                    }
                    int playerDamage = 0;
                    int enemyDamage = 0;
                    object owner = new object();
                    RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
                    {
                        if (hit.TargetSide == GameSide.Player) { playerDamage += hit.Damage; }
                        else { enemyDamage += hit.Damage; }
                    }, owner: owner);
                    try
                    {
                        battle.Tick(3f);
                        Check(battle.StartBattle(), "固定种子实际开战");
                        battle.Tick(Consts.VictoryAssistFinishSeconds + Consts.BattleSimulationStepSeconds);
                        Check(battle.LastWinner == GameSide.Player && battle.Phase == GamePhase.Settle, "固定种子对局最终可胜");
                        Check(playerDamage > 0 && enemyDamage > 0, "双方均有真实伤害，敌方不是纯站桩挨打");
                        // 最低验收线是15秒；固定步恰好落在15.0也应通过，不能误判成未达到15秒。
                        Check(battle.BattleElapsed >= 15f && battle.BattleEventCount >= 2, "交战至少15秒且展开两次以上事件");
                        report.AppendLine(chapter + "/" + round + "：" + battle.BattleElapsed + "秒，事件" + battle.BattleEventCount + "，我方受伤" + playerDamage + "，敌方受伤" + enemyDamage + "，" + battle.VictorySupportStatus);
                    }
                    finally { RevEvent.RemoveAllByOwner(owner); }
                }
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException("战术验收失败：" + message); }
            _count++;
        }

        /// <summary>
        /// 真正用同一份服务连续推进六章，而不是每回合重新造空棋盘。
        /// 这能检查“残局继承 + 新敌阵 + 六牌 + 奖励”一起运行时是否挤满棋盘或卡住流程。
        /// 固定策略仅代表回归样本，不等于所有玩家布阵都完成了平衡验收。
        /// </summary>
        private static void VerifyCampaign(StringBuilder report)
        {
            foreach (int seed in new[] { 20261018, 701 })
            {
                var battle = new BattleService(true, seed);
                Check(battle.BeginCampaign(1), "连续闯关从第一章正常开始");
                int finished = 0;
                float seconds = 0f;
                int events = 0;
                object owner = new object();
                RevEvent.AddEventListener<bool>(Events.GameFinished, won => { if (won) { finished++; } }, owner: owner);
                try
                {
                    for (int index = 0; index < LevelCatalog.TotalRounds; index++)
                    {
                        Check(battle.Chapter == index / Consts.RoundsPerChapter + 1 && battle.Round == index % Consts.RoundsPerChapter + 1, "连续回合没有跳章或重复");
                        Check(battle.Hand.Count == Consts.MaxHandSize, "带残局的下一回合仍发满六牌");
                        while (battle.Hand.Count > 0)
                        {
                            int cardId = battle.Hand[0].Id;
                            battle.SelectCard(cardId);
                            bool placed = false;
                            for (int y = 0; y < Consts.BoardSize && !placed; y++)
                            {
                                for (int x = 0; x < Consts.BoardSize && !placed; x++)
                                {
                                    var pos = new BoardPos(x, y);
                                    if (battle.CanPlaySelectedAt(pos)) { placed = battle.TryPlayCard(cardId, pos); }
                                }
                            }
                            // 极满残局下允许保留手牌；服务本来就允许至少一颗己方棋子开战。
                            if (!placed) { break; }
                        }
                        battle.Tick(3f);
                        Check(battle.StartBattle(), "带残局正常开战");
                        battle.Tick(Consts.BattleDurationSeconds);
                        Check(battle.Phase == GamePhase.Settle && battle.LastWinner == GameSide.Player, "连续闯关本轮真实取胜");
                        seconds += battle.BattleElapsed;
                        events += battle.BattleEventCount;
                        Check(battle.CompleteSettle(), "完整敌阵可在真实残局上生成（" + seed + "，" + battle.Chapter + "/" + battle.Round + "）：" + battle.LastError);
                        if (battle.Phase == GamePhase.Reward) { Check(battle.ChooseReward(0), "第一项章节奖励可正常进入下一章"); }
                    }
                    Check(finished == 1 && battle.UnlockedChapter == Consts.ChapterCount, "最终通关只通知一次且解锁六章");
                    report.AppendLine("连续闯关种子" + seed + "：66回合取胜，战斗合计" + seconds + "秒，事件" + events + "次，奖励固定选第1项");
                }
                finally { RevEvent.RemoveAllByOwner(owner); }
            }
        }
    }
}

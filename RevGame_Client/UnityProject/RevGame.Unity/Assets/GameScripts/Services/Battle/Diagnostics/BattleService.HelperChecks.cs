#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>只在Editor创建独立内存夹具，验证正式入口和失败边界，不改变当前场景/存档。</summary>
    public sealed partial class BattleService
    {
#if REVGAME_OFFLINE_TESTS
        /// <summary>控制台独立回合覆盖只接真实关卡重置，绝不设胜负或加属性；失败后的下一轮覆盖不冒充连续通关。</summary>
        public bool BeginHelperBalanceFixture(int chapter, int round) { return BeginCampaignAtRound(chapter, round); }
#endif
        public static void VerifyHelperRules(Action<bool, string> check)
        {
            var battle = new BattleService(false, 781);
            object owner = new object();
            int notices = 0, shots = 0;
            HelperShotInfo lastShot = default;
            RevEvent.AddEventListener<string>(Events.BoardRuleNotice, text => notices++, owner: owner);
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, info => { shots++; lastShot = info; }, owner: owner);
            try
            {
                battle.Chapter = 1; battle.Round = 1;
                var player = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                var enemy = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
                battle.RefreshGoState();
                check(battle.HandleBoardClick(player.Pos) && battle.SelectedPieceId == player.Id, "点击玩家棋选择移除对象");
                int version = battle.Board.TopologyVersion;
                long historical = battle.LifetimeScore;
                check(!battle.RemovePlayerPiece(enemy.Id) && battle.Board.TopologyVersion == version, "不能移除敌棋，拒绝不改占格");
                check(battle.RemovePlayerPiece(player.Id) && battle.Board.FindPiece(player.Id) == null && battle.SelectedPieceId == 0, "移除清占格/名单/选择");
                check(battle.LifetimeScore == historical, "移除不发击杀分");

                battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                for (int i = 0; i < Balance.RangedLimit; i++)
                {
                    battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(i % 9, i / 9));
                }
                battle.DealHand();
                int handCount = battle.Hand.Count;
                int cardCount = battle._cardsPlayedThisRound;
                check(!battle.PlaceCard(PieceKind.Archer, StarTier.One, new BoardPos(8, 7)) && notices == 1, "第三十一个远程被拒绝并发数量提示");
                check(battle.Hand.Count == handCount && battle._cardsPlayedThisRound == cardCount, "远程超限不消耗手牌或额度");
                check(!battle.TryDeployReinforcement(PieceKind.Crossbow, StarTier.Two), "远程援军同样遵守三十上限");
                check(battle.RemovePlayerPiece(battle._playerPieces[0].Id) && battle.PlaceCard(PieceKind.Archer, StarTier.One, new BoardPos(8, 7)), "移除远程后可以重新部署");

                battle.StopEnemyAI(); battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                battle.Chapter = 5; battle.Round = 9;
                for (int i = 0; i < 54; i++)
                {
                    battle.AddHelperCheckPiece(GameSide.Player, i < 40 ? PieceKind.Swordman : PieceKind.Archer, new BoardPos(i % 9, i / 9));
                }
                List<Piece> swallowed;
                List<Piece> retained = battle.PlanBossSwallow(LevelCatalog.GetRound(5, 10), false, out swallowed);
                check(retained.Count == Balance.BossPlayerPieceLimit && swallowed.Count == 54 - Balance.BossPlayerPieceLimit, "吞噬只处理超过四十五的部分");
                check(swallowed.TrueForAll(p => !p.Def.IsRanged), "同星优先吞噬近战");
                check(battle._playerPieces.Count == 54 && battle.Board.Pieces.Count == 54, "吞噬预检不改变真实棋盘");
                check(battle.StartRound(5, 10), "拥挤盘吞噬后完整三乘三Boss必须出生");
                check(battle._playerPieces.Count == Balance.BossPlayerPieceLimit && battle.LastBossSwallowedCount == 9, "成功交割实际只吞九颗");

                battle.StopEnemyAI(); battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                battle.Chapter = 1; battle.Round = 1; battle.IsBossRound = false;
                player = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                enemy = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
                var ranged = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(7, 8));
                battle.Phase = GamePhase.Battle; battle.RefreshGoState(); battle.ResetHelperRound();
                check(battle.SetHelperTarget(enemy.Id), "点击敌棋可以指定帮手目标");
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.Helper.TargetId == enemy.Id, "指定目标覆盖默认远程优先");
                battle.SetHelperTarget(0); battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.Helper.TargetId == ranged.Id, "无指令优先敌远程");
                battle.DamagePiece(enemy, 1, player);
                check(battle.CombatScore == Balance.HelperPlayerHitScore, "玩家真实普攻赚战斗能源");
                long before = battle.LifetimeScore;
                battle.CombatScore = 20; battle._battleElapsed = 4; battle._helperNextMove = 100;
                battle._helperNextShot = 0;
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(shots > 0 && !lastShot.Beam && battle.CombatScore < 20, "普通弹幕真实扣分，返分不足自给自足");
                check(battle.LifetimeScore >= before && battle.Board.FindPiece(0) == null, "花能源不扣历史积分；帮手没有占格身份");
                battle.CombatScore = 0; shots = 0; battle._battleElapsed += 10;
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(shots == 0, "钱包为空不发免费弹幕");
                battle.CombatScore = Balance.HelperBeamReserve + Balance.HelperBeamTickCost;
                battle.ChapterScore = Balance.HelperBeamScore; battle._battleElapsed += 1;
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(lastShot.Beam && battle.CombatScore == Balance.HelperBeamReserve, "足够本章成绩解锁持续激光并逐步扣积分");
                battle.CombatScore = 0; battle._battleElapsed += Balance.BattleSimulationStepSeconds;
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle._helperBeamUntil == 0, "持续激光能量耗尽立即停止");
                check(battle.RemovePlayerPiece(player.Id) && battle.LastWinner == GameSide.Enemy, "无敌帮手不能把玩家全灭改为胜利");

                battle.StopEnemyAI(); battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                player = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                enemy = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Swordman, new BoardPos(8, 8));
                battle.Phase = GamePhase.Battle; battle.RefreshGoState();
                var candidates = new List<BoardPos>();
                EnemyTactics.GetAdvanceCandidates(battle.Board, enemy, candidates);
                BoardPos jump = EnemyTactics.ChooseSafeMove(battle.Board, enemy, candidates, 4);
                check(Math.Abs(jump.X - player.Pos.X) + Math.Abs(jump.Y - player.Pos.Y) <= 2, "远处敌近战跳到玩家远程附近接敌");
                check(enemy.Pos == new BoardPos(8, 8), "跳跃查询不临时移动真实棋子");
                battle.IsBossRound = true;
                var boss = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, 1, true, true);
                battle._board.Place(boss, new BoardPos(6, 6)); battle._enemyPieces.Add(boss);
                battle.ResetHelperRound(); battle.SetHelperTarget(enemy.Id);
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.Helper.TargetId == boss.Id, "Boss回合主将优先于玩家指定普通目标");
                battle._board.Remove(boss); battle._enemyPieces.Remove(boss);
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.Helper.TargetId == enemy.Id, "Boss消失后恢复玩家指定目标");
                var host = new HelperOwner(battle);
                // 构造拥挤输入只测试服务动作；帮手不占格，即便81格全满也能从盘外输出。
                for (int y = 0; y < 6; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        var pos = new BoardPos(x, y);
                        if (battle.Board.GetPiece(pos) == null) { battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Swordman, pos); }
                    }
                }
                version = battle.Board.TopologyVersion;
                host.Crawl();
                check(battle.Helper.Outside && !battle.Helper.Position.IsInside(Consts.BoardSize), "玩家棋较多时帮手主动转盘外");
                check(battle.Board.TopologyVersion == version, "帮手盘外爬行不占格不改变气眼");
                int tier = battle.Helper.Tier;
                battle.ChapterScore += Balance.HelperScoreStep;
                check(battle.Helper.Tier >= tier && battle.Helper.Tier <= Balance.HelperMaxTier, "累计积分提高帮手档位且有上限");
                before = battle.LifetimeScore;
                int pieces = battle.Board.Pieces.Count;
                check(battle.BeginHelperFreshCampaignForCheck(1) && battle.CombatScore == 0 && battle.ChapterScore == 0, "同章重新挑战清可用积分与成长档位");
                check(battle.LifetimeScore == before, "重开仍保留历史累计成绩");
                battle.StopEnemyAI(); battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                battle.Chapter = 6; battle.Round = 9; battle.Phase = GamePhase.Prepare;
                for (int i = 0; i < 81; i++)
                {
                    // 五十四枚低星+二十七枚高星全盘占满：吞噬预算三十六，必须保留全部高星。
                    var piece = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Swordman, i < 54 ? StarTier.One : StarTier.Three), GameSide.Player, 1);
                    battle._board.Place(piece, new BoardPos(i % 9, i / 9), respectEnemyEyes: false); battle._playerPieces.Add(piece);
                }
                bool entered = battle.StartRound(6, 10);
                check(entered && battle.LastBossSwallowedCount == 36 && battle._playerPieces.Count == Balance.BossPlayerPieceLimit,
                    "满盘八十一棋按预算吞噬至四十五且完整Boss出生");
                check(battle._playerPieces.FindAll(p => p.Def.Star == StarTier.Three).Count == 27,
                    "吞噬优先低级棋，不误吞高星棋");
                battle.StopEnemyAI(); battle._board.Reset(); battle._playerPieces.Clear(); battle._enemyPieces.Clear();
                battle.Chapter = 1; battle.Round = 1; battle.IsBossRound = false; battle.Phase = GamePhase.Battle;
                player = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(2, 2));
                Piece melee = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Swordman, new BoardPos(0, 0));
                enemy = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(5, 5));
                battle.RefreshGoState(); battle.ChapterScore = 0;
                int baseRangedDamage = battle.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Attack, BattleDamageKind.Normal, player)).Damage;
                int baseMeleeDamage = battle.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Attack, BattleDamageKind.Normal, melee)).Damage;
                int baseEnemyDamage = battle.EvaluateDamage(new DamageRequest(player, DamageBasis.Attack, BattleDamageKind.Normal, enemy)).Damage;
                player.ActionProgress = 0;
                battle.StepPlayerAttack(player, .01f);
                float baseProgress = player.ActionProgress;
                battle.ChapterScore = (long)Balance.HelperScoreStep * Balance.HelperMaxTier;
                float attackBonus = battle.HelperRangedAttackBonus, speedBonus = battle.HelperRangedSpeedBonus;
                check(attackBonus > 0 && attackBonus <= Balance.HelperRangedAttackCap && speedBonus > 0 && speedBonus <= Balance.HelperRangedSpeedCap,
                    "帮手高档位传递远程攻击和攻速，遵守Excel上限");
                int boostedRanged = battle.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Attack, BattleDamageKind.Normal, player)).Damage;
                check(boostedRanged > baseRangedDamage, "远程共鸣实际提高统一普攻管线伤害");
                check(battle.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Attack, BattleDamageKind.Normal, melee)).Damage == baseMeleeDamage,
                    "共鸣不误提高玩家近战伤害");
                check(battle.EvaluateDamage(new DamageRequest(player, DamageBasis.Attack, BattleDamageKind.Normal, enemy)).Damage == baseEnemyDamage,
                    "共鸣不误提高敌人伤害");
                player.ActionProgress = 0; battle.StepPlayerAttack(player, .01f);
                check(player.ActionProgress > baseProgress, "共鸣实际加快玩家远程攻击进度");
                battle.CombatScore = 100; battle.SpendCombatScore(50);
                check(battle.HelperRangedAttackBonus == attackBonus && battle.HelperRangedSpeedBonus == speedBonus,
                    "花费帮手能源不降低累计成长共鸣");
                battle.BeginHelperFreshCampaignForCheck(1);
                check(battle.HelperRangedAttackBonus == 0 && battle.HelperRangedSpeedBonus == 0,
                    "重新挑战清远程共鸣，不遗留上一战役加成");
            }
            finally { battle.StopEnemyAI(); RevEvent.RemoveAllByOwner(owner); }
        }

        /// <summary>测试输入明确占格后才入名单，血攻仍读真实表。只用于独立Editor/控制台夹具。</summary>
        private Piece AddHelperCheckPiece(GameSide side, PieceKind kind, BoardPos position)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, StarTier.One), side, 1);
            if (!_board.Place(piece, position, respectEnemyEyes: false)) { throw new InvalidOperationException("帮手规则夹具占格冲突"); }
            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }
        private bool BeginHelperFreshCampaignForCheck(int chapter) { return BeginCampaignAtRound(chapter, 1); }
    }
}
#endif

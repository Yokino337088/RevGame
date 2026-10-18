// ============================================================
// BattleService.Checks.cs —— 只在编辑器编译的业务边界断言。
// 夹具直接准备本服务的私有数据，再调用真实业务方法验证结果，不另写一套规则算法。
// 正式游戏没有这些造局入口；Editor/GameRuleChecks 统一调用，失败会带中文原因。
// ============================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>只供编辑器验收，检查实际数值变化、回合事务和结算次数。</summary>
        public static void VerifyDevelopmentContracts(Action<bool, string> check)
        {
            foreach (BattleEventKind kind in Enum.GetValues(typeof(BattleEventKind)))
            {
                BattleService service = EventFixture();
                Piece player = service._playerPieces[0];
                int hp = player.Hp;
                int liberties = player.Liberties;
                BoardPos position = player.Pos;
                check(service.TriggerBattleEvent(kind), "事件效果入口成功：" + kind);
                switch (kind)
                {
                    case BattleEventKind.Reinforcement:
                        check(service.Hand.Count == 0 && service._playerPieces.Count == 4, "天降援兵真实消费一张牌并增加棋子");
                        break;
                    case BattleEventKind.Liberty:
                        check(player.Liberties == liberties + 1, "回气真实增加一口气");
                        break;
                    case BattleEventKind.Heal:
                        check(player.Hp == hp + (int)(player.MaxHp * Consts.HealRatio), "疗愈雨按最大生命回复10%");
                        break;
                    case BattleEventKind.Combo:
                        // 比较实际伤害，不能只检查一个「增益已设置」标志。把两军放到互相可攻击的位置。
                        // 我方远程如今能全盘攻击；本断言只测连击，移除其余友军以隔离额外火力。
                        foreach (Piece other in new List<Piece>(service._playerPieces))
                        {
                            if (other != player) { service._board.Remove(other); service._playerPieces.Remove(other); }
                        }
                        service._board.Move(player, new BoardPos(4, 6));
                        service.RefreshGoState();
                        Piece target = service._board.GetPiece(new BoardPos(4, 7));
                        int before = target.Hp;
                        service.StepSide(GameSide.Player);
                        int oneAction = before - target.Hp;
                        player.ActionProgress = 0.9f;
                        before = target.Hp;
                        service.StepSide(GameSide.Player);
                        check(oneAction > 0 && before - target.Hp >= oneAction * 2, "连击累计进度达到阈值时真实多攻击一次");
                        break;
                    case BattleEventKind.Eye:
                        check(service._board.GetCell(new BoardPos(0, 0)).IsEyeMarked, "补眼真实在缺口放援兵形成角眼");
                        break;
                    case BattleEventKind.BreakChain:
                        check(service._board.GetPiece(new BoardPos(5, 7)) == null && service._board.GetPiecesOf(GameSide.Enemy).Count == 2, "断链移除中间连接子并拆开敌块");
                        break;
                    case BattleEventKind.Teleport:
                        check(player.Pos == position && player.Liberties == liberties + 1, "守阵回气增加气但不移动玩家棋子");
                        break;
                    case BattleEventKind.Artillery:
                        check(service._enemyPieces.TrueForAll(p => p.Hp == p.MaxHp - Consts.SupportDamage), "炮火对密集区域三颗敌子逐一扣血");
                        break;
                }

                check(!service.TriggerBattleEvent(kind), "事件效果不可重复领取：" + kind);
            }

            var battle = new BattleService();
            battle.StartRound(1, 10);
            battle._board.Reset();
            battle._enemyPieces.Clear();
            // 每个偶数行都铺满，留出很多单格空位，却没有任何连续两行，专门拦截「只看左下格」错误。
            for (int y = 0; y < Consts.BoardSize; y += 2)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    battle.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(x, y));
                }
            }

            string oldBoard = battle.Board.Dump();
            int oldCard = battle.Hand[0].Id;
            check(!battle.SkipRound(), "Boss无完整出生空间时拒绝跳过");
            check(battle.Round == 10 && battle.Phase == GamePhase.Prepare && battle.Hand[0].Id == oldCard && battle.Board.Dump() == oldBoard && battle._attackBonus == 0f, "出生失败不改回合、棋盘、手牌，也不白领强化");
            // 同样的拥挤棋盘检验 2★→3★：材料分散在三处，移除它们仍不能凑出 2×2。
            battle._board.Reset();
            battle._playerPieces.Clear();
            for (int y = 0; y < Consts.BoardSize; y += 2)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    bool material = y == 0 && (x == 0 || x == 3 || x == 6);
                    battle.AddCheckPiece(GameSide.Player, material ? PieceKind.Shield : PieceKind.Archer, new BoardPos(x, y), material ? StarTier.Two : StarTier.Three, 1);
                }
            }

            oldBoard = battle.Board.Dump();
            battle.TryMergePieces();
            check(battle.Board.Dump() == oldBoard && battle._playerPieces.FindAll(p => p.Def.Kind == PieceKind.Shield).Count == 3, "合成放不下时保留三份材料");
            foreach (Piece piece in new List<Piece>(battle._playerPieces))
            {
                if (piece.Def.Kind != PieceKind.Shield)
                {
                    battle._board.Remove(piece);
                    battle._playerPieces.Remove(piece);
                }
            }

            battle.TryMergePieces();
            check(battle._playerPieces.Count == 1 && battle._playerPieces[0].Size == 2, "腾出空间后可再次合成为完整大棋");
            battle = new BattleService();
            battle.StartRound(1, 11);
            Piece king = battle._enemyPieces.Find(p => p.IsKing);
            foreach (BoardPos pos in BoardModel.Boundary(king))
            {
                if (!pos.IsInside(Consts.BoardSize))
                {
                    continue;
                }

                Piece occupant = battle.Board.GetPiece(pos);
                if (occupant != null)
                {
                    battle.Board.Remove(occupant);
                    battle._enemyPieces.Remove(occupant);
                }

                battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, pos);
            }

            battle.StartBattle();
            battle.Tick(Consts.BattleTickSeconds);
            check(battle.Phase == GamePhase.Settle && battle.LastWinner == GameSide.Player && king.IsAlive, "主将围死即胜，不必先扣完生命");
            check(battle.ContinueRound() && battle.Phase == GamePhase.Reward && battle.Rewards.Count == 3, "Boss结算进入三选一");
            check(!battle.ChooseReward(-1) && battle.Rewards.Count == 3, "越界奖励不消耗待领选项");
            check(battle.ChooseReward(0) && battle.Chapter == 2 && !battle.ChooseReward(0), "奖励领取一次进入下章，重复拒绝");
            battle = new BattleService();
            battle.StartRound(6, 11);
            battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
            king = battle._enemyPieces.Find(p => p.IsKing);
            battle.DamagePiece(king, king.Hp);
            battle.StartBattle();
            battle.Tick(Consts.BattleTickSeconds);
            check(battle.LastWinner == GameSide.Player && battle._board.GetPiecesOf(GameSide.Enemy).Count > 0, "只击杀主将也能胜利，护卫不必清场");
            int finished = 0;
            object owner = new object ();
            RevEvent.AddEventListener<bool>(Events.GameFinished, won => finished++, owner: owner);
            try
            {
                check(battle.ContinueRound() && !battle.ContinueRound() && finished == 1, "最终章只派发一次通关");
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
            }

            battle = new BattleService(false);
            battle.Chapter = 1;
            battle.Round = 11;
            battle.IsBossRound = true;
            battle.Phase = GamePhase.Battle;
            // 高生命夹具保证能打满一分钟；仍走 Tick 和 Attack，不能只断言时长属性为 Infinity。
            Piece fighter = new Piece(1, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player, hpScale: 1000f);
            king = new Piece(2, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, 1, true, true, 1000f);
            battle._board.Place(fighter, new BoardPos(3, 3));
            battle._playerPieces.Add(fighter);
            battle._board.Place(king, new BoardPos(3, 4));
            battle._enemyPieces.Add(king);
            battle.RefreshGoState();
            int beforeHp = king.Hp;
            battle.Attack(fighter, king);
            int baseDamage = beforeHp - king.Hp;
            battle.Tick(62.5f);
            check(battle.Phase == GamePhase.Battle && battle.BattleElapsed == 62.5f, "关闭轻松辅助时Boss超过60秒仍不超时结算");
            beforeHp = king.Hp;
            battle.Attack(fighter, king);
            check(beforeHp - king.Hp > baseDamage, "60秒后真实伤害按决死加速增加");
            battle.Tick(float.NaN);
            battle.Tick(float.PositiveInfinity);
            battle.Tick(-1f);
            check(battle.BattleElapsed == 62.5f, "非法时间输入不污染战斗时钟");
            battle = new BattleService();
            for (int x = 0; x < 5; x++)
            {
                battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(x, 0));
                if (x == 2)
                {
                    check(Math.Abs(battle.ClassBonus(GameSide.Player) - 0.1f) < 0.001f, "三同职业全队攻击+10%");
                }
            }

            check(Math.Abs(battle.ClassBonus(GameSide.Player) - 0.2f) < 0.001f, "五同职业再加10%");
            for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
            {
                for (int round = 1; round <= Consts.RoundsPerChapter; round++)
                {
                    battle = new BattleService();
                    check(battle.StartRound(chapter, round) && battle._enemyPieces.Count == battle.Board.GetPiecesOf(GameSide.Enemy).Count, "66个回合配置可完整生成，名单与棋盘一致：" + chapter + "/" + round);
                    check(battle.Hand.Count == 6 && battle.CardsRemaining == 6, "普通/Boss均为六张手牌：" + chapter + "/" + round);
                }
            }

            VerifyComfortContracts(check);
            VerifyPlacementAndMovement(check);
            VerifyHandRefreshAndAttackRate(check);
        }

        /// <summary>用真正的最弱阵容与高血量敌人验证兜底，不直接写结算结果。</summary>
        private static void VerifyComfortContracts(Action<bool, string> check)
        {
            for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
            {
                for (int round = 1; round <= Consts.RoundsPerChapter; round++)
                {
                    var service = new BattleService();
                    service.StartRound(chapter, round);
                    // 夹具先放满血棋子再压到 1 血；只留最差起点，仍须走 StartBattle 和 Tick。
                    service._board.Reset();
                    service._playerPieces.Clear();
                    service._enemyPieces.Clear();
                    Piece survivor = service.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(3, 3));
                    survivor.TakeDamage(survivor.Hp - 1);
                    // 不给敌人主将标记，以免围住主将提前获胜，专门逼出最后的决胜支援分支。
                    Piece enemy = new Piece(service._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.Three), GameSide.Enemy, 1, false, false, 1000f);
                    service._board.Place(enemy, new BoardPos(3, 4));
                    service._enemyPieces.Add(enemy);
                    service._hand.Clear();
                    service.RefreshGoState();
                    check(service.StartBattle(), "弱势夹具能正常开始：" + chapter + "/" + round);
                    service.Tick(Consts.VictoryAssistFinishSeconds);
                    check(service.LastWinner == GameSide.Player && service.Phase == GamePhase.Settle, "1血单兵对高血量敌人仍真实取胜：" + chapter + "/" + round);
                    check(service.BattleEventCount >= 3 && service._decisiveSupportUsed, "每回合多次支援并实际触发决胜伤害：" + chapter + "/" + round);
                }
            }

            var battle = EventFixture();
            battle._eventScheduled = false;
            check(battle.TriggerBattleEvent(BattleEventKind.Heal), "首次事件成功");
            check(!battle.TriggerBattleEvent(BattleEventKind.Artillery), "事件不能在同一时刻刷取");
            // 九秒冷却不是0.3125秒模拟步的整数倍，多推进一个小步，检查跨过冷却后的真实结果。
            battle.Tick(Consts.BattleEventInterval + Consts.BattleSimulationStepSeconds);
            check(battle.TriggerBattleEvent(BattleEventKind.Artillery) && battle.BattleEventCount == 2, "冷却后允许第二次不同事件");
            battle = new BattleService();
            battle._rewardCard = PieceKind.Shield;
            battle.StartRound(1, 1);
            check(battle.Hand.Count == 6 && battle.Hand[0].Kind == PieceKind.Shield, "六张上限仍保留奖励同名牌");
            int hits = 0;
            int actual = 0;
            object owner = new object();
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => { hits++; actual += hit.Damage; }, owner: owner);
            try
            {
                Piece victim = battle._enemyPieces[0];
                int hp = victim.Hp;
                battle.DamagePiece(victim, 3);
                battle.DamagePiece(victim, 4);
                check(hits == 2 && actual == 7 && victim.Hp == hp - 7, "同一步两次伤害分别通知，飘字数值与扣血一致");
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
            }
        }

        private static BattleService EventFixture()
        {
            var service = new BattleService();
            service.Chapter = 1;
            service.Round = 1;
            service.Phase = GamePhase.Battle;
            service.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(1, 0)).TakeDamage(40);
            service.AddCheckPiece(GameSide.Player, PieceKind.Swordman, new BoardPos(0, 2));
            service.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(3, 0));
            for (int x = 4; x <= 6; x++)
            {
                service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(x, 7));
            }

            service._hand.Add(PieceKind.Archer, StarTier.One);
            service.RefreshGoState();
            return service;
        }

        private Piece AddCheckPiece(GameSide side, PieceKind kind, BoardPos pos, StarTier star = StarTier.One, int size = 0)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, star), side, size);
            if (!_board.Place(piece, pos))
            {
                throw new InvalidOperationException("测试夹具占格冲突：" + pos);
            }

            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }
    }
}
#endif


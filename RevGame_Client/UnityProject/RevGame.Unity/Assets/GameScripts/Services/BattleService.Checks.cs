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
            // 同样的拥挤棋盘检验原位 2★→3★：没有连续2×2也必须成功，其他棋子全部保留。
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

            Piece original = battle._playerPieces.Find(p => p.Def.Kind == PieceKind.Shield);
            BoardPos originalPosition = original.Pos;
            battle.UpgradePiece(original);
            check(original.Def.Star == StarTier.Three && original.Pos == originalPosition && original.Size == 1, "拥挤棋盘二升三保持原位单格，不要求腾出2×2");
            check(battle._playerPieces.Count == 45 && battle.Board.GetPiece(originalPosition) == original, "原位升级不消耗其他棋子，编号和占格引用不变");
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

            battle.Tick(3f);
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
            battle.Tick(3f);
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

            VerifyPreparationContracts(check);
            VerifyComfortContracts(check);
            VerifyPlacementAndMovement(check);
            VerifyReinforcementContracts(check);
            VerifyHandRefreshAndAttackRate(check);
        }

        /// <summary>逐项验证15种增益的真实结果，并检查延时、唯一发放、双牌事务和换轮清理。</summary>
        private static void VerifyPreparationContracts(Action<bool, string> check)
        {
            var delayed = new BattleService(true, 84);
            delayed.StartRound(1, 1);
            delayed.Tick(2.99f);
            check(!delayed.PreparationGranted && delayed.PreparationBuffs.Count == 0, "三秒前不提前给准备奖励");
            delayed.Tick(0.01f);
            check(delayed.PreparationGranted && delayed.PreparationBuffs.Count >= 3 && delayed.PreparationBuffs.Count <= 5, "到三秒发3～5项准备奖励");
            var unique = new HashSet<PreparationBuffKind>();
            foreach (PreparationBuffDef buff in delayed.PreparationBuffs) { check(unique.Add(buff.Kind), "本轮准备奖励不重复：" + buff.Title); }
            int count = delayed.PreparationBuffs.Count;
            delayed.Tick(30f);
            check(delayed.PreparationBuffs.Count == count && delayed.BattleElapsed == 0f, "准备时钟不会重复抽奖或偷跑战斗");
            check(delayed.StartRound(1, 2) && !delayed.PreparationGranted && delayed.PreparationBuffs.Count == 0, "换轮清空准备计时和播报数据");

            foreach (PreparationBuffKind kind in Enum.GetValues(typeof(PreparationBuffKind)))
            {
                var service = new BattleService(false, 1);
                Piece player = service.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(1, 0));
                service.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 1));
                Piece enemy = new Piece(1000, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, hpScale: 100f);
                service._enemyPieces.Add(enemy);
                service._board.Place(enemy, new BoardPos(5, 5));
                service.RefreshGoState();
                int max = player.MaxHp;
                player.TakeDamage(max / 2);
                int wounded = player.Hp;
                int oldLiberties = player.Liberties;
                service.ApplyPreparationBuff(kind);
                service.RefreshGoState();
                switch (kind)
                {
                    case PreparationBuffKind.Upgrade:
                        check(service._playerPieces.Exists(p => p.Def.Star == StarTier.Two), "觉醒确实升星"); break;
                    case PreparationBuffKind.Elite:
                        check(service._playerPieces.Count == 3 && service._playerPieces.Exists(p => p.Def.Kind == PieceKind.Crossbow && p.Def.Star == StarTier.Two), "免费二星重弩真实入场"); break;
                    case PreparationBuffKind.Recovery:
                        check(player.Hp > wounded, "复苏立即回血");
                        player.TakeDamage(max / 3);
                        int hp = player.Hp;
                        service.DamagePiece(player, 1);
                        check(player.Hp > hp && !service._prepRecoveryBank, "储备在真实受伤后只消费一次"); break;
                    case PreparationBuffKind.EncirclementImmunity:
                        service._board.Move(player, new BoardPos(4, 4));
                        service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(3, 4));
                        service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(5, 4));
                        service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(4, 3));
                        service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(4, 5));
                        service.RefreshGoState();
                        check(player.Liberties == 0 && !player.IsLocked, "实际零气仍不锁行动");
                        int before = service.TotalHp(GameSide.Enemy);
                        service.StepSide(GameSide.Player);
                        check(service.TotalHp(GameSide.Enemy) < before, "免疫围困后仍真实发动远程攻击"); break;
                    case PreparationBuffKind.Vitality:
                        check(player.MaxHp > max && Math.Abs(player.HpRatio - (float)wounded / max) < 0.02f, "生命加成保留伤势比例");
                        service.ResetPreparation();
                        check(player.MaxHp == max, "生命加成换轮可移除"); break;
                    case PreparationBuffKind.Armor:
                        hp = player.Hp; service.DamagePiece(player, 10);
                        check(hp - player.Hp < 10, "护甲确实减少扣血"); break;
                    case PreparationBuffKind.Shields:
                        hp = player.Hp;
                        for (int i = 0; i < 3; i++) { service.DamagePiece(player, 10); }
                        check(player.Hp == hp && player.ShieldCharges == 0, "三次护盾真实挡伤");
                        service.DamagePiece(player, 10);
                        check(player.Hp < hp, "第四次不再挡伤"); break;
                    case PreparationBuffKind.Regeneration:
                        service.TickPreparationCombat(4f);
                        check(player.Hp > wounded, "持续恢复实际回血"); break;
                    case PreparationBuffKind.Haste:
                        player.ActionProgress = 0f;
                        before = enemy.Hp;
                        service.StepSide(GameSide.Player, 0.4f);
                        check(enemy.Hp < before, "攻速加成使未满普通间隔时确实多攻击"); break;
                    case PreparationBuffKind.Liberties:
                        check(player.Liberties == oldLiberties + 2, "额外两口气实际参与规则"); break;
                    case PreparationBuffKind.EyePower:
                        service.TickPreparationCombat(4f);
                        check(player.EyeCount > 0 && player.Hp > wounded, "真实眼阵共鸣产生回复");
                        goto case PreparationBuffKind.Attack;
                    case PreparationBuffKind.Execution:
                        enemy.TakeDamage((int)(enemy.MaxHp * 0.8f));
                        goto case PreparationBuffKind.Attack;
                    case PreparationBuffKind.Ranged:
                    case PreparationBuffKind.Attack:
                        before = enemy.Hp;
                        service.Attack(player, enemy);
                        int enhanced = before - enemy.Hp;
                        service._prepAttack = service._prepRanged = service._prepExecution = 0f;
                        service._prepEyePower = false;
                        before = enemy.Hp;
                        service.Attack(player, enemy);
                        check(enhanced > before - enemy.Hp, "增益确实提高攻击伤害：" + kind); break;
                    case PreparationBuffKind.LifeSteal:
                        service.Attack(player, enemy);
                        check(player.Hp > wounded, "吸血按真实命中回血"); break;
                }
                Piece snapshot = player.CreateSnapshot();
                check(snapshot.MaxHp == player.MaxHp && snapshot.ShieldCharges == player.ShieldCharges && snapshot.IgnoreEncirclement == player.IgnoreEncirclement, "快照保留增益状态：" + kind);
            }

            var pairs = new BattleService(false);
            pairs._hand.Add(PieceKind.Shield, StarTier.One);
            pairs._hand.Add(PieceKind.Shield, StarTier.One);
            pairs.TryPlayCard(pairs.Hand[0].Id, new BoardPos(0, 1));
            Piece original = pairs._playerPieces[0];
            original.TakeDamage(original.MaxHp / 2);
            float ratio = original.HpRatio;
            int second = pairs.Hand[0].Id;
            check(!pairs.TryPlayCard(second, original.Pos) && original.Def.Star == StarTier.One, "失败落点不累计双牌或偷升星");
            check(pairs.TryPlayCard(second, new BoardPos(1, 0)), "第二张同类牌成功落子");
            check(pairs._playerPieces.Count == 2 && pairs.Board.GetPiece(new BoardPos(0, 1)) == original && original.Def.Star == StarTier.Two, "原棋原位升级，新棋保留指定格");
            check(original.Size == 1 && Math.Abs(original.HpRatio - ratio) < 0.02f && original.EyeCount == 1, "升星保留占格、伤势和真实角眼");
            pairs._hand.Add(PieceKind.Shield, StarTier.One);
            pairs._hand.Add(PieceKind.Shield, StarTier.One);
            pairs.TryPlayCard(pairs.Hand[0].Id, new BoardPos(3, 0));
            pairs.TryPlayCard(pairs.Hand[0].Id, new BoardPos(4, 0));
            check(original.Def.Star == StarTier.Three && original.Size == 1 && pairs._playerPieces.Count == 4, "第四张养成原位三星，四颗都保留");
            pairs.BeginCampaign(1);
            check(pairs._playedPairs.Count == 0, "新战役重置双牌累计，避免新局第一张错误升星");
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
                    service.Tick(3f);
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
            // 七秒冷却不是0.3125秒模拟步的整数倍，多推进一个小步，检查跨过冷却后的真实结果。
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

        /// <summary>直接验证援军最终兵种、落点和实际攻击范围，避免只测“评分函数返回了true”。</summary>
        private static void VerifyReinforcementContracts(Action<bool, string> check)
        {
            BattleService service = EventFixture();
            service._hand.Clear();
            service._hand.Add(PieceKind.HeavySword, StarTier.Three);
            service._hand.Add(PieceKind.Archer, StarTier.One);
            service._hand.Add(PieceKind.Crossbow, StarTier.Two);
            int allowance = service.CardsRemaining;
            check(service.TriggerBattleEvent(BattleEventKind.Reinforcement), "混合手牌援军成功");
            Piece born = service._playerPieces[service._playerPieces.Count - 1];
            check(born.Def.Kind == PieceKind.Crossbow && born.Def.Star >= StarTier.Two, "优先最强远程而非排在前面的近战");
            check(service.Hand.Count == 2 && service.CardsRemaining == allowance, "只消费真正部署的牌，不扣手动落子额度");
            check(born.Pos.Y <= 2 && born.Pos.X <= 4, "远程援军落在己方主体附近的安全后排");
            check(service._enemyPieces.TrueForAll(p => BodyDistance(born.Pos, born.Size, p) > 2), "远程援军避开贴敌位置");

            service = EventFixture();
            service._hand.Clear();
            check(service.TriggerBattleEvent(BattleEventKind.Reinforcement), "六牌用尽仍能获得强援");
            born = service._playerPieces[service._playerPieces.Count - 1];
            check(born.Def.Kind == PieceKind.Crossbow && born.Def.Star == StarTier.Two && service.Hand.Count == 0, "空手补二星重弩，不凭空增加手牌");

            service = EventFixture();
            service._hand.Clear();
            service._hand.Add(PieceKind.Swordman, StarTier.One);
            check(service.TriggerBattleEvent(BattleEventKind.Reinforcement), "近战援军可以部署");
            born = service._playerPieces[service._playerPieces.Count - 1];
            check(!born.Def.IsRanged && RevGame.Battle.MoveRules.GetAttackTargets(service._board, born).Count > 0, "近战落下就有真实射程内目标，不再丢在后排发呆");

            service = new BattleService();
            service.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(1, 0));
            service.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 1));
            service.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(7, 8));
            service.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 7));
            service._prepImmune = true;
            service.RefreshGoState();
            check(service._board.GetCell(new BoardPos(8, 8)).EyeSide == GameSide.Enemy, "敌方真实角眼夹具成立");
            check(service.TryDeployReinforcement(PieceKind.Swordman, StarTier.Two), "免围困近战可突入敌眼");
            born = service._playerPieces[service._playerPieces.Count - 1];
            check(born.Pos == new BoardPos(8, 8) && RevGame.Battle.MoveRules.GetAttackTargets(service._board, born).Count > 0, "敌眼落点可用且落下就能攻击时优先破眼");
            check(service._board.GetCell(new BoardPos(0, 0)).EyeSide == GameSide.Player, "自动援军不填己方眼位");
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


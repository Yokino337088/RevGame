// 固定输入的控制台断言。失败立即抛出中文原因并以非零退出；成功会列出实际执行的场景数。
// 不使用 dotnet test：本工程没有测试适配器，不能把“0 项测试”当成战斗回归通过。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

internal static class Program
{
    /// <summary>实际执行过的断言条数；多帧容量检查也计入，因此不能把它当独立玩法场景数量。</summary>
    private static int _assertions;
    /// <summary>完成的规则造局场景数；公平缓冲模拟单独报告，不冒充规则场景。</summary>
    private static int _scenarios;

    private static int Main(string[] args)
    {
        try
        {
            string data = args.Length > 0 ? Path.GetFullPath(args[0]) : FindDataDirectory();
            GameConfigFileLoader.LoadFromDirectory(data);
            Console.WriteLine("真实配置目录：" + data);
            if (args.Length > 1 && args[1] == "boss-expansion")
            {
                Console.WriteLine(BattleService.VerifyBossSkillExpansion((ok, message) => { Assert(ok, message); Console.WriteLine("PASS " + message); }));
                Console.WriteLine("Boss专属与轮换断言通过：" + _assertions);
                return 0;
            }
            if (args.Length > 1 && args[1] == "storm")
            {
                BattleService.VerifyStormRules((ok, message) => { Assert(ok, message); Console.WriteLine("PASS " + message); });
                Console.WriteLine("风暴与集火断言通过：" + _assertions); return 0;
            }
            if (args.Length > 1 && args[1] == "boss")
            {
                int assertions = BossRegression.Run(out int scenarios);
                Console.WriteLine("BossRegression: " + scenarios + " scenarios, " + assertions + " assertions passed.");
                return 0;
            }
            if (args.Length > 1 && args[1] == "helper-balance") { return HelperBalanceRegression.Run(args.Length > 2 ? args[2] : "."); }
            if (args.Length > 1 && args[1] == "helper")
            {
                BattleService.VerifyHelperRules((ok, message) => { Assert(ok, message); Console.WriteLine("PASS " + message); });
                Console.WriteLine("帮手控制台真实规则断言：" + _assertions);
                return 0;
            }
            foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
            {
                for (int tier = 1; tier <= 3; tier++)
                {
                    StarTier star = (StarTier)tier;
                    VerifyNormalAttack(kind, star);
                    VerifyDistantAttackThroughFriendlyLine(kind, star);
                    VerifyLockedRecovery(kind, star);
                    VerifyEnemyLockStillStopsAttack(kind, star);
                    VerifyEyeAttack(kind, star, false);
                    VerifyEyeAttack(kind, star, true);
                }
            }
            VerifyFortyAttackersInOneStep();
            VerifyBurstRetargetsAfterKill();
            VerifyMeleeAdjacentPriority(PieceKind.Swordman, StarTier.One);
            VerifyMeleeAdjacentPriority(PieceKind.Assassin, StarTier.Three);
            VerifyDeadAttackerStaysSilent();
            VerifyNoEnemyStaysSilent();
            VerifyEncircledPlayerStillTakesExtraDamage();
            VerifyGoDeploymentTactics();
            VerifyFrequentEnemyDeployment();
            VerifyGuaranteedArrowVolley();
            VerifyHeavyEnemyAI();
            int bossScenarios;
            _assertions += BossRegression.Run(out bossScenarios);
            _scenarios += bossScenarios;
            _assertions += CombatPacingRegression.Run(out int pacingScenarios);
            _scenarios += pacingScenarios;
            _assertions += FeedbackRegression.Run();
            Console.WriteLine("CombatAttackRegression: " + _scenarios + " scenarios, " + _assertions + " assertions passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("CombatAttackRegression FAILED: " + error);
            return 1;
        }
        finally
        {
            GameConfig.Reset();
        }
    }

    /// <summary>
    /// 从工程输出目录向上找已提交的导表数据，避免依赖调用命令时的工作目录。
    /// 这是测试宿主的文件定位；正式游戏仍经 RevResManager 加载资源，不得把此办法搬进游戏。
    /// </summary>
    private static string FindDataDirectory()
    {
        DirectoryInfo root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null)
        {
            string candidate = Path.Combine(root.FullName, "RevGame_Client", "UnityProject", "RevGame.Unity", "Assets", "GameRes", "Data");
            if (File.Exists(Path.Combine(candidate, "PieceConfig.txt")))
            {
                return candidate;
            }
            root = root.Parent;
        }
        throw new DirectoryNotFoundException("找不到导表数据，可把 Assets/GameRes/Data 绝对目录作为第一个参数。");
    }

    /// <summary>基础攻击：远程隔友军、非直线跨盘仍能打；近战相邻时不受移动走法误限。所有类型的远处援击另设场景。</summary>
    private static void VerifyNormalAttack(PieceKind kind, StarTier star)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Player, new BoardPos(1, 1));
        BoardPos destination = actor.Def.IsRanged ? new BoardPos(7, 6) : new BoardPos(1 + actor.Size, 1);
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, destination, 100f);
        if (actor.Def.IsRanged)
        {
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(1 + actor.Size, 1));
        }
        battle.RefreshRegressionGoState();
        Assert(!actor.IsLocked, Label(actor) + "正常场景没有被锁死");
        Assert(MoveRules.GetAttackTargets(battle.Board, actor).Contains(target.Pos), Label(actor) + "真实目标查询包含敌人");
        AssertActualAttack(battle, actor, target, "正常攻击");
        _scenarios++;
    }

    /// <summary>
    /// 用户要求所有我方棋子参战，因此我方零气现在仍有真实反击。
    /// 围困对我方的代价仍由原有受击增伤规则承担，不在此另造攻击削弱倍率。
    /// 解除包围后还必须刷新气状态并继续输出，不能把旧状态永久缓存下来。
    /// </summary>
    private static void VerifyLockedRecovery(PieceKind kind, StarTier star)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Player, new BoardPos(1, 1));
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(1 + actor.Size, 1), 100f);
        Piece blocker = null;
        // 3★ 新落子的身体可能为 2×2；沿真实身体边界围满，不能把它当成一格棋子制造假锁死。
        foreach (BoardPos pos in RevGame.Board.BoardModel.Boundary(actor))
        {
            if (pos == target.Pos)
            {
                continue;
            }
            Piece added = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, pos, 100f);
            if (blocker == null)
            {
                blocker = added;
            }
        }
        battle.RefreshRegressionGoState();
        Assert(actor.IsLocked, Label(actor) + "零气围困真实判定为锁死");
        Piece lockedTarget = battle.Board.GetPiece(MoveRules.GetAttackTargets(battle.Board, actor)[0]);
        // 2×2 身体的最近目标可能就是左侧包围者。开气口时移走另一颗，才能前后核对同一靶子。
        foreach (Piece enemy in battle.EnemyPieces)
        {
            if (enemy != lockedTarget)
            {
                blocker = enemy;
                break;
            }
        }
        float lockedDamagePerHit = MeasureDamagePerHit(battle, actor, lockedTarget);
        Assert(lockedDamagePerHit > 0f, Label(actor) + "我方零气时仍必须真实反击");
        Assert(actor.IsLocked && actor.Liberties == 0, Label(actor) + "允许反击不能篡改零气/锁死状态");
        battle.RemoveRegressionPiece(blocker);
        Assert(!actor.IsLocked && actor.Liberties > 0, Label(actor) + "解除围困会刷新状态");
        float recoveredDamagePerHit = MeasureDamagePerHit(battle, actor, lockedTarget);
        Assert(recoveredDamagePerHit > 0f, Label(actor) + "解除零气后仍继续真实攻击");
        _scenarios++;
    }

    /// <summary>全部九种棋子即使隔着友军且敌人不相邻，也必须守住原位参与攻击，不再等敌人主动贴脸。</summary>
    private static void VerifyDistantAttackThroughFriendlyLine(PieceKind kind, StarTier star)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        BoardPos origin = new BoardPos(1, 1);
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Player, origin);
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(origin.X + actor.Size, origin.Y));
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(7, 6), 100f);
        battle.RefreshRegressionGoState();
        Assert(MoveRules.GetAttackTargets(battle.Board, actor).Contains(target.Pos), Label(actor) + "隔友军/非相邻仍有真实援击目标");
        Assert(MoveRules.GetAttackCoverage(battle.Board, actor).Contains(target.Pos), Label(actor) + "显示射界必须包含真实援击目标");
        AssertActualAttack(battle, actor, target, "远处守阵援击");
        Assert(actor.Pos == origin, Label(actor) + "援击过程中保持玩家布置的位置");
        _scenarios++;
    }

    /// <summary>同样的零气状态，敌方仍不能攻击；玩家专属反击不能意外提升敌人或破坏围棋制衡。</summary>
    private static void VerifyEnemyLockStillStopsAttack(PieceKind kind, StarTier star)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Enemy, new BoardPos(2, 2));
        var surroundingPlayers = new List<Piece>();
        foreach (BoardPos pos in RevGame.Board.BoardModel.Boundary(actor))
        {
            surroundingPlayers.Add(battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, pos, 100f));
        }
        battle.RefreshRegressionGoState();
        Assert(actor.IsLocked, Label(actor) + "敌方零气应维持锁死状态");
        Assert(MoveRules.GetAttackTargets(battle.Board, actor).Count == 0, Label(actor) + "敌方零气不应有攻击目标");
        Assert(MoveRules.GetAttackCoverage(battle.Board, actor).Count == 0, Label(actor) + "敌方零气不应显示虚假射界");
        int attacks = 0;
        int beforeHp = 0;
        foreach (Piece player in surroundingPlayers)
        {
            beforeHp += player.Hp;
        }
        object owner = new object();
        RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info =>
        {
            if (info.PieceId == actor.Id)
            {
                attacks++;
            }
        }, owner: owner);
        try
        {
            battle.RunRegressionEnemyStep(4f);
            int afterHp = 0;
            foreach (Piece player in surroundingPlayers)
            {
                afterHp += player.Hp;
            }
            Assert(attacks == 0 && beforeHp == afterHp, Label(actor) + "敌方零气没有出手事件，也没有真实扣血");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
        _scenarios++;
    }

    /// <summary>只收集当前棋子针对指定目标的实际伤害，未出手直接失败，不自行重写业务伤害公式。</summary>
    private static float MeasureDamagePerHit(BattleService battle, Piece actor, Piece target, GameSide side = GameSide.Player)
    {
        int total = 0;
        int hits = 0;
        object owner = new object();
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
        {
            if (info.SourceId == actor.Id && info.TargetId == target.Id)
            {
                total += info.Damage;
                hits++;
            }
        }, owner: owner);
        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (side == GameSide.Player)
                {
                    battle.RunRegressionPlayerStep();
                }
                else
                {
                    battle.RunRegressionEnemyStep();
                }
            }
            Assert(hits > 0 && total > 0, Label(actor) + "必须具有可核对的真实单击伤害");
            return (float)total / hits;
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
    }

    /// <summary>一眼/两眼只让阵型守住位置，绝不能把不能移动误解成不能攻击。</summary>
    private static void VerifyEyeAttack(PieceKind kind, StarTier star, bool twoEyes)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        // 两眼夹具放到内部，避免贴边的密集实子按现行“己方单格棋也可驻眼”规则额外形成第三只眼。
        // 仍严格断言恰好两眼；修正输入而不是放宽预期，更不修改正式判眼规则。
        BoardPos origin = twoEyes ? new BoardPos(5, 2) : new BoardPos(1, 0);
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Player, origin);
        if (twoEyes)
        {
            for (int y = 1; y <= 3; y++)
            {
                for (int x = 1; x <= 5; x++)
                {
                    BoardPos pos = new BoardPos(x, y);
                    bool bodyCell = x >= origin.X && x < origin.X + actor.Size && y >= origin.Y && y < origin.Y + actor.Size;
                    if (bodyCell || (y == 2 && (x == 2 || x == 4)))
                    {
                        continue;
                    }
                    battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, pos);
                }
            }
        }
        else
        {
            battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(0, 1));
            if (actor.Size == 1)
            {
                battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(1, 1));
            }
        }
        BoardPos enemyPos = new BoardPos(origin.X + actor.Size, origin.Y);
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, enemyPos, 100f);
        battle.RefreshRegressionGoState();
        Assert(actor.EyeCount == (twoEyes ? 2 : 1), Label(actor) + "真实气眼算法识别" + (twoEyes ? "两眼" : "一眼"));
        Assert(MoveRules.GetMoveTargets(battle.Board, actor).Count == 0, Label(actor) + "成眼后保持阵位");
        AssertActualAttack(battle, actor, target, twoEyes ? "两眼仍可攻击" : "一眼仍可攻击");
        Assert(actor.Pos == origin, Label(actor) + "攻击不能拆散眼阵");
        _scenarios++;
    }

    /// <summary>
    /// 同时核对真实攻击通知和敌人扣血。只看到弹幕不代表伤害生效，只看到目标数量也不代表真的出手。
    /// 监听限定为当前 actor，眼阵里的辅助棋子不能替它“冒领”通过结果；结束时必须解除监听。
    /// </summary>
    private static void AssertActualAttack(BattleService battle, Piece actor, Piece target, string scenario)
    {
        int attacks = 0;
        int damage = 0;
        int hp = target.Hp;
        object owner = new object();
        RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info =>
        {
            if (info.PieceId == actor.Id)
            {
                attacks++;
            }
        }, owner: owner);
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
        {
            if (info.HasAttacker && info.SourceId == actor.Id && info.Source == actor.Pos && info.TargetId == target.Id)
            {
                damage += info.Damage;
            }
        }, owner: owner);
        try
        {
            // 走真实行动积累；四个标准周期足够覆盖最低攻速，不直接调用 Attack 绕过出手条件。
            for (int i = 0; i < 4; i++)
            {
                battle.RunRegressionPlayerStep();
            }
            Assert(attacks > 0, Label(actor) + "：" + scenario + "，应派发真实攻击通知");
            Assert(damage > 0 && target.Hp < hp, Label(actor) + "：" + scenario + "，应结算对应攻击者的真实伤害");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
    }

    private static string Label(Piece actor)
    {
        return actor.Def.Kind + " " + (int)actor.Def.Star + "★";
    }

    /// <summary>
    /// 同场检验名单后段，专门复现“先落下高星，后落下一级”的顺序。
    /// 前十颗从一星原位升到三星，所以身体仍是一格（正式业务同样保留原有身体）；
    /// 后三十颗仍是一星。四十颗远程共享一名高血量敌人，按真实服务名单同一步出手。
    /// </summary>
    private static void VerifyFortyAttackersInOneStep()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        PieceKind[] rangedKinds = { PieceKind.Archer, PieceKind.Crossbow, PieceKind.MageWater, PieceKind.MageFire, PieceKind.Priest };
        var actors = new List<Piece>();
        for (int index = 0; index < 40; index++)
        {
            Piece actor = battle.AddRegressionPiece(rangedKinds[index % rangedKinds.Length], StarTier.One, GameSide.Player, new BoardPos(index % 8, index / 8));
            if (index < 10)
            {
                Assert(actor.UpgradeInPlace() && actor.UpgradeInPlace(), "同场夹具的前十颗棋子真实原位升到三星");
            }
            actors.Add(actor);
        }
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(8, 8), 10000f);
        battle.RefreshRegressionGoState();
        var attackingIds = new HashSet<int>();
        var damageBySource = new Dictionary<int, int>();
        int totalDamage = 0;
        object owner = new object();
        RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => attackingIds.Add(info.PieceId), owner: owner);
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
        {
            if (info.TargetId == target.Id)
            {
                damageBySource.TryGetValue(info.SourceId, out int before);
                damageBySource[info.SourceId] = before + info.Damage;
                totalDamage += info.Damage;
            }
        }, owner: owner);
        int targetHp = target.Hp;
        try
        {
            battle.RunRegressionPlayerStep();
            Assert(attackingIds.Count == 40, "真实同一步中，全部四十名远程都必须派发攻击事件");
            foreach (Piece actor in actors)
            {
                Assert(!actor.IsLocked && actor.Def.IsRanged, "同场造局的棋子必须有合法远程出手机会：" + actor.Id);
                Assert(attackingIds.Contains(actor.Id), "名单后段也应出手：" + Label(actor) + " #" + actor.Id);
                Assert(damageBySource.TryGetValue(actor.Id, out int damage) && damage > 0, "每颗棋子都应有对应编号的真实伤害：" + actor.Id);
            }
            Assert(totalDamage == targetHp - target.Hp, "同场四十名攻击者的伤害快照总和必须等于真实扣血");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
        _scenarios++;
    }

    /// <summary>一轮连射的第一发击败首目标后，后续额度应重查目标，不能对旧空格一直打空枪。</summary>
    private static void VerifyBurstRetargetsAfterKill()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(0, 0));
        Piece first = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(1, 0), 0.001f);
        Piece second = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(2, 0), 100f);
        battle.RefreshRegressionGoState();
        int secondHp = second.Hp;
        var hitOrder = new List<int>();
        object owner = new object();
        RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
        {
            if (info.SourceId == actor.Id)
            {
                hitOrder.Add(info.TargetId);
            }
        }, owner: owner);
        try
        {
            battle.RunRegressionPlayerStep(2f);
            Assert(!first.IsAlive && battle.Board.GetPiece(first.Pos) == null, "连射首目标应真实死亡并清除占格");
            Assert(second.Hp < secondHp, "首目标死亡后的连射额度应改打下一个敌人");
            Assert(hitOrder.Count > 1 && hitOrder[0] == first.Id && hitOrder[1] == second.Id, "真实伤害顺序应从首目标切换到新目标");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
        _scenarios++;
    }

    /// <summary>援击只是没有贴脸目标时的后备；普通单格近战和两格马步近战都应先保护身边阵线。</summary>
    private static void VerifyMeleeAdjacentPriority(PieceKind kind, StarTier star)
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        BoardPos origin = new BoardPos(4, 4);
        Piece actor = battle.AddRegressionPiece(kind, star, GameSide.Player, origin);
        Piece adjacent = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(origin.X + actor.Size, origin.Y), 100f);
        Piece distant = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(0, 0), 100f);
        battle.RefreshRegressionGoState();
        List<BoardPos> targets = MoveRules.GetAttackTargets(battle.Board, actor);
        Assert(targets.Count == 1 && targets[0] == adjacent.Pos, Label(actor) + "有相邻敌人时先处理接触目标，不去援击远处");
        int distantHp = distant.Hp;
        AssertActualAttack(battle, actor, adjacent, "近战优先相邻敌人");
        Assert(distant.Hp == distantHp && actor.Pos == origin, Label(actor) + "守阵优先攻击身边敌人，远处敌人和原阵位保持不变");
        _scenarios++;
    }

    /// <summary>单独制造死亡但仍在名单中的旧引用，确认新 CanAttack 不会把“我方都攻击”误写成尸体也出手。</summary>
    private static void VerifyDeadAttackerStaysSilent()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(1, 1));
        Piece target = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(7, 6), 100f);
        // 这里只把夹具设置为死亡状态；后面的待验证攻击仍走 StepSide，不用本调用伪造敌人掉血。
        actor.TakeDamage(actor.Hp);
        battle.RefreshRegressionGoState();
        Assert(!MoveRules.CanAttack(actor) && MoveRules.GetAttackTargets(battle.Board, actor).Count == 0, "死亡我方不能取得攻击资格或目标");
        Assert(MoveRules.GetAttackCoverage(battle.Board, actor).Count == 0, "死亡我方不能显示可攻击射界");
        int attacks = 0;
        object owner = new object();
        RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => attacks++, owner: owner);
        try
        {
            int hp = target.Hp;
            battle.RunRegressionPlayerStep(4f);
            Assert(attacks == 0 && target.Hp == hp, "死亡名单引用不会产生攻击通知或真实伤害");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
        _scenarios++;
    }

    /// <summary>我方有资格出手但敌人全灭时，应安静结束该次行动，不能凭空创建攻击目标或通知。</summary>
    private static void VerifyNoEnemyStaysSilent()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece actor = battle.AddRegressionPiece(PieceKind.Swordman, StarTier.One, GameSide.Player, new BoardPos(1, 1));
        battle.RefreshRegressionGoState();
        Assert(MoveRules.GetAttackTargets(battle.Board, actor).Count == 0, "没有敌人时返回空目标列表");
        int attacks = 0;
        object owner = new object();
        RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => attacks++, owner: owner);
        try
        {
            battle.RunRegressionPlayerStep(4f);
            Assert(attacks == 0 && actor.Pos == new BoardPos(1, 1), "没有敌人时不虚构攻击、不自动拆阵移动");
        }
        finally
        {
            RevEvent.RemoveAllByOwner(owner);
        }
        _scenarios++;
    }

    /// <summary>
    /// 我方被围仍能反击，不等于移除了围棋惩罚：相同敌人攻击同一目标，零气时应比解围后每击更痛。
    /// 四名同职业敌人围住玩家，移走一名后仍有三名，维持同一职业档位，避免职业加成变化污染比较。
    /// 夹具使用高血量玩家，确认差异来自真实伤害而不是死亡截断；不复制业务倍率公式。
    /// </summary>
    private static void VerifyEncircledPlayerStillTakesExtraDamage()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece player = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(2, 2), 1000f);
        Piece attacker = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Enemy, new BoardPos(2, 3));
        Piece blocker = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Enemy, new BoardPos(1, 2));
        battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Enemy, new BoardPos(3, 2));
        battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Enemy, new BoardPos(2, 1));
        battle.RefreshRegressionGoState();
        Assert(player.IsLocked && player.Liberties == 0, "受击比较的玩家确实处于零气状态");
        float lockedDamage = MeasureDamagePerHit(battle, attacker, player, GameSide.Enemy);
        Assert(player.IsLocked && player.Liberties == 0, "受到攻击不会自行移除围困规则状态");
        battle.RemoveRegressionPiece(blocker);
        Assert(!player.IsLocked && player.Liberties > 0, "移走包围者后玩家真实解围");
        float normalDamage = MeasureDamagePerHit(battle, attacker, player, GameSide.Enemy);
        Assert(lockedDamage > normalDamage && normalDamage > 0f, "同一攻击者的真实单击伤害证明零气受击增伤仍在");
        _scenarios++;
    }

    /// <summary>真实造出最后一口气和共同断点；验证AI选点而非只验证评分常量。</summary>
    private static void VerifyGoDeploymentTactics()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        Piece player = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 4));
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(3, 4));
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(4, 3));
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(5, 4));
        battle.RefreshRegressionGoState();
        string before = battle.Board.Dump();
        Piece candidate = new Piece(900, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy);
        BoardPos pos;
        string plan;
        Assert(EnemyTactics.TryChooseDeployment(battle.Board, candidate, 4, out pos, out plan), "围困造局有合法落点");
        Assert(pos == new BoardPos(4, 5) && plan == "收网围困", "AI优先封住对方最后一口气");
        Assert(battle.Board.Dump() == before && player.Liberties == 1, "试落子不污染实战占格与气数");
        battle = BattleService.CreateAttackRegressionFixture();
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(3, 4));
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(5, 4));
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(4, 2));
        battle.RefreshRegressionGoState();
        Assert(EnemyTactics.TryChooseDeployment(battle.Board, candidate, 4, out pos, out plan), "断点造局有合法落点");
        Assert(pos == new BoardPos(4, 4) && plan == "切断接应", "AI抢占两个棋群的共同连接点");
        _scenarios += 2;
    }

    /// <summary>跳过攻击只推进真实落子入口，核对冷却和事件一次提交一次发布。</summary>
    private static void VerifyFrequentEnemyDeployment()
    {
        BattleService battle = BattleService.CreateAttackRegressionFixture();
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 4), 1000f);
        battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(4, 7), 1000f);
        battle.RefreshRegressionGoState();
        object owner = new object();
        int events = 0;
        RevEvent.AddEventListener<int, string>(Events.EnemyDeployed, (id, plan) =>
        {
            Assert(battle.Board.FindPiece(id) != null && !string.IsNullOrEmpty(plan), "落子通知发生时真实棋子和战术解释已存在");
            events++;
        }, owner: owner);
        try
        {
            battle.ResetRegressionDeployment();
            battle.RunRegressionDeploymentAt(Balance.DeployStartSeconds - 0.01f);
            Assert(battle.EnemyDeploymentCount == 0, "首手时刻未到不落子");
            battle.RunRegressionDeploymentAt(Balance.DeployStartSeconds);
            int first = battle.EnemyDeploymentCount;
            Assert(first == 1, "首手时刻到期真实提交一手");
            float due = battle.NextEnemyDeploymentAt;
            battle.RunRegressionDeploymentAt(due - 0.01f);
            Assert(battle.EnemyDeploymentCount == first, "冷却未到不能重复落子");
            for (int i = 0; i < 7; i++) { battle.RunRegressionDeploymentAt(battle.NextEnemyDeploymentAt); }
            Assert(battle.EnemyDeploymentCount >= 5, "多个连续战术窗口真实提交至少五手，不再仅补两三手");
            Assert(events == battle.EnemyDeploymentCount, "每手真实落子仅发布一次弹幕事件");
            Assert(battle.NextEnemyDeploymentAt < 40f, "多手落子在前段完成，而不是等待十几秒一手");
            int finished = battle.EnemyDeploymentCount;
            battle.RunRegressionDeploymentAt(Balance.DeployStopSeconds);
            Assert(battle.EnemyDeploymentCount == finished && events == finished, "停手时刻后不再生成敌棋或虚构落子通知");
            foreach (Piece enemy in new List<Piece>(battle.EnemyPieces)) { battle.RemoveRegressionPiece(enemy); }
            battle.ResetRegressionDeployment();
            battle.RunRegressionDeploymentAt(Balance.DeployStartSeconds);
            Assert(battle.EnemyDeploymentCount == 0 && battle.Board.IsSideWiped(GameSide.Enemy), "敌方全灭不能借频繁增援复活");
        }
        finally { RevEvent.RemoveAllByOwner(owner); }
        _scenarios++;
    }

    /// <summary>多种子验证保底、去重和后入场继承，不以单次随机碰巧抽到为证据。</summary>
    private static void VerifyGuaranteedArrowVolley()
    {
        for (int seed = 0; seed < 32; seed++)
        {
            BattleService battle = BattleService.CreatePreparationRegressionFixture(seed);
            battle.GrantRegressionPreparation();
            int volleys = 0;
            var kinds = new HashSet<PreparationBuffKind>();
            foreach (PreparationBuffDef buff in battle.PreparationBuffs)
            {
                Assert(kinds.Add(buff.Kind), "准备增益不重复：种子 " + seed);
                if (buff.Kind == PreparationBuffKind.ArrowVolley) { volleys++; }
            }
            Assert(volleys == 1, "每个随机种子都有且只有一次万箭齐发");
            Assert(kinds.Count >= Balance.PrepBuffCountMin && kinds.Count <= Balance.PrepBuffCountMax, "保底计入本轮总增益名额");
            Piece archer = battle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Player, new BoardPos(7, 7));
            Assert(battle.RegressionArrowRate(archer) == 1f + PreparationBuffCatalog.Get(PreparationBuffKind.ArrowVolley).Amount, "领取后入场弓箭手继承实际攻速倍率");
            int count = battle.PreparationBuffs.Count;
            battle.GrantRegressionPreparation();
            Assert(battle.PreparationBuffs.Count == count, "重复推进准备时钟不重复领取");
        }
        _scenarios++;
    }

    /// <summary>直接装配真实RevHeavyFSM，核对状态行为、时间、历史、终态与服务回合回收。</summary>
    private static void VerifyHeavyEnemyAI()
    {
        BattleService actionBattle = BattleService.CreateAttackRegressionFixture();
        Piece actor = actionBattle.AddRegressionPiece(PieceKind.Archer, StarTier.One, GameSide.Enemy, new BoardPos(4, 4));
        Piece target = actionBattle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 5), 1000f);
        actionBattle.RefreshRegressionGoState();
        EnemyAI ai = actionBattle.RegressionEnemyAI(actor);
        ai.Tick(0.25f);
        Assert(ai.State == EnemyAIState.Attack && ai.StateTime == 0f, "状态自己感知接敌并切Attack，框架切换清零时间");
        ai.Tick(1f);
        Assert(ai.StateTime == 1f && target.Hp < target.MaxHp, "持续Attack真实扣血，不是回调计数冒充攻击");
        int hp = target.Hp;
        actionBattle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(3, 4));
        actionBattle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(5, 4));
        actionBattle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(4, 3));
        actionBattle.RefreshRegressionGoState();
        ai.Tick(1f);
        Assert(ai.State == EnemyAIState.Locked && target.Hp == hp, "状态自己感知零气锁死，停止攻击");
        actionBattle.RemoveRegressionPiece(target);
        ai.Tick(0.25f);
        Assert(ai.State == EnemyAIState.Attack && ai.History[ai.History.Length - 1].Reason == "发现合法攻击目标", "解除包围后状态自己恢复攻防并保存原因");
        ai.Tick(EnemyAIState.Dead, 0f, "真实死亡");
        ai.Tick(1f);
        Assert(ai.State == EnemyAIState.Dead, "死亡态不再次感知复活");
        ai.Dispose();
        ai.Dispose();
        bool rejected = false;
        try { ai.Tick(0f); }
        catch (ObjectDisposedException) { rejected = true; }
        Assert(rejected, "已处置AI不能再次驱动");
        actionBattle.StopEnemyAI();
        BattleService movement = BattleService.CreateAttackRegressionFixture();
        Piece walker = movement.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Enemy, new BoardPos(7, 7));
        movement.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player, new BoardPos(1, 1), 1000f);
        movement.RefreshRegressionGoState();
        EnemyAI movingAI = movement.RegressionEnemyAI(walker);
        movingAI.Tick(0.25f);
        Assert(movingAI.State == EnemyAIState.Hold && walker.Pos == new BoardPos(7, 7), "无射界且冷却未到，由Hold状态保持原位");
        movement.SetRegressionElapsed(Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks);
        movingAI.Tick(0.25f);
        Assert(movingAI.State == EnemyAIState.Advance && walker.Pos != new BoardPos(7, 7), "Advance状态真实选择并提交合法移动");
        BoardPos movedPosition = walker.Pos;
        movingAI.Tick(0.25f);
        Assert(movingAI.State == EnemyAIState.Hold && walker.Pos == movedPosition, "移动后下一步自主回到等待，不重复移动");
        movement.StopEnemyAI();
        BattleService battle = new BattleService(false, 1);
        Assert(battle.StartRound(1, 1), "真实回合布阵成功");
        Assert(battle.EnemyAICount == battle.EnemyPieces.Count && battle.EnemyAIStatus().Contains("Prepare"), "开局敌军每颗都有机器，指挥进入Prepare");
        Assert(battle.StartRound(1, 2), "下一回合布阵成功");
        Assert(battle.EnemyAICount == battle.EnemyPieces.Count, "换回合回收旧机器，只持有新敌军");
        battle.StopEnemyAI();
        Assert(battle.EnemyAICount == 0 && battle.EnemyAIStatus().Contains("已停止"), "域退出清空敌棋与指挥机器");
        _scenarios++;
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

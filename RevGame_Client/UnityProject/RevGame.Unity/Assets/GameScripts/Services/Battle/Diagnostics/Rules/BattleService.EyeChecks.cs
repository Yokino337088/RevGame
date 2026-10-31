// ============================================================
// BattleService.EyeChecks.cs —— 编辑器眼位规则的固定输入验收。
// 造局只通过真实Place/Remove，动作走真实AI、支援与伤害出口；不能直接赋值眼位或增益制造通过。
// 测试服务与生产服务隔离，所有机器和事件在finally释放；发行包不包含这些入口。
// ============================================================
#if UNITY_EDITOR
using System;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>搭建真正角眼，三个守子相连；返回其中一个骨架棋用于破眼死亡测试。</summary>
        private Piece AddEyeGuards(GameSide side, bool upper)
        {
            int edge = upper ? 8 : 0;
            int inner = upper ? 7 : 1;
            Piece guard = AddImpactPiece(side, PieceKind.Shield, new BoardPos(inner, edge));
            AddImpactPiece(side, PieceKind.Shield, new BoardPos(edge, inner));
            AddImpactPiece(side, PieceKind.Shield, new BoardPos(inner, inner));
            RefreshGoState();
            return guard;
        }
        private static BattleService EyeFixture()
        {
            return new BattleService(false, 525) { Chapter = 1, Round = 1, Phase = GamePhase.Battle };
        }
        /// <summary>模型/AI验收入口，返回所有失败的具体业务原因；不使用画面或成功日志替代数值断言。</summary>
        public static void VerifyEyeRules(Action<bool, string> check)
        {
            VerifyLateEyeFormation(check);
            foreach (GameSide side in new[] { GameSide.Player, GameSide.Enemy })
            {
                var service = EyeFixture();
                try
                {
                    Piece guard = service.AddEyeGuards(side, false);
                    Piece garrison = service.AddImpactPiece(side, PieceKind.Crossbow, new BoardPos(0, 0));
                    Piece target = service.AddImpactPiece(side == GameSide.Player ? GameSide.Enemy : GameSide.Player, PieceKind.Shield, new BoardPos(8, 8));
                    service.RefreshGoState();
                    check(garrison.IsEyeGarrison && garrison.EyeCount == 1 && !garrison.IsLocked, side + "落入真眼后仍保留眼和行动能力");
                    check(!guard.IsEyeGarrison && guard.EyeAttackMultiplier == 1f, "守眼骨架不冒充眼中棋，不领取驻眼增益");
                    check(Math.Abs(garrison.EyeAttackMultiplier - (side == GameSide.Player ? 1f + Balance.PlayerEyeAttackBonus : 1f + Balance.EnemyEyeAttackBonus)) < 0.00001f && garrison.EyeSpeedMultiplier == (side == GameSide.Player ? 1f + Balance.PlayerEyeSpeedBonus : 1f + Balance.EnemyEyeSpeedBonus), side + "驻眼倍率符合最新阵营配置");
                    check(garrison.EyeDefenseMultiplier == (side == GameSide.Player ? 1f + Balance.PlayerEyeDefenseBonus : 1f), side + "驻眼防御符合阵营差异");
                    for (int repeat = 0; repeat < 5; repeat++)
                    {
                        GoRules.Evaluate(service.Board);
                    }
                    check(Math.Abs(garrison.EyeAttackMultiplier - (side == GameSide.Player ? 1f + Balance.PlayerEyeAttackBonus : 1f + Balance.EnemyEyeAttackBonus)) < 0.00001f && garrison.EyeSpeedMultiplier == (side == GameSide.Player ? 1f + Balance.PlayerEyeSpeedBonus : 1f + Balance.EnemyEyeSpeedBonus), "重复刷新不叠加属性");
                    BoardModel snapshot = service.Board.CreateSnapshot();
                    check(snapshot.FindPiece(garrison.Id).IsEyeGarrison, "深快照保留眼中棋身份");
                    snapshot.Remove(snapshot.FindPiece(guard.Id));
                    GoRules.Evaluate(snapshot);
                    check(!snapshot.FindPiece(garrison.Id).IsEyeGarrison && garrison.IsEyeGarrison, "试盘破眼不污染实盘");

                    int beforeHp = garrison.Hp;
                    service.DamagePiece(garrison, 100);
                    check(beforeHp - garrison.Hp == (side == GameSide.Player ? (int)(100d / (1d + Balance.PlayerEyeDefenseBonus)) : 100), "真实扣血符合当前表中我方防御、敌方不减伤");
                    BattleDamageKind damageKind;
                    int enhanced = service.MeasureComparableHit(garrison, target, out damageKind);
                    var ordinary = EyeFixture();
                    try
                    {
                        // 对照用同为四颗的直线棋群：连通加成相同但几何未成眼，不能再用落子顺序制造普通棋。
                        Piece plain = ordinary.AddImpactPiece(side, PieceKind.Crossbow, new BoardPos(0, 0));
                        for (int x = 1; x <= 3; x++)
                        {
                            ordinary.AddImpactPiece(side, PieceKind.Shield, new BoardPos(x, 0));
                        }
                        Piece dummy = ordinary.AddImpactPiece(target.Side, PieceKind.Shield, target.Pos);
                        ordinary.RefreshGoState();
                        check(!plain.IsEyeGarrison && plain.ConnectedCells == garrison.ConnectedCells, "同数量直线阵未成眼，连通加成一致");
                        int normal = ordinary.MeasureComparableHit(plain, dummy, out BattleDamageKind ignoredKind, damageKind);
                        check(Math.Abs(enhanced - normal * (side == GameSide.Player ? 1f + Balance.PlayerEyeAttackBonus : 1f + Balance.EnemyEyeAttackBonus)) <= 2, side + "真实同种伤害符合阵营倍率（仅允许整数取整差）：" + normal + "→" + enhanced);
                    }
                    finally { ordinary.StopEnemyAI(); }
                    int attacks = 0;
                    object owner = new object();
                    RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info =>
                    {
                        if (info.PieceId == garrison.Id)
                        {
                            attacks++;
                        }
                    }, owner: owner);
                    try
                    {
                        const int steps = 80;
                        for (int step = 0; step < steps; step++)
                        {
                            if (side == GameSide.Player)
                            {
                                service.EnsurePlayerAI(garrison).Tick(Balance.BattleSimulationStepSeconds);
                            }
                            else
                            {
                                service.EnsureEnemyAI(garrison).Tick(Balance.BattleSimulationStepSeconds);
                            }
                        }
                        float rate = side == GameSide.Player ? service.RangedActionsPerTick : Balance.EnemyAttackRate * garrison.EnemyRangedSpeedMultiplier;
                        rate /= Balance.AttackProfile(garrison.Def.Kind).IntervalScale;
                        int expected = (int)(steps * Balance.BattleSimulationStepSeconds / Balance.BattleTickSeconds * Balance.AttackSpeedScale * rate * (side == GameSide.Player ? 1f + Balance.PlayerEyeSpeedBonus : 1f + Balance.EnemyEyeSpeedBonus));
                        check(Math.Abs(attacks - expected) <= 1, side + "真实FSM驻眼攻速：实际" + attacks + "，预期" + expected);
                        check(garrison.Pos == new BoardPos(0, 0), "驻眼AI持续守阵，攻击不离眼");
                    }
                    finally { RevEvent.RemoveAllByOwner(owner); }
                    check(service.Board.Move(garrison, new BoardPos(4, 4)), "真实移动离开眼位");
                    service.RefreshGoState();
                    check(!garrison.IsEyeGarrison && garrison.EyeAttackMultiplier == 1f && garrison.EyeSpeedMultiplier == 1f, "离眼立即移除全部驻眼倍率");
                    // 移回几何有效的本方眼，应恢复驻眼增益。
                    check(service.Board.Move(garrison, new BoardPos(0, 0)), "可以重新驻眼");
                    service.RefreshGoState();
                    check(garrison.IsEyeGarrison, "重新驻眼恢复增益");
                    // 骨架死亡必须在DamagePiece返回前刷新，不允许多段技能继续使用旧防御。
                    service.DamagePiece(guard, int.MaxValue);
                    check(!garrison.IsEyeGarrison && service.Board.GetCell(new BoardPos(0, 0)).EyeSide == null, "骨架真实死亡同一步破眼并撤增益");
                    check(enhanced > 0, "驻眼攻击实际造成伤害");
                    service.StopEnemyAI();
                    check(service.PlayerAICount == 0 && service.EnemyAICount == 0, "结束后两方AI全部释放");
                }
                finally { service.StopEnemyAI(); }
            }
            VerifyEyeBenefits(check);
            VerifyEyePacing(check);
            VerifyEyeInvasion(check);
            VerifyEyeSupport(check);
            VerifyEyeDeploymentTiming(check);
        }
        /// <summary>回归中心先落的真实顺序：角、边、中央和双方均检查；补最后守子时必须更新既有中心棋。</summary>
        private static void VerifyLateEyeFormation(Action<bool, string> check)
        {
            foreach (GameSide side in new[] { GameSide.Player, GameSide.Enemy })
            {
                foreach (BoardPos center in new[] { new BoardPos(0, 0), new BoardPos(0, 4), new BoardPos(4, 4) })
                {
                    var service = EyeFixture();
                    try
                    {
                        Piece existing = service.AddImpactPiece(side, PieceKind.Crossbow, center);
                        service.RefreshGoState();
                        check(!existing.IsEyeGarrison, "中心棋先落且尚未围齐时不亮驻眼特效：" + side + center);
                        Piece last = null;
                        int count = 0, required = center.X == 0 ? (center.Y == 0 ? 2 : 3) : 4;
                        foreach (BoardPos pos in center.GetOrthogonalNeighbors())
                        {
                            if (!pos.IsInside(Consts.BoardSize))
                            {
                                continue;
                            }
                            last = service.AddImpactPiece(side, PieceKind.Shield, pos);
                            service.RefreshGoState();
                            count++;
                            check(existing.IsEyeGarrison == (count == required), "补齐最后四邻的同一次全盘刷新激活既有中心棋");
                        }
                        check(service.Board.GetCell(center).EyeSide == side && existing.EyeSpeedMultiplier == (side == GameSide.Player ? 1f + Balance.PlayerEyeSpeedBonus : 1f + Balance.EnemyEyeSpeedBonus), "后成眼模型标记与属性同时生效");
                        check(service.Board.CreateSnapshot().FindPiece(existing.Id).IsEyeGarrison, "后成眼不依赖落子历史，独立快照结果一致");
                        service.Board.Remove(last);
                        service.RefreshGoState();
                        check(!existing.IsEyeGarrison && existing.EyeSpeedMultiplier == 1f, "最后守子移除后即时撤销后成眼状态");
                        service.Board.Place(last, last.Pos);
                        service.RefreshGoState();
                        check(existing.IsEyeGarrison, "补回骨架后不移动中心也能重新激活");
                    }
                    finally { service.StopEnemyAI(); }
                }
            }
        }
        /// <summary>固定高生命双方只为让计时窗口完整发生；不修改伤害/无敌/计数，补兵仍由真实Tick与指挥状态机提交。</summary>
        public static int VerifyEyeDeploymentTiming(Action<bool, string> check)
        {
            var service = EyeFixture();
            try
            {
                service.AddEyeGuards(GameSide.Enemy, true);
                service.AddImpactPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
                service.RefreshGoState();
                service.ResetEnemyDeployment();
                service.EnsureEnemyCommander().Tick(EnemyAIState.Prepare, 0f, "固定高生命补兵夹具就绪");
                int previous = 0, windows = 0;
                float lastTime = 0f;
                while (service.BattleElapsed < Balance.DeployStartSeconds + 5f)
                {
                    service.Tick(Balance.BattleSimulationStepSeconds);

                    if (service.EnemyDeploymentCount == previous)
                    {
                        continue;
                    }
                    float interval = previous == 0 ? Balance.DeployStartSeconds : Math.Max(Balance.DeployIntervalMinSeconds, Balance.DeployIntervalBaseSeconds - service.EnemyIntelligence * Balance.DeployIntervalPerLevelSeconds);
                    check(service.BattleElapsed - lastTime >= interval, "真实Tick补兵仍遵守Excel独立冷却");
                    check(service.EnemyDeploymentCount - previous <= Balance.DeployBatchCount, "真实补兵每窗口不超过配置批量");
                    previous = service.EnemyDeploymentCount;
                    lastTime = service.BattleElapsed;
                    windows++;
                }
                // 新落子也可能把原有近战围成另一个驻眼棋；这里验证远程补兵，不能取名单里的第一颗驻眼棋冒充它。
                Piece garrison = service._enemyPieces.Find(p => p.IsEyeGarrison && p.Def.IsRanged);
                check(windows > 0 && garrison != null && garrison.Def.IsRanged, "真实战斗窗口向敌方眼位补下远程");
                check(garrison != null && Math.Abs(garrison.EyeAttackMultiplier - (1f + Balance.EnemyEyeAttackBonus)) < 0.00001f && Math.Abs(garrison.EyeSpeedMultiplier - (1f + Balance.EnemyEyeSpeedBonus)) < 0.00001f && garrison.EyeDefenseMultiplier == 1f, "战斗中后补的敌方驻眼远程也领取正确增益");
                int count = service.EnemyDeploymentCount;
                service.Phase = GamePhase.Settle;
                service.Tick(5f);
                check(service.EnemyDeploymentCount == count, "结算之后不迟到补兵");
                return windows;
            }
            finally { service.StopEnemyAI(); }
        }
        private static void VerifyEyeInvasion(Action<bool, string> check)
        {
            var service = EyeFixture();
            try
            {
                // 中央3×3空心敌阵：外围未封时中心虽然是真眼，也绝不能抢先钻进去。
                for (int y = 3; y <= 5; y++)
                {
                    for (int x = 3; x <= 5; x++)
                    {
                        if (x != 4 || y != 4)
                        {
                            service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(x, y));
                        }
                    }
                }
                Piece actor = service.AddImpactPiece(GameSide.Player, PieceKind.Swordman, new BoardPos(0, 0));
                service.RefreshGoState();
                BoardPos eye = new BoardPos(4, 4);
                check(!EyeTactics.CanEnter(service.Board, eye, 1, GameSide.Player, false), "外围仍有气，近战禁止提前钻眼");
                service._battleElapsed = Balance.PlayerMoveSeconds;
                service.EnsurePlayerAI(actor).Tick(0f);
                check(actor.Pos != eye && service._playerAI[actor.Id].State == PlayerAIState.Surround, "真实重量级AI先在外围封气");
                service.Board.Remove(actor);
                // 补齐外围所有出口；actor放在远角，保证其移动不会重新打开敌阵外气。
                for (int n = 3; n <= 5; n++)
                {
                    foreach (BoardPos pos in new[] { new BoardPos(2, n), new BoardPos(6, n), new BoardPos(n, 2), new BoardPos(n, 6) })
                    {
                        if (service.Board.CanPlace(pos))
                        {
                            service.AddImpactPiece(GameSide.Player, PieceKind.Shield, pos);
                        }
                    }
                }
                check(service.Board.Place(actor, new BoardPos(0, 0)), "破眼棋回到独立起点");
                service.RefreshGoState();
                check(EyeTactics.CanEnter(service.Board, eye, 1, GameSide.Player, false), "只剩最后一眼时允许近战突入");
                check(!EyeTactics.CanEnter(service.Board, eye, 1, GameSide.Player, true), "远程仍不得闯敌眼");
                string original = service.Board.Dump();
                service._battleElapsed += Balance.PlayerMoveSeconds;
                check(service.TryPlanPlayerMove(actor, out BoardPos choice, out bool breaksEye) && choice == eye && breaksEye, "封完外围后规划确实选择最后眼位");
                check(service.Board.Dump() == original, "全候选规划不改实战棋盘");
                actor.NextMoveAt = service._battleElapsed;
                service.EnsurePlayerAI(actor).Tick(0f);
                check(actor.Pos == eye && service._playerAI[actor.Id].State == PlayerAIState.BreakEye, "真实状态切换到破眼并提交占格");
                check(!actor.IsEyeGarrison && service._enemyPieces.TrueForAll(p => p.IsLocked), "侵入敌眼不领取己方增益，敌群真实锁死");
            }
            finally { service.StopEnemyAI(); }
            // 两眼完整敌群即使外围全封也不允许自动填其中一眼。
            var board = new BoardModel(9);
            int id = 1;
            for (int y = 3; y <= 5; y++)
            {
                for (int x = 2; x <= 6; x++)
                {
                    if (y == 4 && (x == 3 || x == 5))
                    {
                        continue;
                    }
                    board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy), new BoardPos(x, y));
                }
            }
            GoRules.Evaluate(board);
            check(!EyeTactics.CanEnter(board, new BoardPos(3, 4), 1, GameSide.Player, false), "两个眼仍受保护，不能提前填其中一眼");
            // 再封死外围，排除只是因为有外气才拒绝，真正验证两眼本身的保护条件。
            for (int x = 2; x <= 6; x++)
            {
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player), new BoardPos(x, 2));
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player), new BoardPos(x, 6));
            }
            for (int y = 3; y <= 5; y++)
            {
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player), new BoardPos(1, y));
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player), new BoardPos(7, y));
            }
            GoRules.Evaluate(board);
            check(board.GetPiece(new BoardPos(2, 3)).OpenLiberties == 2 && board.GetPiece(new BoardPos(2, 3)).EyeCount == 2, "两眼夹具外围确实全封");
            check(!EyeTactics.CanEnter(board, new BoardPos(3, 4), 1, GameSide.Player, false), "外围全封但还有两个眼时仍禁止突入");
        }
        private static void VerifyEyeSupport(Action<bool, string> check)
        {
            foreach (PieceKind kind in new[] { PieceKind.Swordman, PieceKind.Crossbow })
            {
                var service = EyeFixture();
                try
                {
                    service.AddEyeGuards(GameSide.Player, false);
                    service.AddEyeGuards(GameSide.Enemy, true);
                    // 本方外围也完全被敌军封住；支援填最后空眼后零空格气，但保留驻眼，仍必须成功。
                    foreach (BoardPos pos in new[] { new BoardPos(2, 0), new BoardPos(2, 1), new BoardPos(0, 2), new BoardPos(1, 2) })
                    {
                        service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, pos);
                    }
                    service.RefreshGoState();
                    check(service.TryDeployReinforcement(kind, StarTier.One), kind + "支援真实部署");
                    Piece born = service._playerPieces[service._playerPieces.Count - 1];
                    check(born.Pos == new BoardPos(0, 0) && born.IsEyeGarrison, "近战/远程支援均严格优先己方眼");
                    check(born.OpenLiberties == 0 && !born.IsLocked, "填眼虽消耗最后空格气，驻眼仍保全棋群");
                    service.RebuildBoardFromRoster();
                    service.RefreshGoState();
                    check(born.IsEyeGarrison, "跨回合重建保留玩家眼中驻军");
                }
                finally { service.StopEnemyAI(); }
            }
            for (int round = 1; round <= Balance.RoundsPerChapter; round++)
            {
                var service = new BattleService(false, 600 + round);
                try
                {
                    check(service.StartRound(1, round), "每回合真实生成敌阵");
                    bool hasGarrison = service._enemyPieces.Exists(p => p.IsEyeGarrison && p.Def.IsRanged);
                    bool hasEmptyEye = EyeTactics.TryFindOwnEye(service.Board, GameSide.Enemy, 1, out BoardPos ignored);
                    check(hasGarrison || !hasEmptyEye || service._enemyPieces.Count >= Balance.DeployLivingCap, "第" + round + "回合存在可用眼则部署远程守军");
                    check(service.EnemyDeploymentCount == 0, "初始驻眼属于初始阵容，不偷占战斗补兵预算");
                }
                finally { service.StopEnemyAI(); }
            }
        }
        /// <summary>图形Play专用可恢复场景：两方各保留一个空眼、一个真实驻眼棋，便于图标和Shader同时验收。</summary>
        public bool LoadEyeScenario()
        {
            if (!StartRound(1, 1))
            {
                return false;
            }
            // 先正常领取准备增益，再搭固定画面，避免随机精锐援军抢占用于验收3秒支援的空眼。
            Tick(Balance.PrepDelaySeconds + Balance.BattleSimulationStepSeconds);
            // 此专用场景隔离普通随机支援，眼位定时支援和双方AI仍由正式Tick驱动。
            _eventScheduled = false;
            StopEnemyAI();
            _board.Reset();
            _playerPieces.Clear();
            _enemyPieces.Clear();
            // 中心先落、守子后围：两颗中心均没有“落入已成型空眼”的历史，画面验收必须覆盖这个顺序。
            AddImpactPiece(GameSide.Player, PieceKind.Crossbow, new BoardPos(0, 0));
            AddImpactPiece(GameSide.Enemy, PieceKind.Crossbow, new BoardPos(8, 8));
            AddEyeGuards(GameSide.Player, false);
            AddEyeGuards(GameSide.Enemy, true);
            // 两个独立空眼放在中央，避免卡通眼睛被驻军身体挡住而无法核对。
            foreach (BoardPos p in new[] { new BoardPos(2, 3), new BoardPos(3, 2), new BoardPos(4, 3), new BoardPos(3, 4) })
            {
                AddImpactPiece(GameSide.Player, PieceKind.Shield, p);
            }
            foreach (BoardPos p in new[] { new BoardPos(4, 5), new BoardPos(5, 4), new BoardPos(6, 5), new BoardPos(5, 6) })
            {
                AddImpactPiece(GameSide.Enemy, PieceKind.Shield, p);
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }
        /// <summary>仅用于已装配眼位夹具的Play验收：真实伤害打伤四邻并致死驻军，返回是否发生原地复活。</summary>
        public bool PreviewEyeRevival()
        {
            Piece garrison = _board.GetPiece(new BoardPos(0, 0));

            if (Phase != GamePhase.Battle || garrison == null || !garrison.IsEyeGarrison || garrison.EyeReviveUsed)
            {
                return false;
            }
            foreach (BoardPos pos in garrison.Pos.GetOrthogonalNeighbors())
            {
                Piece guard = _board.GetPiece(pos);

                if (guard != null && guard.Side == GameSide.Player)
                {
                    DamagePiece(guard, guard.MaxHp);
                }
            }
            DamagePiece(garrison, int.MaxValue);
            return garrison.IsAlive && garrison.EyeReviveUsed;
        }
    }
}
#endif

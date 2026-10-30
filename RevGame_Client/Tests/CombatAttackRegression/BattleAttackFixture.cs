// 夹具只接线和造局：以下方法均不计算目标、不扣伤害、不计算气眼。
// 它和链接进来的 BattleService 属于同一 partial 类型，因而可调用真实私有方法。
// 此文件在 Assets 外，不会进入 Unity 或游戏包，也不向正式游戏暴露作弊入口。
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>关闭保胜辅助、随机事件和敌人出手，仅隔离验证这颗棋子的真实攻击链。</summary>
        public static BattleService CreateAttackRegressionFixture(int seed = 20261019)
        {
            return new BattleService(false, seed)
            {
                Chapter = 1,
                Round = 1,
                Phase = GamePhase.Battle
            };
        }
        /// <summary>借助正式占格入口添加高血量靶子；高血量避免连击第一发杀死目标后无法继续测量。</summary>
        public Piece AddRegressionPiece(PieceKind kind, StarTier star, GameSide side, BoardPos pos, float hpScale = 1f)
        {
            Piece piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, star), side, hpScale: hpScale);

            if (!_board.Place(piece, pos))
            {
                throw new System.InvalidOperationException("测试造局占格失败：" + kind + " @" + pos);
            }
            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }
        /// <summary>删除一个包围者后必须走正式 Remove 和刷新入口，验证拓扑版本缓存确实失效。</summary>
        public void RemoveRegressionPiece(Piece piece)
        {
            _board.Remove(piece);
            (piece.Side == GameSide.Player ? _playerPieces : _enemyPieces).Remove(piece);
            RefreshGoState();
        }
        public void RefreshRegressionGoState()
        {
            RefreshGoState();
        }
        /// <summary>处理真实行动进度、目标查询、Attack、DamagePiece 及事件；不能直接调 TakeDamage 伪造结果。</summary>
        public void RunRegressionPlayerStep(float fraction = 1f)
        {
            StepSide(GameSide.Player, fraction);
        }
        /// <summary>单独推进敌人，核对玩家守阵援击的改动没有放开敌方零气禁攻约束。</summary>
        public void RunRegressionEnemyStep(float fraction = 1f)
        {
            StepSide(GameSide.Enemy, fraction);
        }
        public RevGame.Battle.EnemyAI RegressionEnemyAI(Piece piece)
        {
            return EnsureEnemyAI(piece);
        }
        public void SetRegressionElapsed(float seconds)
        {
            _battleElapsed = seconds;
        }
        public void ResetRegressionDeployment()
        {
            ResetEnemyDeployment();
            EnsureEnemyCommander().Tick(RevGame.Battle.EnemyAIState.Prepare, 0f, "测试回合初始化");
        }
        /// <summary>仅测试宿主可直接推进落子时钟，不执行攻击，以隔离冷却、预算和落子事件。</summary>
        public void RunRegressionDeploymentAt(float seconds)
        {
            _battleElapsed = seconds;
            TickEnemyDeployment();
        }
        public static BattleService CreatePreparationRegressionFixture(int seed)
        {
            return new BattleService(true, seed) { Chapter = 1, Round = 1, Phase = GamePhase.Prepare };
        }
        public void GrantRegressionPreparation()
        {
            TickPreparation(Balance.PrepDelaySeconds);
        }
        public float RegressionArrowRate(Piece piece)
        {
            return PreparationAttackRate(piece);
        }
        /// <summary>
        /// Boss 造局：指定章节的 Boss 回合，放一颗带王冠的 Boss 主将（走正式占格）。
        /// 只用于离线验证 Boss 状态机，不经过完整布阵，避免随机敌阵干扰技能断言。
        /// </summary>
        public static BattleService CreateBossRegressionFixture(int chapter)
        {
            return new BattleService(false, 20261020) { Chapter = chapter, Round = Balance.BossRoundIndex, IsBossRound = true, Phase = GamePhase.Battle };
        }
        public Piece AddRegressionBoss(BoardPos pos, int size, float hpScale = 50f)
        {
            Piece boss = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.HeavySword, StarTier.Three), GameSide.Enemy, size, true, true, hpScale);

            if (!_board.Place(boss, pos))
            {
                throw new System.InvalidOperationException("测试造局 Boss 占格失败 @" + pos);
            }
            _enemyPieces.Add(boss);
            return boss;
        }
        /// <summary>按正式路径推进敌方一个模拟小步（Boss 走 BossAI，其余走 EnemyAI）。</summary>
        public void RunRegressionEnemyAI(float seconds)
        {
            _battleElapsed += seconds;
            StepEnemyAI(seconds);
        }
        public RevGame.Battle.BossAI RegressionBossAI(Piece boss)
        {
            return EnsureBossAI(boss);
        }
        /// <summary>只接真实百分比伤害出口；查询不扣血，提交仍触发正式护盾/死亡/计分。</summary>
        public int RegressionPercentDamage(Piece target, float ratio, int floor, bool predict)
        {
            var request = new DamageRequest(target, DamageBasis.MaxHp, BattleDamageKind.Artillery, factor: ratio, floor: floor);
            return predict ? EvaluateDamage(request).Damage : ResolveDamage(request);
        }
    }
}

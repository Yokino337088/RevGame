// ============================================================
// BattleService.EnemyAI.cs —— 敌方重量级状态机的宿主与生命周期。
// 一颗敌棋一台机器（包括Boss和后补棋），一队敌军一台指挥机器。
// 固定模拟 → 感知 → RevHeavyFSM切换 → 当前状态执行；评分算法只负责选择，不负责调度。
// ============================================================
using System.Collections.Generic;
using System.Text;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>
        /// 每颗敌棋一台状态机，键是棋子编号。
        /// 换回合/结算/退出时整批 Dispose 并清空，避免旧棋子的机器继续推进已移除的棋子。
        /// </summary>
        private readonly Dictionary<int, EnemyAI> _enemyAI = new Dictionary<int, EnemyAI>();
        /// <summary>
        /// 敌方指挥状态机：负责布阵（Prepare）与战术增援（Deploy），与棋子机器独立。
        /// </summary>
        private EnemyAI _enemyCommander;
        /// <summary>
        /// 本次布阵已通过预检、等待提交的完整名单；只在 Prepare 状态存在的瞬间有值。
        /// 提交后立即置空，避免下一次 Tick 重复生成同一批敌棋。
        /// </summary>
        private IReadOnlyList<Piece> _plannedEnemyFormation;
        /// <summary>每步驱动敌方 AI 时复用的名单快照（容量按整盘预留，避免每步分配）。</summary>
        private readonly List<Piece> _aiStepBuffer = new List<Piece>(81);
        /// <summary>
        /// 当前持有状态机的敌棋数量（不含指挥机器，含 Boss 主将的 BossAI）。
        /// 换回合/结算后应回到与敌军数量一致，GM 与测试用它核对是否泄漏。
        /// </summary>
        public int EnemyAICount => _enemyAI.Count + _bossAI.Count;

        /// <summary>只适配数据与原子提交，不写感知、选目标、冷却或战术分支；那些属于EnemyStates。</summary>
        private sealed class EnemyOwner : IEnemyAIOwner
        {
            private readonly BattleService _service;
            public Piece Actor { get; }
            public EnemyOwner(BattleService service, Piece actor) { _service = service; Actor = actor; }
            public RevGame.Board.BoardModel Board => _service._board;
            public GamePhase Phase => _service.Phase;
            public float Elapsed => _service._battleElapsed;
            public int Intelligence => _service.EnemyIntelligence;
            public int Chapter => _service.Chapter;
            public int Round => _service.Round;
            public bool KingCaptured => _service.IsKingCaptured();
            public int DeploymentCount => _service._enemyDeploymentCount;
            public IReadOnlyList<Piece> PlannedEnemies => _service._plannedEnemyFormation;
            public float NextDeploymentAt { get { return _service._nextEnemyDeployAt; } set { _service._nextEnemyDeployAt = value; } }
            public void CommitFormationPiece(Piece piece)
            {
                if (!Board.Place(piece, piece.Pos))
                {
                    throw new System.InvalidOperationException("敌方占格预检与提交不一致");
                }
                _service._enemyPieces.Add(piece);
                // Boss 主将拿 BossAI，其余敌棋拿 EnemyAI；同一颗棋只会有一台机器。
                _service.EnsureAIFor(piece);
            }
            public Piece CreateDeployment(PieceDef definition, RoundDef round)
            {
                return new Piece(_service._nextPieceId, definition, GameSide.Enemy, hpScale: round.HpScale, attackScale: round.AttackScale);
            }
            public void CommitDeployment(Piece piece, BoardPos position, string intention) { _service.CommitEnemyDeployment(piece, position, intention); }
            public bool PredictAttackDefeat(Piece attacker, Piece target) { return _service.PredictAttackDefeat(attacker, target); }
            public void Attack(Piece attacker, Piece target) { _service.Attack(attacker, target); }
            public void Move(Piece piece, BoardPos position)
            {
                if (!Board.Move(piece, position))
                {
                    throw new System.InvalidOperationException("敌方移动预检与提交不一致");
                }
                _service.RefreshGoState();
                RevEvent.DispatchEvent<int>(piece.Def.IsRanged ? Events.PieceMoved : Events.PieceFormationMoved, piece.Id);
            }
        }

        /// <summary>
        /// 按棋子编号懒创建棋子状态机：已有就复用，没有才 new。
        /// 为什么懒创建：布阵、增援、旧回合残棋都走同一入口，调用方不必关心创建时机。
        /// </summary>
        private EnemyAI EnsureEnemyAI(Piece piece)
        {
            EnemyAI ai;
            if (!_enemyAI.TryGetValue(piece.Id, out ai))
            {
                ai = new EnemyAI(false, new EnemyOwner(this, piece));
                _enemyAI.Add(piece.Id, ai);
            }
            return ai;
        }

        /// <summary>
        /// 懒创建敌方指挥机器（负责布阵与增援），与单颗棋子的机器完全独立。
        /// 分开的原因：指挥的 Prepare/Deploy/Stopped 与棋子的 Attack/Hold 状态语义不同，
        /// 共用一台会导致当前状态互相覆盖。
        /// </summary>
        private EnemyAI EnsureEnemyCommander()
        {
            if (_enemyCommander == null)
            {
                _enemyCommander = new EnemyAI(true, new EnemyOwner(this, null));
            }
            return _enemyCommander;
        }

        /// <summary>布阵已完成纯算法预检；只有指挥Prepare状态才能提交。失败预检不能清掉旧回合AI。</summary>
        private void CommitEnemyFormation(List<Piece> planned)
        {
            _plannedEnemyFormation = planned;
            try { EnsureEnemyCommander().Tick(EnemyAIState.Prepare, 0f, "敌阵预检完成，进入布阵状态"); }
            finally { _plannedEnemyFormation = null; }
        }

        /// <summary>
        /// 固定模拟小步驱动所有敌棋：只传时间，不替状态决定攻击还是移动。
        /// 每颗棋的机器自己感知并执行；擒王后立刻停止本步剩余棋子，避免继续扣血。
        /// </summary>
        private void StepEnemyAI(float seconds)
        {
            if (Phase != GamePhase.Battle)
            {
                return;
            }
            // 遍历快照而不是 _enemyPieces 本身：Boss 召唤会在循环中途加入新小兵，
            // 直接遍历原列表会抛“集合已修改”。新召唤的小兵从下一个模拟步才开始行动。
            _aiStepBuffer.Clear();
            _aiStepBuffer.AddRange(_enemyPieces);
            foreach (Piece piece in _aiStepBuffer)
            {
                // Boss 主将走自己的技能状态机；小兵与 Boss 护卫走普通敌棋状态机。
                if (IsBossMaster(piece))
                {
                    EnsureBossAI(piece).Tick(seconds);
                }
                else
                {
                    EnsureEnemyAI(piece).Tick(seconds);
                }
                if (IsKingCaptured())
                {
                    return;
                }
            }
        }

        /// <summary>
        /// 真实扣血死亡后把该棋强制切到 Dead 终态。
        /// 为什么用强制入口：死亡是事实，不需要再经过感知；表现层动画是否播完与逻辑无关。
        /// </summary>
        private void MarkEnemyDead(Piece piece)
        {
            if (piece.Side != GameSide.Enemy)
            {
                return;
            }
            if (IsBossMaster(piece))
            {
                EnsureBossAI(piece).Force(BossAIState.Dead, "真实扣血后死亡");
            }
            else
            {
                EnsureEnemyAI(piece).Tick(EnemyAIState.Dead, 0f, "真实扣血后死亡");
            }
        }

        /// <summary>停止AI并释放状态实例/委托。用于结算、回合替换和域退出，不等死亡特效播放完。</summary>
        public void StopEnemyAI()
        {
            // 旧公共清理入口由流程/夹具统一调用，同时收走玩家机器，防止跨回合残留。
            StopPlayerAI();
            _helperAI?.Dispose();
            _helperAI = null;
            ResetHelperProtection();
            ResetHelperStorm();
            ClearBossEffects();
            foreach (EnemyAI ai in _enemyAI.Values) { ai.Dispose(); }
            _enemyAI.Clear();
            foreach (BossAI ai in _bossAI.Values) { ai.Dispose(); }
            _bossAI.Clear();
            _enemyCommander?.Dispose();
            _enemyCommander = null;
            _plannedEnemyFormation = null;
        }

        /// <summary>GM只读快照：列出当前状态、模拟状态时长及最近切换原因，不提供任意强制改状态。</summary>
        public string EnemyAIStatus()
        {
            var result = new StringBuilder();
            result.AppendLine("敌方指挥：" + (_enemyCommander == null ? "未创建/已停止" : _enemyCommander.State.ToString()));
            foreach (KeyValuePair<int, BossAI> boss in _bossAI)
            {
                var bossHistory = boss.Value.History;
                string bossReason = bossHistory.Length == 0 ? "" : bossHistory[bossHistory.Length - 1].Reason;
                result.AppendLine("Boss #" + boss.Key + " " + boss.Value.State + " · " + boss.Value.StateTime.ToString("0.00") + "秒 · " + bossReason);
            }
            foreach (KeyValuePair<int, EnemyAI> entry in _enemyAI)
            {
                var history = entry.Value.History;
                string reason = history.Length == 0 ? "" : history[history.Length - 1].Reason;
                result.AppendLine("#" + entry.Key + " " + entry.Value.State + " · " + entry.Value.StateTime.ToString("0.00") + "秒 · " + reason);
            }
            return result.ToString();
        }
    }
}

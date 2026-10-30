// ============================================================
// BattleService.BossAI.cs —— Boss 主将状态机的宿主：原子动作 + 生命周期。
//
// 本文件是 BattleService 的 partial 片段（与其他 BattleService.*.cs 共享同一实例）。
// 它只做两件事：
// 1) 实现 IBossAIOwner：把“技能伤害 / 瞬移 / 回血 / 召唤 / 广播”变成走统一规则的原子操作；
// 2) 管理 Boss 状态机的创建与释放（与普通敌棋的 EnemyAI 并列，见 BattleService.EnemyAI.cs）。
// “什么时候放哪招”不在这里决定——那是 BossStates.cs 与 BossTactics.cs 的工作。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>
        /// Boss 主将的状态机，键是棋子编号。实际只会有一项（每个 Boss 回合一个主将），
        /// 用字典是为了与 _enemyAI 的回收方式保持一致，并防止编号复用时串到旧机器。
        /// </summary>
        private readonly Dictionary<int, BossAI> _bossAI = new Dictionary<int, BossAI>();

        /// <summary>
        /// Boss 宿主适配器：只提供数据和原子动作，不写任何“放哪招”的判断。
        /// Boss 自己的运行数据（冷却、是否狂暴…）也存在这里：一个 Boss 一份，回合结束随机器一起丢弃，
        /// 不会把上一章 Boss 的冷却带到下一章。
        /// </summary>
        private sealed class BossOwner : IBossAIOwner
        {
            private readonly BattleService _service;
            public Piece Boss { get; }
            public BossOwner(BattleService service, Piece boss)
            {
                _service = service;
                Boss = boss;
                // 开战后先等一会儿再放第一招，给玩家看清 Boss 阵型的时间。
                NextSkillAt = service._battleElapsed + Balance.BossFirstSkillSeconds;
            }
            public RevGame.Board.BoardModel Board => _service._board;
            public GamePhase Phase => _service.Phase;
            public float Elapsed => _service._battleElapsed;
            public int Chapter => _service.Chapter;
            public int Intelligence => _service.EnemyIntelligence;
            public bool KingCaptured => _service.IsKingCaptured();
            public bool Enraged { get; set; }
            public float NextSkillAt { get; set; }
            /// <summary>长度 = 技能枚举数量；初始全是 0，表示所有技能一开始都不在冷却。</summary>
            public float[] SkillReadyAt { get; } = new float[Enum.GetValues(typeof(BossSkillKind)).Length];
            public int[] SkillUseCounts { get; } = new int[System.Enum.GetValues(typeof(BossSkillKind)).Length];
            public void ExclusiveHit(BossSkillInfo info) { _service.ApplyExclusiveHit(Boss, info); }
            public BossSkillKind? LastSkill { get; set; }
            public BossSkillPlan PendingPlan { get; set; }
            public bool PredictAttackDefeat(Piece attacker, Piece target) { return _service.PredictAttackDefeat(attacker, target); }
            public void Attack(Piece attacker, Piece target) { _service.Attack(attacker, target); }
            public void Move(Piece piece, BoardPos position)
            {
                if (!Board.Move(piece, position))
                {
                    throw new InvalidOperationException("Boss 移动预检与提交不一致");
                }
                _service.RefreshGoState();
                RevEvent.DispatchEvent<int>(Events.PieceMoved, piece.Id);
            }
            public int SkillDamage(Piece target, float power) { return _service.BossSkillDamage(Boss, target, power, PendingPlan.Kind); }
            public bool Teleport(BoardPos position) { return _service.TeleportBoss(Boss, position); }
            public int HealBoss(int amount)
            {
                int healed = Boss.Heal(amount);
                if (healed > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, Boss.Id);
                }
                return healed;
            }
            public int Summon(int count) { return _service.SummonNearBoss(Boss, count); }
            public void Announce(BossSkillInfo info, bool telegraph) { _service.AnnounceBossSkill(info, telegraph); }
        }

        /// <summary>
        /// 按“是不是 Boss 主将”为敌棋创建对应的状态机：主将 → BossAI，其他（含 Boss 护卫） → EnemyAI。
        /// 布阵、增援、驱动都经过这里，保证同一颗棋只会有一台机器。
        /// </summary>
        private void EnsureAIFor(Piece piece)
        {
            if (IsBossMaster(piece))
            {
                EnsureBossAI(piece);
            }
            else
            {
                EnsureEnemyAI(piece);
            }
        }

        /// <summary>只有 Boss 回合里带王冠的主将使用 Boss 状态机。</summary>
        private bool IsBossMaster(Piece piece)
        {
            return IsBossRound && piece.Side == GameSide.Enemy && piece.IsKing;
        }

        /// <summary>懒创建 Boss 状态机：已有就复用，没有才 new。</summary>
        private BossAI EnsureBossAI(Piece boss)
        {
            BossAI ai;
            if (!_bossAI.TryGetValue(boss.Id, out ai))
            {
                ai = new BossAI(new BossOwner(this, boss));
                _bossAI.Add(boss.Id, ai);
            }
            return ai;
        }

        /// <summary>
        /// 技能伤害公式：Boss 一次普通攻击 × 技能倍率（已含章节换算）× 锁死倍率，
        /// 再叠加 Boss 回合的“决死加速”。走 DamagePiece 统一出口，护盾、减伤、眼位复活照常生效。
        /// </summary>
        private int BossSkillDamage(Piece boss, Piece target, float power, BossSkillKind kind = BossSkillKind.Cleave)
        {
            if (target == null || !target.IsAlive || power <= 0f)
            {
                return 0;
            }
            int real = ResolveDamage(new DamageRequest(target, DamageBasis.BossSkill, (BattleDamageKind)((int)BattleDamageKind.BossCleave + (int)kind), boss, power));
            // 击杀会改变占格和气眼，立刻刷新，后面几段伤害读到的是最新棋盘。
            RefreshGoState();
            return real;
        }

        /// <summary>
        /// 瞬移：整块身体放得下才移动（无视眼位，这是技能），成功后刷新气眼并广播移动。
        /// 返回 false 时 Boss 原地不动，由技能状态决定原地爆炸。
        /// </summary>
        private bool TeleportBoss(Piece boss, BoardPos position)
        {
            if (position == boss.Pos || !_board.CanFit(position, boss.Size, GameSide.Enemy, boss, respectEnemyEyes: false))
            {
                return false;
            }
            if (!_board.Move(boss, position, respectEnemyEyes: false))
            {
                return false;
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMoved, boss.Id);
            return true;
        }

        /// <summary>
        /// 在 Boss 附近召唤小兵：兵种沿用增援规则（EnemyReinforceConfig），落点取离 Boss 最近的空格。
        /// 复用 CommitEnemyDeployment 提交，因此占格、编号、AI 创建、事件广播与普通增援完全一致；
        /// 同样计入本回合增援次数，避免“召唤”绕过敌方数量上限。
        /// </summary>
        private int SummonNearBoss(Piece boss, int count)
        {
            RoundDef round = LevelCatalog.GetRound(Chapter, Round);
            if (round == null)
            {
                return 0;
            }
            int summoned = 0;
            for (int i = 0; i < count; i++)
            {
                if (_board.CountSide(GameSide.Enemy) >= Balance.DeployLivingCap)
                {
                    break;
                }
                PieceKind? kind = LevelCatalog.PickReinforcement(_enemyDeploymentCount, EnemyIntelligence);
                BoardPos position;
                if (!kind.HasValue || !TryFindCellNear(boss, out position))
                {
                    break;
                }
                PieceDef def = PieceCatalog.Get(kind.Value, LevelCatalog.GetEnemyStar(Chapter, false));
                var minion = new Piece(_nextPieceId, def, GameSide.Enemy, hpScale: round.HpScale, attackScale: round.AttackScale);
                // 新召唤的小兵先等一个完整移动冷却，不会落地瞬间就冲到玩家脸上。
                minion.NextMoveAt = _battleElapsed + Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks;
                CommitEnemyDeployment(minion, position, "Boss 召唤影卫");
                summoned++;
            }
            return summoned;
        }

        /// <summary>找离 Boss 身体最近的可放置空格（同距离按扫描顺序，结果可复现）。</summary>
        private bool TryFindCellNear(Piece boss, out BoardPos position)
        {
            position = boss.Pos;
            int best = int.MaxValue;
            for (int y = 0; y < _board.Size; y++)
            {
                for (int x = 0; x < _board.Size; x++)
                {
                    var cell = new BoardPos(x, y);
                    if (!_board.CanFit(cell, 1, GameSide.Enemy))
                    {
                        continue;
                    }
                    int gap = Math.Max(Math.Max(0, Math.Max(x - (boss.Pos.X + boss.Size - 1), boss.Pos.X - x)), Math.Max(0, Math.Max(y - (boss.Pos.Y + boss.Size - 1), boss.Pos.Y - y)));
                    if (gap < best)
                    {
                        best = gap;
                        position = cell;
                    }
                }
            }
            return best != int.MaxValue;
        }

        /// <summary>
        /// 广播技能事件。预警时额外写一条战报（HUD 弹幕由面板订阅预警事件自己显示）。
        /// 事件只携带值类型快照，订阅者不能借此修改规则数据。
        /// </summary>
        private void AnnounceBossSkill(BossSkillInfo info, bool telegraph)
        {
            if (telegraph)
            {
                BossSkillDef def = BossSkillCatalog.Get(info.Kind);
                RevEvent.DispatchEvent<BossSkillInfo>(Events.BossSkillTelegraph, info);
                RevEvent.DispatchEvent<string>(Events.BattleLog, "Boss 蓄力 · " + def.Name + "：" + def.Hint);
            }
            else
            {
                RevEvent.DispatchEvent<BossSkillInfo>(Events.BossSkillCast, info);
            }
        }

        /// <summary>
        /// 测试/GM 入口：让当前 Boss 立刻放指定技能（仍会检查是否有合法目标）。
        /// 正式战斗由 Boss 自己挑技能；这里只用于验收每个技能的效果与特效。
        /// </summary>
        public bool ForceBossSkill(BossSkillKind kind)
        {
            foreach (KeyValuePair<int, BossAI> entry in _bossAI)
            {
                return entry.Value.TryCast(kind);
            }
            return false;
        }

        /// <summary>当前 Boss 状态（GM/测试只读）；没有 Boss 时返回 null。</summary>
        public BossAIState? BossState
        {
            get
            {
                foreach (KeyValuePair<int, BossAI> entry in _bossAI)
                {
                    return entry.Value.State;
                }
                return null;
            }
        }
    }
}

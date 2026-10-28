// ============================================================
// BossAI.cs —— Boss 主将专属的 Revolution 重量级状态机：装配、驱动、释放（不含决策）。
//
// 【为什么 Boss 要单独一台状态机，而不是复用 EnemyAI】
// 普通敌棋只有“等待/攻击/推进/锁死/死亡”五个状态；Boss 有十个以上技能状态，
// 每个技能还分“蓄力 → 命中 → 收招”三段。塞进 EnemyAI 会让小兵的状态表也背上 Boss 的技能，
// 而且两边的状态枚举会互相污染。分开后：小兵改行为不影响 Boss，给 Boss 加技能也不影响小兵。
//
// 【谁会拿到这台机器】
// 只有 Boss 回合里带王冠的主将（Piece.IsKing）。同一章的其他 Boss 身体（护卫）仍用 EnemyAI。
//
// 【文件分工】（与 EnemyAI 一致）
// · 本文件        ：状态枚举、宿主接口、机器装配与驱动；
// · BossStates.cs ：每个状态的真实业务（感知、蓄力、命中、收招）；
// · BossTactics.cs：技能评分与目标计算（纯算法）；
// · BattleService.BossAI.cs：服务侧宿主，提供“扣血/瞬移/回血/召唤/广播”这些原子动作。
//
// 【用到的框架概念】
// RevHeavyFSM（重量级状态机）、RevHeavyFSMState（状态基类）、RevIHeavyFSMOwner（宿主标记）、
// StateTime / GetHistory（状态时长与切换历史，GM 观察用）、IDisposable（回合结束显式释放）。
// ============================================================
using System;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>
    /// Boss 状态。技能状态的名字与 <see cref="BossSkillKind"/> 一一对应（Enrage 也是一个状态）。
    /// Idle 不是“发呆”：它负责普通攻击、靠近玩家，并决定下一招放什么。
    /// </summary>
    public enum BossAIState
    {
        Idle,
        Cleave,
        Snipe,
        Bombard,
        BulletRing,
        BlinkBlast,
        LaserSpin,
        SoulDrain,
        Summon,
        Enrage,
        MeteorShower,
        CrossLaser,
        FrostNova,
        ChainLightning,
        PoisonMarsh,
        FlameWall,
        IceLance,
        GravityWell,
        DoomSigil,

        StarStorm, BlackHole, SolarFlare, SunBrand, TidalSurge, Maelstrom, SwampMaw, MireChains, FrozenPrison, GlacialAegis, DivineCross, Sanctuary,

        Dead,
        Stopped,
    }

    /// <summary>
    /// Boss 状态能调用的宿主能力。和 IEnemyAIOwner 一样是“窄接口”：
    /// 状态只能通过这些方法改变世界，伤害公式、占格预检、事件广播仍由服务统一负责。
    /// </summary>
    public interface IBossAIOwner : RevIHeavyFSMOwner, IAttackPrediction
    {
        // ---------------- 只读局面 ----------------
        /// <summary>真实棋盘（只读查询；改变棋盘必须走下面的动作方法）。</summary>
        BoardModel Board { get; }
        /// <summary>这台机器负责的 Boss 主将。</summary>
        Piece Boss { get; }
        /// <summary>当前阶段；离开战斗时 Boss 停手。</summary>
        GamePhase Phase { get; }
        /// <summary>战斗累计模拟秒数，所有冷却以它为准。</summary>
        float Elapsed { get; }
        /// <summary>当前章节：决定解锁哪些技能、冷却和伤害的章节换算。</summary>
        int Chapter { get; }
        /// <summary>AI 档位（1~4），影响技能评分偏好。</summary>
        int Intelligence { get; }
        /// <summary>主将是否已被擒（死亡或零气）；为 true 时立刻停止一切动作。</summary>
        bool KingCaptured { get; }

        // ---------------- Boss 自己的运行数据（每个 Boss 一份，换回合随宿主一起丢弃） ----------------
        /// <summary>是否已经狂暴（每场只触发一次）。</summary>
        bool Enraged { get; set; }
        /// <summary>下一次允许放技能的最早时刻（公共间隔）。</summary>
        float NextSkillAt { get; set; }
        /// <summary>按 BossSkillKind 下标存放的各技能“冷却结束时刻”。</summary>
        float[] SkillReadyAt { get; }
        /// <summary>本局各技能开始施放的次数。优先轮换未用招式，防止全屏评分长期垄断。</summary>
        int[] SkillUseCounts { get; }
        /// <summary>专属技能提交，仍由服务统一处理扣血、控制与占格。</summary>
        void ExclusiveHit(BossSkillInfo info);
        /// <summary>上一次使用的技能，用于“避免连放同一招”。</summary>
        BossSkillKind? LastSkill { get; set; }
        /// <summary>Idle 选好、等待技能状态在 OnEnter 读取的计划。</summary>
        BossSkillPlan PendingPlan { get; set; }

        // ---------------- 原子动作（由服务实现） ----------------
        /// <summary>普通攻击：与小兵共用同一套伤害公式。</summary>
        void Attack(Piece attacker, Piece target);
        /// <summary>普通移动（靠近玩家）。</summary>
        void Move(Piece piece, BoardPos position);
        /// <summary>技能伤害：按“Boss 一次普通攻击 × power”结算，返回真实扣掉的血量。</summary>
        int SkillDamage(Piece target, float power);
        /// <summary>瞬移：整块身体移动到 position，失败返回 false（落点被占）。</summary>
        bool Teleport(BoardPos position);
        /// <summary>Boss 回血，返回真实回复量。</summary>
        int HealBoss(int amount);
        /// <summary>在 Boss 附近召唤小兵，返回实际召唤数量（空间不足时可能少于 count）。</summary>
        int Summon(int count);
        /// <summary>广播技能事件：telegraph=true 为蓄力预警，false 为命中。同时负责 HUD 弹幕与战报。</summary>
        void Announce(BossSkillInfo info, bool telegraph);
    }

    /// <summary>
    /// 一台 Boss 状态机的包装。与 EnemyAI 一样：只装配、驱动、释放，不写任何 AI 决策。
    /// </summary>
    public sealed class BossAI : IDisposable
    {
        /// <summary>状态读取数据、提交动作的宿主；Dispose 时置空，尽早释放对服务的引用。</summary>
        private IBossAIOwner _owner;
        /// <summary>框架的重量级状态机。</summary>
        private readonly RevHeavyFSM<BossAIState, IBossAIOwner> _machine;
        /// <summary>是否已释放：释放后再驱动会抛异常，防止旧回合 Boss 被继续推进。</summary>
        private bool _disposed;
        /// <summary>当前状态（GM/测试只读）。</summary>
        public BossAIState State => _machine.CurrentStateType;
        /// <summary>当前状态已持续的模拟秒数。</summary>
        public float StateTime => _machine.StateTime;
        /// <summary>切换历史（含原因），排查“Boss 为什么没放技能”时先看这里。</summary>
        public RevHeavyFSMTransition<BossAIState>[] History => _machine.GetHistory();

        /// <summary>创建并注册全部 Boss 状态，初始为 Idle。</summary>
        public BossAI(IBossAIOwner owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _machine = new RevHeavyFSM<BossAIState, IBossAIOwner>(_owner);
            // 显式注册（不用反射）：IL2CPP 裁剪时能看到每个状态类型，也没有运行期查找开销。
            // 未解锁的技能也注册：解锁与否由 BossTactics 在挑选时判断，状态表保持完整、便于测试单个技能。
            _machine.AddState(BossAIState.Idle, new BossIdleState(_machine));
            _machine.AddState(BossAIState.Cleave, new BossCleaveState(_machine));
            _machine.AddState(BossAIState.Snipe, new BossSnipeState(_machine));
            _machine.AddState(BossAIState.Bombard, new BossBombardState(_machine));
            _machine.AddState(BossAIState.BulletRing, new BossBulletRingState(_machine));
            _machine.AddState(BossAIState.BlinkBlast, new BossBlinkBlastState(_machine));
            _machine.AddState(BossAIState.LaserSpin, new BossLaserSpinState(_machine));
            _machine.AddState(BossAIState.SoulDrain, new BossSoulDrainState(_machine));
            _machine.AddState(BossAIState.Summon, new BossSummonState(_machine));
            _machine.AddState(BossAIState.Enrage, new BossEnrageState(_machine));
            _machine.AddState(BossAIState.MeteorShower, new BossMeteorShowerState(_machine));
            _machine.AddState(BossAIState.CrossLaser, new BossCrossLaserState(_machine));
            _machine.AddState(BossAIState.FrostNova, new BossFrostNovaState(_machine));
            _machine.AddState(BossAIState.ChainLightning, new BossChainLightningState(_machine));
            _machine.AddState(BossAIState.PoisonMarsh, new BossPoisonMarshState(_machine));
            _machine.AddState(BossAIState.FlameWall, new BossFlameWallState(_machine));
            _machine.AddState(BossAIState.IceLance, new BossIceLanceState(_machine));
            _machine.AddState(BossAIState.GravityWell, new BossGravityWellState(_machine));
            _machine.AddState(BossAIState.DoomSigil, new BossDoomSigilState(_machine));
            _machine.AddState(BossAIState.StarStorm, new BossExclusiveState(_machine, BossSkillKind.StarStorm));
            _machine.AddState(BossAIState.BlackHole, new BossExclusiveState(_machine, BossSkillKind.BlackHole));
            _machine.AddState(BossAIState.SolarFlare, new BossExclusiveState(_machine, BossSkillKind.SolarFlare));
            _machine.AddState(BossAIState.SunBrand, new BossExclusiveState(_machine, BossSkillKind.SunBrand));
            _machine.AddState(BossAIState.TidalSurge, new BossExclusiveState(_machine, BossSkillKind.TidalSurge));
            _machine.AddState(BossAIState.Maelstrom, new BossExclusiveState(_machine, BossSkillKind.Maelstrom));
            _machine.AddState(BossAIState.SwampMaw, new BossExclusiveState(_machine, BossSkillKind.SwampMaw));
            _machine.AddState(BossAIState.MireChains, new BossExclusiveState(_machine, BossSkillKind.MireChains));
            _machine.AddState(BossAIState.FrozenPrison, new BossExclusiveState(_machine, BossSkillKind.FrozenPrison));
            _machine.AddState(BossAIState.GlacialAegis, new BossExclusiveState(_machine, BossSkillKind.GlacialAegis));
            _machine.AddState(BossAIState.DivineCross, new BossExclusiveState(_machine, BossSkillKind.DivineCross));
            _machine.AddState(BossAIState.Sanctuary, new BossExclusiveState(_machine, BossSkillKind.Sanctuary));
            _machine.AddState(BossAIState.Dead, new BossDeadState(_machine));
            _machine.AddState(BossAIState.Stopped, new BossStoppedState(_machine));
            _machine.ChangeState(BossAIState.Idle, "创建Boss AI");
        }

        /// <summary>技能种类 → 对应的技能状态。新增技能时这里也要加一行。</summary>
        public static BossAIState StateOf(BossSkillKind kind)
        {
            switch (kind)
            {
                case BossSkillKind.Cleave: return BossAIState.Cleave;
                case BossSkillKind.Snipe: return BossAIState.Snipe;
                case BossSkillKind.Bombard: return BossAIState.Bombard;
                case BossSkillKind.BulletRing: return BossAIState.BulletRing;
                case BossSkillKind.BlinkBlast: return BossAIState.BlinkBlast;
                case BossSkillKind.LaserSpin: return BossAIState.LaserSpin;
                case BossSkillKind.SoulDrain: return BossAIState.SoulDrain;
                case BossSkillKind.Summon: return BossAIState.Summon;
                case BossSkillKind.Enrage: return BossAIState.Enrage;
                case BossSkillKind.MeteorShower: return BossAIState.MeteorShower;
                case BossSkillKind.CrossLaser: return BossAIState.CrossLaser;
                case BossSkillKind.FrostNova: return BossAIState.FrostNova;
                case BossSkillKind.ChainLightning: return BossAIState.ChainLightning;
                case BossSkillKind.PoisonMarsh: return BossAIState.PoisonMarsh;
                case BossSkillKind.FlameWall: return BossAIState.FlameWall;
                case BossSkillKind.IceLance: return BossAIState.IceLance;
                case BossSkillKind.GravityWell: return BossAIState.GravityWell;
                case BossSkillKind.DoomSigil: return BossAIState.DoomSigil;
                case BossSkillKind.StarStorm: return BossAIState.StarStorm;
                case BossSkillKind.BlackHole: return BossAIState.BlackHole;
                case BossSkillKind.SolarFlare: return BossAIState.SolarFlare;
                case BossSkillKind.SunBrand: return BossAIState.SunBrand;
                case BossSkillKind.TidalSurge: return BossAIState.TidalSurge;
                case BossSkillKind.Maelstrom: return BossAIState.Maelstrom;
                case BossSkillKind.SwampMaw: return BossAIState.SwampMaw;
                case BossSkillKind.MireChains: return BossAIState.MireChains;
                case BossSkillKind.FrozenPrison: return BossAIState.FrozenPrison;
                case BossSkillKind.GlacialAegis: return BossAIState.GlacialAegis;
                case BossSkillKind.DivineCross: return BossAIState.DivineCross;
                case BossSkillKind.Sanctuary: return BossAIState.Sanctuary;


            }
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "没有对应的Boss技能状态");
        }

        /// <summary>
        /// 正常驱动：把固定模拟小步交给当前状态。感知、切换、执行都在 BossStates.cs 里完成。
        /// </summary>
        /// <param name="seconds">固定模拟步长（不是渲染帧时间），保证不同帧率下技能节奏一致。</param>
        public void Tick(float seconds)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BossAI));
            }
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }
            _machine.UpdateState(seconds);
        }

        /// <summary>
        /// 强制进入终态（死亡/停止）。只用于“事实已确定”的场合，例如真实扣血后死亡。
        /// 死亡是终态：已死后再调用不会把 Boss 切回别的状态。
        /// </summary>
        public void Force(BossAIState state, string reason)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BossAI));
            }
            if (state != BossAIState.Dead && state != BossAIState.Stopped)
            {
                throw new InvalidOperationException("外部只能把Boss强制切到 Dead/Stopped：" + state);
            }
            if (State != BossAIState.Dead)
            {
                _machine.ChangeState(state, reason);
            }
        }

        /// <summary>
        /// 测试/GM 专用：让 Boss 立刻进入指定技能（仍会检查技能是否有合法目标）。
        /// 返回 false = 当前局面这一招打不到人，状态保持不变。正式战斗由 Idle 自己挑技能，不走这里。
        /// </summary>
        public bool TryCast(BossSkillKind kind)
        {
            if (_disposed || State == BossAIState.Dead || _owner == null || !BossSkillCatalog.IsUnlocked(kind, _owner.Chapter))
            {
                return false;
            }
            BossSkillPlan plan;
            if (!BossTactics.TryPlan(_owner.Board, _owner.Boss, kind, _owner.Intelligence, out plan))
            {
                return false;
            }
            _owner.PendingPlan = plan;
            return _machine.ChangeState(StateOf(kind), "测试/GM 指定技能");
        }

        /// <summary>释放状态机与宿主引用；可重复调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _machine.Dispose();
            _owner = null;
            _disposed = true;
        }
    }
}

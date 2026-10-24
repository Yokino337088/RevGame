// ============================================================
// EnemyStates.cs —— 敌方AI的“业务本体”：每个状态自己感知、切换并执行。
//
// 【和另外两个文件的分工】
// · EnemyAI.cs      ：建机器、注册状态、把时间喂给框架、释放。不写任何 AI 决策。
// · 本文件          ：状态负责“现在该攻击/推进/等待/布阵吗”，并调用算法与服务接口执行。
// · EnemyTactics.cs ：纯评分算法，回答“打谁 / 走哪格 / 补哪里”，没有状态、没有计时。
// · BattleService   ：提供棋盘与原子动作（扣血、占格、发事件），不判断 AI 该做什么。
//
// 【一颗棋子在一个模拟步里的顺序】
//   服务驱动 Tick(seconds)
//     → 当前状态的 OnUpdate（本文件 EnemyState.OnUpdate）
//        1) 感知：死亡？非战斗？零气？有射界？移动冷却到？
//        2) 切换：需要换状态就 ChangeTo（框架允许在 OnUpdate 中同步切换）
//        3) 执行：只执行“切换后当前状态”的 Act 一次
//     → 服务继续下一颗棋
// 这个顺序保证接敌当步就能出手，不用多等一个 0.3125 秒的小步。
//
// 【为什么切换后要手动执行 Act，而不是等下一帧】
// 框架的 ChangeState 只负责 OnExit/OnEnter，**不会**自动调用新状态的 Update。
// 如果这里不执行，AI 每次换状态都会白等一步，节奏和旧实现不一致。
//
// 【为什么不在 OnEnter/OnExit 里切状态】
// 框架明确规定交割期间不能对同一台机器再次 ChangeState（DEBUG 会抛异常）。
// 业务必须放在 OnUpdate 里，切换路径才唯一、可回放。
//
// 【用到的框架概念】
// RevHeavyFSMState（状态基类）、ChangeTo（状态内的安全切换）、StateTime（状态时长）、
// RevIHeavyFSMOwner（宿主标记）、RevEvent/RevLog 由宿主实现代为调用。
// ============================================================
using System;
using Revolution;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using System.Collections.Generic;

namespace RevGame.Battle
{
    /// <summary>
    /// 状态能看到的“宿主能力”窄接口。
    ///
    /// 为什么不是直接传 BattleService：
    /// · AI 目录不反向依赖 Services 目录，离线测试可以塞一个假宿主；
    /// · 状态只能通过这里列出的动作改世界，不能顺手改血量、改回合或碰 UI；
    /// · 服务实现这些动作时仍走统一出口（伤害公式、占格预检、事件广播），规则只有一份。
    /// </summary>
    public interface IEnemyAIOwner : RevIHeavyFSMOwner
    {
        /// <summary>当前棋盘，只读用于查询；落子/移动必须走下面的提交方法。</summary>
        BoardModel Board { get; }
        /// <summary>这台机器负责的棋子；指挥机器为 null。</summary>
        Piece Actor { get; }
        /// <summary>当前阶段；非战斗时棋子应停止行动。</summary>
        GamePhase Phase { get; }
        /// <summary>战斗累计模拟秒数，移动冷却与增援冷却都以它为准。</summary>
        float Elapsed { get; }
        /// <summary>AI 档位 1~4，只影响评分因素，不改变固定时钟。</summary>
        int Intelligence { get; }
        /// <summary>当前章节与回合，用于读取本回合的成长倍率和增援兵种。</summary>
        int Chapter { get; }
        int Round { get; }
        /// <summary>主将是否已被擒；已擒时立即停止一切攻击/移动。</summary>
        bool KingCaptured { get; }
        /// <summary>下一次允许尝试增援的时刻；由 Deploy 状态推进，服务只保存。</summary>
        float NextDeploymentAt { get; set; }
        /// <summary>本回合已成功增援次数；同时作为兵种轮换下标与预算判断。</summary>
        int DeploymentCount { get; }
        /// <summary>已通过预检、等待提交的完整敌军名单；Prepare 状态据此落子。</summary>
        IReadOnlyList<Piece> PlannedEnemies { get; }
        /// <summary>提交一颗已规划好的敌棋；服务负责真实占格并创建它的棋子机器。</summary>
        void CommitFormationPiece(Piece piece);
        /// <summary>提交一次增援：服务负责占格、编号、事件和提示，状态不直接改名单。</summary>
        void CommitDeployment(Piece piece, BoardPos position, string intention);
        /// <summary>走服务统一伤害出口（含锁死倍伤、护盾、胜负），状态不能自己扣血。</summary>
        void Attack(Piece attacker, Piece target);
        /// <summary>走棋盘真实移动并刷新气眼；失败由宿主抛出或忽略，不留下半移动状态。</summary>
        void Move(Piece piece, BoardPos position);
        /// <summary>按本回合成长倍率创建一颗待出生敌棋；只在真正提交前调用。</summary>
        Piece CreateDeployment(PieceDef definition, RoundDef round);
    }

    /// <summary>
    /// 所有棋子状态的共同基类：集中“感知 + 切换 + 执行”三步，子类只实现 Act。
    ///
    /// 为什么把感知放基类：五个棋子状态的判断条件完全相同，各写一份迟早分叉；
    /// 子类只关心“轮到我时做什么”，读起来就是一张状态表。
    ///
    /// Targets 是每个状态自带的复用缓冲：固定小步每 0.3125 秒查询一次，
    /// 每次 new List 会产生持续 GC；缓冲区只在本状态内使用，不会跨状态串数据。
    /// </summary>
    internal abstract class EnemyState : RevHeavyFSMState<EnemyAIState, IEnemyAIOwner>
    {
        /// <summary>本状态复用的候选坐标缓冲（攻击目标或移动落点），容量按整盘 81 格预留。</summary>
        protected readonly List<BoardPos> Targets = new List<BoardPos>(81);
        protected EnemyState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }

        /// <summary>
        /// 感知并执行。顺序不能调换：先判死亡/阶段/锁死，再看射界与移动冷却，
        /// 最后切换并执行一次。若先执行旧状态再感知，零气棋会多打一步才停手。
        /// </summary>
        public override void OnUpdate(float seconds)
        {
            EnemyAIState next;
            string reason;
            Piece actor = Owner.Actor;
            // 1) 死亡优先于一切：真实扣血已经移除棋子，状态不能再攻击或移动。
            if (!actor.IsAlive)
            {
                next = EnemyAIState.Dead; reason = "真实死亡";
            }
            // 2) 离开战斗（结算/返回大厅）后停止，避免在非战斗阶段继续改棋盘。
            else if (Owner.Phase != GamePhase.Battle)
            {
                next = EnemyAIState.Stopped; reason = "非战斗阶段";
            }
            // 3) 零气锁死：本游戏规则下敌方锁死不能出手，也不能积攒攻击额度。
            else if (!MoveRules.CanAttack(actor))
            {
                next = EnemyAIState.Locked; reason = "零气锁死";
            }
            else
            {
                // 4) 有合法目标就接敌；没有则等独立移动冷却，冷却到了才推进。
                //    查询使用服务提供的复用缓冲，返回的坐标只在本小步有效。
                MoveRules.GetAttackTargets(Owner.Board, actor, Targets);
                if (Targets.Count > 0)
                {
                    next = EnemyAIState.Attack; reason = "发现合法攻击目标";
                }
                else if (Owner.Elapsed >= Math.Max(Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks, actor.NextMoveAt))
                {
                    next = EnemyAIState.Advance; reason = "没有射界且移动冷却完成";
                }
                else
                {
                    next = EnemyAIState.Hold; reason = "等待移动冷却";
                }
            }
            // 5) 只在目标不同的时候切换，避免同状态反复 OnExit/OnEnter 重置 StateTime。
            //    框架允许在 OnUpdate 中同步切换；禁止的是在 OnEnter/OnExit 里再切。
            if (next != StateType)
            {
                ChangeTo(next, reason);
            }
            // 6) 框架切换不会自动执行新状态，所以这里补执行一次，且不再递归感知。
            ((EnemyState)Machine.CurrentState).Act(seconds);
        }

        /// <summary>当前状态真正做的事；等待/锁死/死亡不需要覆盖（默认什么也不做）。</summary>
        protected virtual void Act(float seconds) { }

        /// <summary>
        /// 按标准行动周期积攒攻击进度，返回“本步可以出手几次”。
        ///
        /// 细节：扣掉整数次数、保留小数余量，所以 4 个小步正好攒满 1 次，
        /// 而不是每步四舍五入成 1 次（那会让低频攻击变成高频）。
        /// 指数只乘 AttackSpeedScale，不受玩家“万箭齐发”影响——那张牌只服务我方弓箭手。
        /// </summary>
        protected int ConsumeActions(float seconds)
        {
            Piece actor = Owner.Actor;
            // 敌方专属倍率只作用于普攻额度，不乘模拟时间，避免连带加快技能、补兵与区域伤害。
            actor.ActionProgress += seconds / Balance.BattleTickSeconds * Balance.AttackSpeedScale * Balance.EnemyAttackRate;
            int actions = (int)actor.ActionProgress;
            actor.ActionProgress -= actions;
            return actions;
        }
    }

    /// <summary>
    /// 接敌状态：每个模拟步先攒攻击进度，攒满几次就出手几次。
    /// 每一发都重新查询目标：上一发可能已经击杀，不能继续打一个已移除的棋子对象。
    /// 选谁打交给 EnemyTactics（远程/残血/断连），本状态只负责“什么时候打、打几发”。
    /// </summary>
    internal sealed class EnemyAttackState : EnemyState
    {
        public EnemyAttackState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Attack;
        protected override void Act(float seconds)
        {
            int actions = ConsumeActions(seconds);
            for (int i = 0; i < actions; i++)
            {
                MoveRules.GetAttackTargets(Owner.Board, Owner.Actor, Targets);
                if (Targets.Count == 0)
                {
                    break;
                }
                Piece target = EnemyTactics.ChooseTarget(Owner.Board, Owner.Actor, Targets, Owner.Intelligence);
                Owner.Attack(Owner.Actor, target);
                // 擒王后整场立即结束，不再处理本步剩余连击。
                if (Owner.KingCaptured)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 推进状态：没有射界且移动冷却已到时，安全靠近一次。
    ///
    /// 三个容易踩坑的点：
    /// 1) 先在进入时就把 NextMoveAt 推到下一次冷却，**失败也算**——否则被堵住时每个小步都会全盘搜索；
    /// 2) 无眼近战追远程时按Excel限制连续直线冲锋，逐格检查占用；其它普通走法仍接近一格，马步保留原跳法；
    /// 3) 具体落点由 EnemyTactics 在深快照里试走，本状态不自己判断气眼，避免和规则算法分叉。
    /// </summary>
    internal sealed class EnemyAdvanceState : EnemyState
    {
        public EnemyAdvanceState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Advance;
        protected override void Act(float seconds)
        {
            // 推进时也要消费攻击进度：规则是“没有射界就攒不出整次出手”，不是暂停计时。
            ConsumeActions(seconds);
            Piece actor = Owner.Actor;
            actor.NextMoveAt = Owner.Elapsed + Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks;
            // 状态决定何时推进、失败是否消费冷却；通用合法候选与距离过滤交给算法。
            EnemyTactics.GetAdvanceCandidates(Owner.Board, actor, Targets);
            if (Targets.Count == 0)
            {
                return;
            }
            BoardPos best = EnemyTactics.ChooseSafeMove(Owner.Board, actor, Targets, Owner.Intelligence);
            if (best != actor.Pos)
            {
                Owner.Move(actor, best);
            }
        }
    }

    /// <summary>
    /// 等待状态：没有射界，且移动冷却还没到。
    ///
    /// 为什么还要消费攻击进度：旧规则里“等待接敌”期间攻击条继续走整数部分，
    /// 只是没有目标所以打不出去。若这里完全不攒，接敌瞬间会凭空少一次出手，
    /// 让“敌人靠近后立刻开火”的节奏变慢；若保留整数不消费，又会积攒成一次爆发。
    /// </summary>
    internal sealed class EnemyHoldState : EnemyState
    {
        public EnemyHoldState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Hold;
        protected override void Act(float seconds) { ConsumeActions(seconds); }
    }

    /// <summary>
    /// 锁死状态：零气，按规则不能出手。
    /// 与 Hold 的区别是不积攒攻击额度，避免一解围就连打多下；
    /// 每个小步仍走一次感知，解围后会自己切回 Attack/Advance，不需要服务额外通知。
    /// </summary>
    internal sealed class EnemyLockedState : EnemyState
    {
        public EnemyLockedState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Locked;
    }

    /// <summary>
    /// 死亡终态：不再感知、不执行、不会被“下一次 Tick”复活。
    /// 逻辑死亡由真实扣血触发，与表现层的溶解动画无关——动画没播完也不影响 AI 停手。
    /// 覆盖 OnUpdate 为空是故意的：基类的感知会让死棋重新切到别的状态。
    /// </summary>
    internal sealed class EnemyDeadState : EnemyState
    {
        public EnemyDeadState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Dead;
        public override void OnUpdate(float seconds) { }
    }

    /// <summary>
    /// 停止状态：
    /// · 棋子：战斗结束/返回大厅后停手，不再改棋盘；
    /// · 指挥：停止增援（达到预算、超过停援时刻、任一方全灭、擒王）。
    /// 指挥恢复时由服务显式切回 Deploy，本状态不擅自恢复，避免“停了又偷偷补兵”。
    /// </summary>
    internal sealed class EnemyStoppedState : EnemyState
    {
        public EnemyStoppedState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Stopped;
        public override void OnUpdate(float seconds)
        {
            // 棋子仍允许感知（若又被切回 Stopped 之外的状态由基类负责）；
            // 指挥没有 Actor，基类感知会空引用，因此只停着。
            if (Owner.Actor != null)
            {
                base.OnUpdate(seconds);
            }
        }
    }

    /// <summary>
    /// 布阵状态：把“已经通过纯算法预检”的名单逐颗提交到棋盘。
    ///
    /// 为什么不在本状态里做规划：规划需要整盘搜索和降级策略（EnemyFormationPlanner），
    /// 与“提交”是两件事；预检失败时棋盘必须保持原样，所以先算后交，状态只负责后者。
    /// 提交时服务会为每颗新棋创建它自己的棋子状态机。
    /// </summary>
    internal sealed class EnemyPrepareState : EnemyState
    {
        public EnemyPrepareState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Prepare;
        public override void OnUpdate(float seconds)
        {
            // 没有待提交名单说明不是布阵时机（例如重复 Tick），直接返回，不能凭空生成敌棋。
            if (Owner.PlannedEnemies == null)
            {
                return;
            }
            foreach (Piece piece in Owner.PlannedEnemies) { Owner.CommitFormationPiece(piece); }
        }
    }

    /// <summary>
    /// 增援状态（指挥）：按“停援条件 → 冷却 → 在场上限 → 空格 → 兵种 → 快照选点 → 原子提交”执行。
    ///
    /// 新手注意：
    /// · 这个状态只决定“要不要再补一颗、补哪种”，落点由 EnemyTactics 的围棋评分决定；
    /// · 冷却先推进再试算，失败也消耗窗口，避免满盘时每 0.3125 秒重算 81 格；
    /// · 新棋不在这里直接攻击/移动，只设置完整移动冷却，给玩家反应窗口；
    /// · 所有阈值读 Excel（Deploy.*），本文件不写死秒数或数量。
    /// </summary>
    internal sealed class EnemyDeployState : EnemyState
    {
        public EnemyDeployState(RevHeavyFSM<EnemyAIState, IEnemyAIOwner> machine) : base(machine) { }
        public override EnemyAIState StateType => EnemyAIState.Deploy;
        public override void OnUpdate(float seconds)
        {
            // 1) 先判停援：任意一条成立就永久停在本状态，不再生成敌棋。
            if (Owner.Phase != GamePhase.Battle || Owner.Elapsed >= Balance.DeployStopSeconds
                || Owner.DeploymentCount >= Balance.DeployBaseCount + Owner.Intelligence || Owner.KingCaptured
                || Owner.Board.IsSideWiped(GameSide.Enemy) || Owner.Board.IsSideWiped(GameSide.Player))
            {
                ChangeTo(EnemyAIState.Stopped, "停援/战斗结束");
                return;
            }
            // 2) 冷却未到直接返回；冷却值由配置和 AI 档位共同决定。
            if (Owner.Elapsed < Owner.NextDeploymentAt)
            {
                return;
            }
            Owner.NextDeploymentAt = Owner.Elapsed + Math.Max(Balance.DeployIntervalMinSeconds,
                Balance.DeployIntervalBaseSeconds - Owner.Intelligence * Balance.DeployIntervalPerLevelSeconds);
            // 同一窗口最多两子；每颗都提交后再查第二颗，预算/空位/气眼不能沿用第一次的旧值。
            for (int batch = 0; batch < Balance.DeployBatchCount; batch++)
            {
                if (Owner.DeploymentCount >= Balance.DeployBaseCount + Owner.Intelligence || Owner.KingCaptured
                    || Owner.Board.IsSideWiped(GameSide.Player) || Owner.Board.IsSideWiped(GameSide.Enemy)) { break; }
                // 3) 在场上限与空格下限：留空间给玩家调阵和援军，不把棋盘塞满。
                if (Owner.Board.GetPiecesOf(GameSide.Enemy).Count >= Balance.DeployLivingCap)
                {
                    return;
                }
                if (EnemyTactics.CountEmptyCells(Owner.Board) <= Balance.DeployMinEmptyCells)
                {
                    return;
                }
                // 4) 兵种来自 EnemyReinforceConfig：按已补次数与 AI 档位交替，缺规则时明确报错而不是瞎猜。
                RoundDef round = LevelCatalog.GetRound(Owner.Chapter, Owner.Round);
                if (round == null)
                {
                    throw new InvalidOperationException("敌方落子缺少当前回合配置");
                }
                PieceKind? kind = LevelCatalog.PickReinforcement(Owner.DeploymentCount, Owner.Intelligence);
                if (!kind.HasValue)
                {
                    RevLog.Warn("敌方补兵规则没有命中，请检查EnemyReinforceConfig", "RevGame"); return;
                }
                PieceDef def = PieceCatalog.Get(kind.Value, LevelCatalog.GetEnemyStar(Owner.Chapter, false));
                Piece newcomer = Owner.CreateDeployment(def, round);
                BoardPos position;
                string intention;
                // 5) 选点：深快照里试落，不污染实战气眼；无安全点就放弃本次窗口（冷却已消耗）。
                if (!EnemyTactics.TryChooseDeployment(Owner.Board, newcomer, Owner.Intelligence, out position, out intention))
                {
                    return;
                }
                // 6) 新棋落地不立即攻击/移动，先攒满一个完整移动冷却，玩家有机会反应。
                newcomer.NextMoveAt = Owner.Elapsed + Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks;
                Owner.CommitDeployment(newcomer, position, intention);
            }
        }
    }
}

// ============================================================
// BossStates.cs —— Boss 每个状态的真实业务：普通攻击、挑技能、蓄力、命中、收招。
//
// 【和另外几个文件的分工】
// · BossAI.cs      ：注册状态、喂时间、释放，不写决策；
// · 本文件         ：每个状态“现在该做什么”；
// · BossTactics.cs ：纯算法，回答“放哪招、打哪里”；
// · 服务宿主       ：扣血、瞬移、回血、召唤、广播（统一伤害公式与事件）。
//
// 【所有技能共用的三段节奏】（BossSkillState 基类实现，子类只写“命中时做什么”）
//
//   进入技能状态 ──► 蓄力 Windup 秒 ──► 第 1 段命中 ──► 间隔 Interval ──► 第 2 段… ──► 收招 ──► 回到 Idle
//       │                                   │
//       └─ 广播 BossSkillTelegraph          └─ 每段先结算伤害，再广播 BossSkillCast
//          （画危险区域，玩家可以躲）           （表现层只播特效，不再扣血）
//
// 为什么要有蓄力：Boss 技能伤害很高，没有预警就只是“数值惩罚”。
// 预警期间玩家可以把棋子移出危险区，命中时按“那一刻谁在范围里”结算——躲开就是真的躲开了。
//
// 【难度如何体现在状态里】
// 第一章 Boss 只有横扫和狙击；章节越高解锁的技能越多（见 Excel 的 UnlockChapter）；
// 第四章起残血会进入狂暴（BossEnrageState），之后冷却进一步缩短；第六章所有状态都会出现。
//
// 【为什么不在 OnEnter 里切状态】
// 框架规定 OnEnter/OnExit 期间不能再对同一台机器 ChangeState（DEBUG 会抛异常）。
// 所以计划无效时只记一个标记，等第一次 OnUpdate 再安全地切回 Idle。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Battle
{
    /// <summary>所有 Boss 状态的基类：提供“终止条件检查”这一项所有状态都需要的感知。</summary>
    internal abstract class BossState : RevHeavyFSMState<BossAIState, IBossAIOwner>
    {
        protected BossState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }

        /// <summary>
        /// 检查是否必须停手。返回 true 时调用方应立刻 return，不再执行任何动作。
        /// 顺序：死亡（终态）→ 离开战斗 → 已被擒（战斗马上结束，不能再多打一下）。
        /// </summary>
        protected bool StopIfOver()
        {
            if (!Owner.Boss.IsAlive)
            {
                ChangeTo(BossAIState.Dead, "Boss 真实死亡");
                return true;
            }
            if (Owner.Phase != GamePhase.Battle)
            {
                ChangeTo(BossAIState.Stopped, "离开战斗阶段");
                return true;
            }
            return Owner.KingCaptured;
        }
    }

    /// <summary>
    /// 待机状态：Boss 的“大脑”。每个模拟步依次判断：
    /// 1) 是否该狂暴（只触发一次）；2) 公共间隔到了没、有没有值得放的技能；
    /// 3) 都没有就像普通敌棋一样攻击射程内的目标，没有目标就慢慢靠近。
    /// </summary>
    internal sealed class BossIdleState : BossState
    {
        /// <summary>复用的候选坐标缓冲（攻击目标或移动落点），避免每步 new List 产生 GC。</summary>
        private readonly List<BoardPos> _targets = new List<BoardPos>(81);

        public BossIdleState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Idle;

        public override void OnUpdate(float seconds)
        {
            if (StopIfOver())
            {
                return;
            }
            Piece boss = Owner.Boss;
            // ① 狂暴优先：血线一到立刻怒吼，不等公共间隔——这是第四章起 Boss 的“二阶段”信号。
            if (!Owner.Enraged && BossSkillCatalog.IsUnlocked(BossSkillKind.Enrage, Owner.Chapter) && boss.HpRatio <= Balance.BossEnrageHpRatio)
            {
                BossSkillPlan enrage;
                BossTactics.TryPlan(Owner.Board, boss, BossSkillKind.Enrage, Owner.Intelligence, out enrage);
                Owner.PendingPlan = enrage;
                ChangeTo(BossAIState.Enrage, "生命跌破狂暴阈值");
                return;
            }
            // ② 公共间隔到了才挑技能：BossTactics 会过滤掉未解锁、冷却中、打不到人的技能。
            BossSkillPlan plan;
            if (Owner.Elapsed >= Owner.NextSkillAt
                && BossTactics.TryChoose(Owner.Board, boss, Owner.Chapter, Owner.Intelligence, Owner.SkillReadyAt, Owner.Elapsed, Owner.LastSkill, out plan))
            {
                Owner.PendingPlan = plan;
                ChangeTo(BossAI.StateOf(plan.Kind), "选择技能 " + plan.Kind + "（评分 " + plan.Score.ToString("0.#") + "）");
                return;
            }
            // ③ 没有技能可放：普通攻击/靠近。只有待机时才积攒攻击进度——放技能期间不会“偷偷攒普攻”。
            NormalAction(seconds);
        }

        /// <summary>与普通敌棋相同的攻击/推进规则：射程内有人就打，没有就等移动冷却后靠近一格。</summary>
        private void NormalAction(float seconds)
        {
            Piece boss = Owner.Boss;
            boss.ActionProgress += seconds / Balance.BattleTickSeconds * Balance.AttackSpeedScale * Balance.EnemyAttackRate;
            int actions = boss.ConsumeCompletedActions();

            MoveRules.GetAttackTargets(Owner.Board, boss, _targets);
            if (_targets.Count > 0)
            {
                for (int i = 0; i < actions; i++)
                {
                    // 每一发都重新查目标：上一发可能已经击杀，不能打一个已移除的棋子。
                    MoveRules.GetAttackTargets(Owner.Board, boss, _targets);
                    if (_targets.Count == 0)
                    {
                        break;
                    }
                    Owner.Attack(boss, EnemyTactics.ChooseTarget(Owner.Board, boss, _targets, Owner.Intelligence, Owner));
                    if (Owner.KingCaptured)
                    {
                        return;
                    }
                }
                return;
            }
            if (Owner.Elapsed < Math.Max(Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks, boss.NextMoveAt))
            {
                return;
            }
            // 先推进冷却，失败也算一次：被堵住时不会每个小步都全盘搜索。
            boss.NextMoveAt = Owner.Elapsed + Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks;
            EnemyTactics.GetAdvanceCandidates(Owner.Board, boss, _targets);
            if (_targets.Count == 0)
            {
                return;
            }
            BoardPos best = EnemyTactics.ChooseSafeMove(Owner.Board, boss, _targets, Owner.Intelligence);
            if (best != boss.Pos)
            {
                Owner.Move(boss, best);
            }
        }
    }

    /// <summary>
    /// 技能状态基类：实现“蓄力 → 多段命中 → 收招 → 回 Idle”的通用节奏。
    /// 子类只需要说明自己是哪个技能（Kind），以及每一段命中时做什么（Hit）。
    /// </summary>
    internal abstract class BossSkillState : BossState
    {
        /// <summary>本次技能的计划（进入状态时从宿主取出，蓄力期间不变）。</summary>
        protected BossSkillPlan Plan;
        /// <summary>本技能的表格定义（第 1 章基准值）。</summary>
        protected BossSkillDef Def;
        /// <summary>本次施放的事件快照；子类可在命中时更新它（例如瞬移后的落点）。</summary>
        protected BossSkillInfo Info;
        /// <summary>复用的受击名单缓冲。DamagePiece 会改棋盘，所以必须遍历自己的名单而不是棋盘列表。</summary>
        protected readonly List<Piece> Victims = new List<Piece>(16);
        /// <summary>进入本状态后经过的模拟秒数（自己计时，不依赖框架 StateTime 的更新时机）。</summary>
        private float _elapsed;
        /// <summary>已经结算了几段。</summary>
        private int _hits;
        /// <summary>进入时计划无效（直接切进来但打不到人）：第一次 Update 回 Idle。</summary>
        private bool _invalid;

        protected BossSkillState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }

        /// <summary>对应的技能种类。</summary>
        protected abstract BossSkillKind Kind { get; }
        /// <summary>伤害段数：默认等于表里的 Count；Count 另有含义的技能（弹幕/汲取/召唤）改成 1。</summary>
        protected virtual int HitCount => Def.Count;
        /// <summary>本章换算后的伤害倍率。</summary>
        protected float Power => BossSkillCatalog.PowerFor(Kind, Owner.Chapter);

        /// <summary>
        /// 进入技能：取出计划、重置计时、广播预警。
        /// 计划和技能不匹配时（例如测试直接切状态）重新算一次；仍然无效就标记，等 Update 再退出。
        /// </summary>
        public override void OnEnter()
        {
            Def = BossSkillCatalog.Get(Kind);
            Plan = Owner.PendingPlan;
            _elapsed = 0f;
            _hits = 0;
            _invalid = false;
            if (Plan.Kind != Kind && !BossTactics.TryPlan(Owner.Board, Owner.Boss, Kind, Owner.Intelligence, out Plan))
            {
                _invalid = true;
                return;
            }
            OnPlanned();
            Piece boss = Owner.Boss;
            Info = new BossSkillInfo(boss.Id, Kind, boss.Pos, boss.Size, Plan.A, Plan.B, Plan.C, Def.Radius, Def.Windup, 0, Def.Count, Owner.Enraged);
            Owner.Announce(Info, true);
        }

        /// <summary>计划确定后、广播预警前的钩子：子类可以在这里锁定目标（例如汲取锁定残血棋子）。</summary>
        protected virtual void OnPlanned() { }

        public override void OnUpdate(float seconds)
        {
            if (StopIfOver())
            {
                return;
            }
            if (_invalid)
            {
                ChangeTo(BossAIState.Idle, "技能计划无效");
                return;
            }
            _elapsed += seconds;
            int count = HitCount;
            // 用 while 而不是 if：极端情况下一步跨过多段（例如间隔为 0），每段都不会被漏掉。
            while (_hits < count && _elapsed >= Def.Windup + _hits * Def.Interval)
            {
                int index = _hits;
                _hits++;
                Hit(index);
                // 先结算再广播：表现层收到命中事件时，伤害数字与死亡已经发生，二者在画面上同步。
                Owner.Announce(Info.WithIndex(index), false);
                if (Owner.KingCaptured || !Owner.Boss.IsAlive)
                {
                    return;
                }
            }
            if (_hits >= count && _elapsed >= Def.Windup + (count - 1) * Def.Interval + Balance.BossRecoverSeconds)
            {
                Finish();
            }
        }

        /// <summary>第 index 段命中时做什么（结算伤害、瞬移、回血…）。</summary>
        protected abstract void Hit(int index);

        /// <summary>
        /// 收招：记录本技能冷却与公共间隔，然后回 Idle。
        /// 冷却在“结束时”才开始算，而不是开始时：长技能不会一放完就能马上再放。
        /// </summary>
        private void Finish()
        {
            Owner.SkillReadyAt[(int)Kind] = Owner.Elapsed + BossSkillCatalog.CooldownFor(Kind, Owner.Chapter, Owner.Enraged);
            Owner.NextSkillAt = Owner.Elapsed + Balance.BossSkillGapSeconds;
            Owner.LastSkill = Kind;
            ChangeTo(BossAIState.Idle, Kind + " 收招");
        }

        /// <summary>对名单里的每颗棋子造成一次技能伤害。名单是自己的副本，扣血导致死亡移除也不影响遍历。</summary>
        protected void DamageAll(List<Piece> victims)
        {
            foreach (Piece p in victims)
            {
                if (p.IsAlive)
                {
                    Owner.SkillDamage(p, Power);
                }
            }
        }
    }

    /// <summary>横扫震地：命中时打 Boss 身体周围 Radius 格内的所有我方棋子。蓄力期间离开范围就能躲开。</summary>
    internal sealed class BossCleaveState : BossSkillState
    {
        public BossCleaveState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Cleave;
        protected override BossSkillKind Kind => BossSkillKind.Cleave;
        protected override void Hit(int index)
        {
            // 按“命中这一刻”重新收集：预警期间移走的棋子不会被打到。
            Piece boss = Owner.Boss;
            BossTactics.CollectInSquare(Owner.Board, boss.Pos, boss.Size, Def.Radius, Victims);
            DamageAll(Victims);
        }
    }

    /// <summary>
    /// 致命狙击：预警锁定 A 格（威胁最大的那颗棋子所在位置），命中时打“此刻站在 A 格上的棋子”。
    /// 准星锁的是位置不是人：玩家把棋子挪开就能躲掉这一枪——这正是预警存在的意义。
    /// </summary>
    internal sealed class BossSnipeState : BossSkillState
    {
        public BossSnipeState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Snipe;
        protected override BossSkillKind Kind => BossSkillKind.Snipe;
        protected override void Hit(int index)
        {
            Piece target = Owner.Board.GetPiece(Plan.A);
            if (target != null && target.Side == GameSide.Player && target.IsAlive)
            {
                Owner.SkillDamage(target, Power);
            }
        }
    }

    /// <summary>三连轰炸：锁定一块区域，依次在 A、B、C 三个落点爆炸，每发各自结算一次范围伤害。</summary>
    internal sealed class BossBombardState : BossSkillState
    {
        public BossBombardState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Bombard;
        protected override BossSkillKind Kind => BossSkillKind.Bombard;
        protected override void Hit(int index)
        {
            // 表里 Count 可以配成 1~3；超过 3 发时循环使用三个落点。
            BoardPos point = index % 3 == 0 ? Plan.A : (index % 3 == 1 ? Plan.B : Plan.C);
            BossTactics.CollectInSquare(Owner.Board, point, 1, Def.Radius, Victims);
            DamageAll(Victims);
        }
    }

    /// <summary>
    /// 弹幕风暴：以 Boss 身体中心生成一圈子弹同时向全屏散射，没有距离限制。
    /// 表里的 Count 是“子弹数量”（只影响画面密度），
    /// 伤害只结算一次，不会因为子弹多就把同一颗棋打十几次。
    /// </summary>
    internal sealed class BossBulletRingState : BossSkillState
    {
        public BossBulletRingState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.BulletRing;
        protected override BossSkillKind Kind => BossSkillKind.BulletRing;
        protected override int HitCount => 1;
        protected override void Hit(int index)
        {
            // 取快照后逐个结算，避免死亡移除棋子时修改正在遍历的棋盘名单。
            BossTactics.CollectAllPlayers(Owner.Board, Victims);
            DamageAll(Victims);
        }
    }

    /// <summary>
    /// 瞬影爆破：蓄力时在落点 A 显示传送门，命中时整块身体瞬移过去并立刻爆炸。
    /// 落点在蓄力期间被占（玩家把棋子挪进去堵门）时，瞬移失败，Boss 原地爆炸——这是一种“反制”。
    /// </summary>
    internal sealed class BossBlinkBlastState : BossSkillState
    {
        public BossBlinkBlastState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.BlinkBlast;
        protected override BossSkillKind Kind => BossSkillKind.BlinkBlast;
        protected override void Hit(int index)
        {
            Piece boss = Owner.Boss;
            BoardPos from = boss.Pos;
            // 瞬移失败（落点被堵）时 boss.Pos 不变，下面的快照与伤害自然就是“原地爆炸”。
            Owner.Teleport(Plan.A);
            // 更新事件快照：Origin = 出发点，A = 实际爆炸点（瞬移失败时两者相同）。
            Info = new BossSkillInfo(boss.Id, Kind, from, boss.Size, boss.Pos, Plan.B, Plan.C, Def.Radius, Def.Windup, 0, Def.Count, Owner.Enraged);
            BossTactics.CollectInSquare(Owner.Board, boss.Pos, boss.Size, Def.Radius, Victims);
            DamageAll(Victims);
        }
    }

    /// <summary>天光回旋：激光环绕自身旋转，按 Interval 结算多段伤害；每段都重新判定谁还在圈里。</summary>
    internal sealed class BossLaserSpinState : BossSkillState
    {
        public BossLaserSpinState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.LaserSpin;
        protected override BossSkillKind Kind => BossSkillKind.LaserSpin;
        protected override void Hit(int index)
        {
            BossTactics.CollectInCircle(Owner.Board, Owner.Boss, Def.Radius, Victims);
            DamageAll(Victims);
        }
    }

    /// <summary>
    /// 灵魂汲取：蓄力时锁定生命比例最低的 Count 颗棋子（锁人不锁格，躲不掉，所以伤害倍率较低），
    /// 命中时扣血，并把实际造成的伤害全部回到 Boss 身上。应对方法：别让残血棋子拖到 Boss 残血时。
    /// </summary>
    internal sealed class BossSoulDrainState : BossSkillState
    {
        /// <summary>锁定的目标编号（只存编号，蓄力期间目标死亡也不会持有失效对象）。</summary>
        private readonly List<int> _lockedIds = new List<int>(4);

        public BossSoulDrainState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.SoulDrain;
        protected override BossSkillKind Kind => BossSkillKind.SoulDrain;
        protected override int HitCount => 1;

        /// <summary>锁定目标并把前两个目标的位置写进计划，表现层据此画出汲取连线。</summary>
        protected override void OnPlanned()
        {
            _lockedIds.Clear();
            BossTactics.CollectWeakest(Owner.Board, Def.Count, Victims);
            foreach (Piece p in Victims)
            {
                _lockedIds.Add(p.Id);
            }
            Plan.A = Victims.Count > 0 ? Victims[0].Pos : Owner.Boss.Pos;
            Plan.B = Victims.Count > 1 ? Victims[1].Pos : Plan.A;
        }

        protected override void Hit(int index)
        {
            int drained = 0;
            foreach (int id in _lockedIds)
            {
                Piece target = Owner.Board.FindPiece(id);
                if (target != null && target.IsAlive && target.Side == GameSide.Player)
                {
                    drained += Owner.SkillDamage(target, Power);
                }
            }
            if (drained > 0)
            {
                Owner.HealBoss(drained);
            }
        }
    }

    /// <summary>召唤影卫：在 Boss 附近召出 Count 个小兵。小兵走普通敌棋的状态机，与 Boss 互不干扰。</summary>
    internal sealed class BossSummonState : BossSkillState
    {
        public BossSummonState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Summon;
        protected override BossSkillKind Kind => BossSkillKind.Summon;
        protected override int HitCount => 1;
        protected override void Hit(int index)
        {
            Owner.Summon(Def.Count);
        }
    }

    /// <summary>
    /// 狂暴怒吼：每场只触发一次。命中时把 Enraged 置为 true，此后所有技能冷却乘以
    /// Boss.EnrageCooldownScale，特效也换成红色调。怒吼本身不造成伤害。
    /// </summary>
    internal sealed class BossEnrageState : BossSkillState
    {
        public BossEnrageState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Enrage;
        protected override BossSkillKind Kind => BossSkillKind.Enrage;
        protected override int HitCount => 1;
        protected override void Hit(int index)
        {
            Owner.Enraged = true;
            // 快照也跟着变红：命中事件里的 Enraged=true，表现层播放红色冲击波。
            Info = new BossSkillInfo(Info.BossId, Info.Kind, Info.Origin, Info.OriginSize, Info.A, Info.B, Info.C, Info.Radius, Info.Windup, 0, Info.Count, true);
        }
    }

    /// <summary>死亡终态：不感知、不执行，下一次 Tick 也不会把它复活。</summary>
    internal sealed class BossDeadState : BossState
    {
        public BossDeadState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Dead;
        public override void OnUpdate(float seconds) { }
    }

    /// <summary>停止：离开战斗阶段后停手；若重新回到战斗且 Boss 仍存活，则回到待机。</summary>
    internal sealed class BossStoppedState : BossState
    {
        public BossStoppedState(RevHeavyFSM<BossAIState, IBossAIOwner> machine) : base(machine) { }
        public override BossAIState StateType => BossAIState.Stopped;
        public override void OnUpdate(float seconds)
        {
            if (!Owner.Boss.IsAlive)
            {
                ChangeTo(BossAIState.Dead, "Boss 真实死亡");
            }
            else if (Owner.Phase == GamePhase.Battle)
            {
                ChangeTo(BossAIState.Idle, "重新进入战斗");
            }
        }
    }
}

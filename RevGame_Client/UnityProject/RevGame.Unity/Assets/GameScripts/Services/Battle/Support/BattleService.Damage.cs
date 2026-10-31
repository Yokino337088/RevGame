// 唯一伤害管线：请求 → 无副作用计算 → 一次提交。AI预判复用计算，不抽随机数、不消耗盾、不发事件。
using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>棋盘占格版本改变时更新活棋计数。不能每颗棋每次攻击都复制名单或扫描整盘。</summary>
        private int _firepowerTopology = -1, _firepowerAlive;
        private float PlayerFirepowerScale()
        {
            if (_firepowerTopology != _board.TopologyVersion)
            {
                _firepowerTopology = _board.TopologyVersion;
                _firepowerAlive = 0;
                foreach (Piece p in _playerPieces)
                {
                    if (p.IsAlive)
                    {
                        _firepowerAlive++;
                    }
                }
            }
            if (_firepowerAlive <= Balance.PlayerFirepowerSoftCount)
            {
                return 1f;
            }
            // 更多棋子仍提供占格、封气与生命；单位火力收益递减，防六十颗远程同时输出造成秒杀。
            double ratio = Balance.PlayerFirepowerSoftCount / (double)_firepowerAlive;
            return (float)Math.Max(Balance.PlayerFirepowerMinScale, Math.Pow(ratio, Balance.PlayerFirepowerExponent));
        }
        /// <summary>基础来源不同，但最终减伤/护盾/扣血出口相同；百分比技能不能误套普攻加成。</summary>
        private enum DamageBasis
        {
            Fixed, Attack, BossSkill, MaxHp, CurrentHp
        }
        /// <summary>值类型请求只存本次同步结算的上下文；Factor是无量纲倍率，Floor是最少基础伤害，禁止跨帧持有棋子引用。</summary>
        private readonly struct DamageRequest
        {
            public readonly Piece Target, Attacker;
            public readonly DamageBasis Basis;
            public readonly BattleDamageKind Kind;
            public readonly float Factor;
            public readonly int Floor;
            public readonly bool FromSkill;
            public DamageRequest(Piece target, DamageBasis basis, BattleDamageKind kind, Piece attacker = null, float factor = 1f, int floor = 1, bool fromSkill = true)
            {
                Target = target;
                Attacker = attacker;
                Basis = basis;
                Kind = kind;
                Factor = factor;
                Floor = floor;
                FromSkill = fromSkill;
            }
        }
        /// <summary>计算结果不改变任何状态；盾只在提交时扣一次，Damage已包含全部减伤但尚未触发回血/复活。</summary>
        private readonly struct DamageEvaluation
        {
            public readonly int Damage;
            public readonly bool ConsumeShield;
            public DamageEvaluation(int damage, bool consumeShield)
            {
                Damage = damage;
                ConsumeShield = consumeShield;
            }
        }
        /// <summary>唯一公式出口；读取当前服务状态，不写棋盘/棋子，不调用随机源，允许AI在同一模拟步多次查询。</summary>
        /// <summary>
        /// 伤害计算（纯函数）：只算出最终数值，不扣血、不发事件、不抽随机数。
        /// AI 预判会直接复用本方法，所以这里绝不能有任何副作用；真正落地交给 ResolveDamage。
        /// </summary>
        private DamageEvaluation EvaluateDamage(DamageRequest request)
        {
            Piece target = request.Target;
            Piece attacker = request.Attacker;

            // 目标不合法（已死/隐藏/无敌/已被移出棋盘）直接按 0 伤害返回，调用方无需重复判断。
            if (target == null || !target.IsAlive || target.BossHidden || target.BossInvulnerable || _board.FindPiece(target.Id) != target)
            {
                return default;
            }
            int amount;

            // 普攻与 Boss 技能共用"攻击力 × 倍率"这条主线，区别只在下方的加成项。
            if (request.Basis == DamageBasis.Attack || request.Basis == DamageBasis.BossSkill)
            {
                if (attacker == null || !attacker.IsAlive || request.Factor <= 0f)
                {
                    return default;
                }
                // 被"封气锁定"的棋子受到额外伤害，普通情况按基准倍率。
                float multiplier = target.IsLocked ? Balance.LockedDamageMultiplier : Balance.BaseDamageMultiplier;

                if (request.Basis == DamageBasis.Attack)
                {
                    // 孤立目标受额外伤害；带"免孤立"标记的棋子跳过这条。
                    if (!target.IgnoreEncirclement && target.ConnectedCells <= 1)
                    {
                        multiplier *= Balance.IsolatedDamageMultiplier;
                    }
                    // 连击加成：棋子连得越多攻击越高，设上限防止无限叠加。
                    float linked = Math.Min(Balance.MaxLinkAttackBonus, (attacker.ConnectedCells - 1) * Balance.LinkAttackPerCell);
                    // 把连击、职业加成，以及玩家侧的通用/战斗/准备三项加成一次性乘进倍率。
                    multiplier *= 1f + Math.Max(0f, linked) + ClassBonus(attacker.Side)
                        + (attacker.Side == GameSide.Player ? _attackBonus + _combatAttackBonus + PreparationAttackBonus(attacker, target) : 0f);
                    // 玩家火力随存活棋子数递减，防止几十颗远程同时输出直接秒杀；敌方不参与人数递减。
                    // 敌方远程的专属倍率在取整前合入；预测与提交共用此处，技能分支不受影响。
                    float teamScale = attacker.Side == GameSide.Player ? PlayerFirepowerScale() : attacker.EnemyRangedAttackMultiplier;

                    if (HasHelperProtection(attacker))
                    {
                        teamScale *= 1f + (IsBossRound ? Balance.HelperBossHealingAttackBonus : Balance.HelperProtectionAttackBonus);
                    }
                    if (attacker.Side == GameSide.Player && attacker.Def.IsRanged)
                    {
                        // 共鸣只强化远程普攻；不修改图鉴共享数据，不改变敌方或技能伤害。
                        teamScale *= 1f + HelperRangedAttackBonus;
                    }
                    // 普攻最终公式：基础攻击力 × 自身各项倍率 × 上面攒出的 multiplier × 团队火力，最低伤害 1。
                    amount = ClampDamage(attacker.Def.Attack * attacker.AttackScale * attacker.BossAttackFactor * attacker.EyeAttackMultiplier * multiplier * Balance.CombatDamageScale * teamScale * request.Factor, 1);
                }
                else
                {
                    // Boss技能沿用独立技能倍率，不引入普攻的孤立/连击/暴击，避免重构偷偷提高难度。
                    amount = ClampDamage(attacker.Def.Attack * attacker.AttackScale * request.Factor * multiplier * Balance.CombatDamageScale, 1);
                }
                // Boss 战越拖越难：随战斗时长叠一个加速系数，给双方施加时间压力。
                if (IsBossRound || request.Basis == DamageBasis.BossSkill)
                {
                    int acceleration = (int)(_battleElapsed / Balance.BossAccelerationSeconds);
                    amount = ClampDamage(amount + Math.Max(acceleration, amount * (double)acceleration * Balance.BossAccelerationBonus), 1);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                // 开发态压测专用：给自动化攻击统一缩放，不影响正式包。
                if (request.Basis == DamageBasis.Attack)
                {
                    amount = ScaleAutomationAttackDamage(attacker, amount);
                }
#endif
                // 暴击最后再乘，向上取整。
                if (request.Kind == BattleDamageKind.Critical)
                {
                    amount = ClampDamage(Math.Ceiling(amount * (double)Balance.CriticalMultiplier), 1);
                }
            }

            else
            {
                // 百分比与固定伤害：以目标生命为基数，不套上面的普攻加成。
                double basis = request.Basis == DamageBasis.MaxHp ? target.MaxHp : request.Basis == DamageBasis.CurrentHp ? target.Hp : 0;

                // 炸弹/区域/炮火都经过这个出口。仅缩放主将的百分比部分，保留固定伤害下限；
                // 否则单纯增加Boss最大生命也会同比放大支援伤害，厚血仍会被少量支援直接清空。
                if (request.Basis == DamageBasis.MaxHp && IsBossMaster(target))
                {
                    basis *= Balance.BossPercentDamageScale;
                }
                amount = ClampDamage(basis * request.Factor, request.Floor);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (PlayerInvincible && target.Side == GameSide.Player)
            {
                amount = 0;
            }
#endif
            // 玩家护盾优先于一切减伤：只要还有护盾次数，这次伤害直接打盾上，不伤血。
            bool shield = target.Side == GameSide.Player && amount > 0 && target.ShieldCharges > 0;

            if (shield)
            {
                return new DamageEvaluation(0, true);
            }
            // 准备阶段的减伤按比例先砍一刀。
            if (target.Side == GameSide.Player)
            {
                amount = ClampDamage(amount * (1d - _prepDamageReduction), 0);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            amount = ScaleAutomationIncomingDamage(target, amount);

#endif
            // 玩家侧最终防御：战斗防御 × 眼位防御 × 眼防护防御，用除法实现减伤，最低 1。
            if (target.Side == GameSide.Player && amount > 0)
            {
                amount = ClampDamage(amount / ((1d + _combatDefenseBonus) * target.EyeDefenseMultiplier * target.EyeGuardDefenseMultiplier), 1);
            }
            return new DamageEvaluation(amount, false);
        }

        /// <summary>边界值统一用double夹紧，防止int溢出造成负伤害；0伤害请求仍然保持0。</summary>
        private static int ClampDamage(double value, int floor)
        {
            return (int)Math.Max(floor, Math.Min(int.MaxValue, value));
        }

        /// <summary>AI使用非暴击保守预测，不提前消费随机数；能原地复活的眼位棋不会误判为一击永久移除。</summary>
        public bool PredictAttackDefeat(Piece attacker, Piece target)
        {
            float factor = attacker == null || attacker.IsBoss ? 1f : DistinctAttackFactor(attacker, target, true);
            DamageEvaluation result = EvaluateDamage(new DamageRequest(target, DamageBasis.Attack, BattleDamageKind.Normal, attacker, factor, fromSkill: false));
            return target != null && target.IsAlive && result.Damage >= target.Hp
                && !(target.Side == GameSide.Player && target.IsEyeGarrison && !target.EyeReviveUsed);
        }

        /// <summary>开发夹具的固定伤害也必须走同一计算与提交，不允许绕过盾/复活/计分。</summary>
        private int DamagePiece(Piece target, int amount, Piece attacker = null, bool fromSkill = false, BattleDamageKind damageKind = BattleDamageKind.Normal)
        {
            return ResolveDamage(new DamageRequest(target, DamageBasis.Fixed, damageKind, attacker, floor: Math.Max(0, amount), fromSkill: fromSkill));
        }
        
        /// <summary>
        /// 唯一提交出口：合法身份 → 公式 → 消费盾 → 扣血 → 回复/复活 → 计分/事件 → 死亡移除。
        /// 多段伤害逐次结算，不复用过期计算。
        ///
        /// 把"计算"（EvaluateDamage）和"结算"（本方法）拆开、所有扣血都收敛到这一个出口，
        /// 正是王者荣耀等成熟项目的做法：护盾、减伤、复活、吸血这些规则只写这一处，
        /// 任何伤害来源都不会漏掉其中某一环，也不存在绕过扣血直接改血量的旁路。
        /// </summary>
        private int ResolveDamage(DamageRequest request)
        {
            Piece target = request.Target;

            // 重复校验目标身份：调用方可能持有已过期引用，这里以棋盘当前数据为准，不结算已离场的棋子。
            if (target == null || !target.IsAlive || target.BossHidden || target.BossInvulnerable || _board.FindPiece(target.Id) != target)
            {
                return 0;
            }
            DamageEvaluation evaluation = EvaluateDamage(request);
            int amount = evaluation.Damage;
            Piece attacker = request.Attacker;
            bool fromSkill = request.FromSkill;
            BattleDamageKind damageKind = request.Kind;

            // 护盾在扣血前消费：这一下被盾完全挡掉，后面的减伤与命中事件都不再走。
            if (evaluation.ConsumeShield)
            {
                target.ShieldCharges--;
            }
            int real = target.TakeDamage(amount);

            if (real > 0 && target.Side == GameSide.Player && _prepRecoveryRatio > 0f)
            {
                // 复苏储备：首次实际受伤后回复一次，用完清零（比例来自 PrepBuffConfig[Recovery].Extra）。
                float bank = _prepRecoveryRatio;
                _prepRecoveryRatio = 0f;
                HealTeam(bank);
            }
            if (real > 0)
            {
                // 保存攻击者Id供表现公平排队；只传值，不让延迟弹道持有已死亡或换位的Piece对象。
                int sourceId = attacker == null ? 0 : attacker.Id;
                var hit = new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, real, attacker != null, attacker == null ? target.Pos : attacker.Pos, sourceId, fromSkill, attacker == null ? PieceKind.Swordman : attacker.Def.Kind, attacker == null ? 1 : attacker.Size, attacker != null && attacker.Def.IsRanged, damageKind);
                RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, hit);
            }
            // 在死亡广播、移除占格、胜负与积分之前处理复活，视图不会被回池，后续伤害仍可真正击杀它。
            if (!target.IsAlive && target.TryReviveInEye())
            {
                HealRevivedEyeNeighbors(target);
            }
            // 计分与广播在扣血之后统一发生：所有来源都走这里，不会漏计、也不会重复计。
            AwardHitScore(target, attacker, damageKind, real);
            AwardCombatHit(target, attacker, damageKind, real);
            RevEvent.DispatchEvent<int>(Events.DamageResolved, target.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, target.Id);

            // 死亡收尾：先移出棋盘（释放占格）并记录击杀。
            if (!target.IsAlive)
            {
                _board.Remove(target);
                MarkEnemyDead(target);

                // 清理这颗棋子的 AI：先零秒推进一次，让状态机在释放前完成最后一次状态切换；
                // 再 Dispose 释放状态机；最后从字典移除，避免留下指向已死棋子的 AI 引用。
                if (_playerAI.TryGetValue(target.Id, out PlayerAI playerAI))
                {
                    playerAI.Tick(0f);
                    playerAI.Dispose();
                    _playerAI.Remove(target.Id);
                }
                // 玩家可能刚选中这颗棋子准备换位；死亡后清选择，不能留下一个指向已移除棋子的编号。
                if (SelectedPieceId == target.Id)
                {
                    SelectedPieceId = 0;
                    RevEvent.DispatchEvent(Events.SelectionChanged);
                }
                RevEvent.DispatchEvent<int>(Events.PieceDied, target.Id);
                // ★ 先通知死亡，让原棋子转入溶解表现，再刷新棋盘；顺序反过来会把视图提前归池、吞掉死亡动画。
                // 仍在本次DamagePiece返回前撤掉破眼增益，多段技能的后续伤害不会读到旧防御。
                RefreshGoState();
            }
            // 吸血只按实际普攻掉血结算，环境伤害、被盾吸收和重复命中死棋都不能凭空治疗。
            if (request.Basis == DamageBasis.Attack && attacker.Side == GameSide.Player && _prepLifeSteal > 0f && attacker.Heal((int)(real * _prepLifeSteal)) > 0)
            {
                RevEvent.DispatchEvent<int>(Events.PieceHpChanged, attacker.Id);
            }
            return real;
        }
    }
}

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
        private DamageEvaluation EvaluateDamage(DamageRequest request)
        {
            Piece target = request.Target;
            Piece attacker = request.Attacker;

            if (target == null || !target.IsAlive || target.BossHidden || target.BossInvulnerable || _board.FindPiece(target.Id) != target)
            {
                return default;
            }
            int amount;

            if (request.Basis == DamageBasis.Attack || request.Basis == DamageBasis.BossSkill)
            {
                if (attacker == null || !attacker.IsAlive || request.Factor <= 0f)
                {
                    return default;
                }
                float multiplier = target.IsLocked ? Balance.LockedDamageMultiplier : Balance.BaseDamageMultiplier;

                if (request.Basis == DamageBasis.Attack)
                {
                    if (!target.IgnoreEncirclement && target.ConnectedCells <= 1)
                    {
                        multiplier *= Balance.IsolatedDamageMultiplier;
                    }
                    float linked = Math.Min(Balance.MaxLinkAttackBonus, (attacker.ConnectedCells - 1) * Balance.LinkAttackPerCell);
                    multiplier *= 1f + Math.Max(0f, linked) + ClassBonus(attacker.Side)
                        + (attacker.Side == GameSide.Player ? _attackBonus + _combatAttackBonus + PreparationAttackBonus(attacker, target) : 0f);
                    float teamScale = attacker.Side == GameSide.Player ? PlayerFirepowerScale() : 1f;

                    if (HasHelperProtection(attacker))
                    {
                        teamScale *= 1f + (IsBossRound ? Balance.HelperBossHealingAttackBonus : Balance.HelperProtectionAttackBonus);
                    }
                    if (attacker.Side == GameSide.Player && attacker.Def.IsRanged)
                    {
                        // 共鸣只强化远程普攻；不修改图鉴共享数据，不改变敌方或技能伤害。
                        teamScale *= 1f + HelperRangedAttackBonus;
                    }
                    amount = ClampDamage(attacker.Def.Attack * attacker.AttackScale * attacker.BossAttackFactor * attacker.EyeAttackMultiplier * multiplier * Balance.CombatDamageScale * teamScale, 1);
                }
                else
                {
                    // Boss技能沿用独立技能倍率，不引入普攻的孤立/连击/暴击，避免重构偷偷提高难度。
                    amount = ClampDamage(attacker.Def.Attack * attacker.AttackScale * request.Factor * multiplier * Balance.CombatDamageScale, 1);
                }
                if (IsBossRound || request.Basis == DamageBasis.BossSkill)
                {
                    int acceleration = (int)(_battleElapsed / Balance.BossAccelerationSeconds);
                    amount = ClampDamage(amount + Math.Max(acceleration, amount * (double)acceleration * Balance.BossAccelerationBonus), 1);
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (request.Basis == DamageBasis.Attack)
                {
                    amount = ScaleAutomationAttackDamage(attacker, amount);
                }
#endif
                if (request.Kind == BattleDamageKind.Critical)
                {
                    amount = ClampDamage(Math.Ceiling(amount * (double)Balance.CriticalMultiplier), 1);
                }
            }

            else
            {
                double basis = request.Basis == DamageBasis.MaxHp ? target.MaxHp : request.Basis == DamageBasis.CurrentHp ? target.Hp : 0;

                // 炸弹/区域/炮火都经过这个出口。仅缩放主将的百分比部分，保留固定伤害下限；
                // 否则单纯增加Boss最大生命也会同比放大支援伤害，厚血仍会被少量支援直接清空。
                if (request.Basis == DamageBasis.MaxHp && IsBossMaster(target))
                {
                    basis *= Balance.BossPercentDamageScale;
                }
                amount = ClampDamage(basis * request.Factor, request.Floor);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (PlayerInvincible && target.Side == GameSide.Player)
            {
                amount = 0;
            }
#endif
            bool shield = target.Side == GameSide.Player && amount > 0 && target.ShieldCharges > 0;

            if (shield)
            {
                return new DamageEvaluation(0, true);
            }
            if (target.Side == GameSide.Player)
            {
                amount = ClampDamage(amount * (1d - _prepDamageReduction), 0);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            amount = ScaleAutomationIncomingDamage(target, amount);

#endif
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
            DamageEvaluation result = EvaluateDamage(new DamageRequest(target, DamageBasis.Attack, BattleDamageKind.Normal, attacker, fromSkill: false));
            return target != null && target.IsAlive && result.Damage >= target.Hp
                && !(target.Side == GameSide.Player && target.IsEyeGarrison && !target.EyeReviveUsed);
        }
        /// <summary>开发夹具的固定伤害也必须走同一计算与提交，不允许绕过盾/复活/计分。</summary>
        private int DamagePiece(Piece target, int amount, Piece attacker = null, bool fromSkill = false, BattleDamageKind damageKind = BattleDamageKind.Normal)
        {
            return ResolveDamage(new DamageRequest(target, DamageBasis.Fixed, damageKind, attacker, floor: Math.Max(0, amount), fromSkill: fromSkill));
        }
        /// <summary>唯一提交出口：合法身份 → 公式 → 消费盾 → 扣血 → 回复/复活 → 计分/事件 → 死亡移除。多段伤害逐次结算，不复用过期计算。</summary>
        private int ResolveDamage(DamageRequest request)
        {
            Piece target = request.Target;

            if (target == null || !target.IsAlive || target.BossHidden || target.BossInvulnerable || _board.FindPiece(target.Id) != target)
            {
                return 0;
            }
            DamageEvaluation evaluation = EvaluateDamage(request);
            int amount = evaluation.Damage;
            Piece attacker = request.Attacker;
            bool fromSkill = request.FromSkill;
            BattleDamageKind damageKind = request.Kind;

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
            AwardHitScore(target, attacker, damageKind, real);
            AwardCombatHit(target, attacker, damageKind, real);
            RevEvent.DispatchEvent<int>(Events.DamageResolved, target.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, target.Id);

            if (!target.IsAlive)
            {
                _board.Remove(target);
                MarkEnemyDead(target);

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

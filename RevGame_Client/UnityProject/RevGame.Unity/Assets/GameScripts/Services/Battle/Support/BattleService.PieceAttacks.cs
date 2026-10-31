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
        /// <summary>本服务独占的一次出手名单，容量按棋盘上限预留；finally清空引用，退出不会保活旧棋。</summary>
        private readonly List<Piece> _distinctHits = new List<Piece>(81);
        /// <summary>事件可能同步重入服务；禁止内层攻击覆盖外层名单，失败必须可见。</summary>
        private bool _resolvingDistinctAttack;

        /// <summary>
        /// 普通棋子的唯一出手编排：选敌与几何快照→一次暴击抽样→逐实体结算→支援→范围表现。
        /// 伤害仍走ResolveDamage，致死后立即更新气眼；弹道到达只负责画面，不再次结算。
        /// </summary>
        private void ExecuteDistinctAttack(Piece actor, Piece preferred)
        {
            if (!MoveRules.CanAttack(actor) || _board.FindPiece(actor.Id) != actor)
            {
                return;
            }
            if (_resolvingDistinctAttack)
            {
                throw new InvalidOperationException("攻击事件不允许同步重入普通攻击结算");
            }
            Piece primary = PieceAttackRules.SelectPrimary(_board, actor, preferred);
            PieceAttackRules.CollectHits(_board, actor, primary, _distinctHits);
            if (_distinctHits.Count == 0)
            {
                return;
            }
            _resolvingDistinctAttack = true;
            try
            {
                BoardPos center = primary.Pos;
                RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked,
                    new PieceAttackInfo(actor.Id, actor.Def.Kind, actor.Pos, actor.Size, actor.Side));
                bool critical = actor.Side == GameSide.Player && _random.NextDouble() < Balance.CriticalChance;
                BattleDamageKind kind = critical ? BattleDamageKind.Critical : BattleDamageKind.Normal;
                for (int i = 0; i < _distinctHits.Count; i++)
                {
                    Piece target = _distinctHits[i];
                    float factor = DistinctAttackFactor(actor, target, i == 0);
                    // 次要命中不额外生成从攻击者飞来的普通弹头，统一由攻击形状事件绘制范围与连锁。
                    int real = ResolveDamage(new DamageRequest(target, DamageBasis.Attack, kind, actor, factor, fromSkill: i != 0));
                    if (real > 0)
                    {
                        RevEvent.DispatchEvent<string>(Events.BattleLog, FormatAttackLog(actor, target, real));
                        if (actor.Def.Kind == PieceKind.MageWater)
                        {
                            // 每段水流用上一段落点作为起点，不能把连锁画成三发独立直射。
                            BoardPos source = i == 0 ? actor.Pos : _distinctHits[i - 1].Pos;
                            RevEvent.DispatchEvent<PiecePatternInfo>(Events.PiecePattern,
                                new PiecePatternInfo(actor.Id, actor.Def.Kind, source, target.Pos, actor.Side, 0));
                        }
                    }
                }
                ApplyPieceSupport(actor);
                if (actor.Def.Kind != PieceKind.Priest && actor.Def.Kind != PieceKind.MageWater)
                {
                    RevEvent.DispatchEvent<PiecePatternInfo>(Events.PiecePattern,
                        new PiecePatternInfo(actor.Id, actor.Def.Kind, actor.Pos, center, actor.Side, Balance.AttackProfile(actor.Def.Kind).Radius));
                }
                RefreshGoState();
            }
            finally
            {
                _distinctHits.Clear();
                _resolvingDistinctAttack = false;
            }
        }

        /// <summary>预判与真实提交共用倍率，收割读取目标当前血量；不消费随机数、不扣盾、不治疗。</summary>
        private static float DistinctAttackFactor(Piece actor, Piece target, bool primary)
        {
            PieceAttackProfile profile = Balance.AttackProfile(actor.Def.Kind);
            float factor = primary ? profile.PrimaryScale : profile.SecondaryScale;
            if (target != null && profile.ExecuteThreshold > 0f && (double)target.Hp / target.MaxHp <= profile.ExecuteThreshold)
            {
                factor *= profile.ExecuteScale;
            }
            return factor;
        }

        /// <summary>盾战攻击后自疗；牧师在射程内选择损血比例最大的友军，满血不空刷治疗且不复活。</summary>
        private void ApplyPieceSupport(Piece actor)
        {
            PieceAttackProfile profile = Balance.AttackProfile(actor.Def.Kind);
            if (profile.HealScale <= 0f || !actor.IsAlive)
            {
                return;
            }
            Piece target = actor;
            if (actor.Def.Kind == PieceKind.Priest)
            {
                target = null;
                foreach (Piece ally in _board.Pieces)
                {
                    if (ally.Side != actor.Side || !ally.IsAlive || ally.Hp >= ally.MaxHp || PieceAttackRules.Distance(actor, ally) > profile.Range)
                    {
                        continue;
                    }
                    if (target == null || (long)ally.Hp * target.MaxHp < (long)target.Hp * ally.MaxHp
                        || ((long)ally.Hp * target.MaxHp == (long)target.Hp * ally.MaxHp && ally.Id < target.Id))
                    {
                        target = ally;
                    }
                }
            }
            if (target != null)
            {
                int amount = ClampDamage(actor.Def.Attack * actor.AttackScale * profile.HealScale * Balance.CombatDamageScale, 0);
                if (target.Heal(amount) > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, target.Id);
                    if (actor.Def.Kind == PieceKind.Priest)
                    {
                        // 治疗标记只落在真正恢复生命的友军处，不能把攻击落点画成给敌人治疗。
                        RevEvent.DispatchEvent<PiecePatternInfo>(Events.PiecePattern,
                            new PiecePatternInfo(actor.Id, PieceKind.Priest, actor.Pos, target.Pos, actor.Side, 0));
                    }
                }
            }
        }
    }
}

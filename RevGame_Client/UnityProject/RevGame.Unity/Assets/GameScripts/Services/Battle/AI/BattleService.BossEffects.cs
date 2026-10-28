// 专属技能的持续效果属于本局BattleService，用固定模拟时间推进；不让Tween/渲染帧负责伤害或解冻。
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
        /// <summary>短期持续伤害/区域的租约。施法者死亡或离场时由StopEnemyAI统一清除。</summary>
        private sealed class BossEffectLease
        {
            public Piece Source, Target;
            public BossSkillInfo Info;
            public float EndsAt, NextTick;
        }
        private readonly List<BossEffectLease> _bossEffects = new List<BossEffectLease>(32);
        private readonly List<Piece> _exclusiveVictims = new List<Piece>(81);

        /// <summary>状态改变事件只传棋子编号。视图重新读取本局事实，不持有可跨局变更的租约。</summary>
        private static void AnnounceBossStatus(Piece p)
        {
            RevEvent.DispatchEvent<int>(Events.BossStatusChanged, p.Id);
        }

        private void ApplyExclusiveHit(Piece boss, BossSkillInfo info)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            BossExclusiveGeometry.Collect(_board, info, _exclusiveVictims);
            if (info.Kind == BossSkillKind.Sanctuary || info.Kind == BossSkillKind.MireChains)
            {
                _bossEffects.Add(new BossEffectLease { Source = boss, Info = info, EndsAt = _battleElapsed + def.Duration });
            }
            foreach (Piece target in _exclusiveVictims)
            {
                if (info.Kind == BossSkillKind.MireChains && !target.Def.IsRanged) { continue; }
                if (info.Kind == BossSkillKind.BlackHole && target.EyeCount <= 0) { continue; }
                BossSkillDamage(boss, target, BossSkillCatalog.PowerFor(info.Kind, Chapter), info.Kind);
                if (!target.IsAlive) { continue; }
                switch (info.Kind)
                {
                    case BossSkillKind.BlackHole:
                    case BossSkillKind.SwampMaw:
                        // 黑洞保留占格作为返场预约：同一Id/血量/眼阵四秒后原位恢复，期间不能行动或被伤害。
                        // 不把暂时消失当死亡，也不让其它棋子抢格造成永久丢棋或强制删棋。
                        target.BossHidden = true;
                        target.BossHiddenUntil = Math.Max(target.BossHiddenUntil, _battleElapsed + def.Duration);
                        target.ActionProgress = 0f;
                        break;
                    case BossSkillKind.FrozenPrison:
                        target.BossFrozen = true;
                        target.BossFrozenUntil = Math.Max(target.BossFrozenUntil, _battleElapsed + def.Duration);
                        target.ActionProgress = 0f;
                        break;
                    case BossSkillKind.SolarFlare:
                    case BossSkillKind.SunBrand:
                        // 灼烧逐次走统一伤害出口，护盾/减伤/死亡仍有效。间隔复用技能表，不按帧扣血。
                        _bossEffects.Add(new BossEffectLease { Source = boss, Target = target, Info = info,
                            EndsAt = _battleElapsed + def.Duration, NextTick = _battleElapsed + def.Interval });
                        break;
                    case BossSkillKind.TidalSurge:
                    case BossSkillKind.Maelstrom:
                        DisplaceByWater(target, info);
                        break;
                }
                AnnounceBossStatus(target);
            }
            RefreshBossZones();
        }

        /// <summary>水流只提交真实合法空格。逐格推进，不跨过占格、不越界，不为了动画移动实战棋子试算。</summary>
        private void DisplaceByWater(Piece p, BossSkillInfo info)
        {
            int dx = info.Kind == BossSkillKind.TidalSurge ? (info.Index % 2 == 0 ? 1 : -1) : Math.Sign(info.A.Y - p.Pos.Y);
            int dy = info.Kind == BossSkillKind.TidalSurge ? 0 : Math.Sign(p.Pos.X - info.A.X);
            if (dx == 0 && dy == 0) { dx = 1; }
            BoardPos to = new BoardPos(p.Pos.X + dx, p.Pos.Y + dy);
            if (!_board.CanFit(to, p.Size, p.Side, p, respectEnemyEyes: false)) { return; }
            if (!_board.Move(p, to, respectEnemyEyes: false)) { throw new InvalidOperationException("水流位移预检与提交不一致"); }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMoved, p.Id);
        }

        /// <summary>每小步先过期，再行动，保证2秒冻结结束的第一个小步可以攻击，冻结期间不积攒进度。</summary>
        private void TickBossEffects()
        {
            foreach (Piece p in _playerPieces)
            {
                bool changed = false;
                if (p.BossHidden && _battleElapsed >= p.BossHiddenUntil) { p.BossHidden = false; changed = true; }
                if (p.BossFrozen && _battleElapsed >= p.BossFrozenUntil) { p.BossFrozen = false; changed = true; }
                if (changed) { AnnounceBossStatus(p); }
            }
            for (int i = _bossEffects.Count - 1; i >= 0; i--)
            {
                BossEffectLease lease = _bossEffects[i];
                if (!lease.Source.IsAlive || _battleElapsed >= lease.EndsAt || (lease.Target != null && !lease.Target.IsAlive))
                { _bossEffects.RemoveAt(i); continue; }
                if (lease.Target == null || _battleElapsed < lease.NextTick) { continue; }
                BossSkillDef def = BossSkillCatalog.Get(lease.Info.Kind);
                lease.NextTick += def.Interval;
                BossSkillDamage(lease.Source, lease.Target, BossSkillCatalog.PowerFor(lease.Info.Kind, Chapter), lease.Info.Kind);
            }
            RefreshBossZones();
        }

        /// <summary>区域效果按当前坐标重算；多个重叠领域取最强减益，不累乘、不把永久属性改坏。</summary>
        private void RefreshBossZones()
        {
            foreach (Piece p in _playerPieces)
            {
                float attack = 1f, speed = 1f;
                foreach (BossEffectLease lease in _bossEffects)
                {
                    if (lease.Target != null || !lease.Source.IsAlive || !p.IsAlive || p.BossHidden) { continue; }
                    if (lease.Info.Kind == BossSkillKind.MireChains && !p.Def.IsRanged) { continue; }
                    if (!BossExclusiveGeometry.Touches(p, lease.Info)) { continue; }
                    BossSkillDef def = BossSkillCatalog.Get(lease.Info.Kind);
                    attack = Math.Min(attack, def.AttackFactor); speed = Math.Min(speed, def.SpeedFactor);
                }
                bool changed = p.BossAttackFactor != attack || p.BossSpeedFactor != speed;
                p.BossAttackFactor = attack; p.BossSpeedFactor = speed;
                if (changed) { AnnounceBossStatus(p); }
            }
        }

        /// <summary>取消/结算/换局唯一清理：结束租约并恢复所有仍存活棋子，旧回合的效果不能延续下一局。</summary>
        private void ClearBossEffects()
        {
            _bossEffects.Clear();
            foreach (Piece p in _playerPieces)
            {
                p.BossHidden = false; p.BossFrozen = false;
                p.BossHiddenUntil = 0f; p.BossFrozenUntil = 0f;
                p.BossAttackFactor = 1f; p.BossSpeedFactor = 1f;
                AnnounceBossStatus(p);
            }
            foreach (Piece p in _enemyPieces) { p.BossInvulnerable = false; }
        }
    }
}

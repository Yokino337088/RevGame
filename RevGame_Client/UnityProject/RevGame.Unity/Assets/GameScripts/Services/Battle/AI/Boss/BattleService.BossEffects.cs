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
        /// <summary>单个目标的灼烧，固定步逆序结算。值保存在本局列表内，每次命中无需单独分配托管对象。</summary>
        private struct BossBurnLease
        {
            /// <summary>原施法棋子，死亡后停止灼烧。</summary>
            public Piece Source;
            /// <summary>原中招棋子，死亡后丢弃这条记录。</summary>
            public Piece Target;
            /// <summary>原招式，用于读取每跳间隔和伤害。</summary>
            public BossSkillKind Kind;
            /// <summary>灼烧停止的本局模拟时间，单位为秒。</summary>
            public float EndsAt;
            /// <summary>下一次扣血的本局模拟时间，单位为秒。</summary>
            public float NextTick;
        }

        /// <summary>一次地面领域。覆盖只依赖施法时锁定的格子，查询时仍使用棋子当前身体。</summary>
        private readonly struct BossZoneLease
        {
            /// <summary>施法棋子，死亡后整块区域停止。</summary>
            public readonly Piece Source;
            /// <summary>领域结束的本局模拟时间，单位为秒。</summary>
            public readonly float EndsAt;
            /// <summary>命中时使用的攻击和攻速定义，随本次施法保存。</summary>
            public readonly BossSkillDef Definition;
            /// <summary>施法计划供超出位图容量的棋盘走同一原始几何。</summary>
            private readonly BossSkillInfo _info;
            /// <summary>创建领域时的棋盘边长，零表示使用原始几何。</summary>
            private readonly int _boardSize;
            /// <summary>按棋盘行优先编号，前 64 个格子的覆盖位。</summary>
            private readonly ulong _lowerCells;
            /// <summary>第 65 到 128 个格子的覆盖位。正式 9×9 棋盘只用其中 17 位。</summary>
            private readonly ulong _upperCells;

            /// <summary>沿用技能命中使用的几何构建位图。创建一次后，固定步查询不再重算技能形状。</summary>
            public BossZoneLease(Piece source, BossSkillInfo info, BossSkillDef definition, float endsAt, int boardSize)
            {
                Source = source;
                EndsAt = endsAt;
                Definition = definition;
                _info = info;
                _boardSize = (long)boardSize * boardSize <= 128 ? boardSize : 0;
                ulong lowerCells = 0;
                ulong upperCells = 0;

                for (int y = 0; y < _boardSize; y++)
                {
                    for (int x = 0; x < _boardSize; x++)
                    {
                        if (!BossExclusiveGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, info.Index, new BoardPos(x, y)))
                        {
                            continue;
                        }
                        int cell = y * _boardSize + x;
                        if (cell < 64)
                        {
                            lowerCells |= 1UL << cell;
                        }
                        else
                        {
                            upperCells |= 1UL << (cell - 64);
                        }
                    }
                }
                _lowerCells = lowerCells;
                _upperCells = upperCells;
            }

            /// <summary>检查当前棋子身体是否与领域相交。远近程和存活筛选由调用方处理。</summary>
            public bool Touches(Piece piece)
            {
                BoardPos pos = piece.Pos;
                int size = piece.Size;
                // 正式占格保证身体在盘内。非标准盘或诊断构造的越界棋仍沿用原几何。
                if (_boardSize == 0 || pos.X < 0 || pos.Y < 0 || pos.X + size > _boardSize || pos.Y + size > _boardSize)
                {
                    return BossExclusiveGeometry.Touches(piece, _info);
                }

                for (int y = 0; y < size; y++)
                {
                    int firstCell = (pos.Y + y) * _boardSize + pos.X;
                    for (int x = 0; x < size; x++)
                    {
                        int cell = firstCell + x;
                        bool covered = cell < 64
                            ? (_lowerCells & (1UL << cell)) != 0
                            : (_upperCells & (1UL << (cell - 64))) != 0;
                        if (covered)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>本局仍待推进的灼烧。首个灼烧按需扩容，后续施法复用列表容量。</summary>
        private readonly List<BossBurnLease> _bossBurns = new List<BossBurnLease>();
        /// <summary>本局仍可能施加减益的地面区域，按施法顺序保存和刷新。</summary>
        private readonly List<BossZoneLease> _bossZones = new List<BossZoneLease>();
        /// <summary>上一次刷新时有领域，最后一个区域消失后仍需复位一次棋子倍率。</summary>
        private bool _bossZonesWereActive;
        /// <summary>下一个隐藏或冻结到期的模拟秒数，正无穷表示无需扫描玩家。</summary>
        private float _nextBossControlExpiry = float.PositiveInfinity;
        /// <summary>本次即时命中的名单，收集完整后再扣血或位移，下一次调用覆盖。</summary>
        private readonly List<Piece> _exclusiveVictims = new List<Piece>(81);

        /// <summary>状态改变事件只传棋子编号。视图重新读取本局事实，不持有可跨局变更的租约。</summary>
        private static void AnnounceBossStatus(Piece p)
        {
            RevEvent.DispatchEvent<int>(Events.BossStatusChanged, p.Id);
        }
        /// <summary>用锁定的技能计划登记地面区域、结算即时命中及持续状态，最后刷新棋子的区域倍率。</summary>
        private void ApplyExclusiveHit(Piece boss, BossSkillInfo info)
        {
            BossSkillDef def = BossSkillCatalog.Get(info.Kind);
            BossExclusiveGeometry.Collect(_board, info, _exclusiveVictims);

            if (info.Kind == BossSkillKind.Sanctuary || info.Kind == BossSkillKind.MireChains
                || info.Kind == BossSkillKind.PlagueBloom || info.Kind == BossSkillKind.FinalEdict || info.Kind == BossSkillKind.AbyssalCage)
            {
                _bossZones.Add(new BossZoneLease(boss, info, def, _battleElapsed + def.Duration, _board.Size));
            }
            foreach (Piece target in _exclusiveVictims)
            {
                if (info.Kind == BossSkillKind.MireChains && !target.Def.IsRanged)
                {
                    continue;
                }
                if (info.Kind == BossSkillKind.BlackHole && target.EyeCount <= 0)
                {
                    continue;
                }
                BossSkillDamage(boss, target, BossSkillCatalog.PowerFor(info.Kind, Chapter), info.Kind);

                if (!target.IsAlive)
                {
                    continue;
                }
                switch (info.Kind)
                {
                    case BossSkillKind.BlackHole:
                    case BossSkillKind.SwampMaw:
                        // 黑洞保留占格作为返场预约：同一Id/血量/眼阵四秒后原位恢复，期间不能行动或被伤害。
                        // 不把暂时消失当死亡，也不让其它棋子抢格造成永久丢棋或强制删棋。
                        target.BossHidden = true;
                        target.BossHiddenUntil = Math.Max(target.BossHiddenUntil, _battleElapsed + def.Duration);
                        _nextBossControlExpiry = Math.Min(_nextBossControlExpiry, target.BossHiddenUntil);
                        target.ActionProgress = 0f;
                        break;
                    case BossSkillKind.AbsoluteZero:
                    case BossSkillKind.FrozenPrison:
                        target.BossFrozen = true;
                        target.BossFrozenUntil = Math.Max(target.BossFrozenUntil, _battleElapsed + def.Duration);
                        _nextBossControlExpiry = Math.Min(_nextBossControlExpiry, target.BossFrozenUntil);
                        target.ActionProgress = 0f;
                        break;
                    case BossSkillKind.SolarFlare:
                    case BossSkillKind.SunBrand:
                        // 灼烧逐次走统一伤害出口，护盾/减伤/死亡仍有效。间隔复用技能表，不按帧扣血。
                        _bossBurns.Add(new BossBurnLease
                        {
                            Source = boss,
                            Target = target,
                            Kind = info.Kind,
                            EndsAt = _battleElapsed + def.Duration,
                            NextTick = _battleElapsed + def.Interval
                        });
                        break;
                    case BossSkillKind.TidalSurge:
                    case BossSkillKind.Maelstrom:
                        DisplaceByWater(target, info);
                        break;
                }
                AnnounceBossStatus(target);
            }
            // 命中名单已消费，释放引用，避免已退场的棋子留到下一次施法。
            _exclusiveVictims.Clear();
            RefreshBossZones();
        }
        /// <summary>水流只提交真实合法空格。逐格推进，不跨过占格、不越界，不为了动画移动实战棋子试算。</summary>
        private void DisplaceByWater(Piece p, BossSkillInfo info)
        {
            int dx = info.Kind == BossSkillKind.TidalSurge ? (info.Index % 2 == 0 ? 1 : -1) : Math.Sign(info.A.Y - p.Pos.Y);
            int dy = info.Kind == BossSkillKind.TidalSurge ? 0 : Math.Sign(p.Pos.X - info.A.X);

            if (dx == 0 && dy == 0)
            {
                dx = 1;
            }
            BoardPos to = new BoardPos(p.Pos.X + dx, p.Pos.Y + dy);

            if (!_board.CanFit(to, p.Size, p.Side, p, respectEnemyEyes: false))
            {
                return;
            }
            if (!_board.Move(p, to, respectEnemyEyes: false))
            {
                throw new InvalidOperationException("水流位移预检与提交不一致");
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMoved, p.Id);
        }
        /// <summary>每小步先处理控制到期，再结算灼烧、刷新区域。全部完成后才轮到棋子行动。</summary>
        private void TickBossEffects()
        {
            if (_battleElapsed >= _nextBossControlExpiry)
            {
                ExpireBossControls();
            }

            // 逆序移除已过期领域，保留有效领域之间的施法顺序供后续刷新使用。
            for (int i = _bossZones.Count - 1; i >= 0; i--)
            {
                BossZoneLease zone = _bossZones[i];
                if (!zone.Source.IsAlive || _battleElapsed >= zone.EndsAt)
                {
                    _bossZones.RemoveAt(i);
                }
            }

            // 灼烧可能杀死同格棋子，保留原先从后到前的扣血顺序，并在扣血前提交下一跳时间。
            for (int i = _bossBurns.Count - 1; i >= 0; i--)
            {
                BossBurnLease burn = _bossBurns[i];
                if (!burn.Source.IsAlive || _battleElapsed >= burn.EndsAt || !burn.Target.IsAlive)
                {
                    _bossBurns.RemoveAt(i);
                    continue;
                }
                if (_battleElapsed < burn.NextTick)
                {
                    continue;
                }
                burn.NextTick += BossSkillCatalog.Get(burn.Kind).Interval;
                _bossBurns[i] = burn;
                BossSkillDamage(burn.Source, burn.Target, BossSkillCatalog.PowerFor(burn.Kind, Chapter), burn.Kind);
            }
            RefreshBossZones();
        }

        /// <summary>到达最早期限时才按玩家顺序解冻或返场，同时从仍生效的控制中找下一次期限。</summary>
        private void ExpireBossControls()
        {
            _nextBossControlExpiry = float.PositiveInfinity;
            foreach (Piece p in _playerPieces)
            {
                bool changed = false;

                if (p.BossHidden)
                {
                    if (_battleElapsed >= p.BossHiddenUntil)
                    {
                        p.BossHidden = false;
                        changed = true;
                    }
                    else
                    {
                        _nextBossControlExpiry = Math.Min(_nextBossControlExpiry, p.BossHiddenUntil);
                    }
                }
                if (p.BossFrozen)
                {
                    if (_battleElapsed >= p.BossFrozenUntil)
                    {
                        p.BossFrozen = false;
                        changed = true;
                    }
                    else
                    {
                        _nextBossControlExpiry = Math.Min(_nextBossControlExpiry, p.BossFrozenUntil);
                    }
                }
                if (changed)
                {
                    AnnounceBossStatus(p);
                }
            }
        }

        /// <summary>区域效果按当前坐标重算；多个重叠领域取最强减益，不累乘、不把永久属性改坏。</summary>
        private void RefreshBossZones()
        {
            bool zonesActive = _bossZones.Count > 0;
            if (!zonesActive && !_bossZonesWereActive)
            {
                return;
            }
            _bossZonesWereActive = zonesActive;

            // 最后一块领域离场时仍经过此循环，把上一小步写过的倍率恢复为 1。
            foreach (Piece p in _playerPieces)
            {
                float attack = 1f;
                float speed = 1f;
                if (zonesActive && p.IsAlive && !p.BossHidden)
                {
                    for (int i = 0; i < _bossZones.Count; i++)
                    {
                        BossZoneLease zone = _bossZones[i];
                        if (!zone.Source.IsAlive || (zone.Definition.Kind == BossSkillKind.MireChains && !p.Def.IsRanged) || !zone.Touches(p))
                        {
                            continue;
                        }
                        attack = Math.Min(attack, zone.Definition.AttackFactor);
                        speed = Math.Min(speed, zone.Definition.SpeedFactor);
                    }
                }
                if (p.BossAttackFactor != attack || p.BossSpeedFactor != speed)
                {
                    p.BossAttackFactor = attack;
                    p.BossSpeedFactor = speed;
                    AnnounceBossStatus(p);
                }
            }
        }
        /// <summary>取消/结算/换局唯一清理：结束租约并恢复所有仍存活棋子，旧回合的效果不能延续下一局。</summary>
        private void ClearBossEffects()
        {
            // 先撤掉本局租约和最早期限，再广播恢复状态，避免让订阅方看到旧调度。
            _bossBurns.Clear();
            _bossZones.Clear();
            _bossZonesWereActive = false;
            _nextBossControlExpiry = float.PositiveInfinity;
            foreach (Piece p in _playerPieces)
            {
                p.BossHidden = false;
                p.BossFrozen = false;
                p.BossHiddenUntil = 0f;
                p.BossFrozenUntil = 0f;
                p.BossAttackFactor = 1f;
                p.BossSpeedFactor = 1f;
                AnnounceBossStatus(p);
            }
            foreach (Piece p in _enemyPieces)
            {
                p.BossInvulnerable = false;
            }
        }
    }
}

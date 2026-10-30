// 无限时战斗的真实加速：可调增益、仅伤敌的逐格区域波动、全场能量炸弹；伤害按最大生命比例扣除。
using System;
using System.Collections.Generic;
using System.Globalization;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        // 这四个字段属于当前回合，换轮先归零；图鉴保持只读，新援军通过现有属性出口继承。
        private float _combatAttackBonus, _combatDefenseBonus, _combatHasteBonus, _combatVitalityBonus;
        private float _nextRegionAt, _nextBombAt, _regionStartedAt, _bombImpactAt;
        private bool _regionActive, _bombActive;
        private int _regionCell;
        private RegionPulseInfo _region;
        private EnergyBombInfo _bomb;
        // 一次施放只伤害一颗棋一次；2×2/3×3身体覆盖多格也不能重复乘伤害。
        private readonly HashSet<int> _regionHitIds = new HashSet<int>();
        private readonly List<Piece> _pacingVictims = new List<Piece>(81);
        private readonly List<BoardPos> _regionCandidates = new List<BoardPos>(81);
        public int RegionPulseCount
        {
            get; private set;
        }
        public int EnergyBombCount
        {
            get; private set;
        }
        private float _nextEmergencyAt;
        public int EmergencySupportCount
        {
            get; private set;
        }
        /// <summary>仅StartRound调用。所有等待命中的状态在此失效，下一回合不会收到上一轮伤害。</summary>
        private void ResetCombatPacing()
        {
            _combatAttackBonus = _combatDefenseBonus = _combatHasteBonus = _combatVitalityBonus = 0f;
            _nextEyeSupportAt = Balance.EyeSupportSeconds;
            EyeSupportCount = EyeReviveCount = 0;
            _nextRegionAt = Balance.RegionIntervalSeconds;
            _nextBombAt = Balance.BombIntervalSeconds;
            _regionActive = _bombActive = false;
            _regionHitIds.Clear();
            RegionPulseCount = EnergyBombCount = 0;
            EmergencySupportCount = 0;
            _nextEmergencyAt = Balance.EmergencyFirstSeconds;
        }
        /// <summary>正常事件与GM共用。加成按回合累加并封顶，防御+100%表示等效承伤能力翻倍。</summary>
        public bool AddCombatBuff(BattleEventKind kind, float bonus)
        {
            if (Phase != GamePhase.Battle)
            {
                return Reject("战斗属性增益只能在战斗中使用");
            }
            if (float.IsNaN(bonus) || float.IsInfinity(bonus) || bonus <= 0f || bonus > Balance.CombatBuffCap)
            {
                return Reject("加成必须是有限正比例，且不高于配置的本轮上限");
            }
            if (kind != BattleEventKind.Attack && kind != BattleEventKind.Defense && kind != BattleEventKind.Haste && kind != BattleEventKind.Vitality)
            {
                return Reject("仅支持Attack/Defense/Haste/Vitality属性");
            }
            ApplyCombatBuff(kind, bonus);
            RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, BattleEventCatalog.NameOf(kind));
            return true;
        }
        private void ApplyCombatBuff(BattleEventKind kind, float bonus)
        {
            float cap = Balance.CombatBuffCap;

            if (kind == BattleEventKind.Attack)
            {
                _combatAttackBonus = Math.Min(cap, _combatAttackBonus + bonus);
            }
            else

                if (kind == BattleEventKind.Defense)
                {
                    _combatDefenseBonus = Math.Min(cap, _combatDefenseBonus + bonus);
                }
                else

                    if (kind == BattleEventKind.Haste)
                    {
                        _combatHasteBonus = Math.Min(cap, _combatHasteBonus + bonus);
                    }
                    else

                        if (kind == BattleEventKind.Vitality)
                        {
                            _combatVitalityBonus = Math.Min(cap, _combatVitalityBonus + bonus);
                            foreach (Piece piece in _playerPieces)
                            {
                                if (!piece.IsAlive)
                                {
                                    continue;
                                }
                                ApplyPreparationHealth(piece, _prepHealthScale);
                                RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                            }
                        }

            RevEvent.DispatchEvent<string>(Events.BattleLog, BattleEventCatalog.NameOf(kind) + "：本轮属性+" + ConfigText.Percent(bonus));
        }
        /// <summary>只在双方尚有存活棋子时调度。先识别胜负再支援，避免我方已全灭时炸弹反向制造胜利。</summary>
        private void TickCombatPacing()
        {
            if (Phase != GamePhase.Battle || IsKingCaptured() || _board.IsSideWiped(GameSide.Player) || _board.IsSideWiped(GameSide.Enemy))
            {
                return;
            }
            TryEmergencySupport();

            if (!_regionActive && _battleElapsed >= _nextRegionAt)
            {
                TriggerRegionPulse();
            }
            if (_regionActive)
            {
                while (_regionCell < _region.Size * _region.Size && _battleElapsed >= _regionStartedAt + _region.WarningSeconds + _regionCell * _region.CellDelaySeconds)
                {
                    BoardPos pos = new BoardPos(_region.Origin.X + _regionCell % _region.Size, _region.Origin.Y + _regionCell / _region.Size);
                    _regionCell++;
                    Piece target = _board.GetPiece(pos);

                    if (target != null && target.IsAlive && target.Side == GameSide.Enemy && _regionHitIds.Add(target.Id))
                    {
                        ResolveDamage(new DamageRequest(target, DamageBasis.MaxHp, BattleDamageKind.RegionPulse, factor: Balance.RegionDamageHpRatio));
                    }
                }

                if (_regionCell >= _region.Size * _region.Size)
                {
                    _regionActive = false;
                }
                RefreshGoState();
            }
            // 前一段事件可能结束战斗，后续炸弹不能在我方已经全灭时挽回败局。
            if (_board.IsSideWiped(GameSide.Player) || IsKingCaptured())
            {
                return;
            }
            if (!_bombActive && _battleElapsed >= _nextBombAt)
            {
                TriggerEnergyBomb();
            }
            if (_bombActive && _battleElapsed >= _bombImpactAt)
            {
                _bombActive = false;
                _board.CopyPieces(_pacingVictims, GameSide.Enemy);
                foreach (Piece enemy in _pacingVictims)
                {
                    if (enemy.IsAlive)
                    {
                        ResolveDamage(new DamageRequest(enemy, DamageBasis.MaxHp, BattleDamageKind.EnergyBomb, factor: Balance.BombDamageHpRatio));
                    }
                }
                EnergyBombCount++;
                RefreshGoState();
                RevEvent.DispatchEvent<EnergyBombInfo>(Events.EnergyBombImpact, _bomb);
                RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, "能量巨大");
                RevEvent.DispatchEvent<string>(Events.BattleLog, "能量巨大：全场敌军受到最大生命" + ConfigText.Percent(Balance.BombDamageHpRatio) + "的能量冲击");
            }
        }

        /// <summary>失误支援保留真实风险：只治疗仍存活且队伍血线低的玩家，每轮有限次数，不复活、不删敌、不改胜负。</summary>
        private void TryEmergencySupport()
        {
            if (!VictoryAssistEnabled || EmergencySupportCount >= Balance.EmergencyMaxCount || _battleElapsed < _nextEmergencyAt)
            {
                return;
            }
            int hp = 0, maxHp = 0;
            foreach (Piece piece in _playerPieces)
            {
                if (piece.IsAlive)
                {
                    hp += piece.Hp;
                    maxHp += piece.MaxHp;
                }
            }

            if (maxHp <= 0 || hp / (float)maxHp > Balance.EmergencyHpRatio)
            {
                return;
            }
            EmergencySupportCount++;
            _nextEmergencyAt = _battleElapsed + Balance.EmergencyIntervalSeconds;
            HealTeam(Balance.EmergencyHealRatio);
            RevEvent.DispatchEvent<string>(Events.BattleLog, "应急支援：仅为存活队伍回复最大生命" + ConfigText.Percent(Balance.EmergencyHealRatio));
        }
        /// <summary>随机从“覆盖至少一个敌人”的所有合法矩形中抽取，未找到才从全盘抽取；不是永远选同一密集点。</summary>
        public bool TriggerRegionPulse()
        {
            if (Phase != GamePhase.Battle || _regionActive || _board.IsSideWiped(GameSide.Player))
            {
                return Reject("区域波动需要战斗中且没有正在播放的波动");
            }
            int size = Balance.RegionSize;
            _regionCandidates.Clear();
            for (int y = 0; y <= _board.Size - size; y++)
            {
                for (int x = 0; x <= _board.Size - size; x++)
                {
                    bool enemyFound = false;
                    for (int j = 0; j < size && !enemyFound; j++)
                    {
                        for (int i = 0; i < size; i++)
                        {
                            Piece piece = _board.GetPiece(new BoardPos(x + i, y + j));

                            if (piece != null && piece.IsAlive && piece.Side == GameSide.Enemy)
                            {
                                enemyFound = true;
                                break;
                            }
                        }
                    }
                    if (enemyFound)
                    {
                        _regionCandidates.Add(new BoardPos(x, y));
                    }
                }
            }
            int range = _board.Size - size + 1;
            BoardPos origin = _regionCandidates.Count > 0 ? _regionCandidates[_random.Next(_regionCandidates.Count)] : new BoardPos(_random.Next(range), _random.Next(range));
            _region = new RegionPulseInfo(origin, size, Balance.RegionWarningSeconds, Balance.RegionCellDelaySeconds);
            _regionStartedAt = _battleElapsed;
            _regionCell = 0;
            _regionActive = true;
            _regionHitIds.Clear();
            _nextRegionAt = _battleElapsed + Balance.RegionIntervalSeconds;
            RegionPulseCount++;
            RevEvent.DispatchEvent<RegionPulseInfo>(Events.RegionPulseWarning, _region);
            return true;
        }
        /// <summary>自动施放与GM共用同一次落弹，拒绝重叠；伤害比例固定且受启动校验约束，不会随时间变成秒杀。</summary>
        public bool TriggerEnergyBomb()
        {
            if (Phase != GamePhase.Battle || _bombActive || _board.IsSideWiped(GameSide.Enemy) || _board.IsSideWiped(GameSide.Player))
            {
                return Reject("能量炸弹需要双方存活且当前没有待爆炸弹");
            }
            _bomb = new EnergyBombInfo(new BoardPos(_board.Size / 2, _board.Size / 2), Balance.BombWarningSeconds);
            _bombActive = true;
            _bombImpactAt = _battleElapsed + _bomb.WarningSeconds;
            _nextBombAt = _battleElapsed + Balance.BombIntervalSeconds;
            RevEvent.DispatchEvent<EnergyBombInfo>(Events.EnergyBombWarning, _bomb);
            return true;
        }
        /// <summary>机器与人工共用的真实状态，伤害统计来自正式入口，不能从动画数量推测业务是否成功。</summary>
        public string CombatPacingStatus()
        {
            return "attack=" + _combatAttackBonus.ToString("0.###", CultureInfo.InvariantCulture)
                + ";defense=" + _combatDefenseBonus.ToString("0.###", CultureInfo.InvariantCulture)
                + ";haste=" + _combatHasteBonus.ToString("0.###", CultureInfo.InvariantCulture)
                + ";vitality=" + _combatVitalityBonus.ToString("0.###", CultureInfo.InvariantCulture)
                + ";regions=" + RegionPulseCount + ";bombs=" + EnergyBombCount + ";regionActive=" + _regionActive + ";bombActive=" + _bombActive;
        }
    }
}

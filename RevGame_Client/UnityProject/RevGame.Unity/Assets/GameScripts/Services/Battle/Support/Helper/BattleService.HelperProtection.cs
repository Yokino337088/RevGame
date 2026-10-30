using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>
    /// 普通回合最后单棋保护、Boss回合受伤目标轮换。战斗服务是唯一回血/加成来源，射线只是可取消的表现。
    /// 保护不提供无敌或复活：高于当前生命的瞬间伤害仍可击败它，玩家全灭照常判负。
    /// </summary>
    public sealed partial class BattleService
    {
        /// <summary>仅缓存占格版本内的单棋身份；名单变化通过BoardModel更新版本，不逐发遍历整盘。</summary>
        private int _protectionTopology = -1, _protectionCandidateId, _protectionHealedId;
        /// <summary>Boss治疗锁定目标；未满血持续治疗，满血/死亡/隐藏才换人，避免每小步反复跳线。</summary>
        private int _bossHealingTargetId;
        /// <summary>不足一点的理论治疗余数。每步取整后消费，满血不蓄积无限治疗，换目标清零。</summary>
        private double _protectionHealRemainder;
        public long HelperProtectionHealing
        {
            get; private set;
        }
        public int HelperProtectionTargetId
        {
            get
            {
                if (Phase != GamePhase.Battle)
                {
                    return 0;
                }
                if (IsBossRound)
                {
                    return SelectBossHealingTarget();
                }
                if (_protectionTopology != _board.TopologyVersion)
                {
                    _protectionTopology = _board.TopologyVersion;
                    _protectionCandidateId = 0;
                    int count = 0;
                    foreach (Piece player in _playerPieces)
                    {
                        if (!player.IsAlive || _board.FindPiece(player.Id) != player)
                        {
                            continue;
                        }
                        _protectionCandidateId = player.Id;

                        if (++count > 1)
                        {
                            _protectionCandidateId = 0;
                            break;
                        }
                    }
                }
                Piece candidate = _board.FindPiece(_protectionCandidateId);
                return candidate != null && candidate.IsAlive ? candidate.Id : 0;
            }
        }
        /// <summary>
        /// Boss回合自动治疗：先保持有效的受伤目标，再选受伤远程；同类按生命比例、Id稳定排序。
        /// 不按拓扑缓存血量，满血无需占格变化也能换人；隐藏与死亡目标不能被治疗或带走增益。
        /// </summary>
        private int SelectBossHealingTarget()
        {
            Piece current = _board.FindPiece(_bossHealingTargetId);

            if (current != null && current.Side == GameSide.Player && current.IsAlive && !current.BossHidden && current.Hp < current.MaxHp)
            {
                return current.Id;
            }
            Piece best = null;
            foreach (Piece player in _playerPieces)
            {
                if (!player.IsAlive || player.BossHidden || player.Hp >= player.MaxHp || _board.FindPiece(player.Id) != player)
                {
                    continue;
                }
                if (best == null || (player.Def.IsRanged && !best.Def.IsRanged)
                    || (player.Def.IsRanged == best.Def.IsRanged && (player.HpRatio < best.HpRatio
                        || (player.HpRatio == best.HpRatio && player.Id < best.Id))))
                {
                    best = player;
                }
            }
            _bossHealingTargetId = best == null ? 0 : best.Id;
            return _bossHealingTargetId;
        }
        /// <summary>当前治疗加成动态读取：Boss回合换目标撤销旧加成；普通回合补入同伴撤销单棋保护。不会反复叠进原属性。</summary>
        private bool HasHelperProtection(Piece player)
        {
            return player != null && player.Side == GameSide.Player && player.Id == HelperProtectionTargetId;
        }
        /// <summary>固定模拟步按Excel读取每秒比例；Boss回合70%，普通单棋30%。无敌状态不是治疗来源，不消耗帮手积分或停止其攻击。</summary>
        private void TickHelperProtection(float seconds)
        {
            int targetId = HelperProtectionTargetId;

            if (_protectionHealedId != targetId)
            {
                _protectionHealedId = targetId;
                _protectionHealRemainder = 0d;
            }
            if (targetId == 0 || seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                return;
            }
            Piece target = _board.FindPiece(targetId);
            _protectionHealRemainder += target.MaxHp * (double)(IsBossRound ? Balance.HelperBossHealPerSecond : Balance.HelperProtectionHealPerSecond) * seconds;
            int intended = (int)Math.Min(int.MaxValue, Math.Floor(_protectionHealRemainder + 0.000001d));
            _protectionHealRemainder -= intended;

            if (intended <= 0)
            {
                return;
            }
            int actual = target.Heal(intended);

            if (target.Hp == target.MaxHp)
            {
                _protectionHealRemainder = 0d;
            }
            if (actual <= 0)
            {
                return;
            }
            HelperProtectionHealing += actual;
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, targetId);
        }
        /// <summary>结算、换回合、离战斗域统一清理；历史积分与玩家原始血攻不受影响。</summary>
        private void ResetHelperProtection()
        {
            _bossHealingTargetId = 0;
            _protectionTopology = -1;
            _protectionCandidateId = _protectionHealedId = 0;
            _protectionHealRemainder = 0;
        }
    }
}

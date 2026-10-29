using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>
    /// 最后一颗玩家棋的兜底保护。战斗服务是唯一回血/加成来源，射线只是可取消的表现。
    /// 保护不提供无敌或复活：高于当前生命的瞬间伤害仍可击败它，玩家全灭照常判负。
    /// </summary>
    public sealed partial class BattleService
    {
        /// <summary>仅缓存占格版本内的单棋身份；名单变化通过BoardModel更新版本，不逐发遍历整盘。</summary>
        private int _protectionTopology = -1, _protectionCandidateId, _protectionHealedId;
        /// <summary>不足一点的理论治疗余数。每步取整后消费，满血不蓄积无限治疗，换目标清零。</summary>
        private double _protectionHealRemainder;
        public long HelperProtectionHealing { get; private set; }

        public int HelperProtectionTargetId
        {
            get
            {
                if (Phase != GamePhase.Battle) { return 0; }
                if (_protectionTopology != _board.TopologyVersion)
                {
                    _protectionTopology = _board.TopologyVersion;
                    _protectionCandidateId = 0;
                    int count = 0;
                    foreach (Piece player in _playerPieces)
                    {
                        if (!player.IsAlive || _board.FindPiece(player.Id) != player) { continue; }
                        _protectionCandidateId = player.Id;
                        if (++count > 1) { _protectionCandidateId = 0; break; }
                    }
                }
                Piece candidate = _board.FindPiece(_protectionCandidateId);
                return candidate != null && candidate.IsAlive ? candidate.Id : 0;
            }
        }

        /// <summary>单棋加成动态读取，不把50%反复叠进原属性；补入同伴后立即消失。</summary>
        private bool HasHelperProtection(Piece player)
        {
            return player != null && player.Side == GameSide.Player && player.Id == HelperProtectionTargetId;
        }

        /// <summary>固定模拟步按最大生命每秒30%累计。无敌状态不是治疗来源，不消耗帮手积分或停止其攻击。</summary>
        private void TickHelperProtection(float seconds)
        {
            int targetId = HelperProtectionTargetId;
            if (_protectionHealedId != targetId)
            {
                _protectionHealedId = targetId;
                _protectionHealRemainder = 0d;
            }
            if (targetId == 0 || seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds)) { return; }
            Piece target = _board.FindPiece(targetId);
            _protectionHealRemainder += target.MaxHp * (double)Balance.HelperProtectionHealPerSecond * seconds;
            int intended = (int)Math.Min(int.MaxValue, Math.Floor(_protectionHealRemainder + 0.000001d));
            _protectionHealRemainder -= intended;
            if (intended <= 0) { return; }
            int actual = target.Heal(intended);
            if (actual <= 0) { return; }
            HelperProtectionHealing += actual;
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, targetId);
        }

        /// <summary>结算、换回合、离战斗域统一清理；历史积分与玩家原始血攻不受影响。</summary>
        private void ResetHelperProtection()
        {
            _protectionTopology = -1;
            _protectionCandidateId = _protectionHealedId = 0;
            _protectionHealRemainder = 0;
        }
    }
}

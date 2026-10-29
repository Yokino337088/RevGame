#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>
    /// 帮手保护的隔离Play输入。只开放有限受伤和一次正式援军操作，不允许改保护字段、写期望血量或调动画。
    /// 测试必须先经GameFlow建立独立存档；生产构建完全不包含这些方法。
    /// </summary>
    public sealed partial class BattleService
    {
        /// <summary>给最后一颗玩家棋造成不超过最大生命35%的真实固定伤害，供测试随后自然发生的治疗。</summary>
        public int WoundHelperSupportPreview(int targetId, float ratio)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase != GamePhase.Battle
                || targetId != HelperProtectionTargetId || ratio <= 0 || ratio > .35f || float.IsNaN(ratio))
            { throw new InvalidOperationException("受伤夹具只允许隔离战斗中的单棋，比例必须在0～35%之间"); }
            Piece target = _board.FindPiece(targetId);
            if (target == null || !target.IsAlive) { throw new InvalidOperationException("受伤目标已经离场"); }
            // 仍走护盾、减伤、实际伤害事件、HP更新与死亡门禁，不直接调用TakeDamage制造期望结果。
            int requested = Math.Min(target.Hp - 1, (int)Math.Floor(target.MaxHp * (double)ratio));
            return DamagePiece(target, Math.Max(0, requested), fromSkill: true);
        }

        /// <summary>
        /// 调用正式援军的选牌、空位预检、Board.Place、属性继承和手牌消费流程。
        /// 这个夹具仅提前请求一次援军动作，不改自动事件冷却；用于证明真实增员能撤掉单棋保护。
        /// </summary>
        public bool ReinforceHelperSupportPreview()
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase != GamePhase.Battle || HelperProtectionTargetId == 0)
            { throw new InvalidOperationException("援军夹具只允许隔离战斗的单棋保护阶段"); }
            return ReinforceFromHand();
        }
    }
}
#endif

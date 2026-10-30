// ============================================================
// BattleService.HandRefresh.cs —— 只重抽仍在手中的牌，不改已经落下的棋子或落子额度。
// 本文件是 BattleService 的一个 partial 片段，与其他片段共享同一实例；不是第二个服务。
// 动画是 UI 的事：服务先一次性提交新手牌，再通知 UI 翻牌，动画中断也不会丢牌。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>默认两次；正式游戏启动时由 BattleTuning 导表数据覆盖。规则测试可独立使用默认值。</summary>
        public float RangedActionsPerTick { get; private set; } = 2f;

        /// <summary>启动时应用导表频率；限制在固定模拟精度内，错误不覆盖上次有效配置。</summary>
        public void ConfigureRangedAttackRate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 1f || value > 4f)
            {
                throw new System.ArgumentOutOfRangeException(nameof(value), "我方远程每步攻击次数须在1~4之间");
            }
            RangedActionsPerTick = value;
        }
        /// <summary>免费刷新剩余手牌；剩六张刷六张，落掉两张后只刷四张，不会补回已用额度。</summary>
        public bool RefreshHand()
        {
            if (Phase != GamePhase.Prepare)
            {
                return Reject("只有准备阶段可以刷新手牌");
            }
            if (Hand.Count == 0)
            {
                return Reject("手牌已用完，下一回合再刷新");
            }
            var old = new List<Battle.HandCard>(Hand);
            var kinds = new List<PieceKind>();
            foreach (Battle.HandCard card in old)
            {
                // 优先抽成不同棋种，避免玩家付出一次翻牌等待却每个槽位看起来都没变。
                var choices = new List<PieceKind>();
                foreach (PieceKind kind in _unlockedKinds)
                {
                    if (kind != card.Kind)
                    {
                        choices.Add(kind);
                    }
                }
                kinds.Add(choices.Count == 0 ? card.Kind : choices[_random.Next(choices.Count)]);
            }
            _hand.Clear();
            for (int i = 0; i < old.Count; i++)
            {
                _hand.Add(kinds[i], old[i].Star);
            }
            SelectedCardId = SelectedPieceId = 0;
            EyeStoneSelected = false;
            LastError = string.Empty;
            // 此事件先到面板，让它进入翻牌状态；随后 SelectionChanged 只更新选择，不抢先换正面。
            RevEvent.DispatchEvent(Events.HandRerolled);
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return true;
        }
    }
}

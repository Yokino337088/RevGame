using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>本棋盘独占有界公平队列；保留最多81条、每来源3段水流，过期0.7秒丢弃表现，退出清空历史。</summary>
        private readonly FairFeedbackBuffer<PiecePatternInfo> _patternQueue = new FairFeedbackBuffer<PiecePatternInfo>(81, 3, .7f);
        /// <summary>开发验收读取实际启用的范围Renderer数量；仅在查询时遍历有上限的短效集合。</summary>
        public int ActivePatternCount
        {
            get
            {
                int count = 0;
                foreach (BattleEffectView effect in _meleeEffects)
                {
                    if (effect.IsPatternActive)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        /// <summary>
        /// 攻击形状占用既有短效预算与池租约，异步代次检查、退出回池复用ShowEffect。
        /// 超预算只省略本次表现，不能反向阻止模型命中；数字仍由真实伤害事件独立调度。
        /// </summary>
        private void OnPiecePattern(PiecePatternInfo info)
        {
            if (_released)
            {
                return;
            }
            _patternQueue.Offer(info.SourceId, info, Time.time);
        }

        /// <summary>在已有LateUpdate内分帧交付，每帧至多3份；名额不足先保留候选，不能把名单后段永久饿死。</summary>
        private void FlushPiecePatterns()
        {
            float now = Time.time;
            for (int count = 0; count < 3; count++)
            {
                if (_meleeEffects.Count + _pendingMeleeLoads >= MaxMeleeEffects
                    || !_patternQueue.TryPeek(now, 0f, out PiecePatternInfo info, out int sourceId))
                {
                    return;
                }
                Vector3 source = info.Source.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                Vector3 center = info.Center.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                _pendingMeleeLoads++;
                _patternQueue.MarkDisplayed(sourceId, now);
                ShowEffect(BattleEffectKind.Pattern, source, info.Radius, info.Side, center, info.Kind);
            }
        }
    }
}

// 技能格面的分帧装配：只排显示工作，不排伤害。队列由当前棋盘拥有，换阶段立即清空。
using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>一格最多保留最新一次同类冲击，等价于旧实现新Tween覆盖旧Tween；不累积历史技能。</summary>
        private struct SkillTileRequest
        {
            public BoardCellView Cell;
            public Color Tint;
            public Vector4 Edges;
            public float Started, Delay, Duration;
            public bool Pending;
        }
        // 两种显示层不能互相覆盖请求；固定81格数组没有逐帧扩容，也不持有模型棋子。
        private readonly SkillTileRequest[] _skillTiles = new SkillTileRequest[Consts.BoardSize * Consts.BoardSize * 2];
        private int _skillTileCursor;
        /// <summary>供开发态回归核对分帧上限，统计本帧实际创建的格面动画，不是伤害数量。</summary>
        public int LastSkillTileStarts
        {
            get; private set;
        }

        private void QueueSkillTile(BoardCellView cell, Color tint, float duration, float delay)
        {
            int index = cell.Position.Y * Consts.BoardSize + cell.Position.X;
            _skillTiles[index] = new SkillTileRequest { Cell = cell, Tint = tint, Started = Time.time, Delay = delay, Duration = duration, Pending = true };
        }

        private void QueueHazardTile(BoardCellView cell, float delay, float duration, Vector4 edges)
        {
            int index = Consts.BoardSize * Consts.BoardSize + cell.Position.Y * Consts.BoardSize + cell.Position.X;
            _skillTiles[index] = new SkillTileRequest { Cell = cell, Edges = edges, Started = Time.time, Delay = delay, Duration = duration, Pending = true };
        }

        /// <summary>
        /// 每帧最多12格，累计装配约2ms便让出本帧；一个Unity调用不可抢占，预算不是硬实时保证。
        /// 游标跨帧轮转，Boss与区域波动共享预算；原事件时间保存在请求中，稍后启动会快进到正确相位。
        /// </summary>
        private void FlushSkillTileQueue()
        {
            LastSkillTileStarts = 0;
            double started = Time.realtimeSinceStartupAsDouble;
            for (int visited = 0; visited < _skillTiles.Length; visited++)
            {
                int index = _skillTileCursor;
                _skillTileCursor = (_skillTileCursor + 1) % _skillTiles.Length;
                SkillTileRequest request = _skillTiles[index];
                if (!request.Pending)
                {
                    continue;
                }
                _skillTiles[index] = default;
                if (request.Cell == null)
                {
                    continue;
                }
                float elapsed = Mathf.Max(0f, Time.time - request.Started);
                bool hazard = index >= Consts.BoardSize * Consts.BoardSize;
                // 场景卡住超过整段寿命时不补播过期残影；模型伤害早已按原时钟结算。
                if (elapsed >= request.Duration + (hazard ? 0 : request.Delay))
                {
                    continue;
                }
                if (hazard)
                {
                    request.Cell.PlayHazardWave(request.Delay, request.Duration, request.Edges, elapsed);
                }
                else
                {
                    request.Cell.PlayBossTile(request.Tint, request.Duration, request.Delay, elapsed);
                }
                LastSkillTileStarts++;
                if (LastSkillTileStarts >= 12 || Time.realtimeSinceStartupAsDouble - started >= .002)
                {
                    break;
                }
            }
        }

        /// <summary>先让队列失效，再回收格子；下一回合不能播放旧回合尚未启动的波浪。</summary>
        private void ClearSkillTileQueue()
        {
            System.Array.Clear(_skillTiles, 0, _skillTiles.Length);
            _skillTileCursor = 0;
            LastSkillTileStarts = 0;
        }
    }
}

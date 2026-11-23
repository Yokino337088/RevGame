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
        /// <summary>两个显示层里仍待播放的槽数，覆盖同一槽只替换内容，清空时归零。</summary>
        private int _pendingSkillTiles;
#if UNITY_EDITOR
        /// <summary>最近一次调度实际访问的槽数，为 0 表示空队列入口直接返回。</summary>
        public int LastSkillTileVisits { get; private set; }
#endif
        /// <summary>供开发态回归核对分帧上限，统计本帧实际创建的格面动画，不是伤害数量。</summary>
        public int LastSkillTileStarts
        {
            get; private set;
        }

        /// <summary>登记 Boss 格面请求，重复影响同一格时使用该层最新的视觉请求。</summary>
        private void QueueSkillTile(BoardCellView cell, Color tint, float duration, float delay)
        {
            int index = cell.Position.Y * Consts.BoardSize + cell.Position.X;
            SetPendingSkillTile(index, new SkillTileRequest { Cell = cell, Tint = tint, Started = Time.time, Delay = delay, Duration = duration, Pending = true });
        }

        /// <summary>登记区域波动格面请求，与 Boss 格面分别拥有各自的槽。</summary>
        private void QueueHazardTile(BoardCellView cell, float delay, float duration, Vector4 edges)
        {
            int index = Consts.BoardSize * Consts.BoardSize + cell.Position.Y * Consts.BoardSize + cell.Position.X;
            SetPendingSkillTile(index, new SkillTileRequest { Cell = cell, Edges = edges, Started = Time.time, Delay = delay, Duration = duration, Pending = true });
        }

        /// <summary>只为原本没有待播请求的槽增加计数，覆盖不会误判为两个效果。</summary>
        private void SetPendingSkillTile(int index, SkillTileRequest request)
        {
            if (!_skillTiles[index].Pending)
            {
                _pendingSkillTiles++;
            }
            _skillTiles[index] = request;
        }

        /// <summary>
        /// 每帧最多12格，累计装配约2ms便让出本帧；一个Unity调用不可抢占，预算不是硬实时保证。
        /// 空队列跳过扫描。有请求时按原游标轮转，后启动的动画推进至对应的播放相位。
        /// </summary>
        private void FlushSkillTileQueue()
        {
            LastSkillTileStarts = 0;
#if UNITY_EDITOR
            LastSkillTileVisits = 0;
#endif
            if (_pendingSkillTiles == 0)
            {
                return;
            }

            double started = Time.realtimeSinceStartupAsDouble;
            for (int visited = 0; visited < _skillTiles.Length; visited++)
            {
#if UNITY_EDITOR
                LastSkillTileVisits++;
#endif
                int index = _skillTileCursor;
                _skillTileCursor = (_skillTileCursor + 1) % _skillTiles.Length;
                SkillTileRequest request = _skillTiles[index];
                if (!request.Pending)
                {
                    continue;
                }
                // 失效或过期的槽也已出队，先更新槽数再决定是否播放。
                _skillTiles[index] = default;
                _pendingSkillTiles--;
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
            _pendingSkillTiles = 0;
            LastSkillTileStarts = 0;
#if UNITY_EDITOR
            LastSkillTileVisits = 0;
#endif
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>
    /// 无尽专用轻量ECS世界，不参与六章BattleService。
    /// World保存实体组件和空间桶；EndlessService按固定顺序执行出生、移动、攻击、技能与回收。
    /// 不使用反射、第三方ECS、线程或Unity DOTS；Revolution仍管理流程、资源和域生命周期。
    /// </summary>
    public sealed class EndlessEcsWorld
    {
        // 棋盘外出生口在-3/11，纵深队列可能更远；空间桶覆盖[-32,32)，桶宽一格，是几何结构。
        private const int GridSide = 64, GridOrigin = -32;
        public readonly EndlessHordeComponents Horde;
        public readonly EndlessActorComponents Actors = new EndlessActorComponents();
        /// <summary>每桶链表头与每实体下一项，重建不分配；坐标越界实体走保守补扫，不漏命中。</summary>
        private readonly int[] _heads = new int[GridSide * GridSide];
        private readonly int[] _next;
        private readonly int[] _outside;
        private int _outsideCount;
        /// <summary>索引是否反映本步全部移动/出生；HP变化无需重建，命中时仍实时校验IsAlive。</summary>
        private bool _spatialReady;
        private int _spatialVersion = -1;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>移动、战斗、死亡提交三段的累计 Stopwatch 刻度。只在开发包保存。</summary>
        private readonly long[] _stageTicks = new long[3];
        /// <summary>三段各自观察到的单步最长刻度，峰值可能来自不同模拟步。</summary>
        private readonly long[] _stagePeakTicks = new long[3];
        /// <summary>完整经过三个阶段的逻辑步数。不含暂停、布阵和计时前的业务。</summary>
        private int _timedSteps;
#endif
        public long CandidateChecks
        {
            get;
            private set;
        }
        public long SpatialBuilds
        {
            get;
            private set;
        }
        public EndlessEcsWorld(int capacity)
        {
            Horde = new EndlessHordeComponents(capacity);
            _next = new int[capacity];
            _outside = new int[capacity];
        }
        /// <summary>移动系统结束或生成实体后调用。一次下一次查询重建，共享只读索引，不共享查询结果。</summary>
        public void InvalidateSpatial()
        {
            _spatialReady = false;
        }
        /// <summary>由无尽固定步驱动。保持既有提交顺序，开发包额外记录三个阶段的时间。</summary>
        public void Simulate(EndlessService service, float step)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            EndlessEcsSystems.Movement(service, step);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            began = RecordStage(0, began);
#endif
            EndlessEcsSystems.Combat(service, step);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            began = RecordStage(1, began);
#endif
            EndlessEcsSystems.Death(service);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            RecordStage(2, began);
            _timedSteps++;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>结束一个阶段并交出同一个时间戳，阶段的耗时不会重叠。</summary>
        private long RecordStage(int stage, long began)
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsed = end - began;
            _stageTicks[stage] += elapsed;
            _stagePeakTicks[stage] = Math.Max(_stagePeakTicks[stage], elapsed);
            return end;
        }

        /// <summary>把本局累计刻度换算为毫秒。查询在采样窗外执行，单个峰值对应各自的阶段。</summary>
        public object SimulationTiming()
        {
            double millisecondsPerTick = 1000d / System.Diagnostics.Stopwatch.Frequency;
            return new Dictionary<string, object>
            {
                ["steps"] = _timedSteps,
                ["movementTotalMs"] = _stageTicks[0] * millisecondsPerTick,
                ["combatTotalMs"] = _stageTicks[1] * millisecondsPerTick,
                ["deathTotalMs"] = _stageTicks[2] * millisecondsPerTick,
                ["movementMaxMs"] = _stagePeakTicks[0] * millisecondsPerTick,
                ["combatMaxMs"] = _stagePeakTicks[1] * millisecondsPerTick,
                ["deathMaxMs"] = _stagePeakTicks[2] * millisecondsPerTick
            };
        }
#endif
        private void BuildSpatial()
        {
            if (_spatialReady && _spatialVersion == Horde.SpatialVersion)
            {
                return;
            }
            for (int i = 0; i < _heads.Length; i++)
            {
                _heads[i] = -1;
            }
            _outsideCount = 0;
            int count = Horde.ActiveCount;
            for (int cursor = 0; cursor < count; cursor++)
            {
                int slot = Horde.ActiveSlot(cursor);
                if (!Horde.IsAlive(slot))
                {
                    continue;
                }
                Vector2 position = Horde.Positions[slot];
                int x = Mathf.FloorToInt(position.x) - GridOrigin;
                int y = Mathf.FloorToInt(position.y) - GridOrigin;
                if (x < 0 || x >= GridSide || y < 0 || y >= GridSide)
                {
                    _outside[_outsideCount++] = slot;
                    continue;
                }
                int cell = y * GridSide + x;
                _next[slot] = _heads[cell];
                _heads[cell] = slot;
            }
            _spatialReady = true;
            _spatialVersion = Horde.SpatialVersion;
            SpatialBuilds++;
        }
        /// <summary>
        /// 取矩形候选写入调用方独立缓冲，按原槽顺序排序保持并列与伤害流水。
        /// 调用方再判圆/射线/炮架；嵌套爆炸须另一个缓冲，不能覆盖外层遍历。
        /// </summary>
        public void Query(float x0, float y0, float x1, float y1, List<int> result)
        {
            result.Clear();
            BuildSpatial();
            int left = Mathf.Clamp(Mathf.FloorToInt(x0) - GridOrigin, 0, GridSide - 1);
            int right = Mathf.Clamp(Mathf.FloorToInt(x1) - GridOrigin, 0, GridSide - 1);
            int bottom = Mathf.Clamp(Mathf.FloorToInt(y0) - GridOrigin, 0, GridSide - 1);
            int top = Mathf.Clamp(Mathf.FloorToInt(y1) - GridOrigin, 0, GridSide - 1);
            for (int y = bottom; y <= top; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    for (int slot = _heads[y * GridSide + x]; slot >= 0; slot = _next[slot])
                    {
                        AddCandidate(slot, x0, y0, x1, y1, result);
                    }
                }
            }
            for (int i = 0; i < _outsideCount; i++)
            {
                AddCandidate(_outside[i], x0, y0, x1, y1, result);
            }
            result.Sort();
        }
        private void AddCandidate(int slot, float x0, float y0, float x1, float y1, List<int> result)
        {
            CandidateChecks++;
            Vector2 p = Horde.Positions[slot];
            if (Horde.IsAlive(slot) && p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y1)
            {
                result.Add(slot);
            }
        }
    }
}

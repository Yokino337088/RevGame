using System;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>
    /// 无尽敌潮组件池。实体是固定槽号加代次，位置、生命与冷状态分开保存。
    /// 高频空间查询只读位置数组，不把灼烧、掉落等无关字段搬进查询工作集。
    /// 所有者是本局ECS World；仅存档时导出EndlessHorde快照，生产热循环不复制全场。
    /// </summary>
    public sealed class EndlessHordeComponents
    {
        /// <summary>位置组件，单位为棋格；槽位复用时必须由创建入口覆盖。</summary>
        public readonly Vector2[] Positions;
        /// <summary>生命组件。x为当前生命，y为上限，伤害只由无尽服务提交。</summary>
        private readonly Vector2[] _health;
        /// <summary>冷组件保存身份、状态时钟和奖励标签，位置/生命字段以独立组件为准。</summary>
        private readonly EndlessHorde[] _state;
        /// <summary>实体槽代次；同一槽死后再出生时递增，旧引用不能命中新实体。</summary>
        private readonly int[] _generation;
        /// <summary>本步活跃实体槽号，按槽号稳定排序；查询顺序保持旧算法的并列选择约定。</summary>
        private readonly int[] _active;
        /// <summary>当前活跃槽快照长度，仅结构变化后更新；访问前由RefreshActive保证有效。</summary>
        private int _activeCount;
        /// <summary>出生/死亡标记变化时置位，移动/HP变化不导致无效名单重建。</summary>
        private bool _activeDirty = true;
        /// <summary>所有会改变空间候选的写入版本；直接测试造局也通过同一提交点失效索引。</summary>
        public int SpatialVersion
        {
            get;
            private set;
        }

        public int Length
        {
            get
            {
                return _state.Length;
            }
        }
        public int ActiveCount
        {
            get
            {
                RefreshActive();
                return _activeCount;
            }
        }
        /// <summary>容量来自Excel并发预算；一次预分配，整局不增长、不保留历史实体。</summary>
        public EndlessHordeComponents(int capacity)
        {
            Positions = new Vector2[capacity];
            _health = new Vector2[capacity];
            _state = new EndlessHorde[capacity];
            _generation = new int[capacity];
            _active = new int[capacity];
        }
        /// <summary>兼容现有业务提交契约；读回值快照，写回一次拆到组件，避免两个可变真源。</summary>
        public EndlessHorde this[int slot]
        {
            get
            {
                EndlessHorde value = _state[slot];
                value.x = Positions[slot].x;
                value.y = Positions[slot].y;
                value.hp = _health[slot].x;
                value.maxHp = _health[slot].y;
                return value;
            }
            set
            {
                if (_state[slot].active != value.active || Positions[slot].x != value.x || Positions[slot].y != value.y)
                {
                    SpatialVersion++;
                }
                if (_state[slot].active != value.active)
                {
                    _activeDirty = true;
                }
                if (value.active && _state[slot].id != value.id)
                {
                    _generation[slot]++;
                }
                Positions[slot] = new Vector2(value.x, value.y);
                _health[slot] = new Vector2(value.hp, value.maxHp);
                _state[slot] = value;
            }
        }
        public bool IsAlive(int slot)
        {
            return _state[slot].active && _health[slot].x > 0;
        }
        /// <summary>读取ActiveCount后按cursor取槽号；会修改结构的外层循环需复制槽号到自己的快照。</summary>
        public int ActiveSlot(int cursor)
        {
            return _active[cursor];
        }
        /// <summary>保持旧出生顺序：选择编号最小的空槽，随机源与后续并列目标不因ECS迁移改变。</summary>
        public int FirstFree()
        {
            for (int i = 0; i < Length; i++)
            {
                if (!_state[i].active)
                {
                    return i;
                }
            }
            return -1;
        }
        /// <summary>开发态或延迟消费者核对槽代次，避免旧槽引用误认新实体。</summary>
        public int Generation(int slot)
        {
            return _generation[slot];
        }
        /// <summary>只在结构变化后扫描一次；纯移动和掉血不会反复重建活跃名单。</summary>
        private void RefreshActive()
        {
            if (!_activeDirty)
            {
                return;
            }
            _activeCount = 0;
            for (int slot = 0; slot < Length; slot++)
            {
                if (_state[slot].active)
                {
                    _active[_activeCount++] = slot;
                }
            }
            _activeDirty = false;
        }
        /// <summary>退出清组件和代次；导出的存档与池内数组互不共享。</summary>
        public void Clear()
        {
            Array.Clear(_state, 0, Length);
            Array.Clear(Positions, 0, Length);
            Array.Clear(_health, 0, Length);
            for (int i = 0; i < Length; i++)
            {
                _generation[i]++;
            }
            _activeCount = 0;
            _activeDirty = true;
            SpatialVersion++;
        }
        /// <summary>保存边界创建独立序列化数组，后续组件修改不会改动已交给存档的快照。</summary>
        public EndlessHorde[] Export()
        {
            var result = new EndlessHorde[Length];
            for (int i = 0; i < Length; i++)
            {
                result[i] = this[i];
            }
            return result;
        }
        /// <summary>已完整预检的存档在新World提交，不与旧局组件共享数组；兼容旧并发容量。</summary>
        public void Import(EndlessHorde[] snapshot)
        {
            Clear();
            for (int i = 0; i < snapshot.Length && i < Length; i++)
            {
                this[i] = snapshot[i];
            }
        }
    }
}

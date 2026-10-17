// ============================================================
// RevTimerTable.cs —— 槽位表（纯 C#，可脱离 Unity 直接跑断言）
//
// 位置：Runtime\RevTimer\Core\
//
// 【它是什么】
//   "谁还活着"这件事的唯一出处。槽位 = 固定数组下标，句柄 = 槽位 + 代际号。
//   内核每帧只做一件事：线性扫这张表（王者同款结论：量级不需要的地方不做复杂结构）。
//
// 【★ 代际号解决什么（旧框架的硬伤）】
//   旧框架的句柄是裸 int：一个早就结束/被复用的 id 传给 StopTimer 不报错也不生效，
//   业务分不清"停成功了""早结束了"；更糟的是 id 会被复用 → **误停别人**。
//   这里每个槽位带代际号（这个槽位第几次被分配），句柄里存着分配时的代际：
//     · 过期句柄调 Stop/Pause → 空操作（不抛、不误伤）✓
//     · 槽位被复用后，老句柄的代际对不上 → 依然打不中新计时器 ✓
//
// 【★ 为什么"延迟复用"】分配用轮转游标（不是"刚释放的立刻再用"）：
//   同一个槽位在短时间内被复用的概率越低，"老句柄打中新计时器"的机会就越少 —— 双保险。
//
// 【条目从哪来】由内核用 RevPoolCore 池化（这也是旧框架真出过 bug 的地方：
//   同一实例被归还两次 → 池里两份 → 两次取出拿到同一个对象；RevPoolCore 有重复归还拦截）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>一个计时器的数据（池化对象：复用时靠 <see cref="ResetAll"/> 彻底归零）。</summary>
    internal sealed class RevTimerEntry
    {
        // ── 身份 ──
        internal int Slot;
        internal int Generation;
        internal bool Alive;

        // ── 时间 ──
        internal RevTimeDomain Domain;

        /// <summary>true = 时刻式（比绝对 UTC，Server 域用）；false = 累积式（从创建起走 X 秒）。</summary>
        internal bool Absolute;
        internal DateTime TargetUtc;

        /// <summary>总时长 / 间隔（秒，double 累积 → 没有截断漂移）。</summary>
        internal double Duration;
        internal double Interval;
        internal double Elapsed;

        /// <summary>剩余触发次数（-1 = 无限）。</summary>
        internal long RepeatsLeft;

        /// <summary>创建时给的次数（Restart 复原用）。</summary>
        internal long InitialRepeats;

        /// <summary>已触发次数（给"带次数参数"的回调用）。</summary>
        internal long FiredCount;

        internal bool Paused;

        // ── 业务 ──
        internal Action Callback;
        internal Action<int> RepeatCallback;
        internal object Owner;

        // ── 本帧状态（内核用）──
        internal bool Due;
        internal bool PendingFree;

        /// <summary>
        /// 彻底归零 + **解除全部委托引用**（池化的纪律：复用前归零）。
        /// ★ 只清时间不清委托 = 内存泄漏 + 幽灵回调 —— 王者把这一条单独列为一条哲学，
        ///   因为当年就是"给 OnRecycle 优化掉解绑"出的事故。
        /// </summary>
        internal void ResetAll()
        {
            Slot = -1;
            Generation = 0;
            Alive = false;
            Domain = RevTimeDomain.Scaled;
            Absolute = false;
            TargetUtc = default;
            Duration = 0d;
            Interval = 0d;
            Elapsed = 0d;
            RepeatsLeft = 0;
            InitialRepeats = 0;
            FiredCount = 0;
            Paused = false;
            Callback = null;                 // ★ 解绑（防泄漏）
            RepeatCallback = null;           // ★ 解绑
            Owner = null;                    // ★ 解绑（否则 owner 被池里的对象一直引用着）
            Due = false;
            PendingFree = false;
        }

        /// <summary>剩余时间（秒）。时刻式按"目标时刻 - 现在"算，累积式按"总时长 - 已走"算。</summary>
        internal double Left(double nowUtcSeconds)
        {
            double left = Absolute
                ? TargetUtc.Ticks / (double)TimeSpan.TicksPerSecond - nowUtcSeconds
                : Duration - Elapsed;

            return left > 0d ? left : 0d;
        }

        /// <summary>进度 0~1（UI 的填充条直接用）。</summary>
        internal double Progress
        {
            get
            {
                if (Absolute || Duration <= 0d) return 1d;

                double p = Elapsed / Duration;
                return p < 0d ? 0d : (p > 1d ? 1d : p);
            }
        }
    }

    /// <summary>槽位表：分配 / 释放 / 句柄校验（纯 C#，不含任何调度逻辑）。</summary>
    internal sealed class RevTimerTable
    {
        private readonly RevTimerEntry[] _slots = new RevTimerEntry[RevTimerLimits.MaxTimers];
        private readonly int[] _generation = new int[RevTimerLimits.MaxTimers];

        /// <summary>轮转游标：让"刚释放的槽位"不会立刻被复用（见文件头"延迟复用"）。</summary>
        private int _cursor;

        internal int AliveCount { get; private set; }

        internal RevTimerEntry[] Slots => _slots;

        /// <summary>
        /// 占用一个空槽。返回槽位下标，满了返回 -1（由内核报 Overflow）。
        /// <paramref name="generation"/> 是本槽位本次分配的新代际号 —— 句柄拿它做失效校验。
        /// </summary>
        internal int Rent(Func<RevTimerEntry> create, out int generation)
        {
            int capacity = _slots.Length;

            for (int step = 0; step < capacity; step++)
            {
                int index = _cursor;
                _cursor++;
                if (_cursor >= capacity) _cursor = 0;

                if (_slots[index] != null) continue;        // 在用

                generation = ++_generation[index];           // ★ 只在分配时递增：代际 = 第几次被分配
                RevTimerEntry entry = create();
                entry.Slot = index;
                entry.Generation = generation;
                entry.Alive = true;
                _slots[index] = entry;
                AliveCount++;
                return index;
            }

            generation = 0;
            return -1;
        }

        /// <summary>把槽位标成空闲（代际不动：下次分配才递增 → 老句柄两道校验都过不去）。</summary>
        internal void Release(int slot)
        {
            RevTimerEntry entry = At(slot);
            if (entry == null || !entry.Alive) return;

            entry.Alive = false;
            AliveCount--;
        }

        /// <summary>拿走条目（回池用）：槽位清空，但代际保留。</summary>
        internal RevTimerEntry Detach(int slot)
        {
            RevTimerEntry entry = At(slot);
            _slots[slot] = null;
            return entry;
        }

        internal RevTimerEntry At(int slot)
            => slot >= 0 && slot < _slots.Length ? _slots[slot] : null;

        /// <summary>句柄校验：槽位在、活着、且代际一致（三者缺一都算过期句柄 → 安全空操作）。</summary>
        internal bool IsValid(int slot, int generation)
        {
            RevTimerEntry entry = At(slot);
            return entry != null && entry.Alive && entry.Generation == generation;
        }

        internal void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = null;

            AliveCount = 0;
            _cursor = 0;
        }
    }
}

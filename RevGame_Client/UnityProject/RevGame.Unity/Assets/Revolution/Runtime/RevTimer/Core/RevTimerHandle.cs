// ============================================================
// RevTimerHandle.cs —— 计时器句柄（纯 C#，值类型，可安全持有/传递/存字段）
//
// 位置：Runtime\RevTimer\Core\
//
// 【它替业务守住三件事】
//   ① **过期句柄是安全空操作**：Stop/Pause/Resume 打在已经结束（或槽位被复用）的计时器上，
//      不抛异常、也不会误伤别人 —— 旧框架的裸 int 句柄做不到这一点（"悬空 id 误停新计时器"是它的事故史）。
//   ② **不用记着判空**：默认的 Empty 句柄（`default`）调什么都没事，业务不用写 `if (h != null)`。
//   ③ **UI 直接能读**：`Left`（剩余秒）/ `Progress`（0~1），倒计时文本和填充条不用自己算。
//
// 【为什么不做"是否在跑"的查询哲学】
//   框架对外不做"你还在不在"的查询（要感知结束就自己在回调里做事），
//   但"剩余多久"是**数据**不是状态 —— 所以 Left / Progress 是刻意提供的。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>一个计时器的句柄：可查询剩余时间、可停止/暂停/重置；过期后所有操作都是空操作。</summary>
    public readonly struct RevTimerHandle : IEquatable<RevTimerHandle>
    {
        private readonly RevTimerCore _core;
        private readonly int _slot;
        private readonly int _generation;

        internal RevTimerHandle(RevTimerCore core, int slot, int generation)
        {
            _core = core;
            _slot = slot;
            _generation = generation;
        }

        /// <summary>空句柄（创建失败时返回它）：所有操作都是空操作。</summary>
        public static readonly RevTimerHandle Empty = default;

        public bool IsEmpty => _core == null;

        /// <summary>这个计时器还在跑吗（过期句柄 → false）。</summary>
        public bool IsAlive => _core != null && _core.IsAlive(_slot, _generation);

        /// <summary>剩余秒数（过期句柄 → 0）。UI 倒计时直接用它。</summary>
        public double Left => _core == null ? 0d : _core.LeftOf(_slot, _generation);

        /// <summary>进度 0~1（过期句柄 → 1）。UI 填充条直接用它。</summary>
        public double Progress => _core == null ? 1d : _core.ProgressOf(_slot, _generation);

        /// <summary>停止/取消（回收槽位；过期句柄是空操作）。</summary>
        public void Stop() => _core?.Stop(_slot, _generation);

        /// <summary>暂停（时间冻结，剩余量保留）。</summary>
        public void Pause() => _core?.SetPaused(_slot, _generation, true);

        /// <summary>恢复。</summary>
        public void Resume() => _core?.SetPaused(_slot, _generation, false);

        /// <summary>重新计时（已走时长清零、次数复原、取消暂停）。</summary>
        public void Restart() => _core?.Restart(_slot, _generation);

        public bool Equals(RevTimerHandle other)
            => ReferenceEquals(_core, other._core) && _slot == other._slot && _generation == other._generation;

        public override bool Equals(object obj) => obj is RevTimerHandle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _core != null ? _core.GetHashCode() : 0;
                return ((hash * 397) ^ _slot) * 397 ^ _generation;
            }
        }

        public static bool operator ==(RevTimerHandle a, RevTimerHandle b) => a.Equals(b);

        public static bool operator !=(RevTimerHandle a, RevTimerHandle b) => !a.Equals(b);

        public override string ToString()
            => _core == null ? "[RevTimerHandle Empty]" : $"[RevTimerHandle slot={_slot} gen={_generation} left={Left:F2}s]";
    }
}

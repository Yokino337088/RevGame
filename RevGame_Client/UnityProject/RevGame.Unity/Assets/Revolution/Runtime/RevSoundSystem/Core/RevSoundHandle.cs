// RevSoundHandle.cs —— 播放句柄（值类型）
//
// 【它是什么】一次播放的"票根"：`var h = RevSound.Play("ui_click");` → 想停就 `h.Stop()`。
// 【为什么要有它】你旧框架是 `StopSound(AudioSource)` —— 业务得自己攥着 AudioSource（实现细节），
//   而且从列表里找不到时静默 return（"停不掉、也不知道为什么"）。句柄把这件事收回来：
//   业务只认句柄，内核怎么实现与它无关。
// 【过期句柄不会误伤新声音】每个槽位带"代际号"：老句柄的 (槽位, 代际) 与当前不符 → Stop 是空操作。
//   （王者用 uint playingId + 三张映射表做同一件事，这里用代数校验，O(1) 且只有一个数组。）
// 【框架不提供 IsPlaying】要感知"播完了"，订阅 RevSound.VoiceFinished 自己记 —— 与"框架不做查询"一致。

namespace Revolution
{
    /// <summary>句柄的拥有者（由内核实现）：句柄只依赖这个接口，不依赖内核具体类型。</summary>
    internal interface IRevSoundHandleOwner
    {
        /// <summary>停止某个槽位（内部会校验代际号：过期句柄直接忽略）</summary>
        void StopSlot(int slot, int generation);
    }

    /// <summary>
    /// 一次音效播放的句柄：<c>Stop()</c> 停止它，<c>IsEmpty</c> 判断是否"根本没播出去"。
    /// </summary>
    public readonly struct RevSoundHandle : System.IEquatable<RevSoundHandle>
    {
        /// <summary>空句柄（播放被拒绝 / 系统关闭时返回它；对空句柄做任何操作都是安全的空操作）</summary>
        public static readonly RevSoundHandle Empty = default;

        private readonly IRevSoundHandleOwner _owner;
        private readonly int _slot;
        private readonly int _generation;

        internal RevSoundHandle(IRevSoundHandleOwner owner, int slot, int generation)
        {
            _owner = owner;
            _slot = slot;
            _generation = generation;
        }

        /// <summary>是否空句柄（true = 这次播放没能开始：被策略拦下 / 超出上限 / 系统关闭 / 加载失败）</summary>
        public bool IsEmpty => _owner == null;

        /// <summary>停止这次播放（空句柄、已结束、过期句柄都是安全的空操作）</summary>
        public void Stop() => _owner?.StopSlot(_slot, _generation);

        /// <summary>两个句柄是否指向同一次播放</summary>
        public bool Equals(RevSoundHandle other)
            => ReferenceEquals(_owner, other._owner) && _slot == other._slot && _generation == other._generation;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is RevSoundHandle other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (_owner == null ? 0 : _owner.GetHashCode()) ^ (_slot * 397) ^ _generation;

        /// <summary>调试显示：例如 <c>Sound#3.2</c></summary>
        public override string ToString() => _owner == null ? "Sound(空句柄)" : $"Sound#{_slot}.{_generation}";
    }
}

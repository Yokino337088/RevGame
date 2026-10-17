// RevSoundVoiceTable.cs —— 声音槽位表（纯 C#：不引用 UnityEngine，可脱离引擎单测）
//
// 【它解决什么】王者把"这条声音是谁、在等什么、要不要停"拆进三张映射表（占位 ID / 真实 ID / Prepare 现场），
//   于是每次 Stop 都要全表遍历找对应关系（源码注释自己都写了"这里只能遍历每一个 prepareEvent"）。
//   这里用一张定长数组 + 槽位 + 代际号：查找、失效判定、停止全是 O(1)，一个数组就是全部真相。
//
// 【代际号为什么重要】槽位会复用。老句柄 (槽位, 代际) 与当前槽位代际不符 → 任何操作都是空操作，
//   所以"过期的句柄绝不会误停一条新声音"（这是你旧框架 StopSound 找不到就静默 return 的正确解法）。
//
// 【同帧去重】本帧已经出现过的 (名字 + 分类) 不再放行 —— 防按钮连点、防同帧重复触发。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一个声音槽位的全部元数据（不含 Unity 对象，Unity 侧的对象在 RevSoundCore 里按下标平行持有）。</summary>
    internal struct RevSoundSlot
    {
        /// <summary>是否占用中</summary>
        internal bool Alive;

        /// <summary>代际号（每次释放 +1，用于让老句柄失效）</summary>
        internal int Generation;

        /// <summary>音效名</summary>
        internal string Name;

        /// <summary>分类</summary>
        internal RevSoundKind Kind;

        /// <summary>单次音量（不含分类音量与主音量）</summary>
        internal float Volume;

        /// <summary>是否循环</summary>
        internal bool Loop;

        /// <summary>是否在等资源（异步加载中，资源到了自动开播）</summary>
        internal bool Pending;

        /// <summary>音量每秒变化量（淡入 &gt; 0 / 淡出 &lt; 0）</summary>
        internal float FadeSpeed;

        /// <summary>淡入淡出的目标音量</summary>
        internal float FadeTarget;

        /// <summary>淡出到 0 之后是否直接停掉（StopBgm(fade) 用）</summary>
        internal bool FadeStop;

        /// <summary>分配序号（越大越新）—— 超出上限时用它找"最旧的一条"</summary>
        internal int Sequence;
    }

    /// <summary>声音槽位表：分配 / 释放 / 代际校验 / 同帧去重 / 找最旧的一次性声音。</summary>
    internal sealed class RevSoundVoiceTable
    {
        private readonly struct FrameKey
        {
            internal readonly string Name;
            internal readonly RevSoundKind Kind;

            internal FrameKey(string name, RevSoundKind kind)
            {
                Name = name;
                Kind = kind;
            }
        }

        private RevSoundSlot[] _slots;
        private readonly List<FrameKey> _frameKeys;
        private int _alive;
        private int _sequence;
        private int _cursor;

        internal RevSoundVoiceTable(int capacity)
        {
            _slots = new RevSoundSlot[capacity > 0 ? capacity : 8];
            _frameKeys = new List<FrameKey>(16);
        }

        /// <summary>槽位总数（固定，不扩容 —— 声音数量本来就该有上限）</summary>
        internal int Capacity => _slots.Length;

        /// <summary>正在占用的槽位数量（含"在等资源"的）</summary>
        internal int AliveCount => _alive;

        /// <summary>取槽位引用（调用方保证下标有效）</summary>
        internal ref RevSoundSlot Slot(int index) => ref _slots[index];

        /// <summary>这个 (槽位, 代际) 现在还有效吗（过期句柄 → false）</summary>
        internal bool IsAlive(int index, int generation)
            => index >= 0 && index < _slots.Length && _slots[index].Alive && _slots[index].Generation == generation;

        /// <summary>分配一个空槽位（环形扫描，均摊 O(1)）；没有空位返回 -1</summary>
        internal int Rent(string name, RevSoundKind kind, float volume, bool loop, out int generation)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                int index = _cursor + i;
                if (index >= _slots.Length) index -= _slots.Length;

                ref RevSoundSlot slot = ref _slots[index];
                if (slot.Alive) continue;

                slot.Alive = true;
                slot.Generation++;
                slot.Name = name;
                slot.Kind = kind;
                slot.Volume = volume;
                slot.Loop = loop;
                slot.Pending = false;
                slot.FadeSpeed = 0f;
                slot.FadeTarget = 0f;
                slot.FadeStop = false;
                slot.Sequence = ++_sequence;

                generation = slot.Generation;
                _alive++;
                _cursor = index + 1 < _slots.Length ? index + 1 : 0;
                return index;
            }

            generation = 0;
            return -1;
        }

        /// <summary>
        /// 释放槽位：立即置为空闲（老句柄的 <c>IsAlive</c> 马上变 false）；代际号在下一次分配时递增，
        /// 所以"释放后再复用同一个槽位"的新声音拿到的是新代际 —— 老句柄两道校验都过不去。
        /// </summary>
        internal void Free(int index)
        {
            if (index < 0 || index >= _slots.Length) return;

            ref RevSoundSlot slot = ref _slots[index];
            if (!slot.Alive) return;

            slot.Alive = false;
            slot.Name = null;
            slot.Volume = 0f;
            slot.Loop = false;
            slot.Pending = false;
            slot.FadeSpeed = 0f;
            slot.FadeTarget = 0f;
            slot.FadeStop = false;
            _alive--;
        }

        /// <summary>找"最旧的一条一次性声音"（非循环、不在等资源）—— 超出上限时淘汰它；没有返回 -1</summary>
        internal int FindOldestOneShot()
        {
            int best = -1;
            int bestSequence = int.MaxValue;

            for (int i = 0; i < _slots.Length; i++)
            {
                ref RevSoundSlot slot = ref _slots[i];
                if (!slot.Alive || slot.Loop || slot.Pending) continue;
                if (slot.Sequence >= bestSequence) continue;

                bestSequence = slot.Sequence;
                best = i;
            }

            return best;
        }

        /// <summary>同帧去重：本帧第一次看到 (名字 + 分类) 返回 true（允许播），重复返回 false</summary>
        internal bool TryMarkFrame(string name, RevSoundKind kind)
        {
            for (int i = 0; i < _frameKeys.Count; i++)
            {
                FrameKey key = _frameKeys[i];
                if (key.Kind == kind && string.Equals(key.Name, name, StringComparison.Ordinal)) return false;
            }

            _frameKeys.Add(new FrameKey(name, kind));
            return true;
        }

        /// <summary>进入新的一帧（清空同帧去重记录）</summary>
        internal void AdvanceFrame() => _frameKeys.Clear();

        /// <summary>清空所有槽位（整体释放时用）</summary>
        internal void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) Free(i);
            _frameKeys.Clear();
        }
    }
}

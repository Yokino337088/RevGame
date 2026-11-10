#include "DeterministicRandom.h"

namespace RevGameCore
{
    namespace
    {
        // 用不同步进值把一颗种子扩成两个状态字；全程使用定义明确的无符号回绕。
        uint64_t SplitMix64(uint64_t& seed)
        {
            seed += 0x9E3779B97F4A7C15ULL;
            uint64_t mixed = seed;
            mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9ULL;
            mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBULL;
            return mixed ^ (mixed >> 31);
        }
    }

    // 两次调用依次推进同一个局部种子，生成完整的双字初始状态。
    DeterministicRandom::DeterministicRandom(uint64_t seed) : State0(SplitMix64(seed)), State1(SplitMix64(seed))
    {
    }

    // 用旧双字计算新状态与输出。移位和加法都在无符号类型内执行。
    uint64_t DeterministicRandom::NextUInt64()
    {
        uint64_t first = State0;
        const uint64_t second = State1;
        State0 = second;
        first ^= first << 23;
        State1 = first ^ (first >> 17) ^ second ^ (second >> 26);
        return State1 + second;
    }

    // 在 [0, bound) 均匀抽样。没有合法范围时直接返回且不消耗随机序列。
    uint32_t DeterministicRandom::NextBelow(uint32_t bound)
    {
        if (bound == 0)
        {
            return 0;
        }
        // 去掉使 2^32 个输入无法平均分桶的最小区间，剩余输入可等概率取余。
        const uint32_t threshold = (uint32_t{0} - bound) % bound;
        uint32_t value;
        do
        {
            value = static_cast<uint32_t>(NextUInt64() >> 32);
        } while (value < threshold);
        return value % bound;
    }

    // 以固定顺序复制状态，不改变下一次抽样值。
    std::array<uint64_t, 2> DeterministicRandom::StateWords() const
    {
        return {State0, State1};
    }

    // 全零状态会永久卡在零，因此先拒绝再提交两个字，失败时保留旧状态。
    bool DeterministicRandom::RestoreState(const std::array<uint64_t, 2>& words)
    {
        if ((words[0] | words[1]) == 0)
        {
            return false;
        }
        State0 = words[0];
        State1 = words[1];
        return true;
    }
}

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

    DeterministicRandom::DeterministicRandom(uint64_t seed) : State0(SplitMix64(seed)), State1(SplitMix64(seed))
    {
    }

    uint64_t DeterministicRandom::NextUInt64()
    {
        uint64_t first = State0;
        const uint64_t second = State1;
        State0 = second;
        first ^= first << 23;
        State1 = first ^ (first >> 17) ^ second ^ (second >> 26);
        return State1 + second;
    }

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

    std::array<uint64_t, 2> DeterministicRandom::StateWords() const
    {
        return {State0, State1};
    }
}

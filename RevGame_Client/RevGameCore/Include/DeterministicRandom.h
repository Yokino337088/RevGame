#pragma once

#include <array>
#include <cstdint>

namespace RevGameCore
{
    // 每个原生世界独占的逻辑随机源。权威和隔离预测可各自执行，表现层与指纹读取不抽样。
    class DeterministicRandom
    {
      public:
        // 使用 MiniGameCore 的 SplitMix64 扩种方式，种子为 0 时同样有有效序列。
        explicit DeterministicRandom(uint64_t seed = 0);

        // xorshift128+ 以无符号整数回绕，每次调用推进一次内部状态。
        uint64_t NextUInt64();

        // 采样 [0, bound)。拒绝有偏区间；bound 为 0 时返回 0 且不消耗随机数。
        uint32_t NextBelow(uint32_t bound);

        // 按状态字 0、1 返回值副本，用于固定顺序的对账，读取不抽样。
        std::array<uint64_t, 2> StateWords() const;

        // 只供状态恢复使用。全零是无效的 xorshift 状态，失败时保留原随机序列。
        bool RestoreState(const std::array<uint64_t, 2>& words);

      private:
        // xorshift128+ 的前一个 64 位状态字，参与帧指纹和检查点。
        uint64_t State0;
        // xorshift128+ 的后一个 64 位状态字。只存开局种子无法恢复未来抽样顺序。
        uint64_t State1;
    };
}

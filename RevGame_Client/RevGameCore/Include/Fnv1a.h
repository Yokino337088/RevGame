#pragma once

#include <cstddef>
#include <cstdint>

namespace RevGameCore
{
    constexpr uint32_t Fnv1aOffsetBasis = 0x811c9dc5u;
    constexpr uint32_t Fnv1aPrime = 0x01000193u;

    // FNV-1a 每次只混入一个字节，乘法按无符号 32 位回绕。
    constexpr uint32_t Fnv1aByte(uint32_t hash, uint8_t value)
    {
        return (hash ^ value) * Fnv1aPrime;
    }

    // 将逻辑字段按小端字节依次混入，与 CPU 大小端和结构体布局无关。
    constexpr uint32_t Fnv1aWord(uint32_t hash, uint32_t value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash = Fnv1aByte(hash, static_cast<uint8_t>(value >> shift));
        }
        return hash;
    }

    // 64 位种子和随机状态先混低字，再混高字，保留全部位。
    constexpr uint32_t Fnv1aDoubleWord(uint32_t hash, uint64_t value)
    {
        hash = Fnv1aWord(hash, static_cast<uint32_t>(value));
        return Fnv1aWord(hash, static_cast<uint32_t>(value >> 32));
    }

    // 检查点本身已经是小端字节流，可以直接逐字节校验。
    constexpr uint32_t Fnv1aBytes(const uint8_t* data, std::size_t length)
    {
        uint32_t hash = Fnv1aOffsetBasis;
        for (std::size_t i = 0; i < length; ++i)
        {
            hash = Fnv1aByte(hash, data[i]);
        }
        return hash;
    }
}

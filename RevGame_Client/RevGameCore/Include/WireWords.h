#pragma once

#include <cstdint>
#include <limits>

namespace RevGameCore
{
    // 明确逐字节读写小端整数。FIFO 和检查点共用这一规则，不依赖结构体填充或对齐。
    inline uint32_t ReadWord(const uint8_t* data)
    {
        return uint32_t(data[0]) | (uint32_t(data[1]) << 8) | (uint32_t(data[2]) << 16) | (uint32_t(data[3]) << 24);
    }

    // C++17 无符号超范围转有符号由实现决定。先在 64 位内还原二补码，再做有界转换。
    inline int32_t ReadSignedWord(const uint8_t* data)
    {
        const uint32_t bits = ReadWord(data);
        const int64_t value = bits <= uint32_t(std::numeric_limits<int32_t>::max())
                                  ? static_cast<int64_t>(bits)
                                  : static_cast<int64_t>(bits) - (int64_t{1} << 32);
        return static_cast<int32_t>(value);
    }

    // 每次写低八位再右移。调用方提供至少四字节，写入顺序在大小端机器上保持一致。
    inline void WriteWord(uint8_t* data, uint32_t value)
    {
        for (int i = 0; i < 4; ++i)
        {
            data[i] = static_cast<uint8_t>(value >> (i * 8));
        }
    }
}

// 由 Config/GenerateDuelConfig.py 生成。
#pragma once
#include <array>
#include <cstdint>

namespace RevGameCore
{
    // FIFO 协议版本和固定逻辑步长，共用 Duel.v1.json。
    constexpr int WireVersion = 8;
    constexpr int FrameMilliseconds = 50;
    // 协议尾部从这里开始保存白方的六项命中距离。
    constexpr int YayaRangeOffset = 66;
    // 原生回归与托管开局包共用的规则字，防止测试继续使用旧招式布局。
    inline constexpr std::array<int32_t, 72> DefaultDuelSettings{ 100, 4500, 140, 0, 1700, 100, 1, 12, 50, 9, 2240, 3, 8, 0, 15, 3600, 4, 12, 16, 32, 4800, 7, 20, 55, 540, 68, 480, 18, 2880, 5, 14, 20, 10, 2400, 3, 8, 0, 13, 2656, 4, 11, 0, 5, 8, 0, 15, 6, 12, 420, 12, 21, 16, 210, 0, 7, 13, 100, 6, 5, 9, 110, 15, 6, 11, 150, 14, 2176, 4080, 5040, 3280, 2336, 2528 };
}

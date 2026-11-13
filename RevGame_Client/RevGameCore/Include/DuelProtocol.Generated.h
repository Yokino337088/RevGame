// 由 Config/GenerateDuelConfig.py 生成。
#pragma once
#include <array>
#include <cstdint>

namespace RevGameCore
{
    // FIFO 协议版本和固定逻辑步长，共用 Duel.v1.json。
    constexpr int WireVersion = 9;
    constexpr int FrameMilliseconds = 50;
    // 协议尾部从这里开始保存白方的六项命中距离。
    constexpr int YayaRangeOffset = 66;
    // 原生回归与托管开局包共用的规则字，防止测试继续使用旧招式布局。
    inline constexpr std::array<int32_t, 72> DefaultDuelSettings{ 100, 4500, 140, 0, 1700, 100, 1, 12, 50, 9, 1568, 3, 8, 0, 15, 2520, 4, 12, 16, 32, 3360, 7, 20, 55, 540, 68, 480, 18, 2016, 5, 14, 20, 10, 1680, 3, 8, 0, 13, 1859, 4, 11, 0, 4, 8, 0, 15, 5, 12, 420, 12, 21, 16, 210, 0, 6, 13, 100, 6, 4, 9, 110, 15, 5, 11, 150, 14, 1523, 2856, 3528, 2296, 1635, 1770 };
}

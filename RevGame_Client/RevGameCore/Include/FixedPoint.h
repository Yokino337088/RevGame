#pragma once

#include <cstdint>

namespace RevGameCore
{
    // 战斗坐标以千分格存储。配置和 FIFO 用原始整数，只有内核做定点运算。
    class FixedPoint
    {
      public:
        // 1000 个原始单位等于 1 格，与现有决斗配置和显示协议一致。
        static constexpr int32_t Scale = 1000;

        constexpr FixedPoint() = default;

        // 原始值已包含小数刻度，直接载入，不做缩放或浮点转换。
        static constexpr FixedPoint FromRaw(int32_t raw)
        {
            return FixedPoint(raw);
        }

        // 从整数格转换。超出可表示范围时停在有符号边界。
        static FixedPoint FromInt(int32_t value);

        // 导出原始千分格，供快照和状态指纹读取。
        constexpr int32_t RawValue() const
        {
            return Raw;
        }

        // 先用 64 位中间量，再饱和至 int32；乘法去掉重复刻度并向零截断。
        FixedPoint operator+(FixedPoint other) const;
        FixedPoint operator-(FixedPoint other) const;
        FixedPoint operator*(FixedPoint other) const;

        // 绝对值也经过饱和，避免对最小有符号整数直接取负。
        FixedPoint Abs() const;

        // 相同刻度可以直接比较原始整数。
        constexpr bool operator<(FixedPoint other) const
        {
            return Raw < other.Raw;
        }

        constexpr bool operator<=(FixedPoint other) const
        {
            return Raw <= other.Raw;
        }

        constexpr bool operator==(FixedPoint other) const
        {
            return Raw == other.Raw;
        }

      private:
        // 唯一存储的数值，负数表示左侧位置或向下速度。
        int32_t Raw = 0;

        explicit constexpr FixedPoint(int32_t raw) : Raw(raw)
        {
        }

        // 窄化前统一检查范围，避免编译器自行定义有符号窄化结果。
        static FixedPoint FromWide(int64_t value);
    };
}

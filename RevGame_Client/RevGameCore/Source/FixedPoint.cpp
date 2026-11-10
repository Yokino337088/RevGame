#include "FixedPoint.h"
#include <algorithm>
#include <limits>

namespace RevGameCore
{
    // 先限制在 int32 的闭区间再窄化，防止越界转换在不同编译器上得出不同结果。
    FixedPoint FixedPoint::FromWide(int64_t value)
    {
        return FromRaw(static_cast<int32_t>(
            std::clamp<int64_t>(value, std::numeric_limits<int32_t>::min(), std::numeric_limits<int32_t>::max())));
    }

    // 整格转千分格。先扩宽再乘，避免中间值溢出。
    FixedPoint FixedPoint::FromInt(int32_t value)
    {
        return FromWide(static_cast<int64_t>(value) * Scale);
    }

    // 用同一刻度直接加原始值，再走统一的范围检查。
    FixedPoint FixedPoint::operator+(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) + other.Raw);
    }

    // 原始值相减保留有符号方向，仍用 64 位中间值。
    FixedPoint FixedPoint::operator-(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) - other.Raw);
    }

    // 两个千分格相乘带了两次刻度，除去一次后才能作为普通定点坐标使用。
    FixedPoint FixedPoint::operator*(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) * other.Raw / Scale);
    }

    // 最小 int32 的相反数无法用 int32 表示，因此先扩宽再取绝对值。
    FixedPoint FixedPoint::Abs() const
    {
        return FromWide(Raw < 0 ? -static_cast<int64_t>(Raw) : Raw);
    }
}

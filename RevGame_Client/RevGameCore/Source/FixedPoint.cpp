#include "FixedPoint.h"
#include <algorithm>
#include <limits>

namespace RevGameCore
{
    FixedPoint FixedPoint::FromWide(int64_t value)
    {
        return FromRaw(static_cast<int32_t>(
            std::clamp<int64_t>(value, std::numeric_limits<int32_t>::min(), std::numeric_limits<int32_t>::max())));
    }

    FixedPoint FixedPoint::FromInt(int32_t value)
    {
        return FromWide(static_cast<int64_t>(value) * Scale);
    }

    FixedPoint FixedPoint::operator+(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) + other.Raw);
    }

    FixedPoint FixedPoint::operator-(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) - other.Raw);
    }

    FixedPoint FixedPoint::operator*(FixedPoint other) const
    {
        return FromWide(static_cast<int64_t>(Raw) * other.Raw / Scale);
    }

    FixedPoint FixedPoint::Abs() const
    {
        return FromWide(Raw < 0 ? -static_cast<int64_t>(Raw) : Raw);
    }
}

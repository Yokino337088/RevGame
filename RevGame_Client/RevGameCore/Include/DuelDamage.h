#pragma once

#include "DuelTypes.h"

namespace RevGameCore
{
    // 对同一帧的双方命中先取样，再统一扣血。此入口是决斗中唯一的伤害提交点。
    DuelHits ResolveDuelDamage(DuelFighters& fighters, const Settings& config, bool noDamage);
}

#include "DuelDamage.h"
#include <algorithm>

namespace RevGameCore
{
    namespace
    {
        // 用移动阶段结束后的定点距离与招式年龄读取候选伤害，不修改任何座位。
        int32_t EvaluateHit(const Fighter& attacker, const Fighter& defender, const Settings& config)
        {
            if (attacker.Action == 0)
            {
                return 0;
            }
            const int offset = 9 + (attacker.Action - 1) * 5;
            const auto distance = (defender.X - attacker.X) * FixedPoint::FromInt(attacker.Facing);
            if (attacker.Age == config[offset + 2] && FixedPoint{} <= distance &&
                distance <= FixedPoint::FromRaw(config[offset + 1]) &&
                (attacker.Y - defender.Y).Abs() <= FixedPoint::FromRaw(config[26]))
            {
                return config[offset];
            }
            return 0;
        }
    }

    DuelHits ResolveDuelDamage(DuelFighters& fighters, const Settings& config, bool noDamage)
    {
        DuelHits hits{};
        for (const Seat seat : SeatOrder)
        {
            hits[seat] = EvaluateHit(fighters[seat], fighters[Opponent(seat)], config);
        }
        // 先收集再提交，黑方先遍历不会使同帧被击倒的白方失去已经命中的攻击。
        if (!noDamage)
        {
            for (const Seat seat : SeatOrder)
            {
                Fighter& target = fighters[seat];
                target.Hp = std::max(0, target.Hp - hits[Opponent(seat)]);
            }
        }
        return hits;
    }
}

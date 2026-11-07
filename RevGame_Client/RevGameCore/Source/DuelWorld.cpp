// 与 MiniGameCore 的逻辑分段一致：先接纳两席意图，再结算命中，最后输出只读帧。
#include "DuelWorld.h"
#include <algorithm>
#include <cstdlib>

namespace RevGameCore
{
    bool DuelWorld::Start(const Settings& settings)
    {
        if (Valid || settings[0] < 1 || settings[0] > 10000 || settings[1] < 1000 || settings[1] > 100000 ||
            settings[2] < 1 || settings[2] > 1000 || settings[3] < 10 || settings[3] > 10000 ||
            settings[4] < 1 || settings[4] >= settings[1] || settings[5] < 1 || settings[5] > 1000 ||
            settings[6] < 0 || settings[6] > settings[5] || settings[7] < 0 || settings[7] > settings[5] ||
            settings[8] < 10 || settings[8] > 200)
        {
            return false;
        }
        // 尾部三项是跳跃初速、每帧重力、可命中的垂直间距。
        if (settings[24] < 1 || settings[24] > 600 || settings[25] < 1 || settings[25] > 200 ||
            settings[26] < 1 || settings[26] > 4000)
        {
            return false;
        }
        for (int offset = 9; offset < 24; offset += 5)
        {
            if (settings[offset] < 1 || settings[offset] > 10000 || settings[offset + 1] < 1 ||
                settings[offset + 1] > settings[1] * 2 || settings[offset + 2] < 1 ||
                settings[offset + 3] < settings[offset + 2] || settings[offset + 3] > 600 ||
                settings[offset + 4] < 0 || settings[offset + 4] > settings[5])
            {
                return false;
            }
        }
        Config = settings;
        Fighters[0] = Fighter{-Config[4], Config[0], 0, 0, 0, 1};
        Fighters[1] = Fighter{Config[4], Config[0], 0, 0, 0, -1};
        Valid = true;
        return true;
    }

    void DuelWorld::MoveAndStart(Fighter& fighter, int32_t input)
    {
        // 跳跃用定步长整数积分。空中也推进重力，攻击硬直不会让角色悬停。
        if (fighter.Y == 0 && fighter.Action == 0 && (input & 16) != 0)
        {
            fighter.VerticalSpeed = Config[24];
        }
        if (fighter.Y != 0 || fighter.VerticalSpeed > 0)
        {
            fighter.Y = std::max(0, fighter.Y + fighter.VerticalSpeed);
            fighter.VerticalSpeed = fighter.Y == 0 ? 0 : fighter.VerticalSpeed - Config[25];
        }
        if (fighter.Action != 0)
        {
            int offset = 9 + (fighter.Action - 1) * 5;
            if (++fighter.Age <= Config[offset + 3])
            {
                return;
            }
            fighter.Action = 0;
            fighter.Age = 0;
        }
        // 位 0/1 是左右，位 2～3 是单一技能槽。无效方向组合在桥接处拒绝。
        const int direction = (input & 1) != 0 ? -1 : (input & 2) != 0 ? 1 : 0;
        fighter.X = std::clamp(fighter.X + direction * Config[2], -Config[1], Config[1]);
        const int action = (input >> 2) & 3;
        if (action != 0)
        {
            const int cost = Config[9 + (action - 1) * 5 + 4];
            if (fighter.Energy >= cost)
            {
                fighter.Action = action;
                fighter.Age = 0;
                fighter.Energy -= cost;
            }
        }
    }

    int32_t DuelWorld::ResolveHit(const Fighter& attacker, const Fighter& defender) const
    {
        if (attacker.Action == 0)
        {
            return 0;
        }
        const int offset = 9 + (attacker.Action - 1) * 5;
        const int distance = (defender.X - attacker.X) * attacker.Facing;
        if (attacker.Age == Config[offset + 2] && distance >= 0 && distance <= Config[offset + 1] &&
            std::abs(attacker.Y - defender.Y) <= Config[26])
        {
            return Config[offset];
        }
        return 0;
    }

    bool DuelWorld::Advance(int32_t frame, int32_t first, int32_t second, Snapshot& snapshot)
    {
        if (!Valid || frame != Frame + 1 || first < 0 || first > 31 || second < 0 || second > 31 ||
            (first & 3) == 3 || (second & 3) == 3)
        {
            return false;
        }
        Frame = frame;
        int firstHit = 0;
        int secondHit = 0;
        if (Result == 0)
        {
            MoveAndStart(Fighters[0], first);
            MoveAndStart(Fighters[1], second);
            if (Fighters[0].X != Fighters[1].X)
            {
                Fighters[0].Facing = Fighters[0].X < Fighters[1].X ? 1 : -1;
                Fighters[1].Facing = -Fighters[0].Facing;
            }
            // 先取两个命中再修改血量，避免同帧互击由座位顺序决定胜负。
            firstHit = ResolveHit(Fighters[0], Fighters[1]);
            secondHit = ResolveHit(Fighters[1], Fighters[0]);
            Fighters[0].Hp = std::max(0, Fighters[0].Hp - secondHit);
            Fighters[1].Hp = std::max(0, Fighters[1].Hp - firstHit);
            Fighters[0].Energy = std::min(Config[5], Fighters[0].Energy + Config[6] + (firstHit ? Config[7] : 0));
            Fighters[1].Energy = std::min(Config[5], Fighters[1].Energy + Config[6] + (secondHit ? Config[7] : 0));
            if (Fighters[0].Hp == 0 || Fighters[1].Hp == 0 || Frame >= Config[3])
            {
                Result = Fighters[0].Hp == Fighters[1].Hp ? 3 : Fighters[0].Hp > Fighters[1].Hp ? 1 : 2;
            }
        }
        Produce(snapshot);
        snapshot[14] = firstHit;
        snapshot[15] = secondHit;
        return true;
    }

    void DuelWorld::Produce(Snapshot& snapshot) const
    {
        snapshot[0] = Frame;
        snapshot[1] = Result;
        for (int i = 0; i < 2; ++i)
        {
            const auto& fighter = Fighters[i];
            int offset = 2 + i * 6;
            snapshot[offset] = fighter.X;
            snapshot[offset + 1] = fighter.Hp;
            snapshot[offset + 2] = fighter.Energy;
            snapshot[offset + 3] = fighter.Action;
            snapshot[offset + 4] = fighter.Age;
            snapshot[offset + 5] = fighter.Facing;
        }
        // 核对所有会影响未来执行的世界字段和配置；没有未纳入的计时器或随机源。
        uint32_t hash = 2166136261u;
        for (int i = 0; i < 14; ++i)
        {
            hash = (hash ^ static_cast<uint32_t>(snapshot[i])) * 16777619u;
        }
        for (auto word : Config)
        {
            hash = (hash ^ static_cast<uint32_t>(word)) * 16777619u;
        }
        snapshot[17] = Fighters[0].Y;
        snapshot[18] = Fighters[1].Y;
        for (const auto& fighter : Fighters)
        {
            hash = (hash ^ static_cast<uint32_t>(fighter.Y)) * 16777619u;
            hash = (hash ^ static_cast<uint32_t>(fighter.VerticalSpeed)) * 16777619u;
        }
        snapshot[16] = static_cast<int32_t>(hash & 0x7fffffffu);
    }
}

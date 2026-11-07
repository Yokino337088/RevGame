#pragma once

#include <array>
#include <cstdint>

namespace RevGameCore
{
    // 对外包全部是小端 int32 字段，内部位置单位为千分格。结构体布局不会跨 ABI。
    constexpr int ConfigWords = 27;
    constexpr int SnapshotWords = 19;
    using Settings = std::array<int32_t, ConfigWords>;
    using Snapshot = std::array<int32_t, SnapshotWords>;

    struct Fighter
    {
        int32_t X = 0;
        int32_t Hp = 0;
        int32_t Energy = 0;
        int32_t Action = 0;
        int32_t Age = 0;
        int32_t Facing = 1;
        int32_t Y = 0;
        int32_t VerticalSpeed = 0;
    };

    class DuelWorld
    {
    public:
        bool Start(const Settings& settings);
        // 入参是同一权威帧两席已封口的输入；0 表示该席无动作。
        bool Advance(int32_t frame, int32_t first, int32_t second, Snapshot& snapshot);

    private:
        bool Valid = false;
        Settings Config{};
        std::array<Fighter, 2> Fighters{};
        int32_t Frame = 0;
        // 0 继续、1/2 对应胜者座位、3 平局。
        int32_t Result = 0;

        void MoveAndStart(Fighter& fighter, int32_t input);
        int32_t ResolveHit(const Fighter& attacker, const Fighter& defender) const;
        void Produce(Snapshot& snapshot) const;
    };
}

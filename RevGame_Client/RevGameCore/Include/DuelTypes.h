#pragma once

#include "FixedPoint.h"
#include <array>
#include <cstddef>
#include <cstdint>

namespace RevGameCore
{
    // 席位是固定身份，也是存储下标。所有战斗阶段以相同的黑、白顺序遍历。
    enum Seat : std::size_t
    {
        Black = 0,
        White = 1
    };

    // 当前决斗仅有两席，用固定数组表达顺序，无需排序或依赖哈希桶顺序。
    constexpr std::array<Seat, 2> SeatOrder{Black, White};

    // 配置和快照按 FIFO 定义的 32 位字段顺序保存，不直接跨边界复制结构体。
    constexpr int ConfigWords = 27;
    constexpr int SnapshotWords = 19;
    using Settings = std::array<int32_t, ConfigWords>;
    using Snapshot = std::array<int32_t, SnapshotWords>;

    // 不限时使用 0。运行和检查点验收共用判定，避免同一帧在恢复时被误当作超时终局。
    constexpr bool ReachedTimeLimit(const Settings& config, int32_t frame)
    {
        return config[3] > 0 && frame >= config[3];
    }

    // 由一局原生世界持有的单席权威状态。
    struct Fighter
    {
        // 水平位置和离地高度均为千分格定点数。
        FixedPoint X;
        // 生命值使用整数点数，除初始化外统一经伤害模块提交。
        int32_t Hp = 0;
        // 可用于出招的能量，单位为整数点。
        int32_t Energy = 0;
        // 0 空闲，1 至 3 对应三类招式。
        int32_t Action = 0;
        // 招式已经推进的逻辑帧数。
        int32_t Age = 0;
        // 1 向右，-1 向左；同坐标时保留上帧方向。
        int32_t Facing = 1;
        FixedPoint Y;
        // 每逻辑帧的垂直位移，落地时归零。
        FixedPoint VerticalSpeed;
    };

    using DuelFighters = std::array<Fighter, SeatOrder.size()>;
    // 下标是发出命中的一席，值为原始命中伤害，免伤时仍保留给表现层。
    using DuelHits = std::array<int32_t, SeatOrder.size()>;

    // 决斗只有两席，对手身份由席位唯一决定。
    constexpr Seat Opponent(Seat seat)
    {
        return seat == Black ? White : Black;
    }
}

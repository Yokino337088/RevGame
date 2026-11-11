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
        // 封帧字低六位，对应角色数组索引 0。
        Black = 0,
        // 封帧字高六位，对应角色数组索引 1。
        White = 1
    };

    // 当前决斗仅有两席，用固定数组表达顺序，无需排序或依赖哈希桶顺序。
    constexpr std::array<Seat, 2> SeatOrder{Black, White};

    // 开局配置包括六种动作、跳跃及连招窗口表。
    constexpr int ConfigWords = 66;
    // 单条表现输出字数。恢复格式另有隐藏状态，字数和快照不同。
    constexpr int SnapshotWords = 25;
    // 宿主开局包中按固定下标排列的规则整数，运行期间不变。
    using Settings = std::array<int32_t, ConfigWords>;
    // 原生输出给宿主的值副本，跨 ABI 逐个整数写小端字节。
    using Snapshot = std::array<int32_t, SnapshotWords>;

    // 六位意图分别携带方向、动作请求和跳跃。派生的 5/6 段普攻只能由内核选出。
    constexpr int InputMask = 63;
    // 单席输入的跳跃位。
    constexpr int JumpInput = 32;
    // 外部输入的最大招式槽。内部动作 5/6 分别为第二、第三段普攻。
    constexpr int MaxActionInput = 4;
    // 包含两种派生普攻的动作总数。
    constexpr int ActionCount = 6;

    // 解析方向后的三位招式槽。0 表示只移动或跳跃。
    constexpr int ActionInput(int input)
    {
        return (input >> 2) & 7;
    }

    // 两个方向同时按下和网络直接注入派生动作都不允许。
    constexpr bool ValidSeatInput(int input)
    {
        return input >= 0 && input <= InputMask && (input & 3) != 3 && ActionInput(input) <= MaxActionInput;
    }

    // 每种动作的伤害、距离、命中年龄、总时长和耗能按五个配置字排列。
    constexpr int AttackOffset(int action)
    {
        return action <= 3 ? 9 + (action - 1) * 5 : 27 + (action - 4) * 5;
    }

    // 取消年龄、受击硬直、启动位移和后继槽掩码按四字排列。
    constexpr int ChainOffset(int action)
    {
        return 42 + (action - 1) * 4;
    }

    // 不限时使用 0。运行和检查点验收共用判定，避免同一帧在恢复时被误当作超时终局。
    constexpr bool ReachedTimeLimit(const Settings& config, int32_t frame)
    {
        return config[3] > 0 && frame >= config[3];
    }

    // 由一局原生世界持有的单席权威状态。
    struct Fighter
    {
        // 横向位置，单位千分格。场地中心为 0，左负右正。
        FixedPoint X;
        // 生命值使用整数点数，除初始化外统一经伤害模块提交。
        int32_t Hp = 0;
        // 可用于出招的能量，单位为整数点。
        int32_t Energy = 0;
        // 0 空闲，1 至 6 表示真实执行的动作，包括普攻派生段。
        int32_t Action = 0;
        // 招式已经推进的逻辑帧数。
        int32_t Age = 0;
        // 1 向右，-1 向左；同坐标时保留上帧方向。
        int32_t Facing = 1;
        // 离地高度，单位千分格。落地时归零，不允许为负。
        FixedPoint Y;
        // 每逻辑帧的垂直位移，落地时归零。
        FixedPoint VerticalSpeed;
        // 仍需跳过的输入帧数。受击后继续推进空中物理。
        int32_t Stun = 0;
        // 当前动作是否实际命中。只有命中才能开放提前取消。
        bool Landed = false;
        // 当前连段已启动的技能槽掩码，防止两种技能循环取消。
        int32_t UsedSkills = 0;
        // 当前连段已确认命中次数，供 HUD 与原生检查点读取。
        int32_t ComboHits = 0;
    };

    // 一局的两席原生状态，所有阶段遍历同一席位顺序。
    using DuelFighters = std::array<Fighter, SeatOrder.size()>;
    // 下标是发出命中的一席，值为原始命中伤害，免伤时仍保留给表现层。
    using DuelHits = std::array<int32_t, SeatOrder.size()>;

    // 决斗只有两席，对手身份由席位唯一决定。
    constexpr Seat Opponent(Seat seat)
    {
        return seat == Black ? White : Black;
    }
}

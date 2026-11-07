#pragma once

#include <array>
#include <cstdint>

namespace RevGameCore
{
    // 对外包全部是小端 int32 字段，内部位置单位为千分格。结构体布局不会跨 ABI。
    // 开局配置的 32 位整数字段数，C# 打包与 C++ 解包必须一致。
    constexpr int ConfigWords = 27;
    // 每帧输出的整数字段数，决定托管端读取缓冲的长度。
    constexpr int SnapshotWords = 19;
    // 按协议顺序排列的整局固定配置。
    using Settings = std::array<int32_t, ConfigWords>;
    // 逻辑帧执行后的显示及对账数据，以值复制给调用方。
    using Snapshot = std::array<int32_t, SnapshotWords>;

    // 由原生世界持有的单席权威状态，只在完整帧中修改。
    struct Fighter
    {
        // 水平位置，以千分格计，受场地左右边界约束。
        int32_t X = 0;
        // 剩余生命，受到伤害最低扣到零。
        int32_t Hp = 0;
        // 可供招式消耗的能量，逐帧回充并限制在配置上限内。
        int32_t Energy = 0;
        // 当前招式槽，0 为空闲，1 至 3 对应三类攻击。
        int32_t Action = 0;
        // 当前招式已执行的逻辑帧数，用于命中和恢复时机。
        int32_t Age = 0;
        // 攻击方向，1 向右、-1 向左。重叠时保留上一帧方向。
        int32_t Facing = 1;
        // 离地高度，单位千分格，0 表示落地。
        int32_t Y = 0;
        // 每逻辑帧的垂直位移量，正值上升，落地清零。
        int32_t VerticalSpeed = 0;
    };

    // 持有一局确定性规则和角色状态，生命周期由原生桥接实例管理。
    class DuelWorld
    {
    public:
        // 初始化两席并复制配置。配置非法或实例已开局时返回 false。
        bool Start(const Settings& settings);
        // 入参是同一权威帧两席已封口的输入；0 表示该席无动作。
        // 只执行下一连续帧并填充输出；校验失败时不改世界。
        bool Advance(int32_t frame, int32_t first, int32_t second, Snapshot& snapshot);

    private:
        // 开局配置是否已通过校验。未开局不允许步进。
        bool Valid = false;
        // 本局经校验的配置副本，运行期间保持固定。
        Settings Config{};
        // 黑方在索引 0，白方在索引 1，和线上封帧顺序一致。
        std::array<Fighter, 2> Fighters{};
        // 最后执行的逻辑帧号。0 为初始状态，不等于宿主已提交上界。
        int32_t Frame = 0;
        // 0 继续、1/2 对应胜者座位、3 平局。
        int32_t Result = 0;

        // 按固定阶段推进跳跃、招式时钟、水平移动及新招式。
        void MoveAndStart(Fighter& fighter, int32_t input);
        // 读取攻击者与目标状态，返回本帧候选伤害；未命中返回 0。
        int32_t ResolveHit(const Fighter& attacker, const Fighter& defender) const;
        // 按协议顺序复制世界快照，并计算原生状态指纹。
        void Produce(Snapshot& snapshot) const;
    };
}

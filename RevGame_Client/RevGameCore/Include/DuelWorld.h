#pragma once

#include "DeterministicRandom.h"
#include "DuelTypes.h"

namespace RevGameCore
{
    // 黑方 FIFO 字的第 5 位保留给完整帧的双席免伤，低五位仍是角色输入。
    constexpr int32_t NoDamageFrameBit = 1 << 5;

    // 持有一局确定性规则和角色状态，生命周期由原生桥接实例管理。
    class DuelWorld
    {
    public:
        // 初始化两席并复制配置。配置非法或实例已开局时返回 false。
        bool Start(const Settings& settings, uint64_t seed);
        // 入参是同一权威帧两席已封口的输入；0 表示该席无动作。
        // 只执行下一连续帧并填充输出；校验失败时不改世界。
        bool Advance(int32_t frame, int32_t first, int32_t second, Snapshot& snapshot);

    private:
        // 开局配置是否已通过校验。未开局不允许步进。
        bool Valid = false;
        // 本局经校验的配置副本，运行期间保持固定。
        Settings Config{};
        // 黑方在索引 0，白方在索引 1，和线上封帧顺序一致。
        DuelFighters Fighters{};
        // 由开局包的种子初始化。本局独占，当前固定招式规则不抽样。
        DeterministicRandom Random;
        // 最后执行的逻辑帧号。0 为初始状态，不等于宿主已提交上界。
        int32_t Frame = 0;
        // 0 继续、1/2 对应胜者座位、3 平局。
        int32_t Result = 0;

        // 按固定阶段推进跳跃、招式时钟、水平移动及新招式。
        void MoveAndStart(Fighter& fighter, int32_t input);
        // 按协议顺序复制世界快照，并计算原生状态指纹。
        void Produce(Snapshot& snapshot) const;
    };
}

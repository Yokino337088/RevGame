// Release 也执行断言，验证双实例回放、缺帧拒绝与整包背压。
#include "GameCoreExport.h"
#include "DuelWorld.h"
#include <array>
#include <cstdlib>
#include <cstdio>

// Release 也执行的断言；失败用非零退出码通知 CTest。
void Check(bool result)
{
    if (!result)
    {
        std::abort();
    }
}

// 以生产端的固定小端布局编测试包，模板参数表示整数项数。
template <std::size_t Size>
auto Encode(const std::array<int32_t, Size>& words)
{
    std::array<uint8_t, Size * 4> packet{};
    for (std::size_t i = 0; i < Size; ++i)
    {
        for (int j = 0; j < 4; ++j)
        {
            packet[i * 4 + j] = static_cast<uint8_t>(static_cast<uint32_t>(words[i]) >> (8 * j));
        }
    }
    return packet;
}

// 检查双实例确定性、缺帧拒绝、跳跃输出、FIFO 背压和短缓冲。
int main()
{
    auto first = GameCore_Create();
    auto second = GameCore_Create();
    auto config = Encode(std::array<int32_t, 29>{2,1,100,4500,140,1200,1700,100,1,12,50,
        9,1020,3,8,0,17,1630,5,13,22,38,2560,8,21,78,270,34,480});
    Check(GameCore_Write(first, config.data(), int(config.size())) == 1);
    Check(GameCore_Write(second, config.data(), int(config.size())) == 1);
    // 在首帧缺失时先投帧 2，拒绝后仍能从帧 1 开始。
    auto gap = Encode(std::array<int32_t, 5>{2,2,2,0,0});
    Check(GameCore_Write(first, gap.data(), int(gap.size())) == -1);
    std::array<uint8_t, 76> left{}, right{};
    for (int frame = 1; frame <= 128; ++frame)
    {
        auto input = Encode(std::array<int32_t, 5>{2,2,frame,frame == 1 ? 18 : frame < 11 ? 2 : 4,frame < 11 ? 1 : 12});
        Check(GameCore_Write(first, input.data(), int(input.size())) == 1);
        Check(GameCore_Write(second, input.data(), int(input.size())) == 1);
    }
    auto excess = Encode(std::array<int32_t, 5>{2,2,129,0,0});
    // 两个环的容量是固定的。第 129 帧遇到背压后保留原包，消费完再提交。
    Check(GameCore_Write(first, excess.data(), int(excess.size())) == 0);
    Check(GameCore_Read(first, left.data(), 1) == -1);
    for (int frame = 1; frame <= 128; ++frame)
    {
        Check(GameCore_Read(first, left.data(), int(left.size())) == int(left.size()));
        Check(GameCore_Read(second, right.data(), int(right.size())) == int(right.size()));
        Check(left == right && left[0] == frame);
        // 字节偏移 68 对应黑方高度的低位；首帧跳跃不能仍然贴地。
        if (frame == 1) { Check(left[68] != 0 || left[69] != 0); }
    }
    Check(GameCore_Write(first, excess.data(), int(excess.size())) == 1);
    GameCore_Destroy(first);
    GameCore_Destroy(second);

    // 双方同时命中时，免伤帧保留两个命中事件；关闭开关后的下一轮攻击正常扣血。
    auto duel = GameCore_Create();
    Check(GameCore_Write(duel, config.data(), int(config.size())) == 1);
    for (int frame = 1; frame <= 23; ++frame)
    {
        const bool approach = frame <= 10;
        const int protectedFrame = frame <= 14 ? RevGameCore::NoDamageFrameBit : 0;
        auto input = Encode(std::array<int32_t, 5>{2,2,frame,(approach ? 2 : 4) | protectedFrame,approach ? 1 : 4});
        Check(GameCore_Write(duel, input.data(), int(input.size())) == 1);
        Check(GameCore_Read(duel, left.data(), int(left.size())) == int(left.size()));
        if (frame == 14 || frame == 23)
        {
            Check(left[56] == 9 && left[60] == 9);
            Check(left[12] == (frame == 14 ? 100 : 91) && left[36] == left[12]);
        }
    }
    GameCore_Destroy(duel);
    std::puts("RevGameCore deterministic FIFO checks passed");
}

// 双向 FIFO 是宿主与确定性世界的唯一数据通道。读写函数不触碰 Unity、网络或墙钟。
#include "GameCoreExport.h"
#include "DuelWorld.h"
#include "DuelProtocol.Generated.h"
#include <array>
#include <cstddef>
#include <new>

namespace
{
    // 每个 FIFO 的固定容量。队列满时保留内容，等待宿主消费后重试。
    constexpr int Capacity = 128;
    // 单条输出消息的字节数，每个整数占 4 字节。
    constexpr int OutputBytes = RevGameCore::SnapshotWords * 4;

    // 一条已接纳的完整帧，以值拷贝入队，不持有托管数组地址。
    struct InputPacket
    {
        // 该整帧的连续编号，首帧为 1。
        int32_t Frame = 0;
        // 黑方位打包意图，0 为明确空操作。
        int32_t First = 0;
        // 白方位打包意图，与黑方一起进入同一帧。
        int32_t Second = 0;
    };

    // 每局一份，由调用方在同一线程拥有，管理世界与双向 FIFO。
    struct Pipe
    {
        // 原生权威世界，仅 Drain 消费完整帧时推进。
        RevGameCore::DuelWorld World;
        // 已接纳但尚未执行的输入环。
        std::array<InputPacket, Capacity> Input{};
        // 已执行但尚未被宿主取走的输出环。
        std::array<RevGameCore::Snapshot, Capacity> Output{};
        // 下一条待执行输入的槽位，而非逻辑帧号。
        int32_t ReadInput = 0;
        // 输入环有效条数，配合读槽计算追加位置。
        int32_t InputCount = 0;
        // 下一条应返回给宿主的结果槽位。
        int32_t ReadOutput = 0;
        // 等待消费的结果条数，满时暂停执行下一帧。
        int32_t OutputCount = 0;
        // 已接纳的最大连续帧号，可领先于世界的已执行帧。
        int32_t LastSealed = 0;
        // 配置通过后才允许接受整帧命令。
        bool Open = false;
        // 原生步进已失败；后续读写明确返回错误，由宿主销毁该实例。
        bool Fault = false;
    };

    // 从已验证长度的消息中读取四字节，不依赖结构体对齐。
    uint32_t ReadWord(const uint8_t* bytes)
    {
        return uint32_t(bytes[0]) | (uint32_t(bytes[1]) << 8) | (uint32_t(bytes[2]) << 16) | (uint32_t(bytes[3]) << 24);
    }

    // 写出固定顺序的小端字节，保留原始整数位模式。
    void WriteWord(uint8_t* bytes, uint32_t word)
    {
        for (int i = 0; i < 4; ++i)
        {
            bytes[i] = static_cast<uint8_t>(word >> (i * 8));
        }
    }

    // 有输出容量才执行下一帧，确保背压下仍保留每条结果。
    void Drain(Pipe& pipe)
    {
        // C++ 根据队列中的完整帧自行推进。输出队列满则停在下一帧前，绝不跳过离散命中。
        while (pipe.InputCount > 0 && pipe.OutputCount < Capacity)
        {
            const auto& input = pipe.Input[pipe.ReadInput];
            auto& output = pipe.Output[(pipe.ReadOutput + pipe.OutputCount) % Capacity];
            if (!pipe.World.Advance(input.Frame, input.First, input.Second, output))
            {
                pipe.Fault = true;
                return;
            }
            pipe.ReadInput = (pipe.ReadInput + 1) % Capacity;
            --pipe.InputCount;
            ++pipe.OutputCount;
        }
    }
}

// 创建独立实例，内存不足时返回空指针，避免异常跨越 ABI。
void* GameCore_Create(void)
{
    return new (std::nothrow) Pipe();
}

// 释放整局状态；托管端通过 SafeHandle 确保只释放一次。
void GameCore_Destroy(void* handle)
{
    delete static_cast<Pipe*>(handle);
}

// 检查配置或完整帧。返回 1 接纳、0 背压、-1 非法，失败时保留原上界。
int32_t GameCore_Write(void* handle, const uint8_t* data, int32_t size)
{
    auto* pipe = static_cast<Pipe*>(handle);
    if (!pipe || !data || size < 8 || pipe->Fault || ReadWord(data) != RevGameCore::WireVersion)
    {
        return -1;
    }
    const auto operation = ReadWord(data + 4);
    if (operation == 1 && size == (4 + RevGameCore::ConfigWords) * 4)
    {
        RevGameCore::Settings settings{};
        for (std::size_t i = 0; i < settings.size(); ++i)
        {
            const auto word = ReadWord(data + 8 + i * 4);
            if (word > INT32_MAX)
            {
                return -1;
            }
            settings[i] = static_cast<int32_t>(word);
        }
        // 末尾两个字分别是种子的低位和高位。按无符号位拼接，允许完整 64 位取值。
        const auto* seedBytes = data + (2 + RevGameCore::ConfigWords) * 4;
        const uint64_t seed = ReadWord(seedBytes) | (static_cast<uint64_t>(ReadWord(seedBytes + 4)) << 32);
        if (!pipe->World.Start(settings, seed))
        {
            return -1;
        }
        pipe->Open = true;
        return 1;
    }
    if (operation != 2 || size != 20 || !pipe->Open || pipe->LastSealed == INT32_MAX)
    {
        return -1;
    }
    const auto frame = ReadWord(data + 8);
    const auto first = ReadWord(data + 12);
    const auto second = ReadWord(data + 16);
    if (frame != static_cast<uint32_t>(pipe->LastSealed + 1) || first > (31u | RevGameCore::NoDamageFrameBit) || second > 31 ||
        (first & 3u) == 3u || (second & 3u) == 3u)
    {
        return -1;
    }
    if (pipe->InputCount == Capacity)
    {
        return 0;
    }
    // 只有全包校验通过，才能把该帧放入 FIFO 并提升连续上界。
    pipe->Input[(pipe->ReadInput + pipe->InputCount) % Capacity] =
        InputPacket{static_cast<int32_t>(frame), static_cast<int32_t>(first), static_cast<int32_t>(second)};
    ++pipe->InputCount;
    ++pipe->LastSealed;
    return 1;
}

// 读取最老输出，返回字节数、0 无结果或 -1 失败。短缓冲不会消费结果。
int32_t GameCore_Read(void* handle, uint8_t* data, int32_t capacity)
{
    auto* pipe = static_cast<Pipe*>(handle);
    if (!pipe || !data || capacity < OutputBytes || pipe->Fault)
    {
        return -1;
    }
    Drain(*pipe);
    if (pipe->Fault)
    {
        return -1;
    }
    if (pipe->OutputCount == 0)
    {
        return 0;
    }
    const auto& result = pipe->Output[pipe->ReadOutput];
    for (std::size_t i = 0; i < result.size(); ++i)
    {
        WriteWord(data + i * 4, static_cast<uint32_t>(result[i]));
    }
    pipe->ReadOutput = (pipe->ReadOutput + 1) % Capacity;
    --pipe->OutputCount;
    return OutputBytes;
}

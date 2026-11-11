// 双向 FIFO 传递完整帧和结果，排空后可用检查点恢复同局实例。桥接不触碰 Unity、网络或墙钟。
#include "GameCoreExport.h"
#include "DuelWorld.h"
#include "DuelProtocol.Generated.h"
#include "WireWords.h"
#include <array>
#include <cstddef>
#include <new>

namespace
{
    using RevGameCore::ReadWord;
    using RevGameCore::WriteWord;
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
        // 该句柄自己的原生世界。权威、副本、预测均通过 Drain 执行，各自拥有独立内存。
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

    // 普通帧输出和恢复基线共用相同字段编码，调用方需提供完整输出缓冲。
    void WriteSnapshot(uint8_t* bytes, const RevGameCore::Snapshot& snapshot)
    {
        for (std::size_t i = 0; i < snapshot.size(); ++i)
        {
            WriteWord(bytes + i * 4, static_cast<uint32_t>(snapshot[i]));
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
    // 开局包一次读完版本、配置、种子后才打开队列。旧局不能用新的开局包覆盖。
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
    if (frame != static_cast<uint32_t>(pipe->LastSealed + 1) ||
        first > (RevGameCore::InputMask | RevGameCore::NoDamageFrameBit) || second > RevGameCore::InputMask ||
        !RevGameCore::ValidSeatInput(static_cast<int>(first) & RevGameCore::InputMask) ||
        !RevGameCore::ValidSeatInput(static_cast<int>(second)))
    {
        return -1;
    }
    if (pipe->InputCount == Capacity)
    {
        // 尚未接纳本包。保留旧的 LastSealed，调用者能用相同帧号安全重试。
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
    WriteSnapshot(data, pipe->Output[pipe->ReadOutput]);
    pipe->ReadOutput = (pipe->ReadOutput + 1) % Capacity;
    --pipe->OutputCount;
    return OutputBytes;
}

// 检查点只包含已执行的世界，拒绝在任何队列有未消费材料时保存。
int32_t GameCore_SaveCheckpoint(void* handle, uint8_t* data, int32_t capacity)
{
    auto* pipe = static_cast<Pipe*>(handle);
    constexpr int32_t bytes = RevGameCore::DuelWorld::CheckpointBytes;
    if (!pipe || !pipe->Open || pipe->Fault || (data ? capacity < bytes : capacity != 0))
    {
        return -1;
    }
    if (pipe->InputCount != 0 || pipe->OutputCount != 0)
    {
        // 已接纳上界与已消费上界必须一致，不能保存一半未执行或未读回的阶段。
        return 0;
    }
    if (data)
    {
        pipe->World.SaveCheckpoint(data);
    }
    return bytes;
}

// 只在完整验证后更新输入游标与基线，确保重试和跨帧接续都以恢复帧为准。
int32_t GameCore_RestoreCheckpoint(void* handle, const uint8_t* data, int32_t size, uint8_t* snapshot, int32_t capacity)
{
    auto* pipe = static_cast<Pipe*>(handle);
    if (!pipe || !pipe->Open || pipe->Fault || !data || size != RevGameCore::DuelWorld::CheckpointBytes ||
        !snapshot || capacity < OutputBytes)
    {
        return -1;
    }
    if (pipe->InputCount != 0 || pipe->OutputCount != 0)
    {
        return 0;
    }
    RevGameCore::Snapshot baseline{};
    if (!pipe->World.RestoreCheckpoint(data, baseline))
    {
        return -1;
    }
    pipe->LastSealed = baseline[0];
    pipe->ReadInput = 0;
    pipe->ReadOutput = 0;
    WriteSnapshot(snapshot, baseline);
    return 1;
}

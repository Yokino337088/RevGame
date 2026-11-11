// Release 也执行断言，验证双实例回放、缺帧拒绝与整包背压。
#include "GameCoreExport.h"
#include "DuelWorld.h"
#include "DuelProtocol.Generated.h"
#include "Fnv1a.h"
#include "WireWords.h"
#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstdio>
#include <limits>

using namespace RevGameCore;

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

// 用独立计算得到的随机黄金值、负数舍入与溢出边界固定数值语义。
void CheckMathAndRandom()
{
    constexpr uint8_t knownBytes[]{'h', 'e', 'l', 'l', 'o'};
    static_assert(Fnv1aBytes(knownBytes, sizeof(knownBytes)) == 0x4f9f2cabu);
    constexpr uint8_t littleEndian[]{0x04, 0x03, 0x02, 0x81};
    static_assert(Fnv1aWord(Fnv1aOffsetBasis, 0x81020304u) == Fnv1aBytes(littleEndian, sizeof(littleEndian)));
    const auto half = FixedPoint::FromRaw(500);
    Check((FixedPoint::FromRaw(-1235) * half).RawValue() == -617);
    const int32_t minimum = std::numeric_limits<int32_t>::min();
    const int32_t maximum = std::numeric_limits<int32_t>::max();
    Check((FixedPoint::FromRaw(maximum) + half).RawValue() == maximum);
    Check((FixedPoint::FromRaw(minimum) - half).RawValue() == minimum);
    Check(FixedPoint::FromRaw(minimum).Abs().RawValue() == maximum);
    Check(FixedPoint::FromInt(maximum).RawValue() == maximum);

    DeterministicRandom first(42), replay(42), separate(43);
    const auto initial = first.StateWords();
    Check(initial == replay.StateWords() && initial != separate.StateWords());
    Check(first.NextBelow(0) == 0 && first.StateWords() == initial);
    for (const uint64_t expected : {0xaf1f56fc41a4d2d2ULL, 0xbd496f01ee605cebULL, 0x8c8b2271e69fdbf6ULL})
    {
        Check(first.NextUInt64() == expected && replay.NextUInt64() == expected);
    }
    for (int i = 0; i < 1024; ++i)
    {
        const uint32_t value = first.NextBelow(0x80000001u);
        Check(value < 0x80000001u && value == replay.NextBelow(0x80000001u));
    }
    Check(first.StateWords() == replay.StateWords() && separate.StateWords() != first.StateWords());
    // 隐藏随机状态恢复后，后续随机序列也必须完全相同。全零恢复失败不能改变序列。
    Check(separate.RestoreState(first.StateWords()));
    Check(!separate.RestoreState({0, 0}));
    Check(first.NextUInt64() == separate.NextUInt64());
}

// 检查点、表现快照和开局包的尺寸来自当前原生协议。
using StartWords = std::array<int32_t, ConfigWords + 4>;
using Checkpoint = std::array<uint8_t, DuelWorld::CheckpointBytes>;
using DisplayBytes = std::array<uint8_t, SnapshotWords * 4>;

// 为篡改后的测试包重算校验值，用于覆盖语义校验，而非只测文件损坏。
void Recheck(Checkpoint& checkpoint)
{
    uint32_t hash = 2166136261u;
    for (std::size_t i = 0; i < checkpoint.size() - 4; ++i)
    {
        hash = (hash ^ checkpoint[i]) * 16777619u;
    }
    WriteWord(checkpoint.data() + checkpoint.size() - 4, hash);
}

// 固定输入覆盖空中恢复、互击、命中帧无旧事件、移动、锁帧和双实例长后缀。
void CheckCheckpoint(const StartWords& settings)
{
    auto source = GameCore_Create();
    auto target = GameCore_Create();
    auto alien = GameCore_Create();
    const auto start = Encode(settings);
    Check(GameCore_SaveCheckpoint(source, nullptr, 0) == -1);
    Check(GameCore_Write(source, start.data(), int(start.size())) == 1);
    Check(GameCore_Write(target, start.data(), int(start.size())) == 1);

    Checkpoint initial{}, air{}, saved{};
    DisplayBytes first{}, second{}, unchanged{};
    unchanged.fill(0xa5);
    Check(GameCore_SaveCheckpoint(source, nullptr, 0) == int(initial.size()));
    Check(GameCore_SaveCheckpoint(source, initial.data(), int(initial.size()) - 1) == -1);
    Check(GameCore_SaveCheckpoint(source, initial.data(), int(initial.size())) == int(initial.size()));
    Check(GameCore_RestoreCheckpoint(alien, initial.data(), int(initial.size()), first.data(), int(first.size())) == -1);
    Check(GameCore_RestoreCheckpoint(target, initial.data(), int(initial.size()), first.data(), int(first.size())) == 1);
    Check(ReadWord(first.data()) == 0);
    // v7 开局指纹锁定双席距离配置、高位非零的种子和负坐标。
    Check(ReadWord(first.data() + 64) == 0x7fcdb86cu);

    auto foreign = settings;
    ++foreign.back();
    auto foreignPacket = Encode(foreign);
    Check(GameCore_Write(alien, foreignPacket.data(), int(foreignPacket.size())) == 1);
    Check(GameCore_RestoreCheckpoint(alien, initial.data(), int(initial.size()), first.data(), int(first.size())) == -1);
    GameCore_Destroy(alien);
    alien = GameCore_Create();
    foreign = settings;
    ++foreign[2]; // 保持种子相同，改变初始生命配置。
    foreignPacket = Encode(foreign);
    Check(GameCore_Write(alien, foreignPacket.data(), int(foreignPacket.size())) == 1);
    Check(GameCore_RestoreCheckpoint(alien, initial.data(), int(initial.size()), first.data(), int(first.size())) == -1);
    GameCore_Destroy(alien);

    for (int frame = 1; frame <= 310; ++frame)
    {
        const int firstIntent = frame == 1 ? (JumpInput | 2) : frame <= 10 ? 2 : frame == 20 ? 4 : 0;
        const int secondIntent = frame <= 10 ? 1 : frame == 20 ? 4 : 0;
        const auto packet = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, firstIntent, secondIntent});
        Check(GameCore_Write(source, packet.data(), int(packet.size())) == 1);
        if (frame == 4)
        {
            second = unchanged;
            Check(GameCore_SaveCheckpoint(source, nullptr, 0) == 0);
            Check(GameCore_RestoreCheckpoint(source, initial.data(), int(initial.size()), second.data(), int(second.size())) == 0);
            Check(second == unchanged);
        }
        Check(GameCore_Read(source, first.data(), int(first.size())) == int(first.size()));
        if (frame == 4)
        {
            Check(ReadSignedWord(first.data() + 68) > 0);
            Check(GameCore_SaveCheckpoint(source, air.data(), int(air.size())) == int(air.size()));

            // 错帧、截断、短输出和多个语义错误都不得移动目标游标或覆盖输出。
            second = unchanged;
            auto bad = air;
            bad[39] ^= 1;
            Check(GameCore_RestoreCheckpoint(target, bad.data(), int(bad.size()), second.data(), int(second.size())) == -1);
            Check(GameCore_RestoreCheckpoint(target, air.data(), int(air.size()) - 1, second.data(), int(second.size())) == -1);
            Check(GameCore_RestoreCheckpoint(target, air.data(), int(air.size()), second.data(), int(second.size()) - 1) == -1);
            for (const int offset : {4, 8, 12, 16, (7 + ConfigWords) * 4 + 12, (7 + ConfigWords) * 4 + 28})
            {
                bad = air;
                WriteWord(bad.data() + offset, 0x7fffffffu);
                Recheck(bad);
                Check(GameCore_RestoreCheckpoint(target, bad.data(), int(bad.size()), second.data(), int(second.size())) == -1);
            }
            bad = air;
            for (int offset = DuelWorld::CheckpointBytes - 20; offset < DuelWorld::CheckpointBytes - 4; offset += 4)
            {
                WriteWord(bad.data() + offset, 0);
            }
            Recheck(bad);
            Check(GameCore_RestoreCheckpoint(target, bad.data(), int(bad.size()), second.data(), int(second.size())) == -1);
            Check(second == unchanged);
            Check(GameCore_SaveCheckpoint(target, saved.data(), int(saved.size())) == int(saved.size()) && saved == initial);

            // 改写一个有效的隐藏随机状态后，恢复必须原样保存，并生成不同状态指纹。
            bad = air;
            bad[DuelWorld::CheckpointBytes - 20] ^= 1;
            Recheck(bad);
            Check(GameCore_RestoreCheckpoint(target, bad.data(), int(bad.size()), second.data(), int(second.size())) == 1);
            Check(ReadWord(second.data() + 64) != ReadWord(first.data() + 64));
            Check(GameCore_SaveCheckpoint(target, saved.data(), int(saved.size())) == int(saved.size()) && saved == bad);
            Check(GameCore_RestoreCheckpoint(target, air.data(), int(air.size()), second.data(), int(second.size())) == 1);
            Check(first == second);
            Check(GameCore_Write(target, packet.data(), int(packet.size())) == -1);
        }
        else if (frame > 4)
        {
            Check(GameCore_Write(target, packet.data(), int(packet.size())) == 1);
            Check(GameCore_Read(target, second.data(), int(second.size())) == int(second.size()));
            Check(first == second);
        }
        if (frame == 23)
        {
            Check(ReadWord(first.data() + 56) == 9 && ReadWord(first.data() + 60) == 9);
            Check(GameCore_SaveCheckpoint(source, saved.data(), int(saved.size())) == int(saved.size()));
            Check(GameCore_RestoreCheckpoint(target, saved.data(), int(saved.size()), second.data(), int(second.size())) == 1);
            WriteWord(first.data() + 56, 0);
            WriteWord(first.data() + 60, 0);
            Check(first == second);
        }
    }

    // Read 会将两个输入都执行到输出队列。只读出第一条时，仍禁止检查点覆盖第二条。
    for (const int frame : {311, 312})
    {
        const auto packet = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, 0, 0});
        Check(GameCore_Write(target, packet.data(), int(packet.size())) == 1);
    }
    Check(GameCore_Read(target, second.data(), int(second.size())) == int(second.size()));
    Check(ReadWord(second.data()) == 311 && GameCore_SaveCheckpoint(target, nullptr, 0) == 0);
    Check(GameCore_RestoreCheckpoint(target, air.data(), int(air.size()), second.data(), int(second.size())) == 0);
    Check(ReadWord(second.data()) == 311);
    Check(GameCore_Read(target, second.data(), int(second.size())) == int(second.size()));
    Check(ReadWord(second.data()) == 312);
    Check(GameCore_RestoreCheckpoint(target, air.data(), int(air.size()), second.data(), int(second.size())) == 1);
    Check(ReadWord(second.data()) == 4);
    Check(GameCore_Read(target, second.data(), int(second.size())) == 0);
    GameCore_Destroy(source);
    GameCore_Destroy(target);
}

// 超过旧的 60 秒上限时仍可恢复同一局，继续靠实际伤害决出胜负。
void CheckUnlimitedDuel(const StartWords& settings)
{
    auto config = settings;
    config[2] = 9; // 用真实普攻击倒对方，便于只测试一次命中。
    config[5] = 0; // 跳过两个协议头字后，第四个配置字是可选的决斗帧上限。
    const auto start = Encode(config);
    auto source = GameCore_Create();
    auto recovered = GameCore_Create();
    Check(GameCore_Write(source, start.data(), int(start.size())) == 1);
    Check(GameCore_Write(recovered, start.data(), int(start.size())) == 1);

    DisplayBytes first{}, second{};
    constexpr int32_t OldTimeLimit = 1200;
    constexpr int32_t SaveFrame = OldTimeLimit + 1;
    constexpr int32_t AttackFrame = SaveFrame + 11;
    constexpr int32_t HitFrame = AttackFrame + 3;
    for (int32_t frame = 1; frame <= HitFrame; ++frame)
    {
        const bool approach = frame > SaveFrame && frame < AttackFrame;
        const int firstIntent = approach ? 2 : frame == AttackFrame ? 4 : 0;
        const auto packet = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, firstIntent, approach ? 1 : 0});
        Check(GameCore_Write(source, packet.data(), int(packet.size())) == 1);
        Check(GameCore_Read(source, first.data(), int(first.size())) == int(first.size()));
        Check(ReadWord(first.data()) == static_cast<uint32_t>(frame));
        Check(ReadWord(first.data() + 4) == (frame == HitFrame ? 1u : 0u));
        if (frame == SaveFrame)
        {
            Checkpoint saved{};
            Check(GameCore_SaveCheckpoint(source, saved.data(), int(saved.size())) == int(saved.size()));
            Check(GameCore_RestoreCheckpoint(recovered, saved.data(), int(saved.size()), second.data(), int(second.size())) == 1);
            Check(first == second);
        }
        else if (frame > SaveFrame)
        {
            Check(GameCore_Write(recovered, packet.data(), int(packet.size())) == 1);
            Check(GameCore_Read(recovered, second.data(), int(second.size())) == int(second.size()));
            Check(first == second);
        }
    }
    Check(ReadWord(first.data() + 12) == 9 && ReadWord(first.data() + 36) == 0);
    Check(ReadWord(first.data() + 56) == 9 && ReadWord(first.data() + 60) == 0);
    GameCore_Destroy(source);
    GameCore_Destroy(recovered);
}

// 检查双实例确定性、缺帧拒绝、跳跃输出、FIFO 背压和短缓冲。
// 在每项主动招式的距离上沿和外侧各打一轮。开招位移计入起始站位，命中仍由真实 FIFO 结算。
void CheckSeatReach(const StartWords& settings)
{
    for (const Seat seat : SeatOrder)
    {
        for (int action = 1; action <= MaxActionInput; ++action)
        {
            const int attack = AttackOffset(action);
            const int reach = settings[2 + HitRangeOffset(seat, action)];
            const int lunge = settings[2 + ChainOffset(action) + 2];
            const int startFrame = settings[2 + attack + 4] + 1;
            const int hitFrame = startFrame + settings[2 + attack + 2];
            for (const int beyond : {0, 1})
            {
                // 双方在场地中心对称出生。多放一个单位的出生间隔，双方之间就多两个单位。
                Check((reach + lunge) % 2 == 0);
                auto config = settings;
                config[2 + 4] = (reach + lunge) / 2 + beyond;
                auto core = GameCore_Create();
                const auto start = Encode(config);
                Check(GameCore_Write(core, start.data(), int(start.size())) == 1);
                DisplayBytes frameOutput{};
                for (int frame = 1; frame <= hitFrame; ++frame)
                {
                    const int intent = frame == startFrame ? action << 2 : 0;
                    const auto packet = Encode(std::array<int32_t, 5>{WireVersion, 2, frame,
                        seat == Black ? intent : 0, seat == White ? intent : 0});
                    Check(GameCore_Write(core, packet.data(), int(packet.size())) == 1);
                    Check(GameCore_Read(core, frameOutput.data(), int(frameOutput.size())) == int(frameOutput.size()));
                }
                const auto damage = ReadSignedWord(frameOutput.data() + (14 + seat) * 4);
                Check(damage == (beyond == 0 ? config[2 + attack] : 0));
                GameCore_Destroy(core);
            }
        }
    }
}

// 同帧给两席起跳输入，逐帧对比旧重力和初速。整段轨迹的高度翻倍，落地帧一致。
void CheckDoubleJumpHeight(const StartWords& settings)
{
    auto old = settings;
    old[2 + 24] = 270;
    old[2 + 25] = 34;
    auto baseline = GameCore_Create();
    auto current = GameCore_Create();
    const auto baselineStart = Encode(old);
    const auto currentStart = Encode(settings);
    Check(GameCore_Write(baseline, baselineStart.data(), int(baselineStart.size())) == 1);
    Check(GameCore_Write(current, currentStart.data(), int(currentStart.size())) == 1);
    DisplayBytes before{}, after{};
    int peak = 0;
    for (int frame = 1; frame <= 24; ++frame)
    {
        const int jump = frame == 1 ? JumpInput : 0;
        const auto packet = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, jump, jump});
        Check(GameCore_Write(baseline, packet.data(), int(packet.size())) == 1);
        Check(GameCore_Write(current, packet.data(), int(packet.size())) == 1);
        Check(GameCore_Read(baseline, before.data(), int(before.size())) == int(before.size()));
        Check(GameCore_Read(current, after.data(), int(after.size())) == int(after.size()));
        for (const Seat seat : SeatOrder)
        {
            const auto offset = (17 + seat) * 4;
            const int height = ReadSignedWord(after.data() + offset);
            Check(height == ReadSignedWord(before.data() + offset) * 2);
            peak = std::max(peak, height);
        }
    }
    Check(peak == 2416 && ReadSignedWord(after.data() + 68) == 0 && ReadSignedWord(after.data() + 72) == 0);
    GameCore_Destroy(baseline);
    GameCore_Destroy(current);
}

int main()
{
    CheckMathAndRandom();
    auto first = GameCore_Create();
    auto second = GameCore_Create();
    StartWords settings{WireVersion, 1};
    std::copy(DefaultDuelSettings.begin(), DefaultDuelSettings.end(), settings.begin() + 2);
    settings[5] = 1200;
    settings[ConfigWords + 2] = 42;
    settings[ConfigWords + 3] = -123;
    CheckSeatReach(settings);
    CheckDoubleJumpHeight(settings);
    CheckCheckpoint(settings);
    CheckUnlimitedDuel(settings);
    const auto config = Encode(settings);
    auto invalidSettings = settings;
    invalidSettings[0] = WireVersion - 1;
    auto invalid = Encode(invalidSettings);
    Check(GameCore_Write(first, invalid.data(), int(invalid.size())) == -1);
    invalidSettings = settings;
    invalidSettings[10] = 66;
    invalid = Encode(invalidSettings);
    Check(GameCore_Write(first, invalid.data(), int(invalid.size())) == -1);
    Check(GameCore_Write(first, config.data(), int(config.size())) == 1);
    Check(GameCore_Write(second, config.data(), int(config.size())) == 1);
    // 在首帧缺失时先投帧 2，拒绝后仍能从帧 1 开始。
    auto gap = Encode(std::array<int32_t, 5>{WireVersion, 2, 2, 0, 0});
    Check(GameCore_Write(first, gap.data(), int(gap.size())) == -1);
    DisplayBytes left{}, right{};
    for (int frame = 1; frame <= 128; ++frame)
    {
        auto input = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, frame == 1 ? (JumpInput | 2) : frame < 11 ? 2 : 4, frame < 11 ? 1 : 12});
        Check(GameCore_Write(first, input.data(), int(input.size())) == 1);
        Check(GameCore_Write(second, input.data(), int(input.size())) == 1);
    }
    auto excess = Encode(std::array<int32_t, 5>{WireVersion, 2, 129, 0, 0});
    // 两个环的容量是固定的。第 129 帧遇到背压后保留原包，消费完再提交。
    Check(GameCore_Write(first, excess.data(), int(excess.size())) == 0);
    Check(GameCore_Read(first, left.data(), 1) == -1);
    for (int frame = 1; frame <= 128; ++frame)
    {
        Check(GameCore_Read(first, left.data(), int(left.size())) == int(left.size()));
        Check(GameCore_Read(second, right.data(), int(right.size())) == int(right.size()));
        Check(left == right && left[0] == frame);
        // 字节偏移 68 对应黑方高度的低位；首帧跳跃不能仍然贴地。
        if (frame == 1)
        {
            Check(left[68] != 0 || left[69] != 0);
            // 额外覆盖第一帧黑方起跳后的高度和隐藏垂直速度。
            Check(ReadWord(left.data() + 64) == 0x553d3d3cu);
        }
    }
    Check(GameCore_Write(first, excess.data(), int(excess.size())) == 1);
    GameCore_Destroy(first);
    GameCore_Destroy(second);

    // 双方同时命中时，免伤帧保留两个命中事件；关闭开关后的下一轮攻击正常扣血。
    auto duel = GameCore_Create();
    auto mirror = GameCore_Create();
    auto otherSettings = settings;
    ++otherSettings.back();
    const auto otherConfig = Encode(otherSettings);
    Check(GameCore_Write(mirror, otherConfig.data(), int(otherConfig.size())) == 1);
    Check(GameCore_Write(duel, config.data(), int(config.size())) == 1);
    for (int frame = 1; frame <= 23; ++frame)
    {
        const bool approach = frame <= 10;
        const int protectedFrame = frame <= 14 ? RevGameCore::NoDamageFrameBit : 0;
        auto input = Encode(std::array<int32_t, 5>{WireVersion, 2, frame, (approach ? 2 : (frame == 11 || frame == 20 ? 4 : 0)) | protectedFrame, approach ? 1 : (frame == 11 || frame == 20 ? 4 : 0)});
        Check(GameCore_Write(duel, input.data(), int(input.size())) == 1);
        Check(GameCore_Read(duel, left.data(), int(left.size())) == int(left.size()));
        Check(GameCore_Write(mirror, input.data(), int(input.size())) == 1);
        Check(GameCore_Read(mirror, right.data(), int(right.size())) == int(right.size()));
        // 同样的战斗动作仍然产生相同画面，但种子不同必须被指纹识别。
        Check(left != right);
        for (std::size_t i = 0; i < left.size(); ++i)
        {
            if (i < 64 || i >= 68)
            {
                Check(left[i] == right[i]);
            }
        }
        if (frame == 14 || frame == 23)
        {
            Check(left[56] == 9 && left[60] == 9);
            Check(left[12] == (frame == 14 ? 100 : 91) && left[36] == left[12]);
        }
    }
    GameCore_Destroy(duel);
    GameCore_Destroy(mirror);
    std::puts("RevGameCore deterministic FIFO checks passed");
}

#include "DuelWorld.h"
#include "DuelProtocol.Generated.h"
#include "Fnv1a.h"
#include "WireWords.h"

namespace RevGameCore
{
    namespace
    {
        // 格式 v1 依次写标记、版本、逻辑帧、胜负、开局种子、配置、黑白完整状态、随机状态和校验和。
        constexpr uint32_t CheckpointMagic = 0x50434752u; // 小端字节 RGCP
        constexpr uint32_t CheckpointSchema = 1;
        constexpr int ConfigOffset = 7 * 4;
        constexpr int FighterBytes = 8 * 4;
        constexpr int FightersOffset = ConfigOffset + ConfigWords * 4;
        constexpr int RandomOffset = FightersOffset + static_cast<int>(SeatOrder.size()) * FighterBytes;
        constexpr int ChecksumOffset = RandomOffset + 16;
        static_assert(ChecksumOffset + 4 == DuelWorld::CheckpointBytes);

        // 64 位值拆成两个小端字，不读写平台结构体布局。
        void WriteDoubleWord(uint8_t* data, uint64_t value)
        {
            WriteWord(data, static_cast<uint32_t>(value));
            WriteWord(data + 4, static_cast<uint32_t>(value >> 32));
        }

        uint64_t ReadDoubleWord(const uint8_t* data)
        {
            return ReadWord(data) | (static_cast<uint64_t>(ReadWord(data + 4)) << 32);
        }

        // 校验序列化字节，检测传输或保存中的损坏。权限校验仍由持有权威检查点的宿主负责。
        uint32_t Checksum(const uint8_t* data)
        {
            return Fnv1aBytes(data, ChecksumOffset);
        }

        // 使用同一顺序读写八个字段。第八个字段是影响未来落点的隐藏垂直速度。
        void WriteFighter(uint8_t* data, const Fighter& fighter)
        {
            const std::array<int32_t, 8> values{
                fighter.X.RawValue(), fighter.Hp,     fighter.Energy,       fighter.Action,
                fighter.Age,          fighter.Facing, fighter.Y.RawValue(), fighter.VerticalSpeed.RawValue()};
            for (std::size_t i = 0; i < values.size(); ++i)
            {
                WriteWord(data + i * 4, static_cast<uint32_t>(values[i]));
            }
        }

        Fighter ReadFighter(const uint8_t* data)
        {
            return Fighter{FixedPoint::FromRaw(ReadSignedWord(data)),
                           ReadSignedWord(data + 4),
                           ReadSignedWord(data + 8),
                           ReadSignedWord(data + 12),
                           ReadSignedWord(data + 16),
                           ReadSignedWord(data + 20),
                           FixedPoint::FromRaw(ReadSignedWord(data + 24)),
                           FixedPoint::FromRaw(ReadSignedWord(data + 28))};
        }

        // 原生跳跃逐帧先移动再减重力。由速度反求已经走了几步，再核对高度是否可能。
        bool ValidJump(const Fighter& fighter, const Settings& config, int32_t frame)
        {
            const int32_t height = fighter.Y.RawValue();
            const int32_t velocity = fighter.VerticalSpeed.RawValue();
            const int32_t launch = config[24];
            const int32_t gravity = config[25];
            if (height == 0)
            {
                return velocity == 0;
            }
            if (height < 0 || velocity > launch - gravity || velocity < -launch - gravity)
            {
                return false;
            }
            const int32_t difference = launch - velocity;
            const int64_t steps = difference / gravity;
            return difference % gravity == 0 && steps <= frame &&
                   height == steps * (2 * launch - (steps - 1) * gravity) / 2;
        }

        // 在临时对象里核对数值和招式边界，防止非法状态进入下一帧的数组下标或算术。
        bool ValidFighter(const Fighter& fighter, const Settings& config, Seat seat, int32_t frame)
        {
            const int32_t x = fighter.X.RawValue();
            if (x < -config[1] || x > config[1] || fighter.Hp < 0 || fighter.Hp > config[0] || fighter.Energy < 0 ||
                fighter.Energy > config[5] || fighter.Action < 0 || fighter.Action > 3 || fighter.Age < 0 ||
                (fighter.Facing != 1 && fighter.Facing != -1) || !ValidJump(fighter, config, frame))
            {
                return false;
            }
            if (frame == 0)
            {
                const int32_t facing = seat == Black ? 1 : -1;
                return x == -facing * config[4] && fighter.Facing == facing && fighter.Hp == config[0] &&
                       fighter.Energy == 0 && fighter.Action == 0 && fighter.Age == 0;
            }
            if (fighter.Action == 0)
            {
                return fighter.Age == 0;
            }
            return fighter.Age < frame && fighter.Age <= config[12 + (fighter.Action - 1) * 5];
        }

        // 两席相等时保留前一帧的朝向。胜负必须符合当前血量与逻辑帧上限。
        bool ValidOutcome(const DuelFighters& fighters, const Settings& config, int32_t frame, int32_t result)
        {
            const auto& black = fighters[Black];
            const auto& white = fighters[White];
            if (black.Facing != -white.Facing ||
                (!(black.X == white.X) && black.Facing != (black.X < white.X ? 1 : -1)))
            {
                return false;
            }
            const bool finished = black.Hp == 0 || white.Hp == 0 || ReachedTimeLimit(config, frame);
            const int32_t expected = !finished ? 0 : black.Hp == white.Hp ? 3 : black.Hp > white.Hp ? 1 : 2;
            return result == expected;
        }
    }

    void DuelWorld::SaveCheckpoint(uint8_t* data) const
    {
        WriteWord(data, CheckpointMagic);
        WriteWord(data + 4, CheckpointSchema);
        WriteWord(data + 8, WireVersion);
        WriteWord(data + 12, static_cast<uint32_t>(Frame));
        WriteWord(data + 16, static_cast<uint32_t>(Result));
        WriteDoubleWord(data + 20, InitialSeed);
        for (std::size_t i = 0; i < Config.size(); ++i)
        {
            WriteWord(data + ConfigOffset + i * 4, static_cast<uint32_t>(Config[i]));
        }
        for (const Seat seat : SeatOrder)
        {
            WriteFighter(data + FightersOffset + seat * FighterBytes, Fighters[seat]);
        }
        const auto random = Random.StateWords();
        WriteDoubleWord(data + RandomOffset, random[0]);
        WriteDoubleWord(data + RandomOffset + 8, random[1]);
        WriteWord(data + ChecksumOffset, Checksum(data));
    }

    bool DuelWorld::RestoreCheckpoint(const uint8_t* data, Snapshot& baseline)
    {
        if (!Valid || ReadWord(data) != CheckpointMagic || ReadWord(data + 4) != CheckpointSchema ||
            ReadWord(data + 8) != WireVersion || ReadDoubleWord(data + 20) != InitialSeed ||
            ReadWord(data + ChecksumOffset) != Checksum(data))
        {
            return false;
        }
        for (std::size_t i = 0; i < Config.size(); ++i)
        {
            if (ReadSignedWord(data + ConfigOffset + i * 4) != Config[i])
            {
                return false;
            }
        }
        const int32_t frame = ReadSignedWord(data + 12);
        const int32_t result = ReadSignedWord(data + 16);
        if (frame < 0)
        {
            return false;
        }
        DuelFighters fighters{};
        for (const Seat seat : SeatOrder)
        {
            fighters[seat] = ReadFighter(data + FightersOffset + seat * FighterBytes);
            if (!ValidFighter(fighters[seat], Config, seat, frame))
            {
                return false;
            }
        }
        DeterministicRandom random(InitialSeed);
        const std::array<uint64_t, 2> randomWords{ReadDoubleWord(data + RandomOffset),
                                                  ReadDoubleWord(data + RandomOffset + 8)};
        if (!ValidOutcome(fighters, Config, frame, result) || (frame == 0 && randomWords != random.StateWords()) ||
            !random.RestoreState(randomWords))
        {
            return false;
        }

        // 所有检查都通过才替换当前状态。基线不附带旧命中事件，恢复后只消费后续的新命中。
        Fighters = fighters;
        Random = random;
        Frame = frame;
        Result = result;
        baseline = Snapshot{};
        Produce(baseline);
        return true;
    }
}

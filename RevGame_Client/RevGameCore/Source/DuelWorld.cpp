// 与 MiniGameCore 的逻辑分段一致：先接纳两席意图，再结算命中，最后输出只读帧。
#include "DuelWorld.h"
#include "DuelDamage.h"
#include "DuelProtocol.Generated.h"
#include "Fnv1a.h"
#include <algorithm>
#include <limits>

namespace RevGameCore
{
    // 先校验配置范围，再初始化角色，保证后续定点整数运算有界。
    bool DuelWorld::Start(const Settings& settings, uint64_t seed)
    {
        const int32_t frameLimit = settings[3];
        if (Valid || settings[0] < 1 || settings[0] > 10000 || settings[1] < 1000 || settings[1] > 100000 ||
            settings[2] < 1 || settings[2] > 1000 || (frameLimit != 0 && (frameLimit < 10 || frameLimit > 10000)) ||
            settings[4] < 1 || settings[4] >= settings[1] || settings[5] < 1 || settings[5] > 1000 ||
            settings[6] < 0 || settings[6] > settings[5] || settings[7] < 0 || settings[7] > settings[5] ||
            settings[8] != FrameMilliseconds)
        {
            return false;
        }
        // 尾部三项是跳跃初速、每帧重力、可命中的垂直间距。
        if (settings[24] < 1 || settings[24] > 600 || settings[25] < 1 || settings[25] > 200 ||
            settings[26] < 1 || settings[26] > 4000)
        {
            return false;
        }
        // 三个招式各五个配置字。先验证命中帧、持续时长和耗能关系，避免错误下标进入战斗。
        for (int offset = 9; offset < 24; offset += 5)
        {
            if (settings[offset] < 1 || settings[offset] > 10000 || settings[offset + 1] < 1 ||
                settings[offset + 1] > settings[1] * 2 || settings[offset + 2] < 1 ||
                settings[offset + 3] < settings[offset + 2] || settings[offset + 3] > 600 ||
                settings[offset + 4] < 0 || settings[offset + 4] > settings[5])
            {
                return false;
            }
        }
        Config = settings;
        InitialSeed = seed;
        Random = DeterministicRandom(seed);
        for (const Seat seat : SeatOrder)
        {
            Fighter& fighter = Fighters[seat];
            fighter.Facing = seat == Black ? 1 : -1;
            fighter.X = FixedPoint::FromRaw(-fighter.Facing * Config[4]);
            fighter.Hp = Config[0];
        }
        Valid = true;
        return true;
    }

    // 只处理单个座位的意图与时钟。两席使用同一规则，伤害统一延后结算。
    void DuelWorld::MoveAndStart(Fighter& fighter, int32_t input)
    {
        // 跳跃用定步长整数积分。空中也推进重力，攻击硬直不会让角色悬停。
        const FixedPoint ground;
        if (fighter.Y == ground && fighter.Action == 0 && (input & 16) != 0)
        {
            fighter.VerticalSpeed = FixedPoint::FromRaw(Config[24]);
        }
        if (ground < fighter.Y || ground < fighter.VerticalSpeed)
        {
            fighter.Y = std::max(ground, fighter.Y + fighter.VerticalSpeed);
            fighter.VerticalSpeed = fighter.Y == ground ? ground : fighter.VerticalSpeed - FixedPoint::FromRaw(Config[25]);
        }
        if (fighter.Action != 0)
        {
            // 年龄先增长，再决定这一帧是否仍在硬直。到恢复边界可以结束旧招并接纳新招。
            int offset = 9 + (fighter.Action - 1) * 5;
            if (++fighter.Age <= Config[offset + 3])
            {
                return;
            }
            fighter.Action = 0;
            fighter.Age = 0;
        }
        // 位 0/1 是左右，位 2～3 是单一技能槽。无效方向组合在桥接处拒绝。
        const int direction = (input & 1) != 0 ? -1 : (input & 2) != 0 ? 1 : 0;
        const auto delta = FixedPoint::FromInt(direction) * FixedPoint::FromRaw(Config[2]);
        // 地图边缘按固定点闭区间截断，沙盒使用同一规则，所以预测不会穿出场地。
        fighter.X = std::clamp(fighter.X + delta, FixedPoint::FromRaw(-Config[1]), FixedPoint::FromRaw(Config[1]));
        const int action = (input >> 2) & 3;
        if (action != 0)
        {
            const int cost = Config[9 + (action - 1) * 5 + 4];
            if (fighter.Energy >= cost)
            {
                // 年龄从 0 开始，命中帧稍后统一判断。能量不足时维持空闲且不扣资源。
                fighter.Action = action;
                fighter.Age = 0;
                fighter.Energy -= cost;
            }
        }
    }

    // 先验整帧再推进世界。胜负确定后，继续接收连续帧但冻结战斗状态。
    bool DuelWorld::Advance(int32_t frame, int32_t first, int32_t second, Snapshot& snapshot)
    {
        if (!Valid || Frame == std::numeric_limits<int32_t>::max() || frame != Frame + 1 ||
            first < 0 || first > (31 | NoDamageFrameBit) || second < 0 || second > 31 ||
            (first & 3) == 3 || (second & 3) == 3)
        {
            return false;
        }
        Frame = frame;
        DuelHits hits{};
        if (Result == 0)
        {
            const std::array<int32_t, SeatOrder.size()> inputs{first, second};
            for (const Seat seat : SeatOrder)
            {
                MoveAndStart(Fighters[seat], inputs[seat]);
            }
            // 两席都先完成移动，再重算朝向。交叉换边时按同一帧最终位置处理，避免先遍历的座位抢判。
            Fighter& black = Fighters[Black];
            Fighter& white = Fighters[White];
            if (!(black.X == white.X))
            {
                black.Facing = black.X < white.X ? 1 : -1;
                white.Facing = -black.Facing;
            }
            // 命中模块先收集双方伤害再统一扣血，之后统一加能量和判胜。
            hits = ResolveDuelDamage(Fighters, Config, (first & NoDamageFrameBit) != 0);
            for (const Seat seat : SeatOrder)
            {
                Fighter& fighter = Fighters[seat];
                fighter.Energy = std::min(Config[5], fighter.Energy + Config[6] + (hits[seat] ? Config[7] : 0));
            }
            if (black.Hp == 0 || white.Hp == 0 || ReachedTimeLimit(Config, Frame))
            {
                Result = black.Hp == white.Hp ? 3 : black.Hp > white.Hp ? 1 : 2;
            }
        }
        Produce(snapshot);
        // 命中是这一帧的离散输出。检查点恢复的基线不重新发出旧命中。
        snapshot[14] = hits[Black];
        snapshot[15] = hits[White];
        return true;
    }

    // 把快照字段按固定次序写出；插值、粒子等表现数据不进入指纹。
    void DuelWorld::Produce(Snapshot& snapshot) const
    {
        snapshot[0] = Frame;
        snapshot[1] = Result;
        for (const Seat seat : SeatOrder)
        {
            const auto& fighter = Fighters[seat];
            const auto offset = 2 + seat * 6;
            snapshot[offset] = fighter.X.RawValue();
            snapshot[offset + 1] = fighter.Hp;
            snapshot[offset + 2] = fighter.Energy;
            snapshot[offset + 3] = fighter.Action;
            snapshot[offset + 4] = fighter.Age;
            snapshot[offset + 5] = fighter.Facing;
        }
        // 按帧号和胜负、黑白席可见字段、配置、开局种子、垂直状态、完整随机状态的顺序对账。
        // 参考 MiniGameCore 的 HashInt32，整数按小端逐字节混入，而不是一次异或整个整数。
        uint32_t hash = Fnv1aOffsetBasis;
        for (int i = 0; i < 14; ++i)
        {
            hash = Fnv1aWord(hash, static_cast<uint32_t>(snapshot[i]));
        }
        for (auto word : Config)
        {
            hash = Fnv1aWord(hash, static_cast<uint32_t>(word));
        }
        hash = Fnv1aDoubleWord(hash, InitialSeed);
        // 高度会被 HUD 读取，速度虽不展示，但会改变未来落点，因此同样参与指纹。
        snapshot[17] = Fighters[Black].Y.RawValue();
        snapshot[18] = Fighters[White].Y.RawValue();
        for (const Seat seat : SeatOrder)
        {
            const Fighter& fighter = Fighters[seat];
            hash = Fnv1aWord(hash, static_cast<uint32_t>(fighter.Y.RawValue()));
            hash = Fnv1aWord(hash, static_cast<uint32_t>(fighter.VerticalSpeed.RawValue()));
        }
        // 随机状态的高低字按固定顺序混入。读取状态不抽样，避免对账改变战斗结果。
        for (const uint64_t word : Random.StateWords())
        {
            hash = Fnv1aDoubleWord(hash, word);
        }
        // 快照和联机消息沿用非负 int32。只在输出时截去最高位，不缩短内部混合。
        snapshot[16] = static_cast<int32_t>(hash & 0x7fffffffu);
    }
}

"""把策划 JSON 编成两端共享的只读配置。路径按仓库相对位置解析，不依赖作者电脑。"""
from pathlib import Path
import json

# 以生成器位置为起点，使 CI、服务器开发机和 Unity 工程使用同一个数值来源。
folder = Path(__file__).resolve().parent
data = json.loads((folder / "Duel.v1.json").read_text(encoding="utf-8"))
arena = data["arena"]
protocol = data["coreProtocol"]
hash_sample_frames = data["hashSampleFrames"]
frame_limit = arena["frameLimit"]
if type(frame_limit) is not int or (frame_limit != 0 and not 10 <= frame_limit <= 10000):
    raise ValueError("决斗帧上限必须为 0（不限时）或 10 到 10000")
if (type(hash_sample_frames) is not int or not 1 <= hash_sample_frames <= 10000
        or (frame_limit > 0 and hash_sample_frames > frame_limit)):
    raise ValueError("决斗对账间隔必须是正整数，限时对局中不得超过对局帧上限")
if arena["frameMilliseconds"] != 50:
    raise ValueError("决斗逻辑帧固定为 50 毫秒（20 Hz）")
jump = data["jump"]
jump_speed, jump_gravity = jump["speed"], jump["gravity"]
if (type(jump_speed) is not int or not 1 <= jump_speed <= 600
        or type(jump_gravity) is not int or not 1 <= jump_gravity <= 200):
    raise ValueError("决斗跳跃初速或重力超出原生配置范围")
# 原生先加本帧速度再扣重力。累加正速度段得到峰值，只供表现阶段选择。
ascent_frames = (jump_speed + jump_gravity - 1) // jump_gravity
jump_apex_height = ascent_frames * jump_speed - jump_gravity * ascent_frames * (ascent_frames - 1) // 2
# 顺序是小端配置包的契约。新增字段需要同时核对托管端协议与 C++ 消费位置。
words = [arena[k] for k in ("health", "extent", "movePerFrame", "frameLimit", "spawnOffset",
                             "energy", "energyPerFrame", "energyPerHit", "frameMilliseconds")]
# 顺序为普攻一段、技能一、大招、技能二、普攻二段、普攻三段。前 27 字保留场地和跳跃下标。
attacks = data["attacks"]
assert len(attacks) == 6
for attack in attacks[:3]:
    words += [attack[k] for k in ("damage", "range", "hitFrame", "duration", "cost")]
words += [jump[k] for k in ("speed", "gravity", "hitHeight")]
for attack in attacks[3:]:
    words += [attack[k] for k in ("damage", "range", "hitFrame", "duration", "cost")]
# 后续窗口的位 0～3 对应玩家四个输入槽。派生普攻段位只由原生世界选择。
for attack in attacks:
    words += [attack[k] for k in ("cancelFrame", "stun", "advance", "next")]
# 白方鸦鸦只覆盖距离。固定按动作号追加六个字，保留现有招式和跳跃下标。
yaya_ranges = data["yayaRanges"]
if len(yaya_ranges) != len(attacks) or any(type(r) is not int or not 1 <= r <= arena["extent"] * 2 for r in yaya_ranges):
    raise ValueError("鸦鸦的六项距离必须是场地宽度以内的正整数")
yaya_range_offset = len(words)
words += yaya_ranges
assert all(type(x) is int and 0 <= x <= 100000 for x in words)
duration_cases = "\n".join(f'                {slot} => {attack["duration"]},'
                            for slot, attack in enumerate(attacks, 1))
hit_cases = "\n".join(f'                {slot} => {attack["hitFrame"]},'
                       for slot, attack in enumerate(attacks, 1))
range_cases = "\n".join(f'                {slot} => white ? {yaya_range} : {attack["range"]},'
                         for slot, (attack, yaya_range) in enumerate(zip(attacks, yaya_ranges), 1))
# 同时把入场资格和战斗数值纳入指纹，防止两端只更新了一半。
fingerprint = 2166136261
for word in words + [data["eligibility"]["minimumMove"], data["eligibility"]["minimumLeadHalfPoints"], protocol, hash_sample_frames]:
    fingerprint = ((fingerprint ^ word) * 16777619) & 0xffffffff
target = folder.parents[1] / "UnityProject/RevGame.Unity/Assets/GameScripts/Pvp/Duel/DuelConfig.Generated.cs"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(f"""// 由 RevGameCore/Config/GenerateDuelConfig.py 生成，请编辑 Duel.v1.json 后重新生成。
namespace RevGame.Pvp.Duel
{{
    // 两端共用的生成配置。策划改 JSON 后重新生成，并让客户端和服务端一起使用新指纹。
    public static class DuelConfig
    {{
        // 原生 FIFO 协议版本，同时纳入联机决斗指纹。
        internal const int CoreProtocol = {protocol};
        // 从第几手开始允许判定中局劣势。
        public const int MinimumMove = {data["eligibility"]["minimumMove"]};
        // 入场最小估算分差，单位为半目。
        public const int MinimumLead = {data["eligibility"]["minimumLeadHalfPoints"]};
        // 每席的初始生命值，也是 HUD 血条的满值。
        public const int Health = {arena["health"]};
        // 能量上限，用于 HUD 比例换算。
        public const int Energy = {arena["energy"]};
        // 两席距离场地中心的初始横向距离，单位千分格。
        public const int SpawnOffset = {arena["spawnOffset"]};
        // 读取指定座位的招式距离，单位为千分格。表现用它对齐枪芒，判伤仍由原生结算。
        public static int ActionRange(bool white, int action)
        {{
            return action switch
            {{
{range_cases}
                _ => 0
            }};
        }}
        // AI 选择技能意图需要的能量；是否真正扣除由原生招式规则决定。
        public const int SkillCost = {data["attacks"][1]["cost"]};
        // 技能二的能量门槛，HUD 和 AI 共用。
        public const int SkillTwoCost = {data["attacks"][3]["cost"]};
        // AI 选择大招意图需要的能量。
        public const int UltimateCost = {data["attacks"][2]["cost"]};
        // 0 表示不限时。正数是限时规则的逻辑帧上限。
        public const int FrameLimit = {frame_limit};
        // 逻辑帧对应的毫秒数，用于宿主限速和显示时间换算。
        public const int FrameMs = {arena["frameMilliseconds"]};
        // 普通跳跃的原生峰值，单位为千分格。仅供角色表现按高度分段。
        internal const int JumpApexHeight = {jump_apex_height};
        // 联机每隔多少个已执行逻辑帧交换一次指纹。终局另外校验。
        public const int HashSampleFrames = {hash_sample_frames};
        // 招式持续的逻辑帧数，用于表现采样和输入缓冲。
        public static int ActionDuration(int action)
        {{
            return action switch
            {{
{duration_cases}
                _ => 0
            }};
        }}
        // 权威判伤的动作年龄。表现用它对齐枪尖接触与实际命中时刻。
        public static int ActionHitFrame(int action)
        {{
            return action switch
            {{
{hit_cases}
                _ => 0
            }};
        }}
        // 两席握手比较的 31 位规则指纹。
        public const int Fingerprint = {fingerprint & 0x7fffffff};
        // 每局构造一份配置包内容，调用方可按小端协议写入本端实例。
        public static int[] CreateCoreWords()
        {{
            return new[] {{ {", ".join(map(str, words))} }};
        }}
    }}
}}
""", encoding="utf-8")
native_target = folder.parent / "Include/DuelProtocol.Generated.h"
native_target.write_text(f"""// 由 Config/GenerateDuelConfig.py 生成。
#pragma once
#include <array>
#include <cstdint>

namespace RevGameCore
{{
    // FIFO 协议版本和固定逻辑步长，共用 Duel.v1.json。
    constexpr int WireVersion = {protocol};
    constexpr int FrameMilliseconds = {arena["frameMilliseconds"]};
    // 协议尾部从这里开始保存白方的六项命中距离。
    constexpr int YayaRangeOffset = {yaya_range_offset};
    // 原生回归与托管开局包共用的规则字，防止测试继续使用旧招式布局。
    inline constexpr std::array<int32_t, {len(words)}> DefaultDuelSettings{{ {", ".join(map(str, words))} }};
}}
""", encoding="utf-8")

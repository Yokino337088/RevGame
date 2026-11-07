"""把策划 JSON 编成两端共享的只读配置。路径按仓库相对位置解析，不依赖作者电脑。"""
from pathlib import Path
import json

# 以生成器位置为起点，使 CI、服务器开发机和 Unity 工程使用同一个数值来源。
folder = Path(__file__).resolve().parent
data = json.loads((folder / "Duel.v1.json").read_text(encoding="utf-8"))
arena = data["arena"]
# 顺序是小端配置包的契约。新增字段需要同时核对托管端协议与 C++ 消费位置。
words = [arena[k] for k in ("health", "extent", "movePerFrame", "frameLimit", "spawnOffset",
                             "energy", "energyPerFrame", "energyPerHit", "frameMilliseconds")]
assert len(data["attacks"]) == 3
for attack in data["attacks"]:
    words += [attack[k] for k in ("damage", "range", "hitFrame", "duration", "cost")]
words += [data["jump"][k] for k in ("speed", "gravity", "hitHeight")]
assert all(type(x) is int and 0 <= x <= 100000 for x in words)
# 同时把入场资格和战斗数值纳入指纹，防止两端只更新了一半。
fingerprint = 2166136261
for word in words + [data["eligibility"]["minimumMove"], data["eligibility"]["minimumLeadHalfPoints"]]:
    fingerprint = ((fingerprint ^ word) * 16777619) & 0xffffffff
target = folder.parents[1] / "UnityProject/RevGame.Unity/Assets/GameScripts/Pvp/Duel/DuelConfig.Generated.cs"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(f"""// 由 RevGameCore/Config/GenerateDuelConfig.py 生成，请编辑 Duel.v1.json 后重新生成。
namespace RevGame.Pvp.Duel
{{
    // 两端共用的生成配置。策划改 JSON 后重新生成，并让客户端和服务端一起使用新指纹。
    public static class DuelConfig
    {{
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
        // 本地电脑用于接近目标的普攻距离参考，真实命中仍由 C++ 校验。
        public const int JabRange = {data["attacks"][0]["range"]};
        // AI 选择技能意图需要的能量；是否真正扣除由原生招式规则决定。
        public const int SkillCost = {data["attacks"][1]["cost"]};
        // AI 选择大招意图需要的能量。
        public const int UltimateCost = {data["attacks"][2]["cost"]};
        // 本局最多执行的逻辑帧数，到达上限时由原生按生命结算。
        public const int FrameLimit = {arena["frameLimit"]};
        // 逻辑帧对应的毫秒数，用于宿主限速和显示时间换算。
        public const int FrameMs = {arena["frameMilliseconds"]};
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

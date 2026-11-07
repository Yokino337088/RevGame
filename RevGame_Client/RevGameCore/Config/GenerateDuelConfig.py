"""把策划 JSON 编成两端共享的只读配置。路径按仓库相对位置解析，不依赖作者电脑。"""
from pathlib import Path
import json

folder = Path(__file__).resolve().parent
data = json.loads((folder / "Duel.v1.json").read_text(encoding="utf-8"))
arena = data["arena"]
words = [arena[k] for k in ("health", "extent", "movePerFrame", "frameLimit", "spawnOffset",
                             "energy", "energyPerFrame", "energyPerHit", "frameMilliseconds")]
assert len(data["attacks"]) == 3
for attack in data["attacks"]:
    words += [attack[k] for k in ("damage", "range", "hitFrame", "duration", "cost")]
assert all(type(x) is int and 0 <= x <= 100000 for x in words)
fingerprint = 2166136261
for word in words + [data["eligibility"]["minimumMove"], data["eligibility"]["minimumLeadHalfPoints"]]:
    fingerprint = ((fingerprint ^ word) * 16777619) & 0xffffffff
target = folder.parents[1] / "UnityProject/RevGame.Unity/Assets/GameScripts/Pvp/Duel/DuelConfig.Generated.cs"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(f"""// 由 RevGameCore/Config/GenerateDuelConfig.py 生成，请编辑 Duel.v1.json 后重新生成。
namespace RevGame.Pvp.Duel
{{
    public static class DuelConfig
    {{
        public const int MinimumMove = {data["eligibility"]["minimumMove"]};
        public const int MinimumLead = {data["eligibility"]["minimumLeadHalfPoints"]};
        public const int Health = {arena["health"]};
        public const int Energy = {arena["energy"]};
        public const int SpawnOffset = {arena["spawnOffset"]};
        public const int JabRange = {data["attacks"][0]["range"]};
        public const int SkillCost = {data["attacks"][1]["cost"]};
        public const int UltimateCost = {data["attacks"][2]["cost"]};
        public const int FrameLimit = {arena["frameLimit"]};
        public const int FrameMs = {arena["frameMilliseconds"]};
        public const int Fingerprint = {fingerprint & 0x7fffffff};
        public static int[] CreateCoreWords()
        {{
            return new[] {{ {", ".join(map(str, words))} }};
        }}
    }}
}}
""", encoding="utf-8")

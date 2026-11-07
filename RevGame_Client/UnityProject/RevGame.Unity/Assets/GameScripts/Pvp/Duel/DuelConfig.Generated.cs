// 由 RevGameCore/Config/GenerateDuelConfig.py 生成，请编辑 Duel.v1.json 后重新生成。
namespace RevGame.Pvp.Duel
{
    // 两端共用的生成配置。策划改 JSON 后重新生成，并让客户端和服务端一起使用新指纹。
    public static class DuelConfig
    {
        // 从第几手开始允许判定中局劣势。
        public const int MinimumMove = 24;
        // 入场最小估算分差，单位为半目。
        public const int MinimumLead = 24;
        // 每席的初始生命值，也是 HUD 血条的满值。
        public const int Health = 100;
        // 能量上限，用于 HUD 比例换算。
        public const int Energy = 100;
        // 两席距离场地中心的初始横向距离，单位千分格。
        public const int SpawnOffset = 1700;
        // 本地电脑用于接近目标的普攻距离参考，真实命中仍由 C++ 校验。
        public const int JabRange = 1020;
        // AI 选择技能意图需要的能量；是否真正扣除由原生招式规则决定。
        public const int SkillCost = 22;
        // AI 选择大招意图需要的能量。
        public const int UltimateCost = 78;
        // 本局最多执行的逻辑帧数，到达上限时由原生按生命结算。
        public const int FrameLimit = 1200;
        // 逻辑帧对应的毫秒数，用于宿主限速和显示时间换算。
        public const int FrameMs = 50;
        // 两席握手比较的 31 位规则指纹。
        public const int Fingerprint = 182341838;
        // 每局构造一份配置包内容，调用方可按小端协议写入本端实例。
        public static int[] CreateCoreWords()
        {
            return new[] { 100, 4500, 140, 1200, 1700, 100, 1, 12, 50, 9, 1020, 3, 8, 0, 17, 1630, 5, 13, 22, 38, 2560, 8, 21, 78, 270, 34, 480 };
        }
    }
}

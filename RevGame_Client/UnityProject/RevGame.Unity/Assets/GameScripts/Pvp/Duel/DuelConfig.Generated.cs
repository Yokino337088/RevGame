// 由 RevGameCore/Config/GenerateDuelConfig.py 生成，请编辑 Duel.v1.json 后重新生成。
namespace RevGame.Pvp.Duel
{
    // 两端共用的生成配置。策划改 JSON 后重新生成，并让客户端和服务端一起使用新指纹。
    public static class DuelConfig
    {
        // 原生 FIFO 协议版本，同时纳入联机决斗指纹。
        internal const int CoreProtocol = 7;
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
        // 读取指定座位的招式距离，单位为千分格。表现用它对齐枪芒，判伤仍由原生结算。
        public static int ActionRange(bool white, int action)
        {
            return action switch
            {
                1 => white ? 1020 : 1400,
                2 => white ? 1750 : 2250,
                3 => white ? 2500 : 3000,
                4 => white ? 1520 : 1800,
                5 => white ? 1120 : 1500,
                6 => white ? 1240 : 1660,
                _ => 0
            };
        }
        // AI 选择技能意图需要的能量；是否真正扣除由原生招式规则决定。
        public const int SkillCost = 16;
        // 技能二的能量门槛，HUD 和 AI 共用。
        public const int SkillTwoCost = 20;
        // AI 选择大招意图需要的能量。
        public const int UltimateCost = 55;
        // 0 表示不限时。正数是限时规则的逻辑帧上限。
        public const int FrameLimit = 0;
        // 逻辑帧对应的毫秒数，用于宿主限速和显示时间换算。
        public const int FrameMs = 50;
        // 联机每隔多少个已执行逻辑帧交换一次指纹。终局另外校验。
        public const int HashSampleFrames = 40;
        // 招式持续的逻辑帧数，用于表现采样和输入缓冲。
        public static int ActionDuration(int action)
        {
            return action switch
            {
                1 => 8,
                2 => 12,
                3 => 20,
                4 => 14,
                5 => 8,
                6 => 11,
                _ => 0
            };
        }
        // 权威判伤的动作年龄。表现用它对齐枪尖接触与实际命中时刻。
        public static int ActionHitFrame(int action)
        {
            return action switch
            {
                1 => 3,
                2 => 4,
                3 => 7,
                4 => 5,
                5 => 3,
                6 => 4,
                _ => 0
            };
        }
        // 两席握手比较的 31 位规则指纹。
        public const int Fingerprint = 1230509847;
        // 每局构造一份配置包内容，调用方可按小端协议写入本端实例。
        public static int[] CreateCoreWords()
        {
            return new[] { 100, 4500, 140, 0, 1700, 100, 1, 12, 50, 9, 1400, 3, 8, 0, 15, 2250, 4, 12, 16, 32, 3000, 7, 20, 55, 540, 68, 480, 18, 1800, 5, 14, 20, 10, 1500, 3, 8, 0, 13, 1660, 4, 11, 0, 5, 8, 0, 15, 6, 12, 420, 12, 21, 16, 210, 0, 7, 13, 100, 6, 5, 9, 110, 15, 6, 11, 150, 14, 1020, 1750, 2500, 1520, 1120, 1240 };
        }
    }
}

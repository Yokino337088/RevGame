// ============================================================
// Enums.cs
// 把职业、阶段和流程状态统一命名，界面和服务共用定义，避免各写一套数字约定。
// ============================================================
namespace RevGame.Core
{
    /// <summary>局外大流程；战斗内部的准备/战斗/结算继续使用 GamePhase。</summary>
    public enum GameScreen
    {
        Home,
        LevelSelect,
        Battle,
        Exit
    }

    /// <summary>
    /// 4 个职业（策划案 §07：同职业凑够会给加成）。美术上的 9 种棋子都归到 4 个职业里。
    /// </summary>
    public enum PieceClass
    {
        /// <summary>守卫：前排扛伤。</summary>
        Guard = 0,
        /// <summary>法师：远程、隔子打（借象棋"炮"）。</summary>
        Mage = 1,
        /// <summary>射手：远程直线输出。</summary>
        Ranger = 2,
        /// <summary>刺客：高机动近战。</summary>
        Assassin = 3,
    }

    /// <summary>
    /// 9 种棋子外形（对应 ArtRes/Pieces 下 9 套立绘，每套 3 星）。
    /// 为什么单独有"外形"和"职业"两个概念：外形决定用哪张图，职业决定加成与定位。
    /// </summary>
    public enum PieceKind
    {
        Swordman = 0,
        Shield = 1,
        HeavySword = 2,
        Archer = 3,
        Crossbow = 4,
        MageWater = 5,
        MageFire = 6,
        Priest = 7,
        Assassin = 8,
    }

    /// <summary>星级（1★ → 2★ → 3★）。每成功打出两张同类牌，旧棋原位升一档，新棋保留。</summary>
    public enum StarTier
    {
        One = 1,
        Two = 2,
        Three = 3,
    }

    /// <summary>阵营。玩家在下方（Y 小），敌人在上方（Y 大）。</summary>
    public enum GameSide
    {
        Player = 0,
        Enemy = 1,
    }

    /// <summary>
    /// 一个回合的三段（策划案 §03）：准备 → 战斗 → 结算。
    /// 注意：Reward 是"结算之后的三选一"，不是每回合都有（只在过关时出现）。
    /// </summary>
    public enum GamePhase
    {
        /// <summary>准备：先亮出敌方布阵，玩家最多落 6 张牌，可全盘免费调整己方棋子。</summary>
        Prepare = 0,
        /// <summary>战斗：慢节奏全自动，玩家不操作。</summary>
        Battle = 1,
        /// <summary>结算：胜负判定、血量与状态保留。</summary>
        Settle = 2,
        /// <summary>奖励三选一（仅过章节时）。</summary>
        Reward = 3,
    }

    /// <summary>
    /// Boss 技能种类。每一项必须在 Excel 的 BossSkillConfig 表里恰好有一行（启动时校验覆盖）。
    /// 名称同时是 Boss 状态机里对应技能状态的名字，新增技能要三处同步：这里、Excel、BossStates.cs。
    /// </summary>
    public enum BossSkillKind
    {
        /// <summary>横扫震地：贴身范围攻击，打 Boss 身体周围一圈。</summary>
        Cleave = 0,
        /// <summary>致命狙击：锁定我方威胁最大的一颗棋子，单体高伤。</summary>
        Snipe = 1,
        /// <summary>三连轰炸：远处锁定一块区域，分三次落弹。</summary>
        Bombard = 2,
        /// <summary>弹幕风暴：以自身为圆心向外扫射一圈弹幕。</summary>
        BulletRing = 3,
        /// <summary>瞬影爆破：瞬移到我方阵中空位，落地范围爆炸。</summary>
        BlinkBlast = 4,
        /// <summary>天光回旋：多道激光环绕自身旋转，持续多段伤害。</summary>
        LaserSpin = 5,
        /// <summary>灵魂汲取：吸取我方残血棋子的生命为自己回血。</summary>
        SoulDrain = 6,
        /// <summary>召唤影卫：在自身附近召出小兵。</summary>
        Summon = 7,
        /// <summary>狂暴怒吼：血量跌破阈值时只触发一次，此后技能冷却缩短。</summary>
        Enrage = 8,
        /// <summary>流星陨落，参数、预警和解锁章节来自BossSkillConfig。</summary>
        MeteorShower = 9,
        /// <summary>十字天罚，参数、预警和解锁章节来自BossSkillConfig。</summary>
        CrossLaser = 10,
        /// <summary>极寒新星，参数、预警和解锁章节来自BossSkillConfig。</summary>
        FrostNova = 11,
        /// <summary>连锁雷暴，参数、预警和解锁章节来自BossSkillConfig。</summary>
        ChainLightning = 12,
        /// <summary>腐蚀毒沼，参数、预警和解锁章节来自BossSkillConfig。</summary>
        PoisonMarsh = 13,
        /// <summary>烈焰火墙，参数、预警和解锁章节来自BossSkillConfig。</summary>
        FlameWall = 14,
        /// <summary>冰晶贯穿，参数、预警和解锁章节来自BossSkillConfig。</summary>
        IceLance = 15,
        /// <summary>引力坍缩，参数、预警和解锁章节来自BossSkillConfig。</summary>
        GravityWell = 16,
        /// <summary>末日烙印，参数、预警和解锁章节来自BossSkillConfig。</summary>
        DoomSigil = 17,
        /// <summary>星辰风暴：章节专属技能，参数来自配置表。</summary>
        StarStorm = 18,
        /// <summary>黑洞陷落：章节专属技能，参数来自配置表。</summary>
        BlackHole = 19,
        /// <summary>日冕灼潮：章节专属技能，参数来自配置表。</summary>
        SolarFlare = 20,
        /// <summary>烈阳烙印：章节专属技能，参数来自配置表。</summary>
        SunBrand = 21,
        /// <summary>怒涛横卷：章节专属技能，参数来自配置表。</summary>
        TidalSurge = 22,
        /// <summary>回流涡旋：章节专属技能，参数来自配置表。</summary>
        Maelstrom = 23,
        /// <summary>沼泽巨口：章节专属技能，参数来自配置表。</summary>
        SwampMaw = 24,
        /// <summary>泥沼缚射：章节专属技能，参数来自配置表。</summary>
        MireChains = 25,
        /// <summary>极寒囚笼：章节专属技能，参数来自配置表。</summary>
        FrozenPrison = 26,
        /// <summary>冰封终焉：章节专属技能，参数来自配置表。</summary>
        GlacialAegis = 27,
        /// <summary>十字神罚：章节专属技能，参数来自配置表。</summary>
        DivineCross = 28,
        /// <summary>圣域：章节专属技能，参数来自配置表。</summary>
        Sanctuary = 29,
        /// <summary>彗星轨道：第1章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        CometRails = 30,
        /// <summary>星屑追爆：第1章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        AstralShards = 31,
        /// <summary>烈阳贯枪：第2章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        SolarSpears = 32,
        /// <summary>炎羽扇扫：第2章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        EmberFan = 33,
        /// <summary>熔岩环爆：第2章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        MagmaRings = 34,
        /// <summary>潮汐水枪：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        TidalLances = 35,
        /// <summary>泡沫连爆：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        FoamBurst = 36,
        /// <summary>暗流绞阵：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        Undertow = 37,
        /// <summary>礁岩穿刺：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        ReefSpikes = 38,
        /// <summary>腐沼骨刺：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        BogSpines = 39,
        /// <summary>毒雾扇潮：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        VenomFan = 40,
        /// <summary>泥潭连震：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        MireRipples = 41,
        /// <summary>疫种爆裂：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        BlightSeeds = 42,
        /// <summary>寒霜十字：第5章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        SnowCross = 43,
        /// <summary>冰雹连坠：第5章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        HailBarrage = 44,
        /// <summary>冰川断层：第5章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        IceFault = 45,
        /// <summary>裁决光矛：第6章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        JudgementLances = 46,
        /// <summary>圣环脉冲：第6章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        HaloPulse = 47,
        /// <summary>律令棋阵：第6章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        RunicGrid = 48,
        /// <summary>星界崩塌：第1章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        EventHorizon = 49,
        /// <summary>星座封阵：第1章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        ConstellationSeal = 50,
        /// <summary>焚天凤陨：第2章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        PhoenixDescent = 51,
        /// <summary>日蚀审判：第2章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        SolarEclipse = 52,
        /// <summary>巨鲸破浪：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        LeviathanWake = 53,
        /// <summary>深渊潮牢：第3章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        AbyssalCage = 54,
        /// <summary>瘟疫绽放：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        PlagueBloom = 55,
        /// <summary>九首獠牙：第4章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        HydraFangs = 56,
        /// <summary>绝对零域：第5章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        AbsoluteZero = 57,
        /// <summary>万仞雪崩：第5章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        Avalanche = 58,
        /// <summary>天穹裁决：第6章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        Heavenfall = 59,
        /// <summary>终极律令：第6章招式，参数由Excel配置，固定格形允许预警后躲避。</summary>
        FinalEdict = 60,



    }

    /// <summary>
    /// 棋块的围棋状态（策划案 §06）。由 <c>GoRules</c> 每次结算后统一刷新。
    /// </summary>
    public enum GroupState
    {
        /// <summary>正常：至少还有一口气。</summary>
        Normal = 0,
        /// <summary>叫吃：只剩最后一口气（美术上给脉冲提示）。</summary>
        Atari = 1,
        /// <summary>锁死：零气时禁止自动移动并承受额外伤害；敌方停攻，我方保留守阵反击。</summary>
        Locked = 2,
    }

    /// <summary>
    /// 走法（借象棋，策划案 §07-D）。用枚举而不是一堆 if：新增走法只加一个分支。
    /// </summary>
    public enum MovePattern
    {
        /// <summary>单步：上下左右走一格。</summary>
        Step = 0,
        /// <summary>滑步：上下左右任意距离（车）。</summary>
        Slide = 1,
        /// <summary>马步：走"日"字，有蹩马腿判定。</summary>
        Knight = 2,
        /// <summary>炮：直线滑行，但只能隔着且仅隔一个棋子吃子。</summary>
        Cannon = 3,
    }
}

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

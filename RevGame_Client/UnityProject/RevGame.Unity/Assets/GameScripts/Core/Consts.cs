// ============================================================
// Consts.cs
// 集中已确认的规则比例与待替换的占位值，让接入 Excel 时能区分规则和基础数值。
// ============================================================
namespace RevGame.Core
{
    /// <summary>
    /// 全局常量。**所有魔法数字都集中在这里**，不要在业务代码里散落写 9、0.5f 这种字面量。
    ///
    /// 为什么要有这个类：
        /// 策划案的棋盘/章节结构，以及用户后续调整的战斗时长等数字，
    /// 一旦散落在十几个文件里，改一次规则就要全项目搜。集中成常量后只改一处。
    /// </summary>
    public static class Consts
    {
        // ---------------- 棋盘 ----------------
        /// <summary>棋盘边长：策划案是 9×9（81 格），沿用围棋规格。</summary>
        public const int BoardSize = 9;
        /// <summary>一格的边长（世界单位）。棋盘总宽 = BoardSize * CellSize。</summary>
        public const float CellSize = 1.15f;
        /// <summary>棋盘左下角的世界坐标。以棋盘中心为原点，所以是 -(边长/2)。</summary>
        public const float BoardOriginOffset = -(BoardSize * CellSize) * 0.5f;
        // ---------------- 关卡结构（策划案 §04） ----------------
        /// <summary>总共 6 章。</summary>
        public const int ChapterCount = 6;
        /// <summary>每章 10 个普通回合。</summary>
        public const int NormalRoundsPerChapter = 10;
        /// <summary>每章第 11 回合是 Boss 回合（生死回合）。</summary>
        public const int BossRoundIndex = NormalRoundsPerChapter + 1;
        /// <summary>每章总回合数（10 普通 + 1 Boss）。</summary>
        public const int RoundsPerChapter = BossRoundIndex;
        // ---------------- 回合节奏 ----------------
        /// <summary>用户要求放慢体验，覆盖 v18 原来的 15~20 秒，留足观看战斗和支援事件的时间。</summary>
        public const float BattleDurationSeconds = 90f;
        /// <summary>普通棋子的基准行动周期（秒）；模拟小步与远程频率以此为基数，结果不依赖帧率。</summary>
        public const float BattleTickSeconds = 1.25f;
        /// <summary>固定模拟小步：一周期分四次驱动，允许远程在周期中途出手；不会加快普通棋或敌方移动。</summary>
        public const float BattleSimulationStepSeconds = BattleTickSeconds / 4f;
        /// <summary>敌人每四个逻辑步最多靠近一次，即五秒；攻击仍每步正常判定。</summary>
        public const int EnemyMoveEveryTicks = 4;
        /// <summary>普通回合、Boss 和重开都给六次落子；与发牌上限共用一个来源，避免只改 UI 却少发牌。</summary>
        public const int MaxCardsPerRound = MaxHandSize;
        // ---------------- 手牌 ----------------
        /// <summary>首回合与后续回合一致，均发满六张；不继承上一回合的剩余牌数。</summary>
        public const int InitialHandSize = MaxHandSize;
        /// <summary>每回合六张，UI 也必须准备六个常驻槽位，刷新只替换尚未使用的牌。</summary>
        public const int MaxHandSize = 6;
        // ---------------- 数值占位（等 Excel 表接入后替换，见 README） ----------------
        /// <summary>被"锁死"（气=0）时受到的伤害倍率。策划案：挨双倍伤害。</summary>
        public const float LockedDamageMultiplier = 2f;
        /// <summary>基础暴击/无（占位），保证公式里没有裸数字。</summary>
        public const float BaseDamageMultiplier = 1f;
        // 以下均为策划案已明确的比例。基础生命/攻击继续使用占位图鉴，等待 Excel 数值。
        public const float TwoStarHpScale = 2f;
        public const float ThreeStarHpScale = 3.6f;
        public const float TwoStarAttackScale = 1.8f;
        public const float ThreeStarAttackScale = 3.2f;
        public const float RewardAttackBonus = 0.1f;
        public const float ClassAttackBonus = 0.1f;
        public const float LinkAttackPerCell = 0.05f;
        public const float MaxLinkAttackBonus = 0.25f;
        public const float IsolatedDamageMultiplier = 1.2f;
        public const float BossAccelerationSeconds = 60f;
        public const float BossAccelerationBonus = 0.1f;
        public const float BattleEventChance = 0.3f;
        public const float RareEventChance = 0.25f;
        public const float BattleEventDelay = 6f;
        /// <summary>先观看正常交战，再每七秒加入一次增益；提示停留仍独立由 Excel Duration 控制。</summary>
        public const float BattleEventInterval = 7f;
        public const int MaxBattleEventsPerRound = 12;
        /// <summary>最迟兜底时间；正常交战可提前获胜，只有仍未结束的长局才走到这里。</summary>
        public const float VictoryAssistFinishSeconds = 86f;
        /// <summary>僵持/危急局面在此之后才允许渐进支援，给敌方阵型足够发挥时间。</summary>
        public const float VictoryAssistStartSeconds = 36f;
        /// <summary>长局到此进入逐个击破；正常交战和僵持支援都允许提前获胜。</summary>
        public const float VictoryAssistFinalSeconds = 74f;
        /// <summary>连续这么久没有敌人真正掉血，才算火力停滞。</summary>
        public const float VictoryAssistStallSeconds = 9f;
        /// <summary>僵持支援的集火间隔；与普通战斗事件的七秒间隔是两种节奏。</summary>
        public const float VictoryAssistPulseSeconds = 3f;
        /// <summary>原型节奏参数，等待正式数值表替换；双方普通攻击一起放缓，远程攻击频率不变。</summary>
        public const float CombatDamageScale = 0.6f;
        /// <summary>敌方原型生命系数，给远程对射、守眼回血和事件留出展开时间。</summary>
        public const float EnemyHpScale = 2.4f;
        /// <summary>僵持支援单次至少削掉目标最大生命的此比例，不会因目标星级高而失效。</summary>
        public const float VictoryAssistPulseHpRatio = 0.35f;
        /// <summary>普通炮火的比例伤害下限，仍与固定的 SupportDamage 取较大值。</summary>
        public const float ArtilleryHpRatio = 0.12f;
        /// <summary>敌弓直线射程；保留阻挡判定，和重弩跨盘攻击不同。</summary>
        public const int EnemyArcherRange = 6;
        public const float PieceMoveSeconds = 0.45f;
        /// <summary>弹道飞行时长，同时用于安排命中飘字。</summary>
        public const float ProjectileSeconds = 0.3f;
        public const float HealRatio = 0.1f;
        public const float EyeHealSeconds = 5f;
        public const float EyeHealRatio = 0.05f;
        public const float ComboSpeedBonus = 0.2f;
        /// <summary>炮火单颗伤害仍为占位；策划已规定效果，但没有给出最终数值。</summary>
        public const int SupportDamage = 25;
        // ---------------- 层与排序 ----------------
        /// <summary>棋盘格子的 Sprite 排序层。</summary>
        public const int SortingOrderBoard = 0;
        /// <summary>棋子本体排序层。</summary>
        public const int SortingOrderPiece = 10;
        /// <summary>状态标记（气数点、连/断标记）排序层。</summary>
        public const int SortingOrderStatus = 20;
        /// <summary>特效排序层。</summary>
        public const int SortingOrderEffect = 30;
    }
}

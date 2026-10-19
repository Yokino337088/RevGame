// ============================================================
// Consts.cs
// 只放“和预制体/场景/美术资源绑定的结构常量”。战斗数值、成长、概率、时长等平衡参数不在这里：
// 它们由策划在 Excel 里维护（配置表/*.xlsx），经框架导表工具生成后通过 Balance / PieceCatalog / LevelCatalog 读取。
// ============================================================
namespace RevGame.Core
{
    /// <summary>
    /// 全局结构常量。
    ///
    /// 为什么这几个留在代码里而不进 Excel：
    /// 它们改一个值就必须同步改预制体、场景或美术资源，不是“调数值”能解决的事。
    /// 把它们放进表反而会让策划以为改表就能生效，结果界面和数据对不上。
    ///
    /// ★ 想新增“平衡类”数字（时长、倍率、概率、数量上限…）时不要加在这里：
    /// 先在 Excel 里加一行，再到 Balance.cs 读取并校验。这样才不会又长出一份写死的数值。
    /// </summary>
    public static class Consts
    {
        // ---------------- 棋盘（与 81 格棋盘预制体、BoardModel 数组尺寸绑定） ----------------
        /// <summary>棋盘边长：策划案是 9×9（81 格），沿用围棋规格。</summary>
        public const int BoardSize = 9;
        /// <summary>一格的边长（世界单位）。棋盘总宽 = BoardSize * CellSize。</summary>
        public const float CellSize = 1.15f;
        /// <summary>棋盘左下角的世界坐标。以棋盘中心为原点，所以是 -(边长/2)。</summary>
        public const float BoardOriginOffset = -(BoardSize * CellSize) * 0.5f;
        // ---------------- 关卡结构（与选关界面的章节按钮数量绑定） ----------------
        /// <summary>总共 6 章。选关界面按这个数量创建章节按钮；ChapterConfig 的行数必须与它一致（启动时校验）。</summary>
        public const int ChapterCount = 6;
        // ---------------- 手牌（与战斗界面的六个常驻手牌槽位绑定） ----------------
        /// <summary>每回合六张，UI 也必须准备六个常驻槽位，刷新只替换尚未使用的牌。</summary>
        public const int MaxHandSize = 6;
        /// <summary>首回合与后续回合一致，均发满六张；不继承上一回合的剩余牌数。</summary>
        public const int InitialHandSize = MaxHandSize;
        /// <summary>普通回合、Boss 和重开都给六次落子；与发牌上限共用一个来源，避免只改 UI 却少发牌。</summary>
        public const int MaxCardsPerRound = MaxHandSize;
        // ---------------- 纯表现时长（动画 Tween，不影响战斗结果） ----------------
        public const float PieceMoveSeconds = 0.45f;
        /// <summary>弹道飞行时长，同时用于安排命中飘字。</summary>
        public const float ProjectileSeconds = 0.3f;
        // ---------------- 层与排序（与预制体/材质的 Sorting Order 对应） ----------------
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

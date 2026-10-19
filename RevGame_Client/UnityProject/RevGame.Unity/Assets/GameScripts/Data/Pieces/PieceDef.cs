// ============================================================
// PieceDef.cs
// 静态图鉴与局内棋子分开。多个棋子共享定义，但每颗棋子的当前血量独立保存。
// ============================================================
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>
    /// 一种棋子的**静态定义**（只读，运行期不改）。
    ///
    /// 关键区分（新手最容易混）：
    /// - <see cref = "PieceDef"/> = 图鉴里的那一页（"盾战 2★：血 120、攻 12"）；
    /// - <c>Piece</c> = 棋盘上的那一颗（"我方 12 号棋子，现在是 87 血，在 (3,4)"）。
    /// 前者全局只有几十份，后者每局可能几十颗。**绝不要把血量写进 Def**。
    /// </summary>
    public sealed class PieceDef
    {
        /// <summary>棋子外形（决定用哪张立绘）。</summary>
        public PieceKind Kind { get; }
        /// <summary>职业（决定同职业加成、走法倾向）。</summary>
        public PieceClass Class { get; }
        /// <summary>星级（1~3）。</summary>
        public StarTier Star { get; }
        /// <summary>显示名（中文，给 UI 用）。</summary>
        public string DisplayName { get; }
        /// <summary>最大生命。</summary>
        public int MaxHp { get; }
        /// <summary>攻击力。</summary>
        public int Attack { get; }
        /// <summary>走法。</summary>
        public MovePattern Pattern { get; }
        /// <summary>走几步（Step 类用；Slide/Cannon 表示最大距离，0 = 不限）。</summary>
        public int MoveRange { get; }
        /// <summary>是否远程：决定攻速/表现与敌方职业射程；我方近战优先相邻、无接触时全盘援击，远程直接全盘选敌。</summary>
        public bool IsRanged { get; }
        /// <summary>立绘资源名（ArtRes/Pieces 下的文件名，不含扩展名）。</summary>
        public string SpriteName { get; }

        public PieceDef(PieceKind kind, PieceClass pieceClass, StarTier star, string displayName, int maxHp, int attack, MovePattern pattern, int moveRange, bool isRanged, string spriteName)
        {
            Kind = kind;
            Class = pieceClass;
            Star = star;
            DisplayName = displayName;
            MaxHp = maxHp;
            Attack = attack;
            Pattern = pattern;
            MoveRange = moveRange;
            IsRanged = isRanged;
            SpriteName = spriteName;
        }

        /// <summary>唯一键，用于查表与比较（"盾战2星"这种）。</summary>
        public string DefId
        {
            get
            {
                return Kind + "_" + (int)Star;
            }
        }
    }
}

// ============================================================
// PieceCatalog.cs
// 棋子图鉴：数值来自 Excel（配置表/棋子数值配置.xlsx 的 PieceConfig + StarConfig），不在这里写死。
// 棋子与卡牌读取同一份定义，避免两处面板显示不同的血攻。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>
    /// 棋子图鉴：9 种外形 × 3 星 = 27 份定义，由 <see cref="GameConfig.Initialize"/> 装配好的配置现算出来并缓存。
    ///
    /// 为什么不再有静态构造里的 AddTemplate：
    /// 那些血、攻、走法是策划要调的数，写在 C# 里每次调整都要改代码、重新编译，还可能和 Excel 各是一份。
    /// 现在 Excel 是唯一真源；本类只负责“1★ 模板 × 星级倍率 → 每个星级的定义”这一步换算。
    /// </summary>
    public static class PieceCatalog
    {
        // 缓存按配置快照对象绑定：重新装配（Initialize）后快照对象变了，缓存自动失效重建。
        private static ConfigSnapshot _cachedFor;
        private static Dictionary<(PieceKind, int), PieceDef> _defs;
        private static List<PieceDef> _all;

        private static Dictionary<(PieceKind, int), PieceDef> Defs
        {
            get
            {
                ConfigSnapshot snapshot = GameConfig.Current;
                if (!ReferenceEquals(_cachedFor, snapshot)) { Rebuild(snapshot); }
                return _defs;
            }
        }

        private static void Rebuild(ConfigSnapshot snapshot)
        {
            var defs = new Dictionary<(PieceKind, int), PieceDef>();
            var all = new List<PieceDef>();
            // 按 PieceConfig 的行顺序生成，保证遍历顺序稳定（All 的顺序曾经是模板添加顺序）。
            foreach (PieceKind kind in snapshot.PieceOrder)
            {
                PieceTemplate t = snapshot.Pieces[kind];
                for (int star = 1; star <= 3; star++)
                {
                    StarRule rule = snapshot.Stars[star];
                    // ★ 先乘再向下取整，与旧实现完全一致：盾战 2★ = (int)(120 × 2) = 240。
                    PieceDef def = new PieceDef(t.Kind, t.Class, (StarTier)star, t.DisplayName + " " + star + "★", (int)(t.Hp * rule.HpScale), (int)(t.Attack * rule.AttackScale), t.Pattern, t.MoveRange, t.IsRanged, SpriteNameOf(t.SpriteKey, star));
                    defs[(t.Kind, star)] = def;
                    all.Add(def);
                }
            }
            _defs = defs;
            _all = all;
            _cachedFor = snapshot;
        }

        /// <summary>立绘资源名约定：棋子_&lt;名&gt;&lt;星级&gt;_立绘（与 ArtRes/Pieces 下的文件一一对应；这是美术命名约定，不是数值）。</summary>
        private static string SpriteNameOf(string spriteKey, int star)
        {
            return "棋子_" + spriteKey + star + "_立绘";
        }

        /// <summary>外形 → 立绘文件名里的棋子名（来自 PieceConfig.SpriteKey）。</summary>
        public static string SpriteKeyOf(PieceKind kind)
        {
            return GameConfig.Current.Pieces[kind].SpriteKey;
        }

        /// <summary>
        /// 按外形 + 星级取定义。找不到返回 null（**不抛异常**：调用方要能优雅降级，
        /// 但必须自己判空 —— 这是框架“失败必须可见”的约定）。配置尚未装配则抛异常。
        /// </summary>
        public static PieceDef Get(PieceKind kind, StarTier star)
        {
            PieceDef def;
            if (Defs.TryGetValue((kind, (int)star), out def))
            {
                return def;
            }

            return null;
        }

        /// <summary>全部定义（给图鉴/编辑器工具遍历用）。</summary>
        public static IEnumerable<PieceDef> All
        {
            get
            {
                // 触发（必要时）重建后再返回缓存列表。
                Dictionary<(PieceKind, int), PieceDef> unused = Defs;
                return _all;
            }
        }

        /// <summary>我方该星级棋子占的格子边长（StarConfig.PlayerBodySize）。</summary>
        public static int PlayerBodySize(StarTier star)
        {
            return GameConfig.Current.Stars[(int)star].PlayerBodySize;
        }

        /// <summary>开局已在手牌池的棋种（PieceConfig.UnlockedAtStart=TRUE），按表内顺序。</summary>
        internal static HashSet<PieceKind> CreateInitialUnlockedSet()
        {
            ConfigSnapshot snapshot = GameConfig.Current;
            var set = new HashSet<PieceKind>();
            foreach (PieceKind kind in snapshot.PieceOrder)
            {
                if (snapshot.Pieces[kind].UnlockedAtStart) { set.Add(kind); }
            }
            return set;
        }
    }
}

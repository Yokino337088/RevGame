// ============================================================
// PieceCatalog.cs
// 基础数值与星级定义集中生成，棋子与卡牌读取同一份配置，避免两处面板显示不同的血攻。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>
    /// 棋子图鉴：9 种外形 × 3 星 = 27 份定义。
    ///
    /// ⚠️ 数值全部是**占位**（等后续 Excel 表接入后替换，见 README「数据表」一节）。
    /// 基础值待 Excel；星级倍率已按 v18：2★ 血×2/攻×1.8，3★ 血×3.6/攻×3.2（相对 1★）。
    /// 这样做的目的：先让玩法跑通、数值手感有梯度，等策划定表后只改这里与导表逻辑。
    /// </summary>
    public static class PieceCatalog
    {
        /// <summary>按（外形, 星级）索引的图鉴表；静态构造时一次生成。</summary>
        private static readonly Dictionary<(PieceKind, int), PieceDef> DefsByKey = new Dictionary<(PieceKind, int), PieceDef>();
        /// <summary>
        /// 每种外形的"基础档"（1★）模板：职业、血、攻、走法、射程。
        /// 走法取自策划案 §07-D（借象棋）：守卫=单步、射手=滑步、法师=炮、刺客=马步。
        /// </summary>
        private static readonly Dictionary<PieceKind, PieceDef> Templates = new Dictionary<PieceKind, PieceDef>();
        static PieceCatalog()
        {
            // 外形 → (职业, 中文名, 1★血, 1★攻, 走法, 射程, 是否远程)
            AddTemplate(PieceKind.Swordman, PieceClass.Assassin, "剑士", 60, 14, MovePattern.Step, 1, false);
            AddTemplate(PieceKind.Shield, PieceClass.Guard, "盾战", 120, 8, MovePattern.Step, 1, false);
            AddTemplate(PieceKind.HeavySword, PieceClass.Guard, "重剑", 100, 12, MovePattern.Step, 1, false);
            AddTemplate(PieceKind.Archer, PieceClass.Ranger, "弓箭手", 52, 15, MovePattern.Slide, 0, true);
            AddTemplate(PieceKind.Crossbow, PieceClass.Ranger, "重弩", 70, 18, MovePattern.Slide, 0, true);
            AddTemplate(PieceKind.MageWater, PieceClass.Mage, "水法师", 48, 16, MovePattern.Cannon, 0, true);
            AddTemplate(PieceKind.MageFire, PieceClass.Mage, "火法师", 44, 20, MovePattern.Cannon, 0, true);
            AddTemplate(PieceKind.Priest, PieceClass.Mage, "牧师", 56, 10, MovePattern.Cannon, 0, true);
            AddTemplate(PieceKind.Assassin, PieceClass.Assassin, "刺客", 46, 22, MovePattern.Knight, 1, false);
            // 每个模板生成 3 个星级（1★ 用模板本身，2★/3★ 按系数放大）。
            foreach (KeyValuePair<PieceKind, PieceDef> pair in Templates)
            {
                PieceDef t = pair.Value;
                for (int star = 1; star <= 3; star++)
                {
                    float hpScale = star == 1 ? 1f : (star == 2 ? Consts.TwoStarHpScale : Consts.ThreeStarHpScale);
                    float atkScale = star == 1 ? 1f : (star == 2 ? Consts.TwoStarAttackScale : Consts.ThreeStarAttackScale);
                    PieceDef def = new PieceDef(t.Kind, t.Class, (StarTier)star, t.DisplayName + " " + star + "★", (int)(t.MaxHp * hpScale), (int)(t.Attack * atkScale), t.Pattern, t.MoveRange, t.IsRanged, "棋子_" + SpriteKeyOf(t.Kind) + star + "_立绘");
                    DefsByKey[(t.Kind, star)] = def;
                }
            }
        }

        private static void AddTemplate(PieceKind kind, PieceClass pieceClass, string name, int hp, int atk, MovePattern pattern, int range, bool ranged)
        {
            Templates[kind] = new PieceDef(kind, pieceClass, StarTier.One, name, hp, atk, pattern, range, ranged, "");
        }

        /// <summary>外形 → 立绘文件名里的英文键（与 ArtRes/Pieces 下的文件一一对应）。</summary>
        public static string SpriteKeyOf(PieceKind kind)
        {
            switch (kind)
            {
                case PieceKind.Swordman:
                    return "剑士";
                case PieceKind.Shield:
                    return "盾战";
                case PieceKind.HeavySword:
                    return "重剑";
                case PieceKind.Archer:
                    return "弓箭";
                case PieceKind.Crossbow:
                    return "重弩";
                case PieceKind.MageWater:
                    return "水法师";
                case PieceKind.MageFire:
                    return "火法师";
                case PieceKind.Priest:
                    return "牧师";
                case PieceKind.Assassin:
                    return "刺客";
                default:
                    return "剑士";
            }
        }

        /// <summary>
        /// 按外形 + 星级取定义。找不到返回 null（**不抛异常**：调用方要能优雅降级，
        /// 但必须自己判空 —— 这是框架"失败必须可见"的约定）。
        /// </summary>
        public static PieceDef Get(PieceKind kind, StarTier star)
        {
            PieceDef def;
            if (DefsByKey.TryGetValue((kind, (int)star), out def))
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
                return DefsByKey.Values;
            }
        }
    }
}

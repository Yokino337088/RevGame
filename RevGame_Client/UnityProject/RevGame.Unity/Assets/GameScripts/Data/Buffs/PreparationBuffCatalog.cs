// ============================================================
// PreparationBuffCatalog.cs —— 准备期十六种正向增益的名称、说明与效果数值。
// 标题、说明模板、数值全部来自 Excel（配置表/增益与事件配置.xlsx 的 PrepBuffConfig）。
// 说明文字里的数字用占位符引用同一行数值（如 {Amount%}），所以“提示写什么”和“实际是多少”永远同源。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>增益的稳定业务标识；用它分派规则，用中文Title显示，避免改文案时改坏效果。</summary>
    public enum PreparationBuffKind
    {
        Upgrade, // 天赋觉醒：原位升星。
        Elite, // 重弩援军：安全加入强力棋子。
        Attack, // 全军奋起：全队攻击加成。
        Recovery, // 复苏储备：立即治疗与首次受伤后的储备治疗。
        EncirclementImmunity, // 不屈活棋：免疫围困惩罚。
        Haste, // 疾风连击：攻击提速，不改变移动。
        Vitality, // 生命绽放：本轮最大生命提升。
        Armor, // 坚壁守护：本轮减伤。
        LifeSteal, // 汲取之力：按实际伤害吸血。
        Regeneration, // 生生不息：周期回复全队。
        Shields, // 三重护盾：每棋抵挡三次伤害。
        Ranged, // 远射专精：远程专属攻击加成。
        Execution, // 破阵追击：追击残血敌人。
        EyePower, // 慧眼共鸣：成眼棋专属加成。
        Liberties, // 气脉畅通：增加本轮气数。
        ArrowVolley // 万箭齐发：仅己方弓箭手攻击频率翻倍，包括领取后入场的弓箭手。
    }
    /// <summary>不可变的增益定义；界面用同一份说明，避免提示与实际效果各说各的。</summary>
    public sealed class PreparationBuffDef
    {
        /// <summary>用于选择真正执行哪一种业务规则。</summary>
        public PreparationBuffKind Kind
        {
            get;
        }
        /// <summary>横幅的短标题，不参与规则判断。</summary>
        public string Title
        {
            get;
        }
        /// <summary>告诉玩家这一种增益具体改变什么；由 Excel 模板和同一行数值渲染，天然与实际效果一致。</summary>
        public string Detail
        {
            get;
        }
        /// <summary>主效果数值（比例用小数，次数/气数为整数值）；Excel 的 Amount。</summary>
        public float Amount
        {
            get;
        }
        /// <summary>副效果数值；Excel 的 Extra。</summary>
        public float Extra
        {
            get;
        }
        /// <summary>触发阈值；Excel 的 Threshold。</summary>
        public float Threshold
        {
            get;
        }
        /// <summary>援军棋子外形（仅 Elite 有意义）。</summary>
        public PieceKind Piece
        {
            get;
        }
        /// <summary>援军星级（仅 Elite 有意义）。</summary>
        public int Star
        {
            get;
        }
        public PreparationBuffDef(PreparationBuffKind kind, string title, string detail, float amount, float extra, float threshold, PieceKind piece, int star)
        {
            Kind = kind;
            Title = title;
            Detail = detail;
            Amount = amount;
            Extra = extra;
            Threshold = threshold;
            Piece = piece;
            Star = star;
        }
    }
    public static class PreparationBuffCatalog
    {
        /// <summary>
        /// 十六种增益，顺序 = Excel 行顺序。★ 每轮洗牌从这份顺序出发，所以调整行顺序会改变同一随机种子下的抽取结果。
        /// 随机算法洗牌后取 Prep.BuffCountMin～Prep.BuffCountMax 项，同一轮不重复。
        /// </summary>
        public static IReadOnlyList<PreparationBuffDef> All => GameConfig.Current.Buffs;

        /// <summary>按标识取定义（每个枚举值在装配时已保证恰好有一行）。</summary>
        public static PreparationBuffDef Get(PreparationBuffKind kind)
        {
            foreach (PreparationBuffDef def in GameConfig.Current.Buffs)
            {
                if (def.Kind == kind)
                {
                    return def;
                }
            }
            throw new System.InvalidOperationException("增益配置缺少 " + kind + "（装配阶段本应拦截）");
        }
    }
}

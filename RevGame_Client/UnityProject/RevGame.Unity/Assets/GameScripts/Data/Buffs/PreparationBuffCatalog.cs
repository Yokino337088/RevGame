// ============================================================
// PreparationBuffCatalog.cs —— 准备期十六种正向增益的名称与真实规则契约。
// 与战斗数值表分开：这是当前可玩的业务规则目录，尚未冒充已接入正式 Excel 数值。
// ============================================================
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

    /// <summary>不可变播报数据；界面用同一份说明，避免提示与实际效果各说各的。</summary>
    public sealed class PreparationBuffDef
    {
        /// <summary>用于选择真正执行哪一种业务规则。</summary>
        public PreparationBuffKind Kind { get; }
        /// <summary>横幅的短标题，不参与规则判断。</summary>
        public string Title { get; }
        /// <summary>告诉玩家这一种增益具体改变什么；应与服务中的实际参数同步维护。</summary>
        public string Detail { get; }
        public PreparationBuffDef(PreparationBuffKind kind, string title, string detail)
        {
            Kind = kind;
            Title = title;
            Detail = detail;
        }
    }

    public static class PreparationBuffCatalog
    {
        // 随机算法洗牌后取3～5项，同一轮不重复。永久结果只有升星与新增棋子，其余换轮清零。
        public static readonly PreparationBuffDef[] All =
        {
            new PreparationBuffDef(PreparationBuffKind.Upgrade, "天赋觉醒", "随机己方棋原位升一星；暂无目标则保留到下一颗可升级棋入场"),
            new PreparationBuffDef(PreparationBuffKind.Elite, "重弩援军", "免费加入一颗二星重弩，保护现有眼位；无空格则本轮攻击额外+10%"),
            new PreparationBuffDef(PreparationBuffKind.Attack, "全军奋起", "本轮全体攻击加成+25%，之后落下的棋也享受"),
            new PreparationBuffDef(PreparationBuffKind.Recovery, "复苏储备", "立即回复全队40%生命；本轮首次受伤后再回复全队40%"),
            new PreparationBuffDef(PreparationBuffKind.EncirclementImmunity, "不屈活棋", "本轮被围仍可攻击，免疫锁死和孤立增伤；气眼照常显示"),
            new PreparationBuffDef(PreparationBuffKind.Haste, "疾风连击", "本轮全体攻击速度+25%；不会加快移动或破坏阵型"),
            new PreparationBuffDef(PreparationBuffKind.Vitality, "生命绽放", "本轮全队最大生命+30%，按原有血量比例同步；换轮移除"),
            new PreparationBuffDef(PreparationBuffKind.Armor, "坚壁守护", "本轮全体受到伤害降低20%"),
            new PreparationBuffDef(PreparationBuffKind.LifeSteal, "汲取之力", "本轮普通攻击回复实际伤害20%的生命"),
            new PreparationBuffDef(PreparationBuffKind.Regeneration, "生生不息", "本轮战斗每4秒全队回复4%最大生命"),
            new PreparationBuffDef(PreparationBuffKind.Shields, "三重护盾", "每颗己方棋本轮抵挡前3次伤害；新入场棋同样获得"),
            new PreparationBuffDef(PreparationBuffKind.Ranged, "远射专精", "本轮远程棋攻击加成额外+35%"),
            new PreparationBuffDef(PreparationBuffKind.Execution, "破阵追击", "本轮攻击生命低于35%的敌人时，攻击加成额外+50%"),
            new PreparationBuffDef(PreparationBuffKind.EyePower, "慧眼共鸣", "本轮成眼棋攻击加成额外+30%，战斗每4秒回复6%最大生命"),
            new PreparationBuffDef(PreparationBuffKind.Liberties, "气脉畅通", "本轮全体额外获得2口气；之后落下的棋也享受"),
            new PreparationBuffDef(PreparationBuffKind.ArrowVolley, "万箭齐发", "本轮我方所有弓箭手攻击频率+100%；后入场弓箭手同样享受，不加快移动")
        };
    }
}

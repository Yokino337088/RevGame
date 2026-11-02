using System;
using RevGame.Core;
using RevGame.Data;
using Revolution;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽数值的唯一读取入口。数据来自独立Excel，借用框架导表和加载门面；
    /// 不给缺项偷偷填默认值，否则玩家看到的规则和实际计算会分叉。
    /// </summary>
    public static class EndlessConfig
    {
        /// <summary>入场前校验数据和有限结构容量；错误由过场显示，不能带着半份配置开局。</summary>
        public static void Validate()
        {
            foreach (EndlessRule row in RevDataTableManager.Get<EndlessRuleTable>().All)
            {
                Value(row.Key);
            }
            for (int i = 0; i < 6; i++)
            {
                Route(i);
            }
            for (int i = 0; i < 4; i++)
            {
                Boss(i);
            }
            // 六章每章都要有专属技，招式必须有逐格几何，否则红格子和真实伤害会对不上。
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                if (!BossSkillCatalog.IsExpanded(ParseSkill(BossSkill(chapter))))
                {
                    throw new InvalidOperationException("无尽专属技必须复用逐格几何招式：第" + chapter + "章");
                }
            }
            if (RevDataTableManager.Get<EndlessMusicTable>().All.Count == 0)
            {
                throw new InvalidOperationException("无尽BGM曲目表为空");
            }
            // 组合技：棋种名和效果名写错会在入场时直接报出是哪一行，不会打到一半才发现不生效。
            foreach (EndlessCombo row in RevDataTableManager.Get<EndlessComboTable>().All)
            {
                ComboKindA(row);
                ComboKindB(row);
                ComboEffect(row);
                if (row.Every <= 0 || row.Count <= 0 || row.Power <= 0 || row.Radius < 0 || row.FontSize <= 0 || string.IsNullOrEmpty(row.Name))
                {
                    throw new InvalidOperationException("无尽组合技数值非法：" + row.Id);
                }
                if (!UnityEngine.ColorUtility.TryParseHtmlString(row.ColorHex, out _))
                {
                    throw new InvalidOperationException("无尽组合技颜色不是十六进制：" + row.Id);
                }
            }
            // 剑气参数在下方统一读取一次，缺项或非正数会立刻报错。
            Value("AuraRadius");
            Value("AuraStarRadius");
            Value("AuraTickSeconds");
            Value("AuraDamageRatio");
            Count("AuraTargetLimit");
            Value("MeleeMoveSeconds");
            // 开局棋阵：三个棋阵都要有棋子，棋种名、坐标合法且不压棋心（具体占格冲突由开局时 Board.Place 再检查一次）。
            var formations = new bool[3];
            foreach (EndlessFormation row in RevDataTableManager.Get<EndlessFormationTable>().All)
            {
                FormationKind(row);
                if (row.Formation < 0 || row.Formation > 2 || row.X < 0 || row.X > 8 || row.Y < 0 || row.Y > 8 || (row.X == 4 && row.Y == 4))
                {
                    throw new InvalidOperationException("无尽开局棋阵坐标或棋阵编号非法：" + row.Id);
                }
                formations[row.Formation] = true;
            }
            if (!formations[0] || !formations[1] || !formations[2])
            {
                throw new InvalidOperationException("无尽开局棋阵缺少某个棋阵的棋子");
            }
            for (int i = 0; i < RevDataTableManager.Get<EndlessMusicTable>().All.Count; i++)
            {
                Music(i);
            }
            if (Count("MainSlots") > 6 || Count("SealSlots") > 6 || Count("HandLimit") > 6 || Count("HordeLimit") > 200
            || Count("PlayerCells") + Count("EnemyCells") > 81 || Count("ChainLimit") > 8 || Value("DamageReductionCap") >= 1
            || Value("SimulationStep") > Value("CounterSeconds") || Value("PeakStart") >= Value("TailStart") || Value("TailStart") >= Value("MinuteSeconds"))
            {
                throw new InvalidOperationException("无尽配置超出已验证容量或时间顺序");
            }
        }
        public static float Value(string key)
        {
            if (!RevDataTableManager.Get<EndlessRuleTable>().FindByKey(key, out EndlessRule row))
            {
                throw new InvalidOperationException("无尽参数缺失：" + key);
            }
            if (float.IsNaN(row.Value) || float.IsInfinity(row.Value) || row.Value <= 0)
            {
                throw new InvalidOperationException("无尽参数必须为有限正数：" + key);
            }
            return row.Value;
        }
        public static int Count(string key)
        {
            float value = Value(key);
            if (value != (int)value)
            {
                throw new InvalidOperationException("无尽数量参数必须为整数：" + key);
            }
            return (int)value;
        }
        public static EndlessRoute Route(int id)
        {
            if (!RevDataTableManager.Get<EndlessRouteTable>().FindByKey(id, out EndlessRoute row))
            {
                throw new InvalidOperationException("无尽流派不存在：" + id);
            }
            return row;
        }
        /// <summary>读取某一章领主的无尽专属技配置。</summary>
        public static EndlessBossSkill BossSkill(int chapter)
        {
            if (!RevDataTableManager.Get<EndlessBossSkillTable>().FindByKey(chapter, out EndlessBossSkill row))
            {
                throw new InvalidOperationException("无尽专属技不存在，章节：" + chapter);
            }
            return row;
        }
        /// <summary>把配置表里的招式名字转成枚举。名字写错会在入场校验时立刻报错，而不是打到一半才发现。</summary>
        public static BossSkillKind ParseSkill(EndlessBossSkill row)
        {
            if (!Enum.TryParse(row.FxSkill, out BossSkillKind kind))
            {
                throw new InvalidOperationException("无尽专属技招式名不存在：" + row.FxSkill);
            }
            return kind;
        }
        /// <summary>按播放顺序读取一首 BGM 的配置。</summary>
        public static EndlessMusic Music(int id)
        {
            if (!RevDataTableManager.Get<EndlessMusicTable>().FindByKey(id, out EndlessMusic row))
            {
                throw new InvalidOperationException("无尽BGM曲目不存在：" + id);
            }
            return row;
        }
        /// <summary>开局棋阵一行的棋种（Excel 写 PieceKind 枚举名）。</summary>
        public static PieceKind FormationKind(EndlessFormation row)
        {
            return ParseKind(row.Kind, row.Id);
        }
        /// <summary>组合技的触发棋种（Excel 里写 PieceKind 枚举名）。</summary>
        public static PieceKind ComboKindA(EndlessCombo row)
        {
            return ParseKind(row.KindA, row.Id);
        }
        /// <summary>组合技需要同时在场的搭档棋种。</summary>
        public static PieceKind ComboKindB(EndlessCombo row)
        {
            return ParseKind(row.KindB, row.Id);
        }
        /// <summary>组合技效果类型（Excel 写枚举名）。</summary>
        public static EndlessComboEffect ComboEffect(EndlessCombo row)
        {
            if (!Enum.TryParse(row.Effect, out EndlessComboEffect effect))
            {
                throw new InvalidOperationException("无尽组合技效果名不存在：" + row.Effect + "（编号 " + row.Id + "）");
            }
            return effect;
        }
        private static PieceKind ParseKind(string name, int id)
        {
            if (!Enum.TryParse(name, out PieceKind kind))
            {
                throw new InvalidOperationException("无尽组合技棋种名不存在：" + name + "（编号 " + id + "）");
            }
            return kind;
        }
        public static EndlessBoss Boss(int id)
        {
            if (!RevDataTableManager.Get<EndlessBossTable>().FindByKey(id, out EndlessBoss row))
            {
                throw new InvalidOperationException("无尽领主不存在：" + id);
            }
            return row;
        }
    }
}

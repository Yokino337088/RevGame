// ============================================================
// GameTextConfig.cs —— 读取框架已加载的导表结果，不负责加载资源。
// Excel 是文案/动画参数的真源；禁止在表现脚本再维护一份同名文案，避免改表不生效。
// ============================================================
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public static class GameTextConfig
    {
        /// <summary>只读已异步加载完成的表；缺行即报错，不偷偷用旧文案掩盖导表问题。</summary>
        public static BattleText Require(string id)
        {
            BattleTextTable table = RevDataTableManager.Get<BattleTextTable>();
            if (table == null || !table.FindByKey(id, out BattleText row))
            {
                throw new System.InvalidOperationException("弹幕配置缺少 " + id + "，请检查 Excel 并重新导表");
            }
            return row;
        }

        /// <summary>将表中的 #RRGGBB 或 #RRGGBBAA 转为 Unity 颜色；错误明确指出配置编号。</summary>
        public static Color ColorOf(BattleText row)
        {
            if (!ColorUtility.TryParseHtmlString(row.ColorHex, out Color color))
            {
                throw new System.InvalidOperationException("弹幕颜色不是有效十六进制：" + row.Id);
            }
            return color;
        }

        /// <summary>开局先校验所有会用到的编号，缺行时立即报中文原因，避免战斗中途才丢失表现。</summary>
        public static void Validate()
        {
            foreach (Core.PieceKind kind in System.Enum.GetValues(typeof(Core.PieceKind))) { Require("Attack_" + kind); }
            Require("Upgrade_2");
            Require("Upgrade_3");
            Require("Damage");
            for (int i = 0; i < 8; i++) { Require("Event_" + Data.BattleEventCatalog.NameOf((Data.BattleEventKind)i)); }
            Require("Event_决胜支援");
            foreach (BattleText row in RevDataTableManager.Get<BattleTextTable>().All)
            {
                ColorOf(row);
                // NaN 与任何数比较都为 false，必须显式拒绝，否则会绕过区间检查并破坏 Tween 时长。
                if (string.IsNullOrWhiteSpace(row.Text) || float.IsNaN(row.Duration) || float.IsNaN(row.PeakScale))
                {
                    throw new System.InvalidOperationException("弹幕标题不能为空、时长和倍率不能为 NaN：" + row.Id);
                }
                if (row.FontSize < 12 || row.FontSize > 150 || row.Duration < 0.6f || row.Duration > 10f || row.PeakScale < 1f || row.PeakScale > 3f)
                {
                    throw new System.InvalidOperationException("弹幕参数超出可读范围：" + row.Id + "（字号12~150，时长0.6~10秒，倍率1~3）");
                }
            }
        }
    }
}

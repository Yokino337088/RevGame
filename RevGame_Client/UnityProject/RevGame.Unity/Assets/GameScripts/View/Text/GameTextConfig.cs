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
        // 表对象更换时自动失效；不可缓存跨卸载的资源引用。颜色只解析一次，命中热点不反复解析HEX。
        private static BattleTextTable _cachedTable;
        private static readonly System.Collections.Generic.Dictionary<string, Color> Colors = new System.Collections.Generic.Dictionary<string, Color>();
        private static readonly System.Collections.Generic.Dictionary<Core.BattleDamageKind, BattleText> DamageRows = new System.Collections.Generic.Dictionary<Core.BattleDamageKind, BattleText>();
        // 有界缓存常见伤害文案，整场最多128种；表切换立即清空，不能让长战斗收集无限数字字符串。
        private struct DamageTextKey : System.IEquatable<DamageTextKey>
        {
            internal string Row;
            internal long Number;
            public bool Equals(DamageTextKey other) { return Row == other.Row && Number == other.Number; }
            public override bool Equals(object other) { return other is DamageTextKey key && Equals(key); }
            public override int GetHashCode() { return (Row.GetHashCode() * 397) ^ Number.GetHashCode(); }
        }
        private static readonly System.Collections.Generic.Dictionary<DamageTextKey, string> DamageTextCache = new System.Collections.Generic.Dictionary<DamageTextKey, string>(128);
        private static readonly DamageTextKey[] DamageTextSlots = new DamageTextKey[128];
        private static int _damageTextCursor;
        public static long DamageTextCacheHits { get; private set; }
        public static long DamageTextCacheMisses { get; private set; }
        // 关闭Domain Reload时静态缓存仍活着；Play启动必须断开上次表引用，避免跨会话旧文案。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            _cachedTable = null;
            Colors.Clear(); DamageRows.Clear(); DamageTextCache.Clear();
            System.Array.Clear(DamageTextSlots, 0, DamageTextSlots.Length);
            _damageTextCursor = 0;
            DamageTextCacheHits = DamageTextCacheMisses = 0;
        }
        private static void EnsureCache()
        {
            BattleTextTable table = RevDataTableManager.Get<BattleTextTable>();
            if (table == _cachedTable) { return; }
            _cachedTable = table; Colors.Clear(); DamageRows.Clear(); DamageTextCache.Clear();
            System.Array.Clear(DamageTextSlots, 0, DamageTextSlots.Length); _damageTextCursor = 0;
            DamageTextCacheHits = DamageTextCacheMisses = 0;
        }

        /// <summary>仅飘字放大：long容纳int最大伤害乘十，规则/计分仍使用原始int。倍率由Excel读取。</summary>
        public static long DisplayDamage(int actualDamage)
        {
            return (long)actualDamage * Data.Balance.DamageDisplayMultiplier;
        }
        /// <summary>只有新数值才格式化；倍率与文案同源，使用显示值做键，改倍率后不会命中旧数字。</summary>
        public static string FormatDamage(BattleText row, int actualDamage)
        {
            EnsureCache();
            var key = new DamageTextKey { Row = row.Id, Number = DisplayDamage(actualDamage) };
            if (DamageTextCache.TryGetValue(key, out string content)) { DamageTextCacheHits++; return content; }
            DamageTextCacheMisses++;
            // 游标始终在0～127内循环；长期战斗也不依赖int溢出后的负数取模。
            int slot = _damageTextCursor;
            _damageTextCursor = (slot + 1) % DamageTextSlots.Length;
            if (DamageTextSlots[slot].Row != null) { DamageTextCache.Remove(DamageTextSlots[slot]); }
            content = row.Text.Replace("{damage}", key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DamageTextSlots[slot] = key; DamageTextCache[key] = content; return content;
        }

        public static BattleText Damage(Core.BattleDamageKind kind, Core.GameSide side)
        {
            EnsureCache();
            if (kind == Core.BattleDamageKind.Normal) { return Require(side == Core.GameSide.Player ? "Damage_Incoming" : "Damage"); }
            if (!DamageRows.TryGetValue(kind, out BattleText row)) { row = Require("Damage_" + kind); DamageRows.Add(kind, row); }
            return row;
        }
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
            EnsureCache();
            if (Colors.TryGetValue(row.Id, out Color cached)) { return cached; }
            if (!ColorUtility.TryParseHtmlString(row.ColorHex, out Color color))
            {
                throw new System.InvalidOperationException("弹幕颜色不是有效十六进制：" + row.Id);
            }
            Colors[row.Id] = color;
            return color;
        }

        /// <summary>开局先校验所有会用到的编号，缺行时立即报中文原因，避免战斗中途才丢失表现。</summary>
        public static void Validate()
        {
            foreach (Core.PieceKind kind in System.Enum.GetValues(typeof(Core.PieceKind))) { Require("Attack_" + kind); }
            Require("Upgrade_2");
            Require("Upgrade_3");
            Require("Damage");
            Require("Damage_Incoming");
            foreach (Core.BattleDamageKind kind in System.Enum.GetValues(typeof(Core.BattleDamageKind)))
            {
                if (kind != Core.BattleDamageKind.Normal) { Require("Damage_" + kind); }
            }
            Require("EnemyDeployment");
            // Boss 技能预警提示样式（标题 {skill}、副标题 {hint}），缺这一行时启动阶段就报错。
            Require("BossSkill");
            foreach (Data.BattleEventKind kind in System.Enum.GetValues(typeof(Data.BattleEventKind))) { Require("Event_" + Data.BattleEventCatalog.NameOf(kind)); }
            Require("Event_能量巨大");
            Require("Event_眼位成型");
            Require("Event_眼位复苏");
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

// ============================================================
// GameTextConfig.cs —— 读取框架已加载的导表结果，不负责加载资源。
// Excel 是文案/动画参数的真源；禁止在表现脚本再维护一份同名文案，避免改表不生效。
// ============================================================
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    // 表现文案缓存的唯一读取工具：从RevDataTableManager获取已加载表，不自己读文件或加载AB。
    // 缓存跨调用复用，但表对象更换/新Play时清空；显示伤害乘十不改变实际HP或积分。
    public static class GameTextConfig
    {
        // 表对象更换时自动失效；不可缓存跨卸载的资源引用。颜色只解析一次，命中热点不反复解析HEX。
        private static BattleTextTable _cachedTable;
        // 配置行Id→已解析颜色，避免每击重复解析#RRGGBB；表对象变化时与其它文案缓存一起清空。
        private static readonly System.Collections.Generic.Dictionary<string, Color> Colors = new System.Collections.Generic.Dictionary<string, Color>();
        // 技能伤害类型→配置行，省掉每次拼接配置Id；普通伤害仍按阵营区分受击/输出文案。
        private static readonly System.Collections.Generic.Dictionary<Core.BattleDamageKind, BattleText> DamageRows = new System.Collections.Generic.Dictionary<Core.BattleDamageKind, BattleText>();
        // 有界缓存常见伤害文案，整场最多128种；表切换立即清空，不能让长战斗收集无限数字字符串。
        // 一个缓存键由“文案行Id+显示数字”组成：同样100伤害，暴击和普通行可以有不同文字，不能只按数字缓存。
        private struct DamageTextKey : System.IEquatable<DamageTextKey>
        {
            // 文案行Id；前置Validate保证对应行存在。这里保存字符串身份，不保存棋子或Unity对象。
            internal string Row;
            // 乘表现倍率后的显示值，单位为显示伤害点；long容纳int最大值乘十，避免展示溢出为负数。
            internal long Number;
            // 字典比较键时同时比较行与数字；两者不同就不能复用上一次格式化字符串。
            public bool Equals(DamageTextKey other) { return Row == other.Row && Number == other.Number; }
            public override bool Equals(object other) { return other is DamageTextKey key && Equals(key); }
            // 哈希只是查找入口，碰撞时Dictionary仍用Equals核对；397是组合哈希常量，不是玩法数值。
            public override int GetHashCode() { return (Row.GetHashCode() * 397) ^ Number.GetHashCode(); }
        }
        // 最多128种显示文案，不无限保存整场所有伤害值。存的是已格式化字符串，命中时无需ToString/Replace。
        private static readonly System.Collections.Generic.Dictionary<DamageTextKey, string> DamageTextCache = new System.Collections.Generic.Dictionary<DamageTextKey, string>(128);
        // 环形槽记录淘汰顺序。第129个新键覆盖第1槽，先删除旧字典键，缓存规模始终受控。
        private static readonly DamageTextKey[] DamageTextSlots = new DamageTextKey[128];
        // 下一个可覆盖槽，始终0～127。命中不移动，未命中才轮转，这是有界FIFO缓存而非LRU。
        private static int _damageTextCursor;
        // 复用已有格式化文案的累计次数；用于摘要命中率，不是CPU硬件L1/L2缓存计数。
        public static long DamageTextCacheHits { get; private set; }
        // 本会话需要格式化新键的次数；表切换会清零，因此性能采样记录前后差值。
        public static long DamageTextCacheMisses { get; private set; }
        // 关闭Domain Reload时静态缓存仍活着；Play启动必须断开上次表引用，避免跨会话旧文案。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        // Unity每次新Play启动清静态引用，即使禁用了Domain Reload也不留上一次表或数字字符串。
        private static void ResetSession()
        {
            _cachedTable = null;
            Colors.Clear(); DamageRows.Clear(); DamageTextCache.Clear();
            System.Array.Clear(DamageTextSlots, 0, DamageTextSlots.Length);
            _damageTextCursor = 0;
            DamageTextCacheHits = DamageTextCacheMisses = 0;
        }
        // 先从框架取当前表身份，若换表就全部失效；不能让旧行颜色/文案跨资源卸载继续生效。
        // 业务正常通过重新加载表更新配置，不支持原地修改同一表对象后假装缓存知道内容已变化。
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
        // 先算“显示数字”再查键，只有未命中才生成字符串。128是内存预算，不是策划伤害倍率。
        // 此缓存保留正确文案，不合并实际伤害，也不影响积分入账。
        public static string FormatDamage(BattleText row, int actualDamage)
        {
            EnsureCache();
            var key = new DamageTextKey { Row = row.Id, Number = DisplayDamage(actualDamage) };
            if (DamageTextCache.TryGetValue(key, out string content)) { DamageTextCacheHits++; return content; }
            DamageTextCacheMisses++;
            // 游标始终在0～127内循环；长期战斗也不依赖int溢出后的负数取模。
            int slot = _damageTextCursor;
            _damageTextCursor = (slot + 1) % DamageTextSlots.Length;
            // 未命中时先移除当前环形槽旧键，再格式化并写新键；否则槽循环了，字典却仍无限增长。
            if (DamageTextSlots[slot].Row != null) { DamageTextCache.Remove(DamageTextSlots[slot]); }
            content = row.Text.Replace("{damage}", key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            DamageTextSlots[slot] = key; DamageTextCache[key] = content; return content;
        }

        // 把类型与阵营映射到已校验配置行；只缓存与类型有关的技能行，普通输出/受击仍分别选择。
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

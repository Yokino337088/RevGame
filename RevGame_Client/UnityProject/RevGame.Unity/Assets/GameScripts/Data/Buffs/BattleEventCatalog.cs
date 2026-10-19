// ============================================================
// BattleEventCatalog.cs —— 八种战斗事件的标识、名称和效果参数。
// 名称、稀有分组、效果量都来自 Excel（配置表/增益与事件配置.xlsx 的 BattleEventConfig / EventRotationConfig）。
// 事件只从这张表选择，日志、图标和 GM 使用同一个标识，避免各写一套字符串。
// ============================================================
using System.Collections.Generic;

namespace RevGame.Data
{
    public enum BattleEventKind
    {
        Reinforcement,
        Liberty,
        Heal,
        Combo,
        Eye,
        BreakChain,
        Teleport,
        Artillery
    }

    public static class BattleEventCatalog
    {
        /// <summary>事件中文名（同时是弹幕表 Event_&lt;名称&gt; 的后缀）。</summary>
        public static string NameOf(BattleEventKind kind)
        {
            return DefOf(kind).Name;
        }

        /// <summary>事件的效果参数（Amount/Cap 的含义见 Excel 说明页）。</summary>
        public static BattleEventDef DefOf(BattleEventKind kind)
        {
            return GameConfig.Current.Events[kind];
        }

        /// <summary>通关辅助开启时按顺序轮换的事件序列（EventRotationConfig，同一事件可出现多次）。</summary>
        internal static IReadOnlyList<BattleEventKind> Rotation => GameConfig.Current.EventRotation;

        /// <summary>关闭通关辅助时的随机池：IsRare=false 的普通组 / IsRare=true 的稀有组，组内保持表内顺序。</summary>
        internal static IReadOnlyList<BattleEventKind> Pool(bool rare)
        {
            return rare ? GameConfig.Current.RareEvents : GameConfig.Current.CommonEvents;
        }
    }
}

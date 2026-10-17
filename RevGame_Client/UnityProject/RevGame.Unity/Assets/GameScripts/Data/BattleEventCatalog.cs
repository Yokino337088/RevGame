// ============================================================
// BattleEventCatalog.cs —— 八种战斗事件的标识和说明。
// 事件只从这张表选择，日志、图标和 GM 使用同一个标识，避免各写一套字符串。
// ============================================================
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
        private static readonly string[] Names =
        {
            "天降援兵",
            "回气",
            "疗愈雨",
            "连击",
            "补眼",
            "断链",
            "守阵回气",
            "炮火支援"
        };
        public static string NameOf(BattleEventKind kind)
        {
            return Names[(int)kind];
        }
    }
}

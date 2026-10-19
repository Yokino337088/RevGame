// ============================================================
// GameConfigTestSupport.cs —— 编辑器断言开跑前确保“游戏配置已装配”。
//
// 为什么需要：棋子血攻、增益、关卡、奖励、战斗参数全部来自 Excel 导表数据，没装配就读取会立刻报错（这是有意的）。
// 正式游戏由 GameFlow 在启动加载阶段用 RevDataTableManager 走资源系统装配（出包后数据在 AB 包 data_tables 里）；
// 而 GameRuleChecks 这类“非 Play 断言”没有执行过 RevInitialize，资源策略链没装配，拿不到资源系统，
// 所以这里退一步直接读编辑器磁盘上的同一份 txt。两条路共用同一批生成表类、同一个解析器和同一个校验入口，
// 差别只有“文本从哪来”，因此断言结论对真机依然成立。
//
// ★ 规则：Play 中已经由 GameFlow 装配好的配置直接复用，绝不覆盖——断言要测的是游戏真正在用的那一份。
// ============================================================
using RevGame.Data;

namespace RevGame.Editor
{
    internal static class GameConfigTestSupport
    {
        /// <summary>导表数据所在目录（与导表工具的数据目录一致，即资源根目录下的 Data）。</summary>
        private const string DataDirectory = "Assets/GameRes/Data";

        /// <summary>已装配则什么都不做；否则从磁盘上的导表数据文件装配。失败会抛带中文原因的异常。</summary>
        public static void Ensure()
        {
            if (GameConfig.IsLoaded) { return; }
            GameConfigFileLoader.LoadFromDirectory(DataDirectory);
        }
    }
}

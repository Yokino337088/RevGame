// ============================================================
// GameConfigFileLoader.cs —— 从磁盘上的导表数据文件装配配置（仅编辑器 / 工程外测试）。
//
// ★ 为什么要用编译开关把整个类挡在真机包之外：
//   GameRes/Data/*.txt 在出包后**不是磁盘上的散文件**，它们被打进 AB 包 data_tables。
//   真机上 File.ReadAllText("Assets/GameRes/Data/PieceConfig.txt") 必然失败，
//   而且自己拼路径读资源会绕过资源系统的缓存、引用计数、分组卸载和热更（框架铁律：资源只走逻辑路径）。
//   所以这个类只在以下两种“没有资源系统”的场合存在：
//     1) UNITY_EDITOR：编辑器里的非 Play 断言（GameRuleChecks 等没有调用过 RevInitialize，策略链未装配）；
//     2) REVGAME_OFFLINE_TESTS：工程外的纯 C# 控制台测试（等价性比对、配置反向测试）。
//   正式运行时一律走 GameFlow.LoadGameConfigAsync 里的 RevDataTableManager.LoadAsync。
//
//   它与运行时共用同一批生成的表类、同一个 RevDataTextFormat 解析和同一个 GameConfig.Initialize 校验，
//   区别只有“这段文本从哪来”——所以编辑器断言的结论对真机依然成立。
// ============================================================
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System.Collections.Generic;
using System.IO;
using Revolution;

namespace RevGame.Data
{
    public static class GameConfigFileLoader
    {
        /// <summary>读取目录下 12 张游戏配置表并装配；任何一张表缺文件或有解析错误行都会抛出带原因的异常。</summary>
        public static void LoadFromDirectory(string dataDirectory)
        {
            var tables = new GameConfigTables
            {
                Pieces = Read(new PieceConfigTable(), dataDirectory),
                Stars = Read(new StarConfigTable(), dataDirectory),
                PrepBuffs = Read(new PrepBuffConfigTable(), dataDirectory),
                BattleEvents = Read(new BattleEventConfigTable(), dataDirectory),
                EventRotation = Read(new EventRotationConfigTable(), dataDirectory),
                Chapters = Read(new ChapterConfigTable(), dataDirectory),
                EnemyRoster = Read(new EnemyRosterConfigTable(), dataDirectory),
                EnemyReinforce = Read(new EnemyReinforceConfigTable(), dataDirectory),
                Rewards = Read(new RewardConfigTable(), dataDirectory),
                RewardBoosts = Read(new RewardBoostConfigTable(), dataDirectory),
                BossSkills = Read(new BossSkillConfigTable(), dataDirectory),
                Balance = Read(new BalanceRuleTable(), dataDirectory),
            };
            GameConfig.Initialize(tables);
        }

        /// <summary>
        /// 文件名用表自己的 ResourceName，而不是另写一份字符串常量：
        /// 这样改了 Excel 表名、重新导表后，这里和 RevDataTableManager 读的是同一个名字，不会一边对一边错。
        /// </summary>
        private static T Read<T>(T table, string dataDirectory) where T : RevIDataTable
        {
            string path = Path.Combine(dataDirectory, table.ResourceName + ".txt");
            if (!File.Exists(path))
            {
                throw new GameConfigException(new List<string> { "找不到配置数据文件 " + path + "（请先用导表工具导出）" });
            }
            int errors;
            table.LoadText(File.ReadAllText(path), out errors);
            if (errors > 0)
            {
                // 单行解析失败/主键重复在运行时只是“跳过这行”，但配置缺一行就是缺一个数，不能当没发生。
                throw new GameConfigException(new List<string> { "配置数据文件 " + table.ResourceName + ".txt 有 " + errors + " 行无法解析或主键重复（重新导表，或检查 Excel）" });
            }
            return table;
        }
    }
}
#endif

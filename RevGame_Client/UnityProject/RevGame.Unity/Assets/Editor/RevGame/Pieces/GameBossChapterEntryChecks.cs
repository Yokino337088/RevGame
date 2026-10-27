// 六章直达入口的独立配置/注册检查；不进入Play、不切场景、不创建游戏棋盘视图。
using System;
using System.Linq;
using RevGame.Bootstrap;
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;
using Revolution;

namespace RevGame.Editor
{
    public static class GameBossChapterEntryChecks
    {
        public static string Run()
        {
            if (UnityEngine.Application.isPlaying) { throw new InvalidOperationException("Boss直达独立检查只能在编辑态执行"); }
            bool configAlreadyLoaded = GameConfig.IsLoaded;
            bool commandsAlreadyRegistered = RevGM.Commands.Any(c => c.Name == "RevGame/第一章Boss关卡");
            int checks = 0;
            try
            {
                GameConfigTestSupport.Ensure();
                var battle = new BattleService(false, 1027);
                for (int chapter = 1; chapter <= Consts.ChapterCount; chapter++)
                {
                    Require(battle.BeginBossCampaignForDevelopment(chapter), battle.LastError, ref checks);
                    Require(battle.Chapter == chapter && battle.Round == Balance.BossRoundIndex && battle.IsBossRound
                        && battle.Phase == GamePhase.Prepare, "没有进入目标Boss准备回合", ref checks);
                    var bosses = battle.EnemyPieces.Where(p => p.IsBoss && p.IsAlive).ToArray();
                    var lineup = LevelCatalog.BossOf(chapter);
                    Require(bosses.Length == 1 && lineup.BodySizes.Count == 1 && bosses.Count(p => p.IsKing) == 1, "Boss数量/唯一主将错误", ref checks);
                    for (int index = 0; index < bosses.Length; index++)
                    {
                        Require(bosses[index].Def.Kind == lineup.Kind && bosses[index].Def.Star == lineup.Star
                            && bosses[index].Size == lineup.BodySizes[index], "Boss阵容没有读取真实章节配置", ref checks);
                    }
                    Require(battle.Hand.Count == 6 && battle.CardsRemaining == 6 && battle.BattleElapsed == 0, "手牌/落子次数/时间错误", ref checks);
                    Require(battle.PlayerPieces.Count == 0 && battle.UnlockedChapter == 1, "旧玩家棋子残留或修改解锁", ref checks);
                    int oldVersion = battle.Board.TopologyVersion;
                    Require(!battle.BeginBossCampaignForDevelopment(0) && battle.Chapter == chapter
                        && battle.Board.TopologyVersion == oldVersion, "非法章节改变当前棋盘", ref checks);
                    Require(!battle.BeginBossCampaignForDevelopment(7) && battle.Chapter == chapter, "超界章节未拒绝", ref checks);
                    Require(battle.BeginBossCampaignForDevelopment(chapter) && battle.Hand.Count == 6
                        && battle.EnemyPieces.Count(p => p.IsBoss) == 1, "同章重复重开产生残留或少牌", ref checks);
                }
                Require(battle.BeginCampaign(1) && battle.Round == 1 && !battle.IsBossRound, "普通开局被Boss入口污染", ref checks);
                Require(!battle.BeginCampaign(2) && battle.Chapter == 1, "正常入口绕过解锁", ref checks);
                // 仅编辑器GM目录，不执行有效造局命令；非法参数在取得游戏服务前应被校验拒绝。
                GameGmCommands.RegisterAll();
                foreach (string label in new[] { "第一章", "第二章", "第三章", "第四章", "第五章", "第六章" })
                { Require(RevGM.Commands.Count(c => c.Name == "RevGame/" + label + "Boss关卡") == 1, "章别GM缺失或重复", ref checks); }
                Require(RevGM.Commands.Any(c => c.Name == "RevGame/进入Boss关卡")
                    && RevGM.Commands.Any(c => c.Name == "RevGame/Boss关卡就绪"), "通用/只读查询GM未注册", ref checks);
                Require(!RevGM.Execute("RevGame/进入Boss关卡 0").Success && !RevGM.Execute("RevGame/Boss关卡就绪 7").Success,
                    "非法GM参数没有拒绝", ref checks);
                return "六章真实Boss回合/阵容/手牌/非法请求与8条GM注册检查通过：" + checks + "条；未启动Play或切场景";
            }
            finally
            {
                if (!commandsAlreadyRegistered) { GameGmCommands.UnregisterAll(); }
                if (!configAlreadyLoaded) { GameConfig.Reset(); }
            }
        }
        private static void Require(bool condition, string message, ref int checks)
        {
            if (!condition) { throw new InvalidOperationException("Boss入口检查失败：" + message); }
            checks++;
        }
    }
}

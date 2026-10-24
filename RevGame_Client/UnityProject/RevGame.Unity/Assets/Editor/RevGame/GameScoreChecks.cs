using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;

namespace RevGame.Editor
{
    /// <summary>积分/回合/阵型/落子节奏的规则验收；实际UI及Shader另用Play夹具检验。</summary>
    public static partial class GameScoreChecks
    {
        private static int _checks;
        public static string RunRules()
        {
            GameConfigFileLoader.LoadFromDirectory("Assets/GameRes/Data"); _checks = 0;
            BattleService.VerifyScoreRules(Require);
            Require(Balance.RoundsPerChapter == 10 && Balance.BossRoundIndex == 10, "每章10回合且第10回合Boss");
            Require(Balance.DeployBatchCount <= 2, "每窗口最多2子");
            for (int ai = 1; ai <= Balance.EnemyAiMaxLevel; ai++)
            {
                float interval = Math.Max(Balance.DeployIntervalMinSeconds, Balance.DeployIntervalBaseSeconds - ai * Balance.DeployIntervalPerLevelSeconds);
                Require(interval >= 1f && interval <= 2f, "AI" + ai + "增援间隔1～2秒");
            }
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                string previous = ""; var layouts = new HashSet<string>();
                for (int round = 1; round <= Balance.RoundsPerChapter; round++)
                {
                    var battle = new BattleService(false, 381);
                    try
                    {
                        Require(battle.StartRound(chapter, round), "完整初始敌阵可生成：" + chapter + "/" + round);
                        string layout = battle.Board.Dump();
                        Require(layout != previous, "相邻回合布局不同：" + chapter + "/" + round);
                        previous = layout; layouts.Add(layout);
                        Require(battle.EnemyPieces.Count >= Balance.EnemyCountBase, "初始人数达到Excel基础配置");
                        foreach (var enemy in battle.EnemyPieces) { Require(!enemy.IsLocked, "出生棋有气且完整占格"); }
                    }
                    finally { battle.StopEnemyAI(); }
                }
                Require(layouts.Count == Balance.RoundsPerChapter, "同一章每回合均有不同完整布局");
            }
            string result = "通过 " + _checks + " 条积分/10回合/60局敌阵/节奏规则断言";
            Directory.CreateDirectory("Temp/ScoreVerification"); File.WriteAllText("Temp/ScoreVerification/rules.txt", result);
            return result;
        }
        private static void Require(bool ok, string message) { if (!ok) { throw new InvalidOperationException(message); } _checks++; }
    }
}

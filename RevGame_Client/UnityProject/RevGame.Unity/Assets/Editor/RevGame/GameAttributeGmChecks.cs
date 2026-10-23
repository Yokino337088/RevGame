// 属性GM回归：命令走真实RevGM注册，属性验证走真实战斗入口；反射仅给Editor夹具接入私有检查点。
using System;
using System.Collections.Generic;
using System.Reflection;
using RevGame.Animation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>仅编辑器固定输入回归；从首页进入真实战斗，收尾清除强化并返回首页，不修改Excel/存档。</summary>
    public static class GameAttributeGmChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<string> Evidence = new List<string>();
        private static int _assertions;
        public static string LastResult { get; private set; } = "未运行";

        [UnityEditor.MenuItem("RevGame/验收/属性GM（Play）")]
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            Evidence.Clear();
            _assertions = 0;
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            GameAutomationRunner runner = RevServices.GetRequired<GameAutomationRunner>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            try
            {
                Require(runner.State != "running", "没有并发自动化作业");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                VerifyCommands();
                Require(flow.OpenLevels() && flow.EnterBattle(1), "正式入口接受战斗请求");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                Require(battle.LoadDevelopmentScenario("boss"), "加载固定双玩家与Boss护卫夹具");
                // 下列数值检查在同一同步调用中完成，准备计时器不会插入随机增益影响固定对比。
                VerifyBattle(battle);
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                VerifyOwnership(runner, battle);
                runner.Start(new GameAutomationPlan { timeoutSeconds = 10, steps = new[]
                {
                    new GameAutomationStep { name = "设置四项", op = "gm", command = "RevGame/属性设置 250 100 20 30" },
                    new GameAutomationStep { name = "查询精确返回", op = "gm", command = "RevGame/属性查询", expected = Json(250, 100, 20, 30) },
                    new GameAutomationStep { name = "清除四项", op = "gm", command = "RevGame/属性清除", expected = Json(0, 0, 0, 0) },
                    new GameAutomationStep { name = "正常首页", op = "assert", screen = "Home" }
                }});
                for (int i = 0; i < 100 && runner.State == "running"; i++) { await RevTask.Delay(100); }
                Require(runner.State == "passed", "计划内属性GM实际执行与查询通过：" + runner.ReportJson());
                Require(!battle.IsAutomationBoostActive, "完成路径释放租约");
                runner.Start(new GameAutomationPlan { boost = new GameAutomationBoost { healthBonusPercent = 100 },
                    timeoutSeconds = 5, steps = new[] { new GameAutomationStep { op = "assert", screen = "Battle" } } });
                for (int i = 0; i < 50 && runner.State == "running"; i++) { await RevTask.Delay(100); }
                Require(runner.State == "failed" && !battle.IsAutomationBoostActive, "错误断言使计划失败且释放强化");
                LastResult = "通过 " + _assertions + " 条：七条注册、参数原子校验、真实攻击/攻速/防御/生命、继承/换轮/升星、租约与终止清理";
            }
            catch (Exception error)
            {
                LastResult = "失败：" + error;
                RevLog.Exception(error, "属性GM验收失败", "RevGame");
            }
            finally
            {
                if (runner.State == "running") { runner.Cancel(); }
                RevGM.Execute("RevGame/属性清除");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                System.IO.File.WriteAllText("Temp/GameAttributeGmChecks.txt", LastResult + "\n" + string.Join("\n", Evidence));
            }
        }

        private static void VerifyCommands()
        {
            foreach (string name in new[] { "属性设置", "属性查询", "属性清除", "加攻击", "加攻速", "加防御", "加血量" })
            {
                bool found = false;
                foreach (var command in RevGM.Commands) { if (command.Name == "RevGame/" + name) { found = true; } }
                Require(found, "运行期已注册：" + name);
            }
            Command("属性清除");
            Command("属性设置 250 100 0 0");
            Require(Query() == Json(250, 100, 0, 0), "设置与精确JSON查询");
            Command("属性设置 250 100 0 0");
            Require(Query() == Json(250, 100, 0, 0), "重复设置不累计");
            Command("加攻击 25"); Command("加攻速 20"); Command("加防御 50"); Command("加血量 100");
            Require(Query() == Json(275, 120, 50, 100), "四项分别累计");
            Command("加攻击 -25");
            Require(Query() == Json(250, 120, 50, 100), "负增量允许下调到合法范围");
            string before = Query();
            foreach (string invalid in new[] { "属性设置", "属性设置 1 2 3", "属性设置 1 2 3 4 5", "属性设置 1 x 0 0",
                "属性设置 -1 0 0 0", "属性设置 0 0 0 10001", "属性设置 2147483648 0 0 0", "加攻击",
                "加攻击 1 2", "加攻击 2147483647", "加血量 -101", "属性查询 1", "属性清除 1" })
            {
                Require(!RevGM.Execute("RevGame/" + invalid).Success && Query() == before, "拒绝错误且原配置不变：" + invalid);
            }
            Command("属性设置 10000 10000 10000 10000");
            Require(Query() == Json(10000, 10000, 10000, 10000), "技术范围上界可设置");
            Command("属性清除");
        }

        private static void VerifyBattle(BattleService battle)
        {
            Piece player = battle.PlayerPieces[0];
            Piece small = battle.PlayerPieces[1];
            Piece enemy = null;
            foreach (Piece item in battle.EnemyPieces) { if (!item.IsKing && item.IsAlive) { enemy = item; break; } }
            Require(enemy != null && player.IsAlive && small.IsAlive, "固定比较对象均存活");
            int enemyMax = enemy.MaxHp;
            int before = enemy.Hp;
            Call(battle, "Attack", player, enemy);
            int normalDamage = before - enemy.Hp;
            Require(normalDamage > 0 && enemy.IsAlive, "原倍率真实出手造成非致死伤害：" + normalDamage);
            enemy.Heal(enemy.MaxHp);
            Command("属性设置 250 0 0 0");
            before = enemy.Hp;
            Call(battle, "Attack", player, enemy);
            Require(before - enemy.Hp == (int)(normalDamage * 3.5d), "+250%真实普通攻击=原伤害×3.5，沿用整数截断");
            Require(enemy.MaxHp == enemyMax, "未修改敌方最大生命");
            var progress = typeof(Piece).GetProperty("ActionProgress", PrivateInstance);
            progress.SetValue(player, 0f);
            Command("属性清除");
            Call(battle, "StepSide", GameSide.Player, 0.0001f);
            float normalRate = (float)progress.GetValue(player);
            progress.SetValue(player, 0f);
            float clock = Time.timeScale;
            Command("属性设置 0 100 0 0");
            Call(battle, "StepSide", GameSide.Player, 0.0001f);
            Require(normalRate > 0f && Math.Abs((float)progress.GetValue(player) - normalRate * 2f) < 0.000001f,
                "+100%作用于真实攻击进度积累，速度翻倍");
            Require(Time.timeScale == clock, "未改变整个游戏时钟");
            Command("属性设置 0 0 100 0");
            Require((int)Call(battle, "DamagePiece", player, 100, enemy, true) == 50, "+100%等效防御将真实技能正伤害减半");
            Require((int)Call(battle, "DamagePiece", player, 1, enemy, false) == 1, "正伤害至少1");
            typeof(Piece).GetProperty("ShieldCharges").SetValue(player, 1);
            Require((int)Call(battle, "DamagePiece", player, 100, enemy, true) == 0, "护盾抵消后的0伤害保持0");
            Command("属性清除");
            Require((int)Call(battle, "DamagePiece", player, 100, enemy, true) == 100, "清除后原伤害恢复");
            int baseMax = player.MaxHp;
            float wounded = player.HpRatio;
            Command("属性设置 0 0 0 100");
            Require(player.MaxHp == baseMax * 2 && Math.Abs(player.HpRatio - wounded) < 2f / baseMax, "已有伤棋最大生命翻倍且保留伤势比例");
            Command("属性设置 0 0 0 100");
            Require(player.MaxHp == baseMax * 2, "重复设置生命不会乘上旧倍率");
            int card = battle.Hand[1].Id;
            BoardPos empty = new BoardPos(0, 0);
            Require(battle.TryPlayCard(card, empty), "真实手牌落子成功");
            Piece fresh = battle.Board.GetCell(empty).Occupant;
            Require(fresh != null && fresh.Side == GameSide.Player && fresh.MaxHp == fresh.Def.MaxHp * 2, "后落棋继承最大生命倍率");
            Require(small.UpgradeInPlace(), "固定小棋可以升星");
            Require(small.MaxHp == small.Def.MaxHp * 2, "升星后保持开发生命倍率");
            Call(battle, "ApplyPreparationBuff", PreparationBuffCatalog.Get(PreparationBuffKind.Vitality));
            float prep = (float)typeof(BattleService).GetField("_prepHealthScale", PrivateInstance).GetValue(battle);
            Require(player.MaxHp == (int)(baseMax * prep * 2), "开发生命和正常准备生命增益相乘组合");
            Require(battle.StartRound(1, 2), "真实切换下一轮");
            Require(player.MaxHp == baseMax * 2, "换轮清理正常准备增益，保留本次开发生命");
            Call(battle, "DamagePiece", small, small.Hp, null, false);
            Require(!small.IsAlive, "真实致死入口使小棋死亡");
            Command("属性设置 0 0 0 200");
            Require(!small.IsAlive && small.Hp == 0, "调整生命不复活死棋");
            Command("属性清除");
            Require(player.MaxHp == baseMax && !battle.IsAutomationBoostActive, "清除恢复原上限并释放手动租约");
        }

        private static void VerifyOwnership(GameAutomationRunner runner, BattleService battle)
        {
            Command("属性设置 100 0 0 0");
            runner.Start(new GameAutomationPlan { timeoutSeconds = 10, boost = new GameAutomationBoost { attackBonusPercent = 20 },
                steps = new[] { new GameAutomationStep { op = "wait", screen = "LevelSelect" } } });
            Require(Query() == Json(20, 0, 0, 0), "自动化接管手动租约并采用计划值");
            Command("加攻击 5");
            Require(Query() == Json(25, 0, 0, 0), "运行中的GM沿用自动化租约");
            runner.Cancel();
            Require(!battle.IsAutomationBoostActive && Query() == Json(0, 0, 0, 0), "取消路径清除强化");
        }

        private static object Call(object instance, string method, params object[] args)
        {
            return instance.GetType().GetMethod(method, PrivateInstance).Invoke(instance, args);
        }

        private static void Command(string command)
        {
            RevGMResult result = RevGM.Execute("RevGame/" + command);
            Require(result.Success, command + " → " + result.Message);
        }

        private static string Query()
        {
            RevGMResult result = RevGM.Execute("RevGame/属性查询");
            if (!result.Success) { throw new InvalidOperationException(result.Message); }
            return result.Message;
        }

        private static string Json(int attack, int speed, int defense, int health)
        {
            return JsonUtility.ToJson(new GameAutomationBoost { attackBonusPercent = attack, attackSpeedBonusPercent = speed,
                defenseBonusPercent = defense, healthBonusPercent = health });
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            Evidence.Add("PASS " + (++_assertions) + " " + message);
        }
    }
}

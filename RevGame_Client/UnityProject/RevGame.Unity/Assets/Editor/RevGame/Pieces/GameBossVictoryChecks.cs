using System;
using System.Collections.Generic;
using System.Reflection;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>验证真实规则与框架序列，独立服务和Runner归本测试所有，finally只清自己。</summary>
    public static class GameBossVictoryChecks
    {
        public static string Run()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("需编辑态独立检查"); }
            bool loaded = GameConfig.IsLoaded; int checks = 0;
            Action<bool,string> require = (ok,message) => { if (!ok) { throw new InvalidOperationException(message); } checks++; };
            try
            {
                GameConfigTestSupport.Ensure();
                BattleService.VerifyBossVictory(require);
                BattleService.VerifyScoreRules(require);
                var book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
                book.Validate();
                for (int chapter = 1; chapter <= 6; chapter++) { require(book.Find("boss_after_"+chapter).Lines.Length >= 3,"六章SO收尾文案"); }
                FieldInfo blueprint = typeof(GameFlow).GetField("BossVictorySequence",BindingFlags.NonPublic|BindingFlags.Static);
                require(blueprint != null && blueprint.GetValue(null) is RevSequenceDefinition,"正式演出使用缓存RevSequence蓝图");
                var victory = (RevSequenceDefinition)blueprint.GetValue(null);
                require(victory.StepCount == 5 && victory.FinallySteps.Count == 1,"正式击败序列五步与finally契约");
                var intro = (RevSequenceDefinition)typeof(GameFlow).GetField("BossIntroSequence",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
                require(intro.StepCount == 5 && intro.FinallySteps.Count == 1,"正式入场五步与finally契约");
                require(intro.Steps[2].Name == "逆向死亡显现" && intro.Steps[3].Name == "镜头完整拉回" && intro.Steps[4].Name == "闪烁登场与棋盘舞动","入场逆播/弹幕/舞动编排顺序");
                // 固定节点验收真实框架顺序、源互斥和中断finally；不假装已跑正式Play演出。
                var order = new List<string>(); object source = new object();
                RevSequenceDefinition definition = RevSequence.Create("Boss演出取消契约",RevSequenceConcurrency.RejectPerSource)
                    .Do("focus",ctx => order.Add("focus")).Wait(.1f)
                    .Do("death",ctx => order.Add("death")).Wait(.1f)
                    .Do("wave",ctx => order.Add("wave")).Wait(.1f)
                    .Do("banner",ctx => order.Add("banner")).Wait(.1f)
                    .Do("story",ctx => order.Add("story"))
                    .Finally(f => f.Do("restore",ctx => order.Add("restore"))).Build();
                using (var runner = new RevSequenceRunner())
                {
                    RevSequenceHandle handle = runner.Play(definition,source);
                    runner.Tick(.02f);
                    runner.Play(definition,source);
                    for (int frame = 0; frame < 20; frame++) { runner.Tick(.05f); }
                    require(string.Join(",",order)=="focus,death,wave,banner,story,restore","真实Runner顺序及同源互斥");
                    order.Clear(); handle = runner.Play(definition,source); runner.Tick(.02f); handle.Stop(); runner.Tick(0);
                    require(string.Join(",",order)=="focus,restore","中断必经finally且不播放后续剧情");
                }
                return checks+"条断言通过：真实章积分、六章击杀/擒王/去重、SO与RevActionSequence顺序/互斥/取消；未切场景";
            }
            finally { if (!loaded) { GameConfig.Reset(); } }
        }
    }
}

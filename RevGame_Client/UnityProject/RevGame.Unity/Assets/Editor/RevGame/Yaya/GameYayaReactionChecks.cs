// 非Play规则夹具共用Excel生成表与正式分类算法，边界、空窗、模式阈值和损失优先级分别验证。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Data;
using RevGame.Flow;
using RevGame.View;
using Revolution;
using UnityEngine;
namespace RevGame.Editor
{
    /// <summary>互动纯规则与Excel校验，编辑态仅读生成表，不修改正式配置、随机源或玩家存档。</summary>
    public static class GameYayaReactionChecks
    {
        /// <summary>读磁盘生成表仅用于编辑态，无初始化/全局注册；与生产分类函数共用同一Excel参数。</summary>
        public static string Rules()
        {
            var rules = new YayaReactionRuleTable();
            var lines = new YayaReactionLineTable();
            rules.LoadText(File.ReadAllText("Assets/GameRes/Data/YayaReactionRule.txt"), out int ruleErrors);
            lines.LoadText(File.ReadAllText("Assets/GameRes/Data/YayaReactionLine.txt"), out int lineErrors);
            Check(ruleErrors == 0 && lineErrors == 0, "导表解析");
            YayaReactionConfig.Validate(rules, lines);
            float V(string key)
            {
                rules.FindByKey(key, out YayaReactionRule row);
                return row.Value;
            }
            int Classify(int mode, int kills, int losses, long taken, long dealt) => YayaReactionLogic.ClassifyWithRules(kills, losses, taken, dealt, 1000, V(mode == 1 ? "CampaignGoodKills" : "EndlessGoodKills"), V("GoodTakenRatio"), V("PoorTakenRatio"), V("PoorLosses"));
            int good = (int)V("CampaignGoodKills"), endless = (int)V("EndlessGoodKills");
            Check(Classify(1, 0, 0, 0, 0) == 0, "空窗无互动");
            Check(Classify(1, good, 0, 0, 100) == 1, "六章击杀达标");
            Check(Classify(1, good - 1, 0, 0, 100) == 2, "低于阈值为一般");
            Check(Classify(2, endless, 0, 0, 100) == 1, "无尽阈值独立");
            Check(Classify(2, good, 0, 0, 100) == 2, "六章击杀数不足无尽阈值");
            Check(Classify(1, good + 10, 1, 0, 100) == 3, "损失优先于击杀");
            Check(Classify(1, 0, 0, (int)(V("PoorTakenRatio") * 1000) + 1, 100) == 3, "高损伤嘲讽");
            Check(Classify(1, good, 0, (int)(V("GoodTakenRatio") * 1000) + 1, 100) == 2, "受伤过高不能夸奖");
            Check(V("CampaignIdleTauntSeconds") >= V("SampleSeconds"), "六章无操作观察期能覆盖一个采样");
            var idleLines = new HashSet<string>();
            foreach (YayaReactionLine line in lines.All)
            {
                if (line.Band == YayaReactionConfig.CampaignIdleTauntBand)
                {
                    idleLines.Add(line.Text);
                }
            }
            Check(idleLines.Count > 1, "六章无操作重复触发时能换台词");
            CheckSidebar(new Rect(.01f, .028f, .24f, .78f), .25f);
            CheckSidebar(new Rect(.012f, .145f, .145f, .48f), .18f);
            // 16:9 下无尽棋盘投影的左边约为 .302，安全区全宽时应能装下完整两倍区域。
            var mobile = new Rect(.012f, .145f, .29f, .96f);
            Check(YayaReactionPresenter.TryFitSidebar(mobile, new Rect(0, 0, 1, 1), new Rect(.302f, .15f, .396f, .7f), out Rect expanded)
                && Mathf.Approximately(expanded.width, mobile.width) && Mathf.Approximately(expanded.height, mobile.height), "安卓无尽两倍布局");
            return "8项表现分类边界、两模式台词、六章无操作配置、横屏刘海和安卓无尽两倍布局检查通过";
        }
        /// <summary>模拟左边有刘海的横屏。原坐标落在刘海内，适配后应在安全区与棋盘之间保留等比区域。</summary>
        private static void CheckSidebar(Rect preferred, float boardLeft)
        {
            Rect safe = Rect.MinMaxRect(.055f, .035f, .99f, .96f);
            Rect board = Rect.MinMaxRect(boardLeft, .04f, .75f, .9f);
            Check(preferred.xMin < safe.xMin, "原坐标在刘海内");
            Check(YayaReactionPresenter.TryFitSidebar(preferred, safe, board, out Rect area), "刘海内移");
            Check(area.xMin >= safe.xMin && area.yMin >= safe.yMin && area.yMax <= safe.yMax && !area.Overlaps(board), "安全区和棋盘边界");
            Check(Mathf.Approximately(area.width / area.height, preferred.width / preferred.height), "互动区域等比");
        }
        /// <summary>断言失败携带具体场景名交给自动化，禁止只打印日志后计为通过。</summary>
        private static void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("鸦鸦规则检查失败：" + name);
            }
        }
    }
}

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Text;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;

namespace RevGame.Animation
{
    /// <summary>
    /// 将项目测试执行器接到现成 RevGM，再由 Revolution.Automation CLI/Scenario 调用。
    /// 仅注册六条窄入口，不另建HTTP服务/文件桥，不修改框架快照；注册和注销由Bootstrap成对调用。
    /// </summary>
    public static class GameAutomationCommands
    {
        public static void Register()
        {
            RevGM.Register("RevGame/自动化开始", "接收Base64编码的UTF-8测试计划，异步执行真实玩家输入", args =>
            {
                string encoded = args.Str(0, "");
                if (encoded.Length == 0 || encoded.Length > 262144)
                {
                    throw new RevGMUsageException("需要不超过256KB的Base64计划");
                }
                string json;
                try { json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
                catch (FormatException) { throw new RevGMUsageException("计划不是有效的Base64文本"); }
                var plan = JsonUtility.FromJson<GameAutomationPlan>(json);
                RevServices.GetRequired<GameAutomationRunner>().Start(plan);
                return "started";
            }, RevGMArg.Str("计划Base64"));
            RevGM.Register("RevGame/自动化状态", "只读 idle/running/passed/failed/cancelled", args => RevServices.GetRequired<GameAutomationRunner>().State);
            // Scenario.until 在失败时必须立即退出；单纯等待状态变成passed会把真实失败拖成几十分钟超时。
            RevGM.Register("RevGame/自动化检查", "供Scenario轮询：passed/running；失败直接抛出报告原因", args =>
            {
                var runner = RevServices.GetRequired<GameAutomationRunner>();
                if (runner.State == "failed" || runner.State == "cancelled")
                {
                    throw new RevGMUsageException(runner.ReportJson());
                }
                return runner.State;
            });
            RevGM.Register("RevGame/自动化报告", "只读完整JSON报告，含逐步证据和11轮真实交战结果", args => RevServices.GetRequired<GameAutomationRunner>().ReportJson());
            RevGM.Register("RevGame/自动化取消", "幂等取消测试并释放手势、计时器和观察订阅；不修改棋盘", args =>
            {
                RevServices.GetRequired<GameAutomationRunner>().Cancel();
                return "cancelled";
            });
            RevGM.Register("RevGame/自动化收尾", "幂等取消并经真实GameFlow返回首页；loading/clean，可在失败后轮询", args =>
            {
                RevServices.GetRequired<GameAutomationRunner>().Cancel();
                GameFlow flow = RevServices.GetRequired<GameFlow>();
                if (flow.IsTransitioning)
                {
                    return "loading";
                }
                if (flow.Screen == GameScreen.Home)
                {
                    return "clean";
                }
                if (!flow.ReturnHome())
                {
                    throw new RevGMUsageException(flow.LastError);
                }
                return "loading";
            });
        }

        public static void Unregister()
        {
            RevGM.Unregister("RevGame/自动化开始");
            RevGM.Unregister("RevGame/自动化状态");
            RevGM.Unregister("RevGame/自动化检查");
            RevGM.Unregister("RevGame/自动化报告");
            RevGM.Unregister("RevGame/自动化取消");
            RevGM.Unregister("RevGame/自动化收尾");
        }
    }
}
#endif

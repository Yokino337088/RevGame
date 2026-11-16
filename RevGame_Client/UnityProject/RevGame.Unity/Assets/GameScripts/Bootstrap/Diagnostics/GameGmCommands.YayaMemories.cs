#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using Revolution;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        /// <summary>收藏书测试使用同一套玩家按钮、拖页事件与业务存档。状态查询不会修改解锁。</summary>
        private static void RegisterYayaMemoryCommands()
        {
            RegisterCommand("RevGame/鸦鸦点滴/打开", "从大厅左上角的真实按钮打开收藏书",
                _ => ClickPlayerButton("HomePanel/btnMemories"));
            RegisterCommand("RevGame/鸦鸦点滴/翻页", "点击实际翻页按钮，方向1下一页、-1上一页；锁页保留提示",
                args =>
                {
                    int direction = args.Int(0, 1);
                    if (direction != -1 && direction != 1)
                    {
                        throw new RevGMUsageException("翻页方向只接受 -1 或 1");
                    }
                    return ClickPlayerButton(direction > 0 ? "YayaMemoryBookPanel/btnNext" : "YayaMemoryBookPanel/btnPrevious");
                }, RevGMArg.Int("方向", 1));
            RegisterCommand("RevGame/鸦鸦点滴/拖页", "隔离Play模拟书角拖拽，方向±1，幅度0～1；保持后可继续拖或松手",
                args =>
                {
                    var page = YayaMemoryAutomation.Drag(args.Int(0, 1), args.Float(1, .5f), args.Bool(2, true));
                    return "phase=" + page["phase"] + ",page=" + page["chapter"] + ",target=" + page["target"] + ",amount=" + page["amount"];
                }, RevGMArg.Int("方向", 1), RevGMArg.Float("幅度", .5f), RevGMArg.Bool("松手", true));
            RegisterCommand("RevGame/鸦鸦点滴/合上", "点击收藏书原位合上按钮，动画结束后归还图层",
                _ => ClickPlayerButton("YayaMemoryBookPanel/btnClose"));
            RegisterCommand("RevGame/鸦鸦点滴/状态", "只读实际通关、已阅位图、大厅提示和活动书页",
                args =>
                {
                    var state = YayaMemoryAutomation.State();
                    var page = state["book"] as Dictionary<string, object>;
                    return "cleared=" + state["cleared"] + ",read=" + state["read"] + ",unread=" + state["unread"]
                        + ",badge=" + state["badge"] + ",speech=" + state["speech"]
                        + (page == null ? ",book=closed" : ",page=" + page["chapter"] + ",phase=" + page["phase"] + ",artReady=" + page["artReady"]);
                });
        }
    }
}
#endif

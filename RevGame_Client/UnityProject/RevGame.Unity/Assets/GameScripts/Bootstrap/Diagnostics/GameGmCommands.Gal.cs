#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using RevGame.Flow;
using Revolution;

namespace RevGame.Bootstrap
{
    public static partial class GameGmCommands
    {
        /// <summary>续篇测试复用正式场景与输入。测试会话的快存和已读信息仅保存在内存。</summary>
        private static void RegisterGalCommands()
        {
            const string group = "RevGame/旮旯给木/";
            RegisterCommand(group + "打开", "在独立场景从开头预览续篇，参数决定是否播放打字机", args =>
            {
                var flow = RevServices.GetRequired<GameFlow>();
                if (!flow.TryOpenGalForTest(GameFlow.CottageStoryPath, args.Bool(0, true)))
                {
                    throw new RevGMUsageException("请在大厅或选关空闲时打开剧情");
                }
                return "accepted";
            }, RevGMArg.Bool("播放打字机", true));

            RegisterCommand(group + "下一句", "与继续按钮共用推进。可传当前节点 ID 防止重复推进", args =>
            {
                var flow = RevServices.GetRequired<GameFlow>();
                if (!flow.TryAdvanceGalForTest(args.Str(0, "")))
                {
                    throw new RevGMUsageException("当前剧情不可推进，或节点 ID 已变化");
                }
                return "advanced";
            }, RevGMArg.Str("当前节点ID", ""));

            RegisterCommand(group + "状态", "只读当前场景、节点、落屏状态和过场锁", _ =>
            {
                var state = RevServices.GetRequired<GameFlow>().GalAutomationState();
                return "scene=" + state["scene"] + ", node=" + state["nodeId"] + ", ready=" + state["frameReady"]
                    + ", transitioning=" + state["transitioning"] + ", error=" + state["error"];
            });

            RegisterCommand(group + "返回大厅", "取消正在加载的剧情，或按正式流程从剧情返回大厅", _ =>
            {
                if (!RevServices.GetRequired<GameFlow>().CloseGal())
                {
                    throw new RevGMUsageException("当前没有可关闭的剧情");
                }
                return "accepted";
            });
        }
    }
}
#endif

using System;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>跨Editor程序集只暴露固定Boss演出动作；不提供任意方法调用或切换用户场景。</summary>
    public static class GameBossVictoryAutomation
    {
        [RevAutomationAction("game.boss-victory.configure", Environment = "edit", Keys = new string[0], Description = "幂等制作六章死亡材质、胜利UI和SO收尾剧情")]
        public static object Configure(RevAutomationRequest request) { return Call("GameBossVictoryBuild", "Configure"); }
        [RevAutomationAction("game.boss-victory.check", Environment = "edit", Keys = new string[0], Description = "独立验证Boss胜利事实、章积分与序列取消契约")]
        public static object Check(RevAutomationRequest request) { return Call("GameBossVictoryChecks", "Run"); }
        [RevAutomationAction("game.boss-victory.preview", Environment = "edit", Keys = new string[0], Description = "真实PC AB离屏验证六章死亡、棋盘波浪、积分面板与回池")]
        public static object Preview(RevAutomationRequest request) { return Call("GameBossVictoryPreview", "Run"); }
        [RevAutomationAction("game.boss-victory.play-begin", Environment = "play", Keys = new string[0], Description = "隔离存档六章真实入场/击败/剧情/取消验收，started后只读查询")]
        public static object PlayBegin(RevAutomationRequest request) { return Call("GameBossVictoryPlayChecks", "Begin"); }
        [RevAutomationAction("game.boss-victory.play-state", Environment = "play", Keys = new string[0], Description = "只读完整Play演出验收：running/passed/failed")]
        public static object PlayState(RevAutomationRequest request) { return Call("GameBossVictoryPlayChecks", "State"); }
        private static object Call(string typeName, string method)
        {
            Type type = Type.GetType("RevGame.Editor." + typeName + ", Assembly-CSharp-Editor", true);
            try { return new Result { text = (string)type.GetMethod(method).Invoke(null,null) }; }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable] private sealed class Result { public string text; }
    }
}

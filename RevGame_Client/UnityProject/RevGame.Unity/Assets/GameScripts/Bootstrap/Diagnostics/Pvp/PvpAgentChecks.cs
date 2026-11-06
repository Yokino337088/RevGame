#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using RevGame.Pvp.Agent;
namespace RevGame.Pvp
{
internal static class PvpAgentChecks
{
    // 固定假Key写入独立槽，不能读写PlayerSlot。返回值只含断言，不含Key或密文。
    private const string StorageCheckSlot = "RevGame.DeepSeek.IsolatedStorageCheck.v1";
    private const string FixtureKey = "revgame-storage-check-not-an-api-key";

    public static object PrepareStorageCheck()
    {
        var settings = new PvpAgentSettings();
        Assert(settings.TrySet(FixtureKey, "deepseek-flash", true, out _), "存储测试配置有效");
        PvpAgentSettingsStore.Save(settings, StorageCheckSlot);
        Assert(!UnityEngine.PlayerPrefs.GetString(StorageCheckSlot).Contains(FixtureKey), "落盘内容不含明文Key");
        return new System.Collections.Generic.Dictionary<string, object> { ["prepared"] = true, ["encrypted"] = true };
    }

    public static object VerifyStorageCheck()
    {
        try
        {
            Assert(PvpAgentSettingsStore.Load(out PvpAgentSettings loaded, out _, StorageCheckSlot), "重新加载可解密");
            Assert(loaded.Key == FixtureKey && loaded.Model == "deepseek-flash" && loaded.Enabled, "跨Play恢复Key模型和授权");
            UnityEngine.PlayerPrefs.SetString(StorageCheckSlot, "corrupt-fixture");
            UnityEngine.PlayerPrefs.Save();
            Assert(!PvpAgentSettingsStore.Load(out loaded, out _, StorageCheckSlot) && !loaded.Ready, "损坏记录不启用云端");
            PvpAgentSettingsStore.Clear(StorageCheckSlot);
            Assert(!UnityEngine.PlayerPrefs.HasKey(StorageCheckSlot), "清除删除持久化记录");
            return new System.Collections.Generic.Dictionary<string, object> { ["passed"] = true, ["cleaned"] = true };
        }
        finally
        {
            PvpAgentSettingsStore.Clear(StorageCheckSlot);
        }
    }

    public static object ClearStorageCheck()
    {
        PvpAgentSettingsStore.Clear(StorageCheckSlot);
        return new System.Collections.Generic.Dictionary<string, object> { ["cleaned"] = true };
    }

    /// <summary>开发态固定策略，只有测试能力可安装；正式包通过预编译排除整个类型。</summary>
    public static void Install(PvpService service)
    {
        service.ConfigureAgent("development-fixture-not-a-real-key", "fixture", true);
        service.AgentRequestFactory = (_, position) => new Response
        {
            Content = position.Prompt.Contains("阶段=Scoring") ? "{\"actionId\":\"confirm\"}" : "{\"actionId\":\"pass\"}"
        };
    }

    public static void Restore(PvpService service)
    {
        service.ConfigureAgent("", "deepseek-flash", false);
        service.AgentRequestFactory = DeepSeekGoRequest.Start;
    }

    private sealed class Response : IGoAgentRequest
    {
        public bool Done = true, Disposed;
        public string Content = "{\"actionId\":\"pass\"}";
        public bool IsDone => Done;
        public string ReadContent()
        {
            return Content;
        }
        public void Dispose()
        {
            Disposed = true;
        }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Agent: " + message);
        }
    }
    public static string Run(PvpConfig config)
    {
        var s = new PvpService();
        Assert(!s.Submit(GoAction.Play, 4, 4), "未开局提交返回拒绝，不访问空棋局");
        s.Begin(config);
        s.StartComputer();
        Assert(!s.ConfigureAgent("", "model", true), "缺少Key拒绝启用");
        Assert(s.ConfigureAgent("test-key-not-real", "model", true), "设置可启用");
        Assert(!s.SetAgentControl(true), "人机玩家不可托管");
        int requests = 0;
        var response = new Response();
        s.AgentRequestFactory = (_, position) => { requests++; return response; };
        Assert(s.Submit(GoAction.Pass), "玩家停一手");
        s.Poll();
        Assert(s.Core.Phase == GoPhase.Scoring && response.Disposed, "白方Agent停一手进入数子并释放请求");
        response = new Response { Content = "{\"actionId\":\"confirm\"}" };
        s.Poll();
        Assert(s.Core.HasConfirmed(GoSide.White), "Agent只确认白方");
        Assert(s.Submit(GoAction.ConfirmScore) && s.Core.Phase == GoPhase.Finished, "完整人机数子闭环");
        s.StartComputer();
        s.Submit(GoAction.Play, 4, 4);
        response = new Response { Done = false };
        s.Poll();
        int count = requests;
        s.Poll();
        Assert(count == requests, "等待时不重复请求");
        Assert(!s.Submit(GoAction.Play, 3, 3) && s.Core.Sequence == 1, "玩家不可替白方落子");
        s.ConfigureAgent("", "model", false);
        Assert(response.Disposed, "撤销云端授权取消在途请求");
        response.Done = true;
        s.StartLocal();
        s.Poll();
        Assert(s.Core.Sequence == 0, "旧请求不能污染重开棋局");
        s.StartComputer();
        s.ConfigureAgent("test-key-not-real", "model", true);
        response = new Response { Content = "{\"actionId\":\"play_99_99\"}" };
        s.Submit(GoAction.Play, 4, 4);
        s.Poll();
        Assert(s.Core.Sequence == 1, "非法云端动作不提交");
        count = requests;
        s.Poll();
        Assert(s.Core.Sequence == 2 && requests == count, "本手本地回退且不重复付费请求");
        var position = GoAgentPosition.Create(s.Core, GoSide.Black);
        ulong hash = s.Core.StateHash;
        Assert(!position.TryReadAction("{\"actionId\":\"play_4_4\"}", out _), "已占落点不在候选中");
        Assert(!position.TryReadAction(" ", out _) && !position.TryReadAction("not-json", out _), "空白和畸形响应被拒绝");
        Assert(s.Core.StateHash == hash && position.Matches(s.Core), "候选生成与解析不改变实盘");
        s.Dispose();
        return "PASS: Agent 人机闭环、权限、取消、单请求、非法输出、本地回退、候选隔离";
    }
}

}
#endif

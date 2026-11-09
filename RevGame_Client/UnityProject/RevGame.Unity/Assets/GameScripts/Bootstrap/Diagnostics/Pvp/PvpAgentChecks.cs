#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.IO;
using RevGame.Pvp.Agent;
namespace RevGame.Pvp
{
internal static class PvpAgentChecks
{
    // 固定假Key写入独立槽，不能读写PlayerSlot。返回值只含断言，不含Key或密文。
    private const string StorageCheckSlot = "RevGame.DeepSeek.IsolatedStorageCheck.v1";
    private const string TextCheckSlot = "RevGame.DeepSeek.IsolatedTextStorageCheck.v1";
    private const string FixtureKey = "revgame-storage-check-not-an-api-key";

    public static object PrepareStorageCheck()
    {
        var settings = new PvpAgentSettings();
        Assert(settings.TrySet(FixtureKey, "deepseek-flash", true, out _), "存储测试配置有效");
        bool encrypted = !PvpAgentSettingsStore.UsesTextStorage;
        PvpAgentSettingsStore.Save(settings, StorageCheckSlot);
        if (encrypted)
        {
            Assert(UnityEngine.PlayerPrefs.HasKey(StorageCheckSlot) && !UnityEngine.PlayerPrefs.GetString(StorageCheckSlot).Contains(FixtureKey), "Windows落盘内容不含明文Key");
        }

        // 模拟器仍运行在 Windows；另走一次 Android 同用的 TXT 路径，测试已有文件被替换。
        Assert(settings.TrySet(FixtureKey, "previous-fixture", true, out _), "旧TXT测试模型有效");
        PvpAgentSettingsStore.SaveText(settings, TextCheckSlot);
        Assert(settings.TrySet(FixtureKey, "deepseek-flash", true, out _), "新TXT测试模型有效");
        PvpAgentSettingsStore.SaveText(settings, TextCheckSlot);
        string text = File.ReadAllText(PvpAgentSettingsStore.TextPath(TextCheckSlot));
        Assert(text.Contains(FixtureKey) && text.Contains("deepseek-flash") && !text.Contains("previous-fixture"), "TXT保留明文并替换旧模型");
        return new System.Collections.Generic.Dictionary<string, object> { ["prepared"] = true, ["encrypted"] = encrypted, ["plaintext"] = true };
    }

    public static object VerifyStorageCheck()
    {
        try
        {
            Assert(PvpAgentSettingsStore.Load(out PvpAgentSettings loaded, out _, StorageCheckSlot), "正式平台入口可重新加载");
            Assert(loaded.Key == FixtureKey && loaded.Model == "deepseek-flash" && loaded.Enabled, "跨Play恢复Key模型和授权");
            if (!PvpAgentSettingsStore.UsesTextStorage)
            {
                UnityEngine.PlayerPrefs.SetString(StorageCheckSlot, "corrupt-fixture");
                UnityEngine.PlayerPrefs.Save();
                Assert(!PvpAgentSettingsStore.Load(out loaded, out _, StorageCheckSlot) && !loaded.Ready, "损坏密文不启用云端");
            }
            Assert(PvpAgentSettingsStore.LoadText(out loaded, out _, TextCheckSlot) &&
                loaded.Key == FixtureKey && loaded.Model == "deepseek-flash" && loaded.Enabled, "跨Play从TXT恢复Key模型和授权");
            string path = PvpAgentSettingsStore.TextPath(TextCheckSlot);
            File.WriteAllText(path, "corrupt-fixture");
            Assert(!PvpAgentSettingsStore.LoadText(out loaded, out _, TextCheckSlot) && !loaded.Ready, "损坏TXT不启用云端");
            File.WriteAllText(path + ".tmp", FixtureKey);
            PvpAgentSettingsStore.ClearText(TextCheckSlot);
            Assert(!File.Exists(path) && !File.Exists(path + ".tmp"), "清除删除正式和临时TXT");
            PvpAgentSettingsStore.Clear(StorageCheckSlot);
            Assert(!UnityEngine.PlayerPrefs.HasKey(StorageCheckSlot) && !File.Exists(PvpAgentSettingsStore.TextPath(StorageCheckSlot)), "清除删除平台持久化记录");
            return new System.Collections.Generic.Dictionary<string, object> { ["passed"] = true, ["cleaned"] = true, ["plaintext"] = true };
        }
        finally
        {
            ClearStorageCheck();
        }
    }

    public static object ClearStorageCheck()
    {
        PvpAgentSettingsStore.Clear(StorageCheckSlot);
        PvpAgentSettingsStore.ClearText(TextCheckSlot);
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

    public static void InstallLongGame(PvpService service)
    {
        service.ConfigureAgent("development-fixture-not-a-real-key", "fixture", true);
        service.AgentRequestFactory = (_, position) =>
        {
            string action = "pass";
            if (position.Prompt.Contains("阶段=Scoring"))
            {
                action = "confirm";
            }
            else if (service.Core.MoveNumber < 60)
            {
                // 只选生产代码列出的合法候选，不直接操作实盘；60手后双停以覆盖后半局与终局。
                int first = position.Prompt.IndexOf("play_", StringComparison.Ordinal);
                if (first >= 0)
                {
                    int end = position.Prompt.IndexOf(' ', first);
                    action = position.Prompt.Substring(first, end - first);
                }
            }
            return new Response { Content = "{\"actionId\":\"" + action + "\"}" };
        };
    }

    public static void Restore(PvpService service)
    {
        service.ConfigureAgent("", "deepseek-flash", false);
        service.AgentRequestFactory = DeepSeekGoRequest.Start;
    }

    /// <summary>固定白方与建议响应，真实候选、提交、回合和计分仍由正式代码处理。</summary>
    public static void InstallPresentation(PvpService service, bool rejectHint)
    {
        service.ConfigureAgent("presentation-fixture-not-a-real-key", "fixture", true);
        string[] white = { "play_0_0", "play_7_7", "play_8_7", "play_7_8", "play_7_6", "play_8_6", "play_8_5" };
        service.AgentRequestFactory = (_, position) =>
        {
            if (position.Prompt.Contains("阶段=Scoring"))
            {
                return new Response { Content = "{\"actionId\":\"confirm\"}" };
            }
            if (position.Prompt.Contains("这是给玩家的落子建议"))
            {
                return new Response { Content = rejectHint ? "{\"actionIndex\":999}" : "{\"actionId\":\"play_6_6\"}" };
            }
            int number = service.Core.MoveNumber / 2;
            string action = service.Core.ConsecutivePasses > 0 || number >= white.Length ? "pass" : white[number];
            return new Response { Content = "{\"actionId\":\"" + action + "\"}" };
        };
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
    private static void CompletePlacement(PvpService service)
    {
        // 与本文件原有的HTTP时间夹具一致，只推进表现时限，仍由正式Poll提交合法棋步。
        typeof(PvpService).GetField("_previewDue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(service, -1d);
        service.Poll();
    }

    /// <summary>一气和两气棋块同时出现，确认发给模型的气口与可选提子一致，并且不移动实盘。</summary>
    private static void CheckDecisionPrompt(PvpConfig config)
    {
        var core = new GoCore(config.komiHalfPoints);
        Assert(core.TryPlay(GoSide.Black, 0, 0, out _), "建立左下角黑棋");
        Assert(core.TryPlay(GoSide.White, 1, 0, out _), "白棋叫吃黑棋");
        Assert(core.TryPlay(GoSide.Black, 4, 4, out _), "黑棋脱先");
        ulong hash = core.StateHash;
        var position = GoAgentPosition.Create(core, GoSide.White);
        string wire = DeepSeekGoRequest.InspectWireBody(position);
        Assert(wire.Contains("Ke Jie") && wire.Contains("对手最强应手") && wire.Contains("领先时"), "实际系统消息包含读棋和攻守取舍");
        Assert(wire.Contains("1块(0,0)的气=(0,1)") && wire.Contains("2块(1,0)的气=(2,0)(1,1)"), "实际棋面消息包含双方可核对的气口");
        Assert(wire.Contains("\"strict\":true") && wire.Contains("\"tool_choice\":{\"type\":\"function\"") && wire.Contains("\"thinking\":{\"type\":\"disabled\"}"), "保留严格工具和强制调用协议");
        string[] candidates = position.CopyActionIds();
        int capture = Array.IndexOf(candidates, "play_0_1");
        Assert(capture >= 0 && position.TryReadAction("{\"actionIndex\":" + capture + "}", out GoAgentAction move)
            && move.X == 0 && move.Y == 1 && core.TryPreview(GoSide.White, move.X, move.Y, out GoPlayResult result)
            && result.CapturedCount == 1, "候选索引能通过正式规则提一子");
        Assert(core.StateHash == hash && position.Matches(core), "提示词和预演不修改棋局");
    }

    public static string Run(PvpConfig config)
    {
        CheckDecisionPrompt(config);
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
        Assert(s.Core.Sequence == 1 && s.ComputerPreviewPoint >= 0 && requests == count, "本地回退先亮格且不重复付费");
        CompletePlacement(s);
        Assert(s.Core.Sequence == 2, "亮格结束后只提交一次落子");
        var position = GoAgentPosition.Create(s.Core, GoSide.Black);
        ulong hash = s.Core.StateHash;
        Assert(!position.TryReadAction("{\"actionId\":\"play_4_4\"}", out _), "已占落点不在候选中");
        Assert(!position.TryReadAction(" ", out _) && !position.TryReadAction("not-json", out _), "空白和畸形响应被拒绝");
        Assert(s.Core.StateHash == hash && position.Matches(s.Core), "候选生成与解析不改变实盘");
        s.Dispose();
        s = new PvpService();
        s.Begin(config);
        s.StartComputer();
        s.ConfigureAgent("test-key-not-real", "fixture", true);
        response = new Response { Done = false };
        s.AgentRequestFactory = (_, position) => response;
        s.Submit(GoAction.Play, 4, 4);
        s.Poll();
        typeof(PvpService).GetField("_requestStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(s, -120d);
        s.Poll();
        Assert(response.Disposed && s.ComputerPreviewPoint >= 0, "HTTP绝对超时后排入本地应手");
        CompletePlacement(s);
        Assert(s.Core.Sequence == 2, "超时回退完成合法落子");
        s.Dispose();
        return "PASS: Agent 人机闭环、权限、取消、单请求、非法输出、本地回退、候选隔离";
    }
}

}
#endif

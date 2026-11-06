using System;
using RevGame.Pvp;
using RevGame.Pvp.Agent;

// 仅HTTP边界替身；棋局、权限、时钟、快照和提交均链接生产代码。
namespace RevGame.Pvp.Agent
{
    internal interface IGoAgentRequest : IDisposable
    {
        bool IsDone 
        {
            get;
        }
        string ReadContent();
    }
    internal static class DeepSeekGoRequest
    {
        internal static IGoAgentRequest Start(PvpAgentSettings settings, GoAgentPosition position)
        {
            throw new InvalidOperationException("离线测试禁止访问真实云端");
        }
    }
}

internal static class AgentChecks
{
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
    public static void Run()
    {
        var s = new PvpService();
        Assert(!s.Submit(GoAction.Play, 4, 4), "未开局提交返回拒绝，不访问空棋局");
        s.Begin(new PvpConfig());
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
        s = new PvpService();
        s.Begin(new PvpConfig());
        s.StartServer("127.0.0.1:20000", "offline-fixture");
        var state = new Fantasy.GoRelayState { Phase = "playing", RoomId = "fixture", CoreVersion = PvpService.ProtocolVersion,
            RulesJson = s.Config.RulesJson, MySide = 1, BlackName = "B", WhiteName = "W" };
        RevGame.Pvp.Networking.FantasyGoTransport.Latest.Push(state);
        s.ConfigureAgent("test-key-not-real", "model", true);
        response = new Response { Done = false };
        s.AgentRequestFactory = (_, position) => response;
        Assert(s.SetAgentControl(true), "联机自己的座位可托管");
        s.Poll();
        Assert(!s.Submit(GoAction.Play, 4, 4), "托管期间人工动作被拒绝");
        RevGame.Pvp.Networking.FantasyGoTransport.Latest.Busy = true;
        s.Poll();
        Assert(!response.Disposed, "心跳Busy不应取消在途模型请求");
        RevGame.Pvp.Networking.FantasyGoTransport.Latest.Busy = false;
        Assert(s.SetAgentControl(false) && response.Disposed, "停止联机托管立即取消请求");
        s.Dispose();
        Console.WriteLine("PASS: Agent 人机闭环、权限、取消、单请求、非法输出、本地回退、候选隔离。");
    }
}

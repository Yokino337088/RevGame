using System;
using System.Diagnostics;
using System.Reflection;
using Fantasy;
using RevGame.Pvp;
using RevGame.Pvp.Networking;

internal static class Program
{
    private static int _checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Main(string[] options)
    {
        if (options.Length == 1 && options[0] == "--identity")
        {
            CheckMatchIdentity();
            Console.WriteLine("PASS: " + _checks + " online identity assertions.");
            return;
        }
        if (options.Length == 1 && options[0] == "--delta")
        {
            CheckServerDelta();
            Console.WriteLine("PASS: " + _checks + " online delta assertions.");
            return;
        }
        AgentChecks.Run();
        PvpService service = Local();
        ExpireTurn(service);
        Check(service.Submit(GoAction.Play, 4, 4), "expired click accepted as terminal action");
        Check(service.Core.Phase == GoPhase.Finished && service.Core.EndReason == GoEndReason.Timeout, "expired click becomes timeout");
        Check(service.Core.GetStone(4, 4) == GoSide.Empty && service.Moves[0].kind == (int)GoAction.Timeout, "expired move never lands");
        service = Local();
        ExpireTurn(service);
        service.Poll();
        Check(service.Core.Phase == GoPhase.Finished && service.Core.Winner == GoSide.White, "poll submits own timeout without click");
        service = Local();
        ulong before = service.Core.StateHash;
        Check(!service.Submit(GoAction.Timeout) && service.Core.StateHash == before, "premature timeout rejected");

        GoCore candidate = service.Core.Clone();
        candidate.TryPlay(4, 4, out _);
        PvpMove overdue = new PvpMove { sequence = 1, side = 1, kind = 1, x = 4, y = 4, elapsedMs = 3000,
            before = service.Core.StateHash.ToString(), after = candidate.StateHash.ToString() };
        object[] args = { overdue, null };
        bool accepted = (bool)typeof(PvpService).GetMethod("ApplyMove", Private).Invoke(service, args);
        Check(!accepted && service.Core.StateHash == before && service.Moves.Count == 0, "remote overdue move rejected atomically");
        Check(((string)args[1]).Contains("超时"), "remote overdue rejection gives reason");
        Check(service.ClockText(GoSide.Black).StartsWith("00:01"), "remote rejected time not consumed");

        service = new PvpService();
        service.Begin(new PvpConfig());
        service.StartServer("127.0.0.1:20000", "test");
        GoRelayState state = new GoRelayState { Phase = "playing", RoomId = "fixture", CoreVersion = PvpService.ProtocolVersion,
            RulesJson = "test-config", MySide = 1, BlackName = "A", WhiteName = "B" };
        FantasyGoTransport.Latest.Push(state);
        double started = (double)typeof(PvpService).GetField("_turnStarted", Private).GetValue(service);
        FantasyGoTransport.Latest.Push(state);
        Check((double)typeof(PvpService).GetField("_turnStarted", Private).GetValue(service) == started, "duplicate server state does not reset clock");
        Check(service.Submit(GoAction.Play, 4, 4) && !service.CanAct, "pending command locks action");
        FantasyGoTransport.Latest.Push(state);
        Check(!service.CanAct && service.Core.Sequence == 0, "older push cannot unlock unacknowledged command");
        Check(service.Status != "服务器已连接" && !service.ShouldShowStatusNotice, "重复快照不覆盖待回执状态或触发已连接横幅");
        FantasyGoTransport.Latest.Reject("retry");
        Check(service.Connected && service.CanAct && service.Core.Sequence == 0, "business rejection unlocks without disconnecting or mutating board");
        service.StartServer("127.0.0.1:20000", "test");
        FantasyGoTransport.Latest.Push(state);
        Check((double)typeof(PvpService).GetField("_turnStarted", Private).GetValue(service) == started, "same-history reconnect preserves running thinking time");
        Check(!service.Invite("missing") && !service.ReplyInvitation(true), "invitation API reports rejection honestly");
        service = Local();
        Check(service.Submit(GoAction.Pass) && service.Submit(GoAction.Pass), "double pass enters scoring");
        candidate = service.Core.Clone();
        candidate.TryApply(GoAction.ConfirmScore, GoSide.Black, -1, -1, out _);
        PvpMove confirmation = new PvpMove { sequence = candidate.Sequence, side = 1, kind = (int)GoAction.ConfirmScore,
            x = -1, y = -1, elapsedMs = 100, before = service.Core.StateHash.ToString(), after = candidate.StateHash.ToString() };
        before = service.Core.StateHash;
        args = new object[] { confirmation, null };
        accepted = (bool)typeof(PvpService).GetMethod("ApplyMove", Private).Invoke(service, args);
        Check(!accepted && service.Core.StateHash == before && service.Moves.Count == 2, "scoring cannot inject elapsed time or mutate history on rejection");
        confirmation.elapsedMs = 0;
        args = new object[] { confirmation, null };
        Check((bool)typeof(PvpService).GetMethod("ApplyMove", Private).Invoke(service, args), "zero-time remote confirmation accepted");
        Check(service.Submit(GoAction.ConfirmScore) && service.Core.Phase == GoPhase.Finished, "second confirmation finishes game");
        service = Local();
        service.StartComputer();
        Check(service.IsComputerGame && service.MySide == GoSide.Black, "computer game owns white seat");
        Check(service.Submit(GoAction.Play, 4, 4), "human black move accepted");
        Check(!service.Submit(GoAction.Play, 3, 3) && service.Core.Sequence == 1, "human cannot move for computer");
        service.Poll();
        Check(service.Core.Sequence == 2 && service.Core.Turn == GoSide.Black, "computer uses real rule entry for legal reply");
        service.StartComputer();
        Check(service.Submit(GoAction.Pass), "human can propose scoring");
        typeof(PvpService).GetField("_computerAt", Private).SetValue(service, 0d);
        service.Poll();
        Check(service.Core.Phase == GoPhase.Scoring, "computer accepts double pass");
        typeof(PvpService).GetField("_computerAt", Private).SetValue(service, 0d);
        service.Poll();
        Check(service.Core.HasConfirmed(GoSide.White), "computer confirms only its own score seat");
        Check(service.Submit(GoAction.ConfirmScore) && service.Core.Phase == GoPhase.Finished, "human and computer finish scoring");
        service.StartLocal();
        Check(!service.IsComputerGame, "switching local mode clears computer lifetime");
        Console.WriteLine("PASS: " + _checks + " service timing/state assertions. Unity, JSON and sockets are not covered by this harness.");
    }

    private static void CheckServerDelta()
    {
        using var service = new PvpService();
        var config = new PvpConfig();
        service.Begin(config);
        service.StartServer("127.0.0.1:20000", "增量验收");
        var transport = FantasyGoTransport.Latest;
        var expected = new GoCore(config.komiHalfPoints);
        GoRelayCommand Move(int x)
        {
            var side = expected.Turn;
            string before = expected.StateHash.ToString();
            Check(expected.TryApply(GoAction.Play, side, x, 0, out _), "生成一手合法棋谱");
            return new GoRelayCommand { Sequence = expected.Sequence, Side = (int)side, Kind = (int)GoAction.Play,
                X = x, Y = 0, PreviousHash = before, ResultHash = expected.StateHash.ToString() };
        }
        void Push(int baseSequence, params GoRelayCommand[] commands)
        {
            var state = new GoRelayState { Phase = "playing", RoomId = "delta-fixture", CoreVersion = PvpService.ProtocolVersion,
                RulesJson = config.RulesJson, MySide = 1, BaseSequence = baseSequence };
            state.Commands.AddRange(commands);
            transport.Push(state);
        }

        var first = Move(0);
        var second = Move(1);
        var third = Move(2);
        var fourth = Move(3);
        Push(0, first);
        Push(2, third);
        service.Poll();
        service.Poll();
        Check(service.Core.Sequence == 1 && !service.CanAct && transport.SyncCalls == 1,
            "收到跳号增量时保留旧盘、锁定操作并限频请求全量");
        Push(0, first, second, third);
        Check(service.Core.Sequence == 3 && service.CanAct, "完整棋谱补齐缺口后解锁");
        Push(3, fourth);
        Check(service.Core.Sequence == 4 && service.Core.StateHash == expected.StateHash, "连续增量只应用一手且指纹正确");
        Push(3, fourth);
        Check(service.Core.Sequence == 4 && service.Moves.Count == 4, "重复增量幂等");
        fourth.ResultHash = "0";
        Push(3, fourth);
        Check(!service.Connected && service.Core.StateHash == expected.StateHash, "篡改历史的重复包被拒绝且棋盘不变");
    }

    private static void CheckMatchIdentity()
    {
        const string endpoint = "127.0.0.1:20000";
        using var service = new PvpService();
        var config = new PvpConfig();
        service.Begin(config);
        service.StartServer(endpoint, "同一棋友");
        var transport = FantasyGoTransport.Latest;
        Check(transport.SubmittedToken == "", "首次匹配不带旧令牌");
        transport.Push(new GoRelayState { Phase = "idle", RoomId = "" });
        service.LeaveRoom();
        // 保留真实退局流程，测试只把完成退局的最早时刻移到当前帧。
        typeof(PvpService).GetField("_leaveEarliest", Private).SetValue(service, 0d);
        service.Poll();
        service.Begin(config);
        service.StartServer(endpoint, "同一棋友");
        transport = FantasyGoTransport.Latest;
        Check(transport.SubmittedToken == "test-token" && transport.AllowsNewIdentity, "退局重入复用棋力令牌，过期可申请新身份");
        transport.Push(new GoRelayState { Phase = "playing", RoomId = "fixture", CoreVersion = PvpService.ProtocolVersion,
            RulesJson = config.RulesJson, MySide = 1 });
        service.StartServer(endpoint, "同一棋友");
        transport = FantasyGoTransport.Latest;
        Check(transport.SubmittedToken == "test-token" && !transport.AllowsNewIdentity, "正在恢复房间时保留明确的令牌失效结果");
        service.Begin(config);
        service.StartServer(endpoint, "新棋友");
        Check(FantasyGoTransport.Latest.SubmittedToken == "", "不同昵称不借用其他身份");
        service.StartServer("127.0.0.1:20001", "同一棋友");
        Check(FantasyGoTransport.Latest.SubmittedToken == "", "不同服务器不接收旧令牌");
    }

    private static PvpService Local()
    {
        PvpService service = new PvpService();
        service.Begin(new PvpConfig());
        service.StartLocal();
        return service;
    }

    private static void ExpireTurn(PvpService service)
    {
        // 测试只改变业务使用的“本手开始时间”，不修改棋盘或时钟余额，真实 Submit/Poll 负责判定。
        Stopwatch clock = (Stopwatch)typeof(PvpService).GetField("_clock", Private).GetValue(service);
        typeof(PvpService).GetField("_turnStarted", Private).SetValue(service, clock.Elapsed.TotalSeconds - 3);
    }

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
    }
}

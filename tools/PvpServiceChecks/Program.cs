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

    private static void Main()
    {
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
        FantasyGoTransport.Latest.Reject("retry");
        Check(service.Connected && service.CanAct && service.Core.Sequence == 0, "business rejection unlocks without disconnecting or mutating board");
        service.StartServer("127.0.0.1:20000", "test");
        FantasyGoTransport.Latest.Push(state);
        Check((double)typeof(PvpService).GetField("_turnStarted", Private).GetValue(service) == started, "same-history reconnect preserves running thinking time");
        Check(!service.Invite("missing") && !service.ReplyInvitation(true), "invitation API reports rejection honestly");
        Console.WriteLine("PASS: " + _checks + " service timing/state assertions. Unity, JSON and sockets are not covered by this harness.");
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

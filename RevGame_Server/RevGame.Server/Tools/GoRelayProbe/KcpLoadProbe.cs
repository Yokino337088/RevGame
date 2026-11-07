using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Fantasy.Async;
using Fantasy.Entitas;
using Fantasy.Event;
using Fantasy.Network;
using Fantasy.Network.Interface;
using Fantasy.PacketParser.Interface;
using Fantasy.Scheduler;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>探针单独启动一个 Fantasy Scene，使用框架真实 KCP、序列化和 RPC。不会进入生产服务器的程序集。</summary>
public sealed class OnCreateScene_RunGoLoadProbe : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene value)
    {
        if (value.Scene.SceneRuntimeType == SceneRuntimeType.Root &&
            Environment.GetEnvironmentVariable("REVGO_LOAD_ENDPOINT") is { Length: > 0 } endpoint)
        {
            GoKcpLoadProbe.Run(value.Scene, endpoint).Coroutine();
        }
        await FTask.CompletedTask;
    }
}

/// <summary>挂在探针自己的客户端 Session 上。收到推送只记数量和最后一手，不能把消息池里的状态对象保存下来。</summary>
public sealed class GoLoadPeer : Entity
{
    public AClientNetwork Network;
    public Session Session;
    public bool Connected;
    public bool Failed;
    public string Rules = "";
    public string Kind = "";
    public string Token = "";
    public string Room = "";
    public int Side;
    public int LastPushSequence;
    public long PushedCommandRows;
    public int SeenSequence;
    public string SeenHash = "";
    public bool InvalidHistory;
    public Dictionary<long, FTask<IResponse>> Pending;

    /// <summary>探针独立拼接每个座位收到的棋谱，记录缺手、乱序或指纹断链。</summary>
    public void Observe(GoRelayState state)
    {
        if (state.Commands.Count == 0)
        {
            return;
        }
        int next = SeenSequence + 1;
        if (state.BaseSequence > SeenSequence || (state.BaseSequence > 0 &&
            (state.Commands.Count != 1 || state.Commands[0].Sequence != state.BaseSequence + 1)))
        {
            InvalidHistory = true;
            return;
        }
        for (int i = SeenSequence - state.BaseSequence; i < state.Commands.Count; i++)
        {
            var command = state.Commands[i];
            if (command.Sequence != next || (SeenSequence > 0 && command.PreviousHash != SeenHash))
            {
                InvalidHistory = true;
                return;
            }
            SeenSequence = next++;
            SeenHash = command.ResultHash;
        }
    }
}

/// <summary>Net 包没有启用客户端回包分发。仅在探针进程接收本协议的回包，收发仍走原生 Fantasy KCP。</summary>
internal sealed class GoLoadClientScheduler(Scene scene) : ANetworkMessageScheduler(scene)
{
    public override async FTask Scheduler(Session session, APackInfo packet)
    {
        var peer = session.GetComponent<GoLoadPeer>();
        using (packet)
        {
            if (packet.ProtocolCode == OuterOpcode.G2C_GoState)
            {
                using var message = (G2C_GoState)packet.Deserialize(typeof(G2C_GoState));
                if (peer != null && message.State != null && message.State.RoomId == peer.Room)
                {
                    peer.Observe(message.State);
                    peer.LastPushSequence = Math.Max(peer.LastPushSequence, message.State.BaseSequence + message.State.Commands.Count);
                    peer.PushedCommandRows += message.State.Commands.Count;
                }
                return;
            }
            Type type = packet.ProtocolCode switch
            {
                OuterOpcode.G2C_GoHelloResponse => typeof(G2C_GoHelloResponse),
                OuterOpcode.G2C_GoMatchResponse => typeof(G2C_GoMatchResponse),
                OuterOpcode.G2C_GoCommandResponse => typeof(G2C_GoCommandResponse),
                OuterOpcode.G2C_GoSyncResponse => typeof(G2C_GoSyncResponse),
                OuterOpcode.G2C_GoLeaveResponse => typeof(G2C_GoLeaveResponse),
                OuterOpcode.G2C_GoHeartbeatResponse => typeof(G2C_GoHeartbeatResponse),
                _ => throw new InvalidOperationException("探针收到未预期的协议号 " + packet.ProtocolCode)
            };
            var response = (IResponse)packet.Deserialize(type);
            if (peer != null && peer.Pending.Remove(packet.RpcId, out var completion))
            {
                completion.SetResult(response);
            }
            else
            {
                (response as IDisposable)?.Dispose();
            }
        }
        await FTask.CompletedTask;
    }
}

internal static class GoKcpLoadProbe
{
    // 这个 Fantasy.Net 版本只对 Unity 公开客户端入口。探针按锁定版本绑定一次工厂，
    // 其余操作走真实 KCP 和 Session。生产大厅不依赖这个反射适配。
    private static readonly Func<Scene, NetworkProtocolType, NetworkTarget, bool, AClientNetwork> CreateClient =
        typeof(Scene).Assembly.GetType("Fantasy.Network.NetworkProtocolFactory", true)
            .GetMethod("CreateClient")!.CreateDelegate<Func<Scene, NetworkProtocolType, NetworkTarget, bool, AClientNetwork>>();
    private static readonly Action<ANetwork, ANetworkMessageScheduler> SetScheduler =
        typeof(ANetwork).GetProperty(nameof(ANetwork.NetworkMessageScheduler))!.GetSetMethod(true)!
            .CreateDelegate<Action<ANetwork, ANetworkMessageScheduler>>();
    private static readonly FieldInfo PendingRpcField =
        typeof(Session).GetField("RequestCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static async FTask Run(Scene scene, string endpoint)
    {
        try
        {
            await RunLoad(scene, endpoint);
            Environment.Exit(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Environment.Exit(1);
        }
    }

    private static async FTask RunLoad(Scene scene, string endpoint)
    {
        int players = ReadCount("REVGO_LOAD_PLAYERS", 1000);
        int buckets = ReadCount("REVGO_LOAD_BUCKETS", 100);
        int turns = ReadCount("REVGO_LOAD_TURNS", 30);
        Check(players <= 1024 && players % (2 * buckets) == 0 && turns <= 64, "人数须能给每个测试桶分配偶数席位，手数最多64");
        await scene.TimerComponent.Net.WaitAsync(250);
        var peers = new GoLoadPeer[players];
        var timer = Stopwatch.StartNew();
        // 锁定版本会为每个 Socket 探测缓冲区。分批建连并保活，避免探针创建后续连接时首批空闲超时。
        // 连接和登录准备好后，匹配仍是所有玩家一次并发发起。
        const int connectBatchSize = 8;
        double lastKeepAlive = 0;
        for (int start = 0; start < players; start += connectBatchSize)
        {
            int end = Math.Min(start + connectBatchSize, players);
            for (int i = start; i < end; i++)
            {
                int bucket = i % buckets;
                string kind = bucket % 2 == 0 ? "rookie" : "advanced";
                // 两个场次可有相同 RulesJson，验证必须同时按场次与规则分桶。
                string rules = $"{{\"komiHalfPoints\":15,\"reconnectWindowSeconds\":90,\"matchTimeoutSeconds\":60,\"loadBucket\":{bucket / 2}}}";
                peers[i] = Connect(scene, endpoint, rules, kind);
            }
            await Phase(peers[start..end], async peer =>
            {
                await WaitConnected(scene, peer);
                await Hello(peer);
            });
            if (timer.Elapsed.TotalSeconds - lastKeepAlive >= 2)
            {
                await Phase(peers[..end], Heartbeat);
                lastKeepAlive = timer.Elapsed.TotalSeconds;
            }
        }
        double connectMs = timer.Elapsed.TotalMilliseconds;
        var firstPeer = peers[0];
        using (var first = (G2C_GoMatchResponse)await firstPeer.Session.Call(new C2G_GoMatchRequest()))
        {
            Check(first.ErrorCode == 0 && first.State.Phase == "queued", "首位玩家进入等待");
        }
        using (var repeated = (G2C_GoMatchResponse)await firstPeer.Session.Call(new C2G_GoMatchRequest()))
        {
            Check(repeated.ErrorCode == 0 && repeated.State.Phase == "queued", "重复匹配仍只有原排队状态");
        }
        using (var canceled = (G2C_GoMatchResponse)await firstPeer.Session.Call(new C2G_GoMatchRequest { Cancel = true }))
        {
            Check(canceled.ErrorCode == 0 && canceled.State.Phase == "idle", "取消匹配已摘除队列节点");
        }

        double matchMs = await Phase(peers, async peer =>
        {
            using var result = (G2C_GoMatchResponse)await peer.Session.Call(new C2G_GoMatchRequest());
            Check(result.ErrorCode == 0, "匹配被拒绝 " + result.ErrorCode);
        });
        await Phase(peers, ReadRoom);
        var rooms = peers.GroupBy(peer => peer.Room).ToArray();
        Check(rooms.Length == players / 2 && rooms.All(room => room.Key.Length > 0 && room.Count() == 2
              && room.Sum(peer => peer.Side) == 3 && room.All(peer => peer.Kind == room.First().Kind && peer.Rules == room.First().Rules)),
              "所有匹配均形成唯一的同场次同规则双人房");

        // 测试棋谱所有桶使用同一份贴目，逐波提交同一手合法着法。每局只有当前执棋的一侧发请求。
        var black = peers.Where(peer => peer.Side == 1).ToArray();
        var white = peers.Where(peer => peer.Side == 2).ToArray();
        var board = new GoCore(15);
        int totalRounds = turns + 4;
        var samples = new List<double>(totalRounds * rooms.Length);
        string tracePath = Environment.GetEnvironmentVariable("REVGO_LOAD_TRACE");
        var trace = tracePath == null ? null : new List<string>(totalRounds * rooms.Length);
        long responseRows = 0;
        for (int i = 1; i <= totalRounds; i++)
        {
            var command = i <= turns ? NextLegalMove(board) : NextScoringCommand(board, i - turns);
            await Phase(command.Side == 1 ? black : white, async peer =>
            {
                long started = Stopwatch.GetTimestamp();
                using var result = (G2C_GoCommandResponse)await peer.Session.Call(new C2G_GoCommandRequest
                {
                    RoomId = peer.Room,
                    Command = command.DeepCopy()
                });
                long received = Stopwatch.GetTimestamp();
                Check(result.ErrorCode == 0 && result.State?.Commands.Count == 1 &&
                      result.State.BaseSequence == command.Sequence - 1 &&
                      result.State.Commands[0].ResultHash == command.ResultHash, $"第 {command.Sequence} 手增量确认、序号或指纹异常");
                peer.Observe(result.State);
                samples.Add(Stopwatch.GetElapsedTime(started, received).TotalMilliseconds);
                trace?.Add($"{peer.Room},{command.Sequence},{started},{received}");
                responseRows += result.State.Commands.Count;
            });
            if (i % 10 == 0)
            {
                await Phase(peers, Heartbeat);
            }
            // 避免测试把客户端每秒限流当成服务容量上限，同时每波依旧有500局一起落子。
            await scene.TimerComponent.Net.WaitAsync(90);
        }
        await scene.TimerComponent.Net.WaitAsync(300);
        Check(board.Phase == GoPhase.Finished && peers.All(peer => peer.LastPushSequence >= (peer.Side == 1 ? totalRounds : totalRounds - 1)),
            "所有对局完成双停和计分，对手推送全部到齐");
        Check(peers.All(peer => !peer.InvalidHistory && peer.SeenSequence == totalRounds && peer.SeenHash == board.StateHash.ToString()),
            "所有座位拼接出的棋谱连续且与终局指纹一致");
        long pushRows = peers.Sum(peer => peer.PushedCommandRows);

        var previous = peers[0];
        string roomId = previous.Room;
        string token = previous.Token;
        string rulesJson = previous.Rules;
        string matchKind = previous.Kind;
        previous.Network.Dispose();
        var resumed = Connect(scene, endpoint, rulesJson, matchKind);
        await WaitConnected(scene, resumed);
        await Hello(resumed, token);
        await ReadRoom(resumed, totalRounds, board.StateHash.ToString());
        Check(resumed.Room == roomId && resumed.Token == token, "满载时换真实 Socket 可恢复原房间和棋谱");
        peers[0] = resumed;

        await Phase(peers, async peer =>
        {
            using var result = (G2C_GoLeaveResponse)await peer.Session.Call(new C2G_GoLeaveRequest());
            Check(result.ErrorCode == 0 && result.State.RoomId == "", "房间离席");
        });
        await Phase(peers, async peer =>
        {
            using var result = (G2C_GoSyncResponse)await peer.Session.Call(new C2G_GoSyncRequest());
            Check(result.ErrorCode == 0 && result.State.Phase == "idle" && result.State.Commands.Count == 0, "所有房间离席后状态为空");
            peer.Network.Dispose();
        });
        samples.Sort();
        if (trace != null)
        {
            File.WriteAllLines(tracePath, trace);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            players, buckets, rooms = rooms.Length, turns, scoringCommandsPerRoom = 4, commands = samples.Count, connectMs, matchMs,
            p50Ms = Percentile(samples, .50), p95Ms = Percentile(samples, .95), p99Ms = Percentile(samples, .99),
            responseRows, pushRows, reconnect = true, cleanup = true
        }));
    }

    private static GoLoadPeer Connect(Scene scene, string endpoint, string rules, string kind)
    {
        var network = CreateClient(scene, NetworkProtocolType.KCP, NetworkTarget.Outer, false);
        SetScheduler(network, new GoLoadClientScheduler(scene));
        GoLoadPeer peer = null;
        bool failed = false;
        bool connected = false;
        var session = network.Connect(endpoint, () =>
        {
            connected = true;
            if (peer != null)
            {
                peer.Connected = true;
            }
        }, () =>
        {
            failed = true;
            if (peer != null)
            {
                peer.Failed = true;
            }
        }, null, false, 15000);
        peer = session.AddComponent<GoLoadPeer>(false);
        peer.Session = session;
        peer.Network = network;
        peer.Pending = (Dictionary<long, FTask<IResponse>>)PendingRpcField.GetValue(session)!;
        peer.Rules = rules;
        peer.Kind = kind;
        peer.Connected = connected;
        peer.Failed = failed;
        return peer;
    }

    private static async FTask WaitConnected(Scene scene, GoLoadPeer peer)
    {
        var timer = Stopwatch.StartNew();
        while (!peer.Connected && !peer.Failed && timer.Elapsed.TotalSeconds < 16)
        {
            await scene.TimerComponent.Net.WaitAsync(10);
        }
        Check(peer.Connected && !peer.Failed, "KCP握手未完成");
    }

    private static async FTask Hello(GoLoadPeer peer, string token = "")
    {
        using var result = (G2C_GoHelloResponse)await peer.Session.Call(new C2G_GoHelloRequest
        {
            CoreVersion = "revgo-1",
            Nickname = "并发探针",
            MatchKind = peer.Kind,
            RulesJson = peer.Rules,
            ResumeToken = token,
            SupportsDelta = true
        });
        Check(result.ErrorCode == 0 && !string.IsNullOrEmpty(result.Token), "匿名握手或恢复失败 " + result.ErrorCode);
        peer.Token = result.Token;
    }

    private static async FTask ReadRoom(GoLoadPeer peer)
    {
        await ReadRoom(peer, 0, "");
    }

    private static async FTask ReadRoom(GoLoadPeer peer, int turns, string hash)
    {
        using var result = (G2C_GoSyncResponse)await peer.Session.Call(new C2G_GoSyncRequest());
        var state = result.State;
        Check(result.ErrorCode == 0 && state?.Phase == "playing" && state.Commands.Count == turns &&
              (turns == 0 || state.Commands[^1].ResultHash == hash), "房间状态或历史不正确");
        peer.Room = state.RoomId;
        peer.Side = state.MySide;
        Check(peer.Side == 1 || peer.Side == 2, "座位无效");
    }

    private static async FTask Heartbeat(GoLoadPeer peer)
    {
        using var result = (G2C_GoHeartbeatResponse)await peer.Session.Call(new C2G_GoHeartbeatRequest());
        Check(result.ErrorCode == 0, "心跳失败");
    }

    private static async FTask<double> Phase(GoLoadPeer[] peers, Func<GoLoadPeer, FTask> action)
    {
        var timer = Stopwatch.StartNew();
        var pending = new List<FTask<Exception>>(peers.Length);
        foreach (var peer in peers)
        {
            pending.Add(Observe(peer));
        }
        var errors = await FTask.WaitAll(pending);
        var failure = errors.FirstOrDefault(error => error != null);
        if (failure != null)
        {
            throw new InvalidOperationException("并发批次失败", failure);
        }
        return timer.Elapsed.TotalMilliseconds;

        async FTask<Exception> Observe(GoLoadPeer peer)
        {
            try
            {
                await action(peer);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }
    }

    private static GoRelayCommand NextLegalMove(GoCore board)
    {
        string before = board.StateHash.ToString();
        var side = board.Turn;
        for (int i = 0; i < GoCore.PointCount; i++)
        {
            int x = (i * 17) % GoCore.PointCount % GoCore.BoardSize;
            int y = (i * 17) % GoCore.PointCount / GoCore.BoardSize;
            if (board.TryPlay(x, y, out _))
            {
                return new GoRelayCommand { Sequence = board.Sequence, Side = (int)side, Kind = 1, X = x, Y = y,
                    ElapsedMs = 100, Payload = "", PreviousHash = before, ResultHash = board.StateHash.ToString() };
            }
        }
        throw new InvalidOperationException("测试棋谱已无合法点");
    }

    private static GoRelayCommand NextScoringCommand(GoCore board, int index)
    {
        string before = board.StateHash.ToString();
        var action = index < 3 ? GoAction.Pass : GoAction.ConfirmScore;
        var side = index < 3 ? board.Turn : index == 3 ? GoSide.Black : GoSide.White;
        Check(board.TryApply(action, side, -1, -1, out _), "测试棋谱可以正常数子");
        return new GoRelayCommand { Sequence = board.Sequence, Side = (int)side, Kind = (int)action, X = -1, Y = -1,
            ElapsedMs = index < 3 ? 100 : 0, Payload = "", PreviousHash = before, ResultHash = board.StateHash.ToString() };
    }

    private static int ReadCount(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;
    }

    private static double Percentile(List<double> sorted, double quantile)
    {
        return Math.Round(sorted[(int)Math.Ceiling(sorted.Count * quantile) - 1], 2);
    }

    private static void Check(bool passed, string description)
    {
        if (!passed)
        {
            throw new InvalidOperationException(description);
        }
    }
}

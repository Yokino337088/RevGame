// 真实 TCP 协议探针：不是游戏服务，也不注册伪 Handler。
// 使用与客户端同源的 GoCore 和已导出协议，验证真实 Fantasy 服务的收包/回包/重连。
// 测试工具使用有界阻塞 Socket，避免把测试专用 Task/线程模型带进生产业务。
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Fantasy;
using Fantasy.Network.Interface;
using LightProto;
using RevGame.Pvp;

// KCP 压测使用独立 Fantasy 进程和 Scene，旧 TCP 探针命令保持兼容。
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REVGO_LOAD_ENDPOINT")))
{
    await Fantasy.Platform.Net.Entry.Start(new GameConsoleLog());
    return;
}

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 20000;
string mode = args.Length > 2 ? args[2] : "smoke";
string rules = args.Length > 3 ? File.ReadAllText(args[3]).Trim() : "{\"komiHalfPoints\":15,\"reconnectWindowSeconds\":90,\"matchTimeoutSeconds\":60}";
if (mode == "opponent")
{
    Opponent(host, port, rules);
}
else if (mode == "expiry")
{
    Expiry(host, port, rules);
}
else if (mode == "ranking")
{
    Ranking(host, port, rules);
}
else if (mode == "capacity")
{
    Capacity(host, port, rules);
}
else
{
    Smoke(host, port, rules);
}

static void Check(bool value, string name)
{
    if (!value)
    {
        throw new InvalidOperationException("断言失败：" + name);
    }
    Console.WriteLine("PASS " + name);
}

static GoRelayCommand Apply(GoCore core, GoAction kind, GoSide side, int x = 0, int y = 0)
{
    string before = core.StateHash.ToString();
    Check(core.TryApply(kind, side, x, y, out var result), "共享 GoCore 接受 " + kind + ":" + result.Error);
    return new GoRelayCommand { Sequence = core.Sequence, Side = (int)side, Kind = (int)kind, X = x, Y = y,
        // 只有落子、停一手和超时消耗思考时间；确认计分等动作必须为0，与正式客户端契约一致。
        Payload = "", ElapsedMs = kind == GoAction.Play || kind == GoAction.Pass || kind == GoAction.Timeout ? 100 : 0,
        PreviousHash = before, ResultHash = core.StateHash.ToString() };
}

static GoCore Replay(GoRelayState state)
{
    using var json = JsonDocument.Parse(state.RulesJson);
    int komi = json.RootElement.TryGetProperty("komiHalfPoints", out var field) ? field.GetInt32() : 15;
    var core = GoCore.CreateMatch(komi);
    foreach (var command in state.Commands)
    {
        Check(core.StateHash.ToString() == command.PreviousHash, "重放前哈希一致");
        Check(core.TryApply((GoAction)command.Kind, (GoSide)command.Side, command.X, command.Y, out _), "重放规则接受");
        Check(core.StateHash.ToString() == command.ResultHash && core.Sequence == command.Sequence, "重放后哈希和序号一致");
    }
    return core;
}

static void Smoke(string host, int port, string rules)
{
    using var a = new RelayClient(host, port);
    using var b = new RelayClient(host, port);
    var bad = a.Hello("过期客户端", rules, version: "old");
    Check(bad.ErrorCode != 0, "版本不一致拒绝");
    var helloA = a.Hello("测试甲", rules);
    var helloB = b.Hello("测试乙", rules);
    Check(helloA.ErrorCode == 0 && helloB.ErrorCode == 0, "两名匿名玩家握手");
    Check(a.Match().State.Phase == "queued", "第一人排队");
    Check(a.Match(true).State.Phase == "idle", "排队可取消");
    Check(a.Match().State.Phase == "queued", "重新排队");
    var matched = b.Match();
    var stateA = a.Sync().State;
    var stateB = b.Sync().State;
    Check(matched.ErrorCode == 0 && stateA.RoomId == stateB.RoomId && stateA.MySide + stateB.MySide == 3, "真实匹配房间与黑白分配");
    RelayClient black = stateA.MySide == 1 ? a : b;
    RelayClient white = stateA.MySide == 2 ? a : b;
    var core = GoCore.CreateMatch(15);
    var move = Apply(core, GoAction.Play, GoSide.Black, 2, 2);
    var rejected = white.Send(stateA.RoomId, move);
    Check(rejected.ErrorCode != 0 && rejected.State.Commands.Count == 0, "不能冒用另一方身份");
    var forged = move.DeepCopy();
    forged.ResultHash = "0";
    Check(black.Send(stateA.RoomId, forged).ErrorCode == 17 && white.Sync().State.Commands.Count == 0, "Battle 拒绝伪造首手，双方棋谱仍为空");
    var first = black.Send(stateA.RoomId, move);
    Check(first.ErrorCode == 0 && first.State.Commands.Count == 1, "黑方落子已转发");
    Check(black.Send(stateA.RoomId, move).State.Commands.Count == 1, "完全相同重发幂等");
    var altered = move.DeepCopy();
    altered.X = 3;
    Check(black.Send(stateA.RoomId, altered).ErrorCode != 0, "重复序号改内容拒绝");
    Check(white.Sync().State.Commands.Single().ResultHash == core.StateHash.ToString(), "对手收到同一哈希");
    var whiteMove = Apply(core, GoAction.Play, GoSide.White, 6, 6);
    var wrongResult = whiteMove.DeepCopy();
    wrongResult.ResultHash = "0";
    Check(white.Send(stateA.RoomId, wrongResult).ErrorCode == 17 &&
        black.Sync().State.Commands.Single().ResultHash == move.ResultHash, "结果指纹错误后保留上一次确认的棋盘");
    var legacy = white.Send(stateA.RoomId, whiteMove);
    Check(legacy.ErrorCode == 0 && legacy.State.BaseSequence == 0 && legacy.State.Commands.Count == 2,
        "未协商增量的旧客户端仍收到完整棋谱");
    Check(Replay(a.Sync().State).StateHash == Replay(b.Sync().State).StateHash, "双方同源重放完全一致");
    // 断开甲连接，恢复令牌重新建 Socket；不是复用旧会话伪造重连。
    string token = helloA.Token;
    a.Dispose();
    using var resumed = new RelayClient(host, port);
    var recovered = resumed.Hello("测试甲", rules, token);
    Check(recovered.ErrorCode == 0 && recovered.State.RoomId == stateA.RoomId && recovered.State.Commands.Count == 2, "新连接恢复身份和完整操作序列");
    Check(Replay(recovered.State).StateHash == core.StateHash, "恢复局面哈希一致");
    var leave = resumed.Leave();
    Check(leave.ErrorCode == 0 && leave.State.Phase == "idle", "主动离开释放自己的房间引用");
    Check(b.Sync().State.Phase == "closed", "对手收到明确离开状态");
    Check(b.Leave().State.Phase == "idle", "双方退出回到大厅");
    Console.WriteLine("GO_RELAY_SMOKE_PASS");
}

static void Ranking(string host, int port, string rules)
{
    using var a = new RelayClient(host, port) { LocalRating = 1500 };
    using var b = new RelayClient(host, port) { LocalRating = 1500 };
    var helloA = a.Hello("棋力验收甲", rules);
    var helloB = b.Hello("棋力验收乙", rules);
    Check(helloA.ErrorCode == 0 && helloB.ErrorCode == 0, "棋力验收双方握手");
    using var ruleDocument = JsonDocument.Parse(rules);
    int komi = ruleDocument.RootElement.GetProperty("komiHalfPoints").GetInt32();
    void StoreResult(RelayClient winner, RelayClient loser)
    {
        var won = winner.Sync().State;
        var lost = loser.Sync().State;
        Check(won.RatingFinalized && lost.RatingFinalized && won.RatingDelta > 0 && lost.RatingDelta == -won.RatingDelta,
            "服务端只回传终局双方分差，供客户端各自保存");
        winner.LocalRating += won.RatingDelta;
        loser.LocalRating += lost.RatingDelta;
    }
    void ScoreWin(RelayClient winner, RelayClient loser)
    {
        Check(winner.Match().State.Phase == "queued", "固定赢家先进入队列");
        var room = loser.Match().State;
        Check(room.Phase == "playing", "尚在初始棋力窗时立即配对");
        var core = GoCore.CreateMatch(komi);
        var black = room.MySide == 1 ? loser : winner;
        var white = room.MySide == 2 ? loser : winner;
        void Step(RelayClient player, GoAction action, GoSide side)
        {
            var command = Apply(core, action, side);
            Check(player.Send(room.RoomId, command).ErrorCode == 0, "Battle 接受 " + action);
        }
        // 棋盘上只留固定赢家的棋子，其余空点归同一颜色。
        if (black == winner)
        {
            Step(black, GoAction.Play, GoSide.Black);
        }
        else
        {
            Step(black, GoAction.Pass, GoSide.Black);
            Step(white, GoAction.Play, GoSide.White);
        }
        Step(core.Turn == GoSide.Black ? black : white, GoAction.Pass, core.Turn);
        Step(core.Turn == GoSide.Black ? black : white, GoAction.Pass, core.Turn);
        Check(core.Winner == (room.MySide == 1 ? GoSide.White : GoSide.Black), "指定玩家赢得双停自动计分");
        StoreResult(winner, loser);
        winner.Leave();
        loser.Leave();
        Thread.Sleep(1100);
    }
    for (int i = 0; i < 3; i++)
    {
        ScoreWin(a, b);
    }
    Check(a.Match().State.Phase == "queued", "第四局进入匹配");
    var resignation = b.Match().State;
    var resigningBoard = GoCore.CreateMatch(komi);
    var resign = Apply(resigningBoard, GoAction.Resign, (GoSide)resignation.MySide);
    Check(b.Send(resignation.RoomId, resign).ErrorCode == 0, "本人认输有效");
    Check(b.Send(resignation.RoomId, resign).ErrorCode == 0, "同一条认输指令允许幂等重发");
    StoreResult(a, b);
    a.Leave();
    b.Leave();
    Thread.Sleep(1100);
    Check(a.Match().State.Phase == "queued" && b.Match().State.Phase == "queued", "三次数子加一次认输后超出初始棋力窗");
    var wait = System.Diagnostics.Stopwatch.StartNew();
    while (wait.Elapsed.TotalSeconds < 11)
    {
        Thread.Sleep(250);
        var stateA = a.Sync().State;
        var stateB = b.Sync().State;
        if (stateA.Phase == "playing" && stateB.RoomId == stateA.RoomId)
        {
            Check(wait.Elapsed.TotalSeconds >= 7.9, "双方等待满 8 秒才放宽，定时扫描配成同一房间");
            a.Leave();
            b.Leave();
            break;
        }
    }
    Check(a.Sync().State.Phase == "idle" && b.Sync().State.Phase == "idle", "放宽后对局可正常退出");
    using var c = new RelayClient(host, port) { LocalRating = 1500 };
    using var d = new RelayClient(host, port) { LocalRating = 1500 };
    Check(c.Hello("棋力验收丙", rules).ErrorCode == 0 && d.Hello("棋力验收丁", rules).ErrorCode == 0, "新玩家从初始棋力分入场");
    // 甲额外赢一局，离 1500 分更远。让甲先排队，丁入队后仍应优先选更接近的乙。
    ScoreWin(a, c);
    Check(a.Match().State.Phase == "queued" && b.Match().State.Phase == "queued", "两个分差过大的候选保持等待");
    var preferred = d.Match().State;
    Check(preferred.Phase == "playing" && b.Sync().State.RoomId == preferred.RoomId && a.Sync().State.Phase == "queued",
        "同段内优先选分差更小的对手");
    a.Match(true);
    b.Leave();
    d.Leave();
    if (ruleDocument.RootElement.GetProperty("reconnectWindowSeconds").GetInt32() == 1)
    {
        a.Dispose();
        b.Dispose();
        Thread.Sleep(2300);
        using var returnedA = new RelayClient(host, port);
        using var returnedB = new RelayClient(host, port);
        var recoveredA = returnedA.Hello("棋力验收甲", rules, helloA.Token);
        var recoveredB = returnedB.Hello("棋力验收乙", rules, helloB.Token);
        Check(recoveredA.ErrorCode == 4 && recoveredB.ErrorCode == 4, "旧令牌随匿名会话过期，服务端没有棋力档案");
        Check(returnedA.Hello("棋力验收甲", rules).ErrorCode == 0 && returnedB.Hello("棋力验收乙", rules).ErrorCode == 0,
            "两名玩家建立全新匿名连接");
        returnedA.LocalRating = a.LocalRating;
        returnedB.LocalRating = b.LocalRating;
        Check(returnedA.Match().State.Phase == "queued" && returnedB.Match().State.Phase == "queued", "重建连接后仍保留棋力分差");
        returnedA.Match(true);
        returnedB.Match(true);
    }
    Console.WriteLine("GO_RELAY_RANKING_PASS");
}

// 用仅启用一个 Battle 的测试配置，把该节点的 128 席占满再验证候补。
static void Capacity(string host, int port, string rules)
{
    const int players = 258;
    var clients = new List<RelayClient>(players);
    try
    {
        for (int i = 0; i < players; i++)
        {
            var client = new RelayClient(host, port);
            clients.Add(client);
            if (client.Hello($"容量验收{i}", rules).ErrorCode != 0 || client.Match().State.Phase != (i < 256 && i % 2 == 1 ? "playing" : "queued"))
            {
                throw new InvalidOperationException($"容量验收玩家 {i} 的开房状态错误");
            }
        }
        Check(clients[0].Sync().State.Phase == "playing" && clients[255].Sync().State.Phase == "playing", "单节点已有 128 局");
        Check(clients[256].Sync().State.Phase == "queued" && clients[257].Sync().State.Phase == "queued", "容量耗尽时双方继续排队");
        clients[0].Leave();
        clients[1].Leave();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 3)
        {
            Thread.Sleep(250);
            var first = clients[256].Sync().State;
            var second = clients[257].Sync().State;
            if (first.Phase == "playing" && first.RoomId == second.RoomId)
            {
                Console.WriteLine("GO_RELAY_CAPACITY_PASS");
                return;
            }
        }
        throw new InvalidOperationException("释放计算席位后未自动匹配候补");
    }
    finally
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}

static void Opponent(string host, int port, string rules)
{
    using var client = new RelayClient(host, port);
    Check(client.Hello("联机验收对手", rules).ErrorCode == 0, "探针对手握手");
    Check(client.Match().ErrorCode == 0, "探针对手加入匹配");
    Console.WriteLine("OPPONENT_WAITING");
    DateTime deadline = DateTime.UtcNow.AddMinutes(5);
    int lastSequence = -1;
    while (DateTime.UtcNow < deadline)
    {
        var state = client.Sync().State;
        if (state.Phase == "closed")
        {
            Console.WriteLine("OPPONENT_ROOM_CLOSED");
            break;
        }
        if (state.Phase == "playing" && state.Commands.Count != lastSequence)
        {
            lastSequence = state.Commands.Count;
            var core = Replay(state);
            var side = (GoSide)state.MySide;
            if (core.Phase == GoPhase.Finished)
            {
                Console.WriteLine("OPPONENT_FINISHED " + core.StateHash);
            }
            else if (core.Phase == GoPhase.Playing && core.Turn == side)
            {
                client.Send(state.RoomId, Apply(core, GoAction.Pass, side));
            }
        }
        // 只用于开发工具降低轮询频率，不在生产逻辑中使用 Thread 或 Task。
        System.Threading.Thread.Sleep(500);
    }
    client.Leave();
}

static void Expiry(string host, int port, string rules)
{
    using var a = new RelayClient(host, port);
    using var b = new RelayClient(host, port);
    using var waiting = new RelayClient(host, port);
    var hello = a.Hello("断线窗口甲", rules);
    Check(hello.ErrorCode == 0 && b.Hello("断线窗口乙", rules).ErrorCode == 0, "过期测试双方握手");
    a.Match();
    b.Match();
    waiting.Hello("独立排队玩家", rules);
    Check(waiting.Match().State.Phase == "queued", "独立玩家进入等待队列");
    a.Dispose();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    bool sawDisconnect = false;
    bool queueExpired = false;
    bool roomExpired = false;
    while (timer.Elapsed.TotalSeconds < 98)
    {
        var room = b.Sync().State;
        var queue = waiting.Sync().State;
        if (room.Phase == "disconnected")
        {
            if (!sawDisconnect)
            {
                Check(room.ReconnectDeadlineUtcMs > room.ServerUtcMs, "重连截止时间有效");
                sawDisconnect = true;
            }
        }
        if (queue.Phase == "idle")
        {
            queueExpired = true;
        }
        if (room.Phase == "closed")
        {
            roomExpired = true;
            break;
        }
        System.Threading.Thread.Sleep(1000);
    }
    Check(sawDisconnect, "真实TCP断开通知对手");
    Check(queueExpired, "默认60秒匹配窗口自动退出排队");
    Check(roomExpired && timer.Elapsed.TotalSeconds >= 89 && timer.Elapsed.TotalSeconds <= 94, "默认90秒断线窗口清理房间");
    using var old = new RelayClient(host, port);
    Check(old.Hello("已过期身份", rules, hello.Token).ErrorCode != 0, "已过期恢复令牌拒绝");
    Check(b.Leave().State.Phase == "idle", "超时后存活客户端仍可返回大厅");
    Console.WriteLine("GO_RELAY_EXPIRY_PASS " + timer.Elapsed.TotalSeconds.ToString("F1") + "s");
}

/// <summary>只处理工具需要的外网 TCP 分包格式；生产客户端始终使用 FantasyGoTransport。</summary>
sealed class RelayClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _rpcId;
    // 探针模拟本机档案。默认零值让其它测试继续覆盖旧客户端的兼容路径。
    public int LocalRating;

    public RelayClient(string host, int port)
    {
        _tcp = new TcpClient();
        _tcp.ReceiveTimeout = 6000;
        _tcp.SendTimeout = 6000;
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    public G2C_GoHelloResponse Hello(string nickname, string rules, string token = "", string version = GoCore.MatchProtocolVersion)
    {
        return Call<C2G_GoHelloRequest, G2C_GoHelloResponse>(new C2G_GoHelloRequest { Nickname = nickname, RulesJson = rules, ResumeToken = token, CoreVersion = version, MatchKind = "rookie" });
    }
    public G2C_GoMatchResponse Match(bool cancel = false)
    {
        return Call<C2G_GoMatchRequest, G2C_GoMatchResponse>(new C2G_GoMatchRequest { Cancel = cancel, LocalRating = cancel ? 0 : LocalRating });
    }
    public G2C_GoSyncResponse Sync()
    {
        return Call<C2G_GoSyncRequest, G2C_GoSyncResponse>(new C2G_GoSyncRequest());
    }
    public G2C_GoLeaveResponse Leave()
    {
        return Call<C2G_GoLeaveRequest, G2C_GoLeaveResponse>(new C2G_GoLeaveRequest());
    }
    public G2C_GoCommandResponse Send(string room, GoRelayCommand command)
    {
        return Call<C2G_GoCommandRequest, G2C_GoCommandResponse>(new C2G_GoCommandRequest { RoomId = room, Command = command });
    }
    private TResponse Call<TRequest, TResponse>(TRequest request) where TRequest : IMessage, IProtoParser<TRequest> where TResponse : IResponse, IProtoParser<TResponse>
    {
        using var body = new MemoryStream();
        Serializer.Serialize(body, request);
        byte[] payload = body.ToArray();
        byte[] header = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length == 0 ? -1 : payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), request.OpCode());
        uint id = ++_rpcId;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), id);
        _stream.Write(header);
        _stream.Write(payload);
        // 推送可能先于RPC响应到达，必须按rpcId找自己的响应，不能把下一包当作返回值。
        while (true)
        {
            _stream.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < -1 || length > 1048576)
            {
                throw new InvalidDataException("响应包长无效。");
            }
            byte[] bytes = new byte[Math.Max(0, length)];
            _stream.ReadExactly(bytes);
            uint responseId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (responseId == id)
            {
                return Serializer.Deserialize<TResponse>(bytes);
            }
            if (responseId != 0)
            {
                throw new InvalidDataException("收到非本次请求的RPC响应。");
            }
        }
    }
    public void Dispose()
    {
        _tcp.Dispose();
    }
}

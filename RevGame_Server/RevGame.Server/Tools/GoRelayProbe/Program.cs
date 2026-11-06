// 真实 TCP 协议探针：不是游戏服务，也不注册伪 Handler。
// 直接编译客户端同一份 GoCore 和已导出协议，验证真实 Fantasy 服务的收包/回包/重连。
// 测试工具使用有界阻塞 Socket，避免把测试专用 Task/线程模型带进生产业务。
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Fantasy;
using Fantasy.Network.Interface;
using LightProto;
using RevGame.Pvp;

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
        Payload = "", ElapsedMs = 100, PreviousHash = before, ResultHash = core.StateHash.ToString() };
}

static GoCore Replay(GoRelayState state)
{
    using var json = JsonDocument.Parse(state.RulesJson);
    int komi = json.RootElement.TryGetProperty("komiHalfPoints", out var field) ? field.GetInt32() : 15;
    var core = new GoCore(komi);
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
    var core = new GoCore(15);
    var move = Apply(core, GoAction.Play, GoSide.Black, 2, 2);
    var rejected = white.Send(stateA.RoomId, move);
    Check(rejected.ErrorCode != 0 && rejected.State.Commands.Count == 0, "不能冒用另一方身份");
    var first = black.Send(stateA.RoomId, move);
    Check(first.ErrorCode == 0 && first.State.Commands.Count == 1, "黑方落子已转发");
    Check(black.Send(stateA.RoomId, move).State.Commands.Count == 1, "完全相同重发幂等");
    var altered = move.DeepCopy();
    altered.X = 3;
    Check(black.Send(stateA.RoomId, altered).ErrorCode != 0, "重复序号改内容拒绝");
    Check(white.Sync().State.Commands.Single().ResultHash == core.StateHash.ToString(), "对手收到同一哈希");
    var whiteMove = Apply(core, GoAction.Play, GoSide.White, 6, 6);
    Check(white.Send(stateA.RoomId, whiteMove).ErrorCode == 0, "白方合法顺序转发");
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
            else if (core.Phase == GoPhase.Scoring && !core.HasConfirmed(side))
            {
                client.Send(state.RoomId, Apply(core, GoAction.ConfirmScore, side));
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

    public RelayClient(string host, int port)
    {
        _tcp = new TcpClient();
        _tcp.ReceiveTimeout = 6000;
        _tcp.SendTimeout = 6000;
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    public G2C_GoHelloResponse Hello(string nickname, string rules, string token = "", string version = "revgo-1")
    {
        return Call<C2G_GoHelloRequest, G2C_GoHelloResponse>(new C2G_GoHelloRequest { Nickname = nickname, RulesJson = rules, ResumeToken = token, CoreVersion = version, MatchKind = "rookie" });
    }
    public G2C_GoMatchResponse Match(bool cancel = false)
    {
        return Call<C2G_GoMatchRequest, G2C_GoMatchResponse>(new C2G_GoMatchRequest { Cancel = cancel });
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

using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Fantasy.Async;
using Fantasy.Entitas;
using Fantasy.Network;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>
/// 大厅负责连接、匹配、Battle 分配及客户端同步。围棋校验在分配好的 Battle 场景执行，
/// Gate 保留已确认棋谱，供两席同步以及发起决斗时重放局面。
/// </summary>
public static class GoLobbyComponentSystem
{
    /// <summary>握手使用的围棋规则版本。版本不同的客户端不能进入同一套棋规。</summary>
    public const string CoreVersion = GoCore.MatchProtocolVersion;
    // 以下是服务运维边界，不是棋力/棋规数值。限制无身份连接造成的内存和广播放大。
    /// <summary>大厅最多保留的匿名身份数，包含重连等待期内的离线身份。</summary>
    public const int MaxPlayers = 1024;
    /// <summary>单房间最多保存的已确认操作数，限制同步快照和内存开销。</summary>
    public const int MaxCommands = 1024;
    /// <summary>单个 Battle 节点在本大厅视角下允许分配的房间数上限。</summary>
    private const int MaxRoomsPerWorker = 128;

    /// <summary>返回 UTC Unix 毫秒。入队、开房、断线和重连截止时间共用这个时间基准。</summary>
    public static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>从 Session 绑定取当前玩家。销毁过的身份或被新连接接管的绑定均视为无效。</summary>
    public static GoPlayerEntity? Player(this GoLobbyComponent self, Session session)
    {
        var player = session.GetComponent<GoSessionComponent>()?.Player;
        // 同时核对玩家指向的连接，防止旧 Session 用残留绑定继续提交操作。
        return player != null && !player.IsDisposed && player.Connection == session ? player : null;
    }

    /// <summary>校验握手并创建或恢复匿名身份。返回 0 代表成功，非零时 reason 给出原因。</summary>
    public static uint Hello(this GoLobbyComponent self, Session session, C2G_GoHelloRequest request, out GoPlayerEntity? player, out string reason)
    {
        player = null;
        reason = "";
        // 先检查规则版本，避免不兼容的客户端占用大厅身份。
        if (request.CoreVersion != CoreVersion)
        {
            reason = "围棋规则版本不一致，请更新客户端。";
            return 1;
        }
        // 为客户端文本设长度上限，并只接受已定义的场次。
        if (string.IsNullOrWhiteSpace(request.Nickname) || request.Nickname.Length > 24 || string.IsNullOrWhiteSpace(request.RulesJson) || request.RulesJson.Length > 4096 || (request.MatchKind != "rookie" && request.MatchKind != "advanced"))
        {
            reason = "昵称、规则配置或场次不正确。";
            return 2;
        }
        // 停机阶段停止分配新的会话与对局资源。
        if (self.Closing)
        {
            reason = "服务器正在关闭，请稍后再试；单机和局域网对战仍可使用。";
            return 3;
        }
        // 握手时把必须的规则字段转成服务端所用的单位，后续入队与回收可直接读取。
        if (!TryReadRules(request.RulesJson, out long reconnectWindow, out long matchWindow, out int komi))
        {
            reason = "规则配置缺少有效的重连时间、匹配时间或贴目。";
            return 2;
        }
        // 同一连接重复握手不能创建额外身份，也不能悄悄改变进行中的规则。
        player = self.Player(session);
        if (player != null)
        {
            return 0;
        }
        // 有令牌时走恢复路径。校验失败交还客户端处理，避免旧局被当成新身份。
        if (!string.IsNullOrEmpty(request.ResumeToken))
        {
            // 32 字节随机值以十六进制编码，合法长度固定为 64 字符。
            if (request.ResumeToken.Length != 64)
            {
                reason = "恢复令牌无效，请重新匹配。";
                return 4;
            }
            if (!self.Players.TryGetValue(request.ResumeToken, out player))
            {
                reason = "恢复令牌已过期，请重新匹配。";
                return 4;
            }
            // 恢复继续使用原来的规则和场次，防止中途更改配置导致双方棋谱分叉。
            if (player.RulesJson != request.RulesJson || player.MatchKind != request.MatchKind)
            {
                player = null;
                reason = "恢复对局的规则配置不一致。";
                return 5;
            }
        }
        if (player != null)
        {
            // 恢复令牌持有者接管旧连接；先解除关联，旧连接销毁就不会把新连接标为掉线。
            var previous = player.Connection;
            player.Connection = null;
            if (previous != null && !previous.IsDisposed && previous != session)
            {
                var flag = previous.GetComponent<GoSessionComponent>();
                if (flag != null)
                {
                    // 先解绑旧 Session，再 Dispose，避免其销毁回调进入断线逻辑。
                    flag.Player = null;
                }
                previous.Dispose();
            }
        }
        else
        {
            if (self.Players.Count >= MaxPlayers)
            {
                reason = "服务器当前人数已满，请稍后再试。";
                return 6;
            }
            // 让 Fantasy 按 Gate Scene 生命周期创建玩家身份，令牌用加密随机数生成。
            player = Entity.Create<GoPlayerEntity>(self.Scene, false, true);
            player.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            player.Nickname = request.Nickname.Trim();
            // 留一份适合写日志的昵称，剔除可能插入换行或终端控制序列的字符。
            player.LogNickname = new string(player.Nickname.Where(c => !char.IsControl(c)).ToArray());
            player.RulesJson = request.RulesJson;
            player.MatchKind = request.MatchKind;
            player.ReconnectWindowMs = reconnectWindow;
            player.MatchWindowMs = matchWindow;
            player.Komi = komi;
            // 把玩家放进恢复索引，之后断线可用同一令牌找回。
            self.Players.Add(player.Token, player);
        }
        string identity = string.IsNullOrEmpty(request.ResumeToken) ? "新建" : "恢复连接";
        Log.Info($"[PVP/连接] 玩家={GoEventLog.PlayerLabel(player)} 事件=握手或恢复成功 身份={identity} 房间={player.Room?.RoomId ?? "无"}");
        // 建立玩家和当前连接的双向绑定，并结束断线计时。
        if (player.Connection != session)
        {
            player.Room?.Duel?.DisconnectNetworkSeat(player.Side);
        }
        player.Connection = session;
        player.DisconnectedAt = 0;
        var binding = session.GetOrAddComponent<GoSessionComponent>();
        binding.Player = player;
        binding.SupportsDelta = request.SupportsDelta;
        // 握手能力随 Session 绑定；换连接时按新握手重新确认版本。
        binding.DuelFingerprint = request.DuelFingerprint;
        if (player.Room != null)
        {
            // 恢复方从握手回包拿状态，对手需要推送以解除等待重连提示。
            self.Broadcast(player.Room, player);
        }
        return 0;
    }

    /// <summary>处理入队或取消。无法立即匹配时保留队列节点，交给下一次入队或扫尾任务重试。</summary>
    public static uint Match(this GoLobbyComponent self, GoPlayerEntity player, bool cancel, int localRating, out string reason)
    {
        reason = "";
        if (cancel)
        {
            self.RemoveFromQueue(player);
            Log.Info($"[PVP/匹配] 玩家={GoEventLog.PlayerLabel(player)} 事件=取消匹配");
            return 0;
        }
        // 已入房时必须先离席，防止同一身份同时属于两个房间。
        if (player.Room != null)
        {
            reason = "请先退出当前对局。";
            return 7;
        }
        long now = Now();
        // 重复点匹配不会插队、续期或生成两个排队节点。
        if (player.WaitingNode != null && now - player.QueuedAt < player.MatchWindowMs)
        {
            return 0;
        }
        self.RemoveFromQueue(player);
        // 用场次和完整规则文本分隔队列，只有同一组的玩家可以互相匹配。
        var key = (player.MatchKind, player.RulesJson);
        if (!self.Waiting.TryGetValue(key, out var queue))
        {
            queue = new LinkedList<GoPlayerEntity>();
            self.Waiting.Add(key, queue);
        }
        player.QueuedAt = now;
        // 无本地分数时使用默认分，已有分数限制在可接受区间。
        player.QueuedRating = localRating == 0 ? GoMatchPolicy.InitialRating : Math.Clamp(localRating, 100, 3000);
        // 追加到队尾并记住节点，使取消和超时退队能直接删除这一项。
        player.WaitingNode = queue.AddLast(player);
        if (!self.TryMatch(queue, player, now, player))
        {
            Log.Info($"[PVP/匹配] 玩家={GoEventLog.PlayerLabel(player)} 场次={player.MatchKind} 事件=进入队列 同规则等待人数={queue.Count}");
        }
        return 0;
    }

    /// <summary>在同规则队列找棋力最接近的人。没有房间容量时，两人都留在原队列。</summary>
    private static bool TryMatch(this GoLobbyComponent self, LinkedList<GoPlayerEntity> queue, GoPlayerEntity player, long now, GoPlayerEntity? responseRecipient)
    {
        // 少于两人或 Battle 已满载时保留队列，等条件变化再尝试。
        if (queue.Count < 2 || !self.TryFindWorker(out long workerAddress))
        {
            return false;
        }
        GoPlayerEntity? opponent = null;
        int bestDistance = int.MaxValue;
        long playerWaitMs = now - player.QueuedAt;
        foreach (var candidate in queue)
        {
            // 略过自己、已超时和连接失效的人，避免刚配对就进入断线等待。
            long candidateWaitMs = now - candidate.QueuedAt;
            if (candidate == player || candidateWaitMs >= candidate.MatchWindowMs || candidate.Connection == null || candidate.Connection.IsDisposed)
            {
                continue;
            }
            // 用双方较短的等待时间计算可接受分差，避免把刚入队的人拉入过宽的窗口。
            int distance = GoMatchPolicy.Distance(player.QueuedRating, candidate.QueuedRating, Math.Min(playerWaitMs, candidateWaitMs));
            // 只替换成更接近的人。相同分差时按链表顺序保留先到者。
            if (distance >= 0 && distance < bestDistance)
            {
                opponent = candidate;
                bestDistance = distance;
                // 已找到完全同分的候选，继续扫描也不会有更好的结果。
                if (distance == 0)
                {
                    break;
                }
            }
        }
        if (opponent == null)
        {
            return false;
        }
        long opponentWaitMs = now - opponent.QueuedAt;
        // 先确定两位在线玩家与可用节点，再创建房间并占用该节点的一个席位。
        var room = Entity.Create<GoRoomEntity>(self.Scene, false, true);
        room.RoomId = Guid.NewGuid().ToString("N");
        room.WorkerAddress = workerAddress;
        int workerLoad = ++self.WorkerLoads[workerAddress];
        room.StartedAt = now;
        // 关房历史按较长的重连窗口保留，双方都有机会看到结果。
        room.RetentionWindowMs = Math.Max(player.ReconnectWindowMs, opponent.ReconnectWindowMs);
        // 掷币决定黑白，避免先入队者永远执黑。
        bool firstBlack = RandomNumberGenerator.GetInt32(2) == 0;
        room.Black = firstBlack ? player : opponent;
        room.White = firstBlack ? opponent : player;
        // 退队会清掉排队分数，先冻结黑方的预期得分供终局使用。
        room.BlackExpectedResult = GoMatchPolicy.BlackExpectedResult(room.Black.QueuedRating, room.White.QueuedRating);
        self.RemoveFromQueue(player);
        self.RemoveFromQueue(opponent);
        room.BlackName = room.Black.Nickname;
        room.WhiteName = room.White.Nickname;
        // 开房时写入两边的执子方和房间引用，此后命令按座位验证。
        room.Black.Side = 1;
        room.White.Side = 2;
        room.Black.Room = room;
        room.White.Room = room;
        // 双方状态都装配后再放入房间索引并通知，客户端读到的是完整房间。
        self.Rooms.Add(room.RoomId, room);
        self.Broadcast(room, responseRecipient);
        Log.Info($"[PVP/房间] 房间={room.RoomId} 事件=匹配成功/双方入房/对局开始 黑方={GoEventLog.PlayerLabel(room.Black)} 白方={GoEventLog.PlayerLabel(room.White)} 节点={workerAddress} 节点在局={workerLoad} 等待毫秒={playerWaitMs}/{opponentWaitMs}");
        return true;
    }

    /// <summary>选本大厅负载最少的 Battle。满载时继续排队，绝不把两个场景的棋局状态混在一起。</summary>
    private static bool TryFindWorker(this GoLobbyComponent self, out long address)
    {
        address = 0;
        int fewestRooms = MaxRoomsPerWorker;
        foreach (var worker in self.WorkerLoads)
        {
            // 从单节点上限开始比较，只选还有容量且比当前候选更空闲的节点。
            if (worker.Value < fewestRooms)
            {
                fewestRooms = worker.Value;
                address = worker.Key;
            }
        }
        return address != 0;
    }

    /// <summary>取消、超时、断线和开房共用的退队操作，重复调用也能安全清除排队状态。</summary>
    private static void RemoveFromQueue(this GoLobbyComponent self, GoPlayerEntity player)
    {
        var node = player.WaitingNode;
        if (node?.List is { } queue)
        {
            queue.Remove(node);
            if (queue.Count == 0)
            {
                // 最后一位离开后清掉空队列，避免规则组合持续堆积。
                self.Waiting.Remove((player.MatchKind, player.RulesJson));
            }
        }
        // 链表引用、入队时间和本次排队分数一起清空。
        player.WaitingNode = null;
        player.QueuedAt = 0;
        player.QueuedRating = 0;
    }

    /// <summary>向仍在线的玩家推送退队状态和超时提示，客户端可以重新发起匹配。</summary>
    private static void NotifyMatchExpired(this GoLobbyComponent self, GoPlayerEntity player)
    {
        player.Connection?.Send(new G2C_GoState
        {
            State = self.Snapshot(player, "暂时没有找到同规则玩家，可重新匹配或使用局域网邀请。")
        });
    }

    /// <summary>在 Gate 验证房间和请求边界，再等固定 Battle 节点裁定。只有成功回包才提交大厅历史。</summary>
    internal static async FTask<(uint Code, string Reason)> Relay(this GoLobbyComponent self, Session session, GoPlayerEntity player, C2G_GoCommandRequest request, GoMoveTiming? timing)
    {
        var room = player.Room;
        var command = request.Command;
        // 只接收当前房间的围棋指令。关房或转入决斗后，这条入口停止收棋。
        if (room == null || room.RoomId != request.RoomId || room.ClosedAt != 0 || room.Duel != null)
        {
            return (8, "该对局不存在或已经结束。");
        }
        // 对照认证席位，并在路由前限制动作号、客户端上报的毫秒值及各文本长度。
        if (command == null || command.Side != player.Side || command.Kind < 1 || command.Kind > 8 || command.ElapsedMs < 0 || command.ElapsedMs > 86400000 || (command.Payload?.Length ?? 0) > 1024 || (command.PreviousHash?.Length ?? 0) > 128 || (command.ResultHash?.Length ?? 0) > 128)
        {
            return (9, "操作格式或玩家身份不正确。");
        }
        // 落子和点选死子需要坐标，九路棋盘的横纵交叉点编号均为 0 到 8。
        if ((command.Kind == 1 || command.Kind == 4) && (command.X < 0 || command.X > 8 || command.Y < 0 || command.Y > 8))
        {
            return (10, "交叉点坐标超出九路棋盘。");
        }
        // 完全相同的重发得到同一快照；同一序号换内容必须拒绝，不能覆盖已经广播的历史。
        if (command.Sequence > 0 && command.Sequence <= room.Commands.Count)
        {
            var old = room.Commands[command.Sequence - 1];
            if (old.Side == command.Side && old.Kind == command.Kind && old.X == command.X && old.Y == command.Y && old.ElapsedMs == command.ElapsedMs && old.Payload == command.Payload && old.PreviousHash == command.PreviousHash && old.ResultHash == command.ResultHash)
            {
                return (0, "");
            }
            return (11, "重复序号的内容不一致，请重新同步。");
        }
        // 旧序号已在上方处理。新请求必须是下一手，且棋局仍有接收空间。
        if (room.Finished || command.Sequence != room.Commands.Count + 1 || room.Commands.Count >= MaxCommands)
        {
            return (12, "对局已结束、操作序列不连续或对局过长，请重新同步。");
        }
        // Gate 先拦下走错轮次的落子或停手，完整棋规仍由 Battle 判断。
        if ((command.Kind == 1 || command.Kind == 2) && command.Side != room.NextSide)
        {
            return (13, "还没有轮到你落子。");
        }
        // 有一方正在恢复连接时暂停接受棋谱变化，等双方可接收同步。
        if (room.Black?.Connection == null || room.White?.Connection == null)
        {
            return (14, "对手正在重连，暂时不能落子。");
        }
        // 有历史时，执行前指纹必须接得上大厅最近确认的一手。
        if (room.Commands.Count > 0 && command.PreviousHash != room.Commands[^1].ResultHash)
        {
            return (15, "局面校验值不一致，请重新同步。");
        }
        // 同房间上一手的 RPC 尚未结束，下一手先拒绝以保留序号顺序。
        if (room.StepPending)
        {
            return (16, "上一手还在校验，请稍后同步。");
        }

        // FTask 等待期间 Gate 仍可处理其它房间；本房间只允许一条在途操作，避免棋谱乱序。
        // 跨 await 前记下运行时身份和关闭所需的地址、房号，回包后可识别对象回池或被复用。
        long roomRuntimeId = room.RuntimeId;
        long workerAddress = room.WorkerAddress;
        string roomId = room.RoomId;
        room.StepPending = true;
        try
        {
            if (timing != null)
            {
                timing.Routed = Stopwatch.GetTimestamp();
            }
            // 只把规则校验所需的值送到 Battle，网络会话仍由 Gate 持有。
            var result = await GoBattleBatch.Submit(self, workerAddress, new GoBattleStepInput
            {
                RoomId = room.RoomId, Komi = player.Komi, Sequence = command.Sequence, Side = command.Side,
                Kind = command.Kind, X = command.X, Y = command.Y, ElapsedMs = command.ElapsedMs,
                Payload = command.Payload ?? "", PreviousHash = command.PreviousHash ?? "", ResultHash = command.ResultHash ?? ""
            });
            if (timing != null)
            {
                timing.Returned = Stopwatch.GetTimestamp();
            }
            // 等待期间可能已经离席或清房，此时不能把旧结果写入已关闭或复用的对象。
            if (room.RuntimeId != roomRuntimeId || room.ClosedAt != 0)
            {
                return (8, "该对局已经结束。");
            }
            // 空结果代表节点未给出有效响应，当前手尚无可提交的大厅结果。
            if (result is not { } outcome)
            {
                return (16, "对局节点暂时不可用，请重试。");
            }
            // 节点返回了具体棋规拒绝原因，原大厅历史保持不变。
            if (!string.IsNullOrEmpty(outcome.Reason))
            {
                return (17, outcome.Reason);
            }
            // 接受后保存独立副本，请求回包结束时框架会回收原命令对象。
            room.Commands.Add(command.DeepCopy());
            if (timing != null)
            {
                timing.LogStart = Stopwatch.GetTimestamp();
            }
            GoEventLog.Command(room, player, command);
            if (timing != null)
            {
                timing.LogEnd = Stopwatch.GetTimestamp();
            }
            if (command.Kind == 1 || command.Kind == 2)
            {
                // 黑白编号分别为 1 和 2，3 减去当前编号得到另一方。
                room.NextSide = 3 - room.NextSide;
            }
            // 终局标记、棋力结算与释放席位都以 Battle 裁定的结果为准。
            if (outcome.Finished)
            {
                room.Finished = true;
                GoEventLog.Result(room, outcome);
                // 只有数子或认输结局计算本局棋力分差，和棋按半分处理。
                if ((GoEndReason)outcome.EndReason is GoEndReason.Scored or GoEndReason.Resigned)
                {
                    double blackResult = (GoSide)outcome.Winner == GoSide.Empty ? 0.5 : (GoSide)outcome.Winner == GoSide.Black ? 1.0 : 0.0;
                    room.BlackRatingDelta = GoMatchPolicy.BlackRatingDelta(room.BlackExpectedResult, blackResult);
                    room.RatingFinalized = true;
                }
                // 结算后清掉预期分，释放规则节点的计算席位，大厅继续保留棋谱。
                room.BlackExpectedResult = 0;
                self.ReleaseWorker(room);
            }
            // 若提交者重连，旧 RPC 已发不到他，需把当前状态也推送给新连接。
            self.Broadcast(room, self.Player(session) == player ? player : null, command.Sequence - 1);
            if (timing != null)
            {
                timing.PushEnd = Stopwatch.GetTimestamp();
            }
            return (0, "");
        }
        catch (Exception error)
        {
            Log.Warning($"[PVP/节点] 房间={request.RoomId} 对局操作校验失败 类型={error.GetType().Name}");
            return (16, "对局节点暂时不可用，请重试。");
        }
        finally
        {
            // 离席可能早于合批发出。等在途请求完成再删盘，避免晚到首手在 Battle 复活旧房间。
            if (!self.Closing && (room.RuntimeId != roomRuntimeId || room.WorkerReleased))
            {
                self.Scene.Send(workerAddress, new G2B_CloseGoRoom { RoomId = roomId });
            }
            // 只解除原房间的在途标记，不能修改池里已被分配给其他对局的对象。
            if (room.RuntimeId == roomRuntimeId)
            {
                room.StepPending = false;
            }
        }
    }

    /// <summary>一个房间只释放一次计算席位。大厅可继续保留原有客户端快照供重连。</summary>
    private static void ReleaseWorker(this GoLobbyComponent self, GoRoomEntity room)
    {
        if (room.WorkerReleased)
        {
            return;
        }
        // 若仍有一手正在路由，删盘通知交给该手的 finally，避免通知先于操作到达。
        if (!room.StepPending)
        {
            self.Scene.Send(room.WorkerAddress, new G2B_CloseGoRoom { RoomId = room.RoomId });
        }
        // 先记已释放，再扣计数，后续关房路径不会重复扣减。
        room.WorkerReleased = true;
        int workerLoad = --self.WorkerLoads[room.WorkerAddress];
        Log.Info($"[PVP/节点] 房间={room.RoomId} 节点={room.WorkerAddress} 事件=释放计算席位 节点在局={workerLoad}");
    }

    /// <summary>按匿名身份计数，判断本条请求是否仍在一秒窗口的限流额度内。</summary>
    public static bool RateAllowed(GoPlayerEntity player)
    {
        long now = Now();
        // 上一个一秒窗口结束后，从当前请求重新计时和计数。
        if (now - player.LastRequestAt >= 1000)
        {
            player.LastRequestAt = now;
            player.RequestCount = 0;
        }
        // 一秒二十条足够支持落子/心跳/重试，避免完整历史广播被高频请求放大。
        // 决斗输入频率高于围棋，继续使用同一个会话限流计数器但提高上限。
        return ++player.RequestCount <= (player.Room?.Duel == null ? 20 : 60);
    }

    /// <summary>组装当前玩家的状态。默认附完整棋谱，仅在可安全衔接时使用增量。</summary>
    public static GoRelayState Snapshot(this GoLobbyComponent self, GoPlayerEntity player, string reason = "", int afterSequence = 0)
    {
        var room = player.Room;
        // 先填共有字段和大厅状态。未入房时没有棋谱可附。
        var state = new GoRelayState
        {
            RoomId = room?.RoomId ?? "",
            Phase = room == null ? (player.QueuedAt == 0 ? "idle" : "queued") : "playing",
            MySide = player.Side,
            BlackName = room?.BlackName ?? "",
            WhiteName = room?.WhiteName ?? "",
            CoreVersion = CoreVersion,
            RulesJson = player.RulesJson,
            MatchKind = player.MatchKind,
            StartUtcMs = room?.StartedAt ?? 0,
            ServerUtcMs = Now(),
            Reason = reason
        };
        if (room == null)
        {
            return state;
        }
        // 双方收到同一场对局的结果，分数增减按各自的座位方向返回。
        state.RatingFinalized = room.RatingFinalized;
        // 把权威决斗标识和两席兼容性附在围棋状态上，供入场和重连恢复。
        state.DuelId = room.Duel?.Id ?? "";
        state.DuelAvailable = room.HasDuelPeers();
        state.RatingDelta = player.Side == (int)GoSide.Black ? room.BlackRatingDelta : -room.BlackRatingDelta;
        // 只对显式协商的新客户端发送当前一手。握手、重连、主动同步与旧客户端仍用完整棋谱。
        if (afterSequence > 0 && afterSequence == room.Commands.Count - 1 &&
            player.Connection?.GetComponent<GoSessionComponent>()?.SupportsDelta == true)
        {
            // 基序号代表客户端已有的前缀长度，后面的循环从第一条缺失操作开始。
            state.BaseSequence = afterSequence;
        }
        for (int i = state.BaseSequence; i < room.Commands.Count; i++)
        {
            // 回包持有自己的棋谱副本，发送或回收时不会影响房间保存的历史。
            state.Commands.Add(room.Commands[i].DeepCopy());
        }
        // 选择最早的断线时刻。0 表示目前没有人断线。
        long disconnectedAt = room.Black?.DisconnectedAt ?? 0;
        long whiteAt = room.White?.DisconnectedAt ?? 0;
        if (disconnectedAt == 0 || (whiteAt != 0 && whiteAt < disconnectedAt))
        {
            disconnectedAt = whiteAt;
        }
        if (disconnectedAt != 0)
        {
            state.Phase = "disconnected";
            state.ReconnectDeadlineUtcMs = disconnectedAt + player.ReconnectWindowMs;
            state.Reason = "对手连接中断，等待重连；单机功能不受影响。";
        }
        if (room.ClosedAt != 0)
        {
            // 关房结果优先于重连等待状态，避免给客户端显示过时的等待提示。
            state.Phase = "closed";
            state.Reason = room.CloseReason;
        }
        return state;
    }

    /// <summary>给在线的黑白两席各发一份按座位定制的快照，RPC 回包接收方可排除以免重复推送。</summary>
    public static void Broadcast(this GoLobbyComponent self, GoRoomEntity room, GoPlayerEntity? responseRecipient = null, int afterSequence = 0)
    {
        Send(room.Black);
        Send(room.White);
        // 每个接收者的执子方、分差和增量能力不同，因此分别构造消息。
        void Send(GoPlayerEntity? player)
        {
            if (player?.Connection != null && player != responseRecipient && !player.Connection.IsDisposed)
            {
                player.Connection.Send(new G2C_GoState { State = self.Snapshot(player, afterSequence: afterSequence) });
            }
        }
    }

    /// <summary>移除排队节点和当前座位。首次关房通知对手，双方均离席后销毁房间。</summary>
    public static void Leave(this GoLobbyComponent self, GoPlayerEntity player)
    {
        self.RemoveFromQueue(player);
        var room = player.Room;
        if (room == null)
        {
            return;
        }
        Log.Info($"[PVP/房间] 房间={room.RoomId} 玩家={GoEventLog.PlayerLabel(player)} 事件=离开对局");
        if (room.ClosedAt == 0)
        {
            // 首个离席者确定关闭时间和原因，后续离席沿用同一结果。
            room.ClosedAt = Now();
            room.CloseReason = "一方已退出对局。";
            self.ReleaseWorker(room);
            self.Broadcast(room, player);
        }
        // 房间结束后不再持有已离席玩家。否则匿名玩家回池复用时，旧房间会读到另一局的身份。
        if (room.Black == player)
        {
            room.Black = null;
        }
        else if (room.White == player)
        {
            room.White = null;
        }
        // 玩家和房间的引用都断开，归还身份时不会留下旧房间关联。
        player.Room = null;
        player.Side = 0;
        self.ReleaseRoomIfEmpty(room);
    }

    /// <summary>移除当前连接和匹配请求，暂时保留身份与席位供令牌恢复。</summary>
    public static void Disconnected(this GoLobbyComponent self, GoPlayerEntity player)
    {
        if (self.Closing || player.IsDisposed)
        {
            return;
        }
        Log.Warning($"[PVP/连接] 玩家={GoEventLog.PlayerLabel(player)} 房间={player.Room?.RoomId ?? "无"} 事件=连接中断/等待重连");
        player.Room?.Duel?.DisconnectNetworkSeat(player.Side);
        player.Connection = null;
        // 从现在开始计算重连窗口，排队不跨断线保留。
        player.DisconnectedAt = Now();
        self.RemoveFromQueue(player);
        if (player.Room != null)
        {
            self.Broadcast(player.Room);
        }
    }

    /// <summary>按同一个时刻检查匹配与重连超时、重新配对，并释放已超过保留期的房间。</summary>
    public static void Sweep(this GoLobbyComponent self)
    {
        long now = Now();
        // 扫描过程中会移除条目，先取索引快照以便安全遍历。
        foreach (var player in self.Players.Values.ToArray())
        {
            // 先退出超时队列并推送可见状态，再处理离线身份的回收。
            if (player.QueuedAt != 0 && now - player.QueuedAt >= player.MatchWindowMs)
            {
                self.RemoveFromQueue(player);
                self.NotifyMatchExpired(player);
            }
            if (player.DisconnectedAt != 0 && now - player.DisconnectedAt >= player.ReconnectWindowMs)
            {
                self.Leave(player);
                // 先删恢复索引再回收 Entity，避免旧令牌找到后来被复用的对象。
                self.Players.Remove(player.Token);
                player.Dispose();
            }
        }
        // 无新玩家到达时也要按放宽后的窗口重新配对。移除队列节点前先记录下一位。
        foreach (var queue in self.Waiting.Values.ToArray())
        {
            var node = queue.First;
            while (node != null && queue.Count > 1)
            {
                var next = node.Next;
                bool matched = self.TryMatch(queue, node.Value, now, null);
                // 下一位可能刚被一起配走。节点脱链时回队首，避免漏扫仍在队列中的玩家。
                node = matched && next?.List != queue ? queue.First : next;
            }
        }
        // 结束后的房间最多保留一个重连窗口供双方读到结果，然后释放全部引用。
        foreach (var room in self.Rooms.Values.ToArray())
        {
            if (room.ClosedAt != 0 && now - room.ClosedAt >= room.RetentionWindowMs)
            {
                // 只解除仍指向当前房间的席位，避免覆盖玩家后来建立的新关联。
                if (room.Black?.Room == room)
                {
                    room.Black.Room = null;
                    room.Black.Side = 0;
                }
                if (room.White?.Room == room)
                {
                    room.White.Room = null;
                    room.White.Side = 0;
                }
                self.ReleaseRoomIfEmpty(room);
            }
        }
    }

    /// <summary>两席都不再引用房间时移除索引，并由房间 DestroySystem 回收棋谱与决斗资源。</summary>
    private static void ReleaseRoomIfEmpty(this GoLobbyComponent self, GoRoomEntity room)
    {
        if (room.Black?.Room == room || room.White?.Room == room)
        {
            return;
        }
        self.Rooms.Remove(room.RoomId);
        // 字典先移除再回收，后续按房号查找不会看到已释放对象。
        room.Dispose();
    }

    /// <summary>读取握手必需的规则项。成功时输出两个毫秒窗口和半目贴目，格式或取值不合规时返回 false。</summary>
    private static bool TryReadRules(string rulesJson, out long reconnectMs, out long matchMs, out int komi)
    {
        reconnectMs = 0;
        matchMs = 0;
        komi = 0;
        try
        {
            // 用 JSON 文档检查属性类型，方法结束后释放解析缓冲。
            using var json = JsonDocument.Parse(rulesJson);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("reconnectWindowSeconds", out var reconnect) ||
                !root.TryGetProperty("matchTimeoutSeconds", out var match) || !root.TryGetProperty("komiHalfPoints", out var komiValue) ||
                reconnect.ValueKind != JsonValueKind.Number || match.ValueKind != JsonValueKind.Number || komiValue.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            // 配置源仍是客户端导表；服务端仅给运维寿命设上限，避免客户端要求无限保留内存。
            if (!reconnect.TryGetInt32(out int reconnectSeconds) || !match.TryGetInt32(out int matchSeconds) ||
                !komiValue.TryGetInt32(out komi) || komi < 0 || komi > GoCore.PointCount * 2 ||
                reconnectSeconds < 1 || reconnectSeconds > 600 || matchSeconds < 1 || matchSeconds > 600)
            {
                return false;
            }
            // 只在所有字段验证通过后换算时间，使用 long 和 Unix 毫秒运算对齐。
            reconnectMs = reconnectSeconds * 1000L;
            matchMs = matchSeconds * 1000L;
            return true;
        }
        // 无效 JSON 属于可拒绝的输入，回到握手错误响应即可。
        catch (JsonException)
        {
            return false;
        }
    }
}

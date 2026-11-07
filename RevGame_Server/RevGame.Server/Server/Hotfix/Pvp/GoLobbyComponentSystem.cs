using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Fantasy.Async;
using Fantasy.Entitas;
using Fantasy.Network;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>
/// 大厅负责连接、匹配、Battle 分配及客户端同步。实际棋局只在分配好的 Battle 场景上执行。
/// </summary>
public static class GoLobbyComponentSystem
{
    public const string CoreVersion = "revgo-1";
    // 以下是服务运维边界，不是棋力/棋规数值。限制无身份连接造成的内存和广播放大。
    public const int MaxPlayers = 1024;
    public const int MaxCommands = 1024;
    private const int MaxRoomsPerWorker = 128;

    public static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static GoPlayerEntity? Player(this GoLobbyComponent self, Session session)
    {
        var player = session.GetComponent<GoSessionComponent>()?.Player;
        return player != null && !player.IsDisposed && player.Connection == session ? player : null;
    }

    public static uint Hello(this GoLobbyComponent self, Session session, C2G_GoHelloRequest request, out GoPlayerEntity? player, out string reason)
    {
        player = null;
        reason = "";
        if (request.CoreVersion != CoreVersion)
        {
            reason = "围棋规则版本不一致，请更新客户端。";
            return 1;
        }
        if (string.IsNullOrWhiteSpace(request.Nickname) || request.Nickname.Length > 24 || string.IsNullOrWhiteSpace(request.RulesJson) || request.RulesJson.Length > 4096 || (request.MatchKind != "rookie" && request.MatchKind != "advanced"))
        {
            reason = "昵称、规则配置或场次不正确。";
            return 2;
        }
        if (self.Closing)
        {
            reason = "服务器正在关闭，请稍后再试；单机和局域网对战仍可使用。";
            return 3;
        }
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
        if (!string.IsNullOrEmpty(request.ResumeToken))
        {
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
            player = Entity.Create<GoPlayerEntity>(self.Scene, false, true);
            player.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            player.Nickname = request.Nickname.Trim();
            player.LogNickname = new string(player.Nickname.Where(c => !char.IsControl(c)).ToArray());
            player.RulesJson = request.RulesJson;
            player.MatchKind = request.MatchKind;
            player.ReconnectWindowMs = reconnectWindow;
            player.MatchWindowMs = matchWindow;
            player.Komi = komi;
            self.Players.Add(player.Token, player);
        }
        string identity = string.IsNullOrEmpty(request.ResumeToken) ? "新建" : "恢复连接";
        Log.Info($"[PVP/连接] 玩家={GoEventLog.PlayerLabel(player)} 事件=握手或恢复成功 身份={identity} 房间={player.Room?.RoomId ?? "无"}");
        player.Connection = session;
        player.DisconnectedAt = 0;
        var binding = session.GetOrAddComponent<GoSessionComponent>();
        binding.Player = player;
        binding.SupportsDelta = request.SupportsDelta;
        binding.DuelFingerprint = request.DuelFingerprint;
        if (player.Room != null)
        {
            self.Broadcast(player.Room, player);
        }
        return 0;
    }

    public static uint Match(this GoLobbyComponent self, GoPlayerEntity player, bool cancel, int localRating, out string reason)
    {
        reason = "";
        if (cancel)
        {
            self.RemoveFromQueue(player);
            Log.Info($"[PVP/匹配] 玩家={GoEventLog.PlayerLabel(player)} 事件=取消匹配");
            return 0;
        }
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
        var key = (player.MatchKind, player.RulesJson);
        if (!self.Waiting.TryGetValue(key, out var queue))
        {
            queue = new LinkedList<GoPlayerEntity>();
            self.Waiting.Add(key, queue);
        }
        player.QueuedAt = now;
        player.QueuedRating = localRating == 0 ? GoMatchPolicy.InitialRating : Math.Clamp(localRating, 100, 3000);
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
        if (queue.Count < 2 || !self.TryFindWorker(out long workerAddress))
        {
            return false;
        }
        GoPlayerEntity? opponent = null;
        int bestDistance = int.MaxValue;
        long playerWaitMs = now - player.QueuedAt;
        foreach (var candidate in queue)
        {
            long candidateWaitMs = now - candidate.QueuedAt;
            if (candidate == player || candidateWaitMs >= candidate.MatchWindowMs || candidate.Connection == null || candidate.Connection.IsDisposed)
            {
                continue;
            }
            int distance = GoMatchPolicy.Distance(player.QueuedRating, candidate.QueuedRating, Math.Min(playerWaitMs, candidateWaitMs));
            if (distance >= 0 && distance < bestDistance)
            {
                opponent = candidate;
                bestDistance = distance;
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
        var room = Entity.Create<GoRoomEntity>(self.Scene, false, true);
        room.RoomId = Guid.NewGuid().ToString("N");
        room.WorkerAddress = workerAddress;
        int workerLoad = ++self.WorkerLoads[workerAddress];
        room.StartedAt = now;
        room.RetentionWindowMs = Math.Max(player.ReconnectWindowMs, opponent.ReconnectWindowMs);
        // 掷币决定黑白，避免先入队者永远执黑。
        bool firstBlack = RandomNumberGenerator.GetInt32(2) == 0;
        room.Black = firstBlack ? player : opponent;
        room.White = firstBlack ? opponent : player;
        room.BlackExpectedResult = GoMatchPolicy.BlackExpectedResult(room.Black.QueuedRating, room.White.QueuedRating);
        self.RemoveFromQueue(player);
        self.RemoveFromQueue(opponent);
        room.BlackName = room.Black.Nickname;
        room.WhiteName = room.White.Nickname;
        room.Black.Side = 1;
        room.White.Side = 2;
        room.Black.Room = room;
        room.White.Room = room;
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
            if (worker.Value < fewestRooms)
            {
                fewestRooms = worker.Value;
                address = worker.Key;
            }
        }
        return address != 0;
    }

    private static void RemoveFromQueue(this GoLobbyComponent self, GoPlayerEntity player)
    {
        var node = player.WaitingNode;
        if (node?.List is { } queue)
        {
            queue.Remove(node);
            if (queue.Count == 0)
            {
                self.Waiting.Remove((player.MatchKind, player.RulesJson));
            }
        }
        player.WaitingNode = null;
        player.QueuedAt = 0;
        player.QueuedRating = 0;
    }

    private static void NotifyMatchExpired(this GoLobbyComponent self, GoPlayerEntity player)
    {
        player.Connection?.Send(new G2C_GoState
        {
            State = self.Snapshot(player, "暂时没有找到同规则玩家，可重新匹配或使用局域网邀请。")
        });
    }

    internal static async FTask<(uint Code, string Reason)> Relay(this GoLobbyComponent self, Session session, GoPlayerEntity player, C2G_GoCommandRequest request, GoMoveTiming? timing)
    {
        var room = player.Room;
        var command = request.Command;
        if (room == null || room.RoomId != request.RoomId || room.ClosedAt != 0 || room.Duel != null)
        {
            return (8, "该对局不存在或已经结束。");
        }
        if (command == null || command.Side != player.Side || command.Kind < 1 || command.Kind > 8 || command.ElapsedMs < 0 || command.ElapsedMs > 86400000 || (command.Payload?.Length ?? 0) > 1024 || (command.PreviousHash?.Length ?? 0) > 128 || (command.ResultHash?.Length ?? 0) > 128)
        {
            return (9, "操作格式或玩家身份不正确。");
        }
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
        if (room.Finished || command.Sequence != room.Commands.Count + 1 || room.Commands.Count >= MaxCommands)
        {
            return (12, "对局已结束、操作序列不连续或对局过长，请重新同步。");
        }
        if ((command.Kind == 1 || command.Kind == 2) && command.Side != room.NextSide)
        {
            return (13, "还没有轮到你落子。");
        }
        if (room.Black?.Connection == null || room.White?.Connection == null)
        {
            return (14, "对手正在重连，暂时不能落子。");
        }
        if (room.Commands.Count > 0 && command.PreviousHash != room.Commands[^1].ResultHash)
        {
            return (15, "局面校验值不一致，请重新同步。");
        }
        if (room.StepPending)
        {
            return (16, "上一手还在校验，请稍后同步。");
        }

        // FTask 等待期间 Gate 仍可处理其它房间；本房间只允许一条在途操作，避免棋谱乱序。
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
            if (room.RuntimeId != roomRuntimeId || room.ClosedAt != 0)
            {
                return (8, "该对局已经结束。");
            }
            if (result is not { } outcome)
            {
                return (16, "对局节点暂时不可用，请重试。");
            }
            if (!string.IsNullOrEmpty(outcome.Reason))
            {
                return (17, outcome.Reason);
            }
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
                room.NextSide = 3 - room.NextSide;
            }
            if (outcome.Finished)
            {
                room.Finished = true;
                GoEventLog.Result(room, outcome);
                if ((GoEndReason)outcome.EndReason is GoEndReason.Scored or GoEndReason.Resigned)
                {
                    double blackResult = (GoSide)outcome.Winner == GoSide.Empty ? 0.5 : (GoSide)outcome.Winner == GoSide.Black ? 1.0 : 0.0;
                    room.BlackRatingDelta = GoMatchPolicy.BlackRatingDelta(room.BlackExpectedResult, blackResult);
                    room.RatingFinalized = true;
                }
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
        if (!room.StepPending)
        {
            self.Scene.Send(room.WorkerAddress, new G2B_CloseGoRoom { RoomId = room.RoomId });
        }
        room.WorkerReleased = true;
        int workerLoad = --self.WorkerLoads[room.WorkerAddress];
        Log.Info($"[PVP/节点] 房间={room.RoomId} 节点={room.WorkerAddress} 事件=释放计算席位 节点在局={workerLoad}");
    }

    public static bool RateAllowed(GoPlayerEntity player)
    {
        long now = Now();
        if (now - player.LastRequestAt >= 1000)
        {
            player.LastRequestAt = now;
            player.RequestCount = 0;
        }
        // 一秒二十条足够支持落子/心跳/重试，避免完整历史广播被高频请求放大。
        return ++player.RequestCount <= (player.Room?.Duel == null ? 20 : 60);
    }

    public static GoRelayState Snapshot(this GoLobbyComponent self, GoPlayerEntity player, string reason = "", int afterSequence = 0)
    {
        var room = player.Room;
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
        state.RatingFinalized = room.RatingFinalized;
        state.DuelId = room.Duel?.Id ?? "";
        state.DuelAvailable = room.HasDuelPeers();
        state.RatingDelta = player.Side == (int)GoSide.Black ? room.BlackRatingDelta : -room.BlackRatingDelta;
        // 只对显式协商的新客户端发送当前一手。握手、重连、主动同步与旧客户端仍用完整棋谱。
        if (afterSequence > 0 && afterSequence == room.Commands.Count - 1 &&
            player.Connection?.GetComponent<GoSessionComponent>()?.SupportsDelta == true)
        {
            state.BaseSequence = afterSequence;
        }
        for (int i = state.BaseSequence; i < room.Commands.Count; i++)
        {
            state.Commands.Add(room.Commands[i].DeepCopy());
        }
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
            state.Phase = "closed";
            state.Reason = room.CloseReason;
        }
        return state;
    }

    public static void Broadcast(this GoLobbyComponent self, GoRoomEntity room, GoPlayerEntity? responseRecipient = null, int afterSequence = 0)
    {
        Send(room.Black);
        Send(room.White);
        void Send(GoPlayerEntity? player)
        {
            if (player?.Connection != null && player != responseRecipient && !player.Connection.IsDisposed)
            {
                player.Connection.Send(new G2C_GoState { State = self.Snapshot(player, afterSequence: afterSequence) });
            }
        }
    }

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
        player.Room = null;
        player.Side = 0;
        self.ReleaseRoomIfEmpty(room);
    }

    public static void Disconnected(this GoLobbyComponent self, GoPlayerEntity player)
    {
        if (self.Closing || player.IsDisposed)
        {
            return;
        }
        Log.Warning($"[PVP/连接] 玩家={GoEventLog.PlayerLabel(player)} 房间={player.Room?.RoomId ?? "无"} 事件=连接中断/等待重连");
        player.Connection = null;
        player.DisconnectedAt = Now();
        self.RemoveFromQueue(player);
        if (player.Room != null)
        {
            self.Broadcast(player.Room);
        }
    }

    public static void Sweep(this GoLobbyComponent self)
    {
        long now = Now();
        foreach (var player in self.Players.Values.ToArray())
        {
            if (player.QueuedAt != 0 && now - player.QueuedAt >= player.MatchWindowMs)
            {
                self.RemoveFromQueue(player);
                self.NotifyMatchExpired(player);
            }
            if (player.DisconnectedAt != 0 && now - player.DisconnectedAt >= player.ReconnectWindowMs)
            {
                self.Leave(player);
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
                node = matched && next?.List != queue ? queue.First : next;
            }
        }
        // 结束后的房间最多保留一个重连窗口供双方读到结果，然后释放全部引用。
        foreach (var room in self.Rooms.Values.ToArray())
        {
            if (room.ClosedAt != 0 && now - room.ClosedAt >= room.RetentionWindowMs)
            {
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

    private static void ReleaseRoomIfEmpty(this GoLobbyComponent self, GoRoomEntity room)
    {
        if (room.Black?.Room == room || room.White?.Room == room)
        {
            return;
        }
        self.Rooms.Remove(room.RoomId);
        room.Dispose();
    }

    private static bool TryReadRules(string rulesJson, out long reconnectMs, out long matchMs, out int komi)
    {
        reconnectMs = 0;
        matchMs = 0;
        komi = 0;
        try
        {
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
            reconnectMs = reconnectSeconds * 1000L;
            matchMs = matchSeconds * 1000L;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

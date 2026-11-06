using System.Security.Cryptography;
using System.Text.Json;
using Fantasy.Async;
using Fantasy.Entitas;
using Fantasy.Network;

namespace Fantasy;

/// <summary>
/// 大厅负责连接身份、匹配和可靠中转，不决定提子、死活与胜负。
/// 客户端收到序列后必须用同源 GoCore 重放；哈希分歧应停止落子并要求同步。
/// </summary>
public static class GoLobbyComponentSystem
{
    public const string CoreVersion = "revgo-1";
    // 以下是服务运维边界，不是棋力/棋规数值。限制无身份连接造成的内存和广播放大。
    public const int MaxPlayers = 1024;
    public const int MaxCommands = 1024;

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
        if (!TryReadSessionWindows(request.RulesJson, out long reconnectWindow, out long matchWindow))
        {
            reason = "规则配置缺少有效的重连/匹配时间，或不是合法JSON。";
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
            if (!self.Players.TryGetValue(request.ResumeToken, out player) || player.IsDisposed)
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
            player.RulesJson = request.RulesJson;
            player.MatchKind = request.MatchKind;
            player.ReconnectWindowMs = reconnectWindow;
            player.MatchWindowMs = matchWindow;
            self.Players.Add(player.Token, player);
        }
        Log.Info($"[PVP/连接] 玩家={GoEventLog.PlayerLabel(player)} 事件=握手或恢复成功 房间={player.Room?.RoomId ?? "无"}");
        player.Connection = session;
        player.DisconnectedAt = 0;
        session.GetOrAddComponent<GoSessionComponent>().Player = player;
        if (player.Room != null)
        {
            self.Broadcast(player.Room);
        }
        return 0;
    }

    public static uint Match(this GoLobbyComponent self, GoPlayerEntity player, bool cancel, out string reason)
    {
        reason = "";
        self.Waiting.Remove(player);
        player.QueuedAt = 0;
        if (cancel)
        {
            Log.Info($"[PVP/匹配] 玩家={GoEventLog.PlayerLabel(player)} 事件=取消匹配");
            return 0;
        }
        if (player.Room != null)
        {
            reason = "请先退出当前对局。";
            return 7;
        }
        // 同一份规则配置、同一入门/进阶场次才可配对，不按不可信的本地段位算排名。
        var opponent = self.Waiting.FirstOrDefault(x => x != player && x.Connection != null && !x.Connection.IsDisposed && x.RulesJson == player.RulesJson && x.MatchKind == player.MatchKind);
        if (opponent == null)
        {
            player.QueuedAt = Now();
            self.Waiting.Add(player);
            Log.Info($"[PVP/匹配] 玩家={GoEventLog.PlayerLabel(player)} 场次={player.MatchKind} 事件=进入队列 等待人数={self.Waiting.Count}");
            return 0;
        }
        self.Waiting.Remove(opponent);
        opponent.QueuedAt = 0;
        var room = Entity.Create<GoRoomEntity>(self.Scene, false, true);
        room.RoomId = Guid.NewGuid().ToString("N");
        room.StartedAt = Now();
        // 掷币决定黑白，避免先入队者永远执黑。
        bool firstBlack = RandomNumberGenerator.GetInt32(2) == 0;
        room.Black = firstBlack ? player : opponent;
        room.White = firstBlack ? opponent : player;
        room.Black.Side = 1;
        room.White.Side = 2;
        room.Black.Room = room;
        room.White.Room = room;
        self.Rooms.Add(room.RoomId, room);
        self.Broadcast(room);
        Log.Info($"[PVP/房间] 房间={room.RoomId} 事件=匹配成功/双方入房/对局开始 黑方={GoEventLog.PlayerLabel(room.Black)} 白方={GoEventLog.PlayerLabel(room.White)} 当前房间数={self.Rooms.Count}");
        return 0;
    }

    public static uint Relay(this GoLobbyComponent self, GoPlayerEntity player, C2G_GoCommandRequest request, out string reason)
    {
        reason = "";
        var room = player.Room;
        var command = request.Command;
        if (room == null || room.RoomId != request.RoomId || room.ClosedAt != 0)
        {
            reason = "该对局不存在或已经结束。";
            return 8;
        }
        if (command == null || command.Side != player.Side || command.Kind < 1 || command.Kind > 8 || command.ElapsedMs < 0 || command.ElapsedMs > 86400000 || (command.Payload?.Length ?? 0) > 1024 || (command.PreviousHash?.Length ?? 0) > 128 || (command.ResultHash?.Length ?? 0) > 128)
        {
            reason = "操作格式或玩家身份不正确。";
            return 9;
        }
        if ((command.Kind == 1 || command.Kind == 4) && (command.X < 0 || command.X > 8 || command.Y < 0 || command.Y > 8))
        {
            reason = "交叉点坐标超出九路棋盘。";
            return 10;
        }
        // 完全相同的重发得到同一快照；同一序号换内容必须拒绝，不能覆盖已经广播的历史。
        if (command.Sequence > 0 && command.Sequence <= room.Commands.Count)
        {
            var old = room.Commands[command.Sequence - 1];
            if (old.Side == command.Side && old.Kind == command.Kind && old.X == command.X && old.Y == command.Y && old.ElapsedMs == command.ElapsedMs && old.Payload == command.Payload && old.PreviousHash == command.PreviousHash && old.ResultHash == command.ResultHash)
            {
                return 0;
            }
            reason = "重复序号的内容不一致，请重新同步。";
            return 11;
        }
        if (command.Sequence != room.Commands.Count + 1 || room.Commands.Count >= MaxCommands)
        {
            reason = "操作序列不连续或对局过长，请重新同步。";
            return 12;
        }
        if ((command.Kind == 1 || command.Kind == 2) && command.Side != room.NextSide)
        {
            reason = "还没有轮到你落子。";
            return 13;
        }
        if (room.Black?.Connection == null || room.White?.Connection == null)
        {
            reason = "对手正在重连，暂时不能落子。";
            return 14;
        }
        if (room.Commands.Count > 0 && command.PreviousHash != room.Commands[^1].ResultHash)
        {
            reason = "局面校验值不一致，请重新同步。";
            return 15;
        }
        room.Commands.Add(command.DeepCopy());
        GoEventLog.Command(room, player, command);
        if (command.Kind == 1 || command.Kind == 2)
        {
            room.NextSide = 3 - room.NextSide;
        }
        self.Broadcast(room);
        return 0;
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
        return ++player.RequestCount <= 20;
    }

    public static GoRelayState Snapshot(this GoLobbyComponent self, GoPlayerEntity player, string reason = "")
    {
        var room = player.Room;
        var state = new GoRelayState
        {
            RoomId = room?.RoomId ?? "",
            Phase = room == null ? (player.QueuedAt == 0 ? "idle" : "queued") : "playing",
            MySide = player.Side,
            BlackName = room?.Black?.Nickname ?? "",
            WhiteName = room?.White?.Nickname ?? "",
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
        foreach (var command in room.Commands)
        {
            // 普通 DTO 深复制给发送层，避免网络消息池回收房间里的唯一历史。
            state.Commands.Add(command.DeepCopy());
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

    public static void Broadcast(this GoLobbyComponent self, GoRoomEntity room)
    {
        Send(room.Black);
        Send(room.White);
        void Send(GoPlayerEntity? player)
        {
            if (player?.Connection != null && !player.Connection.IsDisposed)
            {
                player.Connection.Send(new G2C_GoState { State = self.Snapshot(player) });
            }
        }
    }

    public static void Leave(this GoLobbyComponent self, GoPlayerEntity player)
    {
        self.Waiting.Remove(player);
        player.QueuedAt = 0;
        var room = player.Room;
        if (room == null)
        {
            return;
        }
        Log.Info($"[PVP/房间] 房间={room.RoomId} 玩家={GoEventLog.PlayerLabel(player)} 事件=离开对局");
        room.ClosedAt = Now();
        room.CloseReason = "一方已退出对局。";
        self.Broadcast(room);
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
        self.Waiting.Remove(player);
        player.QueuedAt = 0;
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
                self.Waiting.Remove(player);
                player.QueuedAt = 0;
                player.Connection?.Send(new G2C_GoState { State = self.Snapshot(player, "暂时没有找到同规则玩家，可重新匹配或使用局域网邀请。") });
            }
            if (player.DisconnectedAt != 0 && now - player.DisconnectedAt >= player.ReconnectWindowMs)
            {
                self.Leave(player);
                self.Players.Remove(player.Token);
                player.Dispose();
            }
        }
        // 结束后的房间最多保留一个重连窗口供双方读到结果，然后释放全部引用。
        foreach (var room in self.Rooms.Values.ToArray())
        {
            long retention = Math.Max(room.Black?.ReconnectWindowMs ?? 0, room.White?.ReconnectWindowMs ?? 0);
            if (room.ClosedAt != 0 && now - room.ClosedAt >= retention)
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

    private static bool TryReadSessionWindows(string rulesJson, out long reconnectMs, out long matchMs)
    {
        reconnectMs = 0;
        matchMs = 0;
        try
        {
            using var json = JsonDocument.Parse(rulesJson);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("reconnectWindowSeconds", out var reconnect) || !root.TryGetProperty("matchTimeoutSeconds", out var match))
            {
                return false;
            }
            // 配置源仍是客户端导表；服务端仅给运维寿命设上限，避免客户端要求无限保留内存。
            if (!reconnect.TryGetInt32(out int reconnectSeconds) || !match.TryGetInt32(out int matchSeconds) || reconnectSeconds < 1 || reconnectSeconds > 600 || matchSeconds < 1 || matchSeconds > 600)
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

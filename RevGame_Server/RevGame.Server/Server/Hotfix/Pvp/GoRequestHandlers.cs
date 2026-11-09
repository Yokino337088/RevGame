using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;

namespace Fantasy;

/// <summary>公开握手入口；只创建匿名休闲身份，不伪装为账号登录或排名认证。</summary>
public sealed class C2G_GoHelloRequestHandler : MessageRPC<C2G_GoHelloRequest, G2C_GoHelloResponse>
{
    /// <summary>握手成功后将令牌和完整状态发给当前连接，失败只返回错误码与原因。</summary>
    protected override async FTask Run(Session session, C2G_GoHelloRequest request, G2C_GoHelloResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        // 将大厅的业务返回码交给客户端，0 表示握手成功。
        response.ErrorCode = lobby.Hello(session, request, out var player, out var reason);
        response.Reason = reason;
        if (response.ErrorCode == 0 && player != null)
        {
            // 令牌只随成功回包交给当前玩家，供断线恢复身份。
            response.Token = player.Token;
            response.State = lobby.Snapshot(player);
        }
        await FTask.CompletedTask;
    }
}

/// <summary>处理开始匹配和取消匹配。具体状态由大厅维护。</summary>
public sealed class C2G_GoMatchRequestHandler : MessageRPC<C2G_GoMatchRequest, G2C_GoMatchResponse>
{
    /// <summary>通过 Session 找到玩家并限流，随后由大厅更新队列并返回当前状态。</summary>
    protected override async FTask Run(Session session, C2G_GoMatchRequest request, G2C_GoMatchResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        // 后续请求从当前 Session 取身份，通过限流后才访问玩家状态。
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        // 取消和入队走同一个大厅方法，响应以处理后的状态为准。
        response.ErrorCode = lobby.Match(player, request.Cancel, request.LocalRating, out var reason);
        response.Reason = reason;
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

/// <summary>接收围棋操作并转交大厅校验。具体状态由大厅维护。</summary>
public sealed class C2G_GoCommandRequestHandler : MessageRPC<C2G_GoCommandRequest, G2C_GoCommandResponse>
{
    /// <summary>等待 Battle 校验完成，再把对应序号的快照和结果交给本次 RPC。</summary>
    protected override async FTask Run(Session session, C2G_GoCommandRequest request, G2C_GoCommandResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        // 后续请求从当前 Session 取身份，通过限流后才访问玩家状态。
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        // 压测开关关闭时返回空。采样对象仅记录这一次请求的各阶段耗时。
        var timing = GoMoveTiming.Start();
        // 等待时 Gate 仍能处理其它事件。拿到的是大厅校验和 Battle 裁决的结果。
        var (code, reason) = await lobby.Relay(session, player, request, timing);
        response.ErrorCode = code;
        if (response.ErrorCode != 0)
        {
            Log.Warning($"[PVP/拒绝] 房间={request.RoomId} 玩家={GoEventLog.PlayerLabel(player)} 序号={request.Command?.Sequence} 错误码={response.ErrorCode} 原因={reason}");
        }
        response.Reason = reason;
        int sequence = request.Command?.Sequence ?? 0;
        // 等待期间连接可能已被替换，只在原 Session 仍属于该玩家时附带快照。
        if (lobby.Player(session) == player)
        {
            // 成功可以按客户端能力发送增量，拒绝时附完整状态供校对。
            response.State = lobby.Snapshot(player, afterSequence: code == 0 ? sequence - 1 : 0);
        }
        if (timing?.PushEnd > 0)
        {
            // 先让 RPC 回包入队再记录时间，避免最后一条样本写盘影响这段测量。
            reply();
            timing.Record(request.RoomId, sequence);
        }
    }
}

/// <summary>提供完整棋谱同步入口。具体状态由大厅维护。</summary>
public sealed class C2G_GoSyncRequestHandler : MessageRPC<C2G_GoSyncRequest, G2C_GoSyncResponse>
{
    /// <summary>重连或增量对不上时，用当前 Session 读取完整的大厅快照。</summary>
    protected override async FTask Run(Session session, C2G_GoSyncRequest request, G2C_GoSyncResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        // 后续请求从当前 Session 取身份，通过限流后才访问玩家状态。
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

/// <summary>处理主动离席或退队。具体状态由大厅维护。</summary>
public sealed class C2G_GoLeaveRequestHandler : MessageRPC<C2G_GoLeaveRequest, G2C_GoLeaveResponse>
{
    /// <summary>大厅清除该玩家的队列或座位关联，响应反映离开后的状态。</summary>
    protected override async FTask Run(Session session, C2G_GoLeaveRequest request, G2C_GoLeaveResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        // 后续请求从当前 Session 取身份，通过限流后才访问玩家状态。
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        // 离开对局与关闭连接是两件事，身份仍可用于再次匹配。
        lobby.Leave(player);
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

/// <summary>确认匿名会话仍有效并回传服务端时间。具体状态由大厅维护。</summary>
public sealed class C2G_GoHeartbeatRequestHandler : MessageRPC<C2G_GoHeartbeatRequest, G2C_GoHeartbeatResponse>
{
    /// <summary>检查会话和请求频率，成功时仅回传 UTC Unix 毫秒。</summary>
    protected override async FTask Run(Session session, C2G_GoHeartbeatRequest request, G2C_GoHeartbeatResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        // 后续请求从当前 Session 取身份，通过限流后才访问玩家状态。
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "会话已经过期，请重新连接。";
            return;
        }
        // 心跳返回时钟用于校准，避免每次保活都发送棋谱。
        response.ServerUtcMs = GoLobbyComponentSystem.Now();
        await FTask.CompletedTask;
    }
}

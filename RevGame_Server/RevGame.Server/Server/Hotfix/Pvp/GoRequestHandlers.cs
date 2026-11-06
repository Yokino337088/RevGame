using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;

namespace Fantasy;

/// <summary>公开握手入口；只创建匿名休闲身份，不伪装为账号登录或排名认证。</summary>
public sealed class C2G_GoHelloRequestHandler : MessageRPC<C2G_GoHelloRequest, G2C_GoHelloResponse>
{
    protected override async FTask Run(Session session, C2G_GoHelloRequest request, G2C_GoHelloResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        response.ErrorCode = lobby.Hello(session, request, out var player, out var reason);
        response.Reason = reason;
        if (response.ErrorCode == 0 && player != null)
        {
            response.Token = player.Token;
            response.State = lobby.Snapshot(player);
        }
        await FTask.CompletedTask;
    }
}

public sealed class C2G_GoMatchRequestHandler : MessageRPC<C2G_GoMatchRequest, G2C_GoMatchResponse>
{
    protected override async FTask Run(Session session, C2G_GoMatchRequest request, G2C_GoMatchResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        response.ErrorCode = lobby.Match(player, request.Cancel, out var reason);
        response.Reason = reason;
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

public sealed class C2G_GoCommandRequestHandler : MessageRPC<C2G_GoCommandRequest, G2C_GoCommandResponse>
{
    protected override async FTask Run(Session session, C2G_GoCommandRequest request, G2C_GoCommandResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        response.ErrorCode = lobby.Relay(player, request, out var reason);
        if (response.ErrorCode != 0)
        {
            Log.Warning($"[PVP/拒绝] 房间={request.RoomId} 玩家={GoEventLog.PlayerLabel(player)} 序号={request.Command?.Sequence} 错误码={response.ErrorCode} 原因={reason}");
        }
        response.Reason = reason;
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

public sealed class C2G_GoSyncRequestHandler : MessageRPC<C2G_GoSyncRequest, G2C_GoSyncResponse>
{
    protected override async FTask Run(Session session, C2G_GoSyncRequest request, G2C_GoSyncResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
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

public sealed class C2G_GoLeaveRequestHandler : MessageRPC<C2G_GoLeaveRequest, G2C_GoLeaveResponse>
{
    protected override async FTask Run(Session session, C2G_GoLeaveRequest request, G2C_GoLeaveResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "请先连接服务器，或稍后再试。";
            return;
        }
        lobby.Leave(player);
        response.State = lobby.Snapshot(player);
        await FTask.CompletedTask;
    }
}

public sealed class C2G_GoHeartbeatRequestHandler : MessageRPC<C2G_GoHeartbeatRequest, G2C_GoHeartbeatResponse>
{
    protected override async FTask Run(Session session, C2G_GoHeartbeatRequest request, G2C_GoHeartbeatResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        if (player == null || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 20;
            response.Reason = "会话已经过期，请重新连接。";
            return;
        }
        response.ServerUtcMs = GoLobbyComponentSystem.Now();
        await FTask.CompletedTask;
    }
}

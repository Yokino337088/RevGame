using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;
using RevGame.Pvp.Duel;

namespace Fantasy;

/// <summary>Gate 校验会话和座位，按房间的固定 Address 转发。决斗状态和时间均归 Battle。</summary>
public sealed class C2G_GoDuelRequestHandler : MessageRPC<C2G_GoDuelRequest, G2C_GoDuelResponse>
{
    /// <summary>向固定 Battle 发出当前席位的请求，回包时重新核对房间和连接身份。</summary>
    protected override async FTask Run(Session session, C2G_GoDuelRequest request, G2C_GoDuelResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        var room = player?.Room;
        if (player == null || room == null || room.RoomId != request.RoomId || room.ClosedAt != 0 ||
            request.Fingerprint != DuelAuthority.NetworkFingerprint || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 30;
            response.Reason = "决斗房间、会话或规则版本无效。";
            return;
        }
        int side = player.Side;
        if ((side != 1 || room.Black != player) && (side != 2 || room.White != player))
        {
            response.ErrorCode = 30;
            response.Reason = "决斗座位无效。";
            return;
        }
        bool opening = request.Operation is DuelAuthority.Challenge or DuelAuthority.TestChallenge;
        bool creating = opening && room.DuelId.Length == 0;
        if (room.WorkerReleased || (creating && (room.Finished || room.StepPending || room.DuelOpeningInFlight || !room.HasDuelPeers())) ||
            (!creating && room.DuelId.Length == 0))
        {
            response.ErrorCode = 31;
            response.Reason = "当前房间尚未进入决斗或无法发起挑战。";
            return;
        }
        int seat = side - 1;
        if (room.DuelRequestsInFlight[seat] >= 8)
        {
            response.ErrorCode = 30;
            response.Reason = "决斗请求过多。";
            return;
        }
        long roomId = room.RuntimeId;
        long epoch = room.DuelSeatEpochs[seat];
        long worker = room.WorkerAddress;
        string roomKey = room.RoomId;
        room.DuelRequestsInFlight[seat]++;
        if (creating)
        {
            // 在发出挑战前冻结围棋。节点没回包时可能已创建，保留冻结并允许挑战重试。
            room.DuelOpening = true;
            room.DuelOpeningInFlight = true;
        }
        try
        {
            var route = G2B_GoDuelRequest.Create();
            route.RoomId = roomKey;
            route.Side = side;
            route.SeatEpoch = epoch;
            route.BoardSequence = room.Commands.Count;
            route.Operation = request.Operation;
            route.AfterFrame = request.AfterFrame;
            route.InputFrame = request.InputFrame;
            route.Input = request.Input;
            route.HashFrame = request.HashFrame;
            route.Hash = request.Hash;
            using var answer = await lobby.Scene.Call(worker, route);
            if (room.RuntimeId != roomId || room.ClosedAt != 0)
            {
                response.ErrorCode = 30;
                response.Reason = "决斗房间已关闭。";
                return;
            }
            if (answer is not B2G_GoDuelResponse result || result.ErrorCode != 0 || result.State == null)
            {
                response.ErrorCode = 33;
                response.Reason = "决斗节点暂时不可用。";
                return;
            }
            var state = result.State;
            if (creating)
            {
                room.DuelOpening = false;
                if (state.DuelId.Length != 0)
                {
                    room.DuelId = state.DuelId;
                    lobby.Broadcast(room);
                }
            }
            if (room.DuelSeatEpochs[seat] != epoch || lobby.Player(session) != player)
            {
                response.ErrorCode = 30;
                response.Reason = "决斗连接已被替换。";
                return;
            }
            if (state.Result != 0)
            {
                room.Finished = true;
            }
            CopyReply(state, response);
        }
        catch (Exception error)
        {
            Log.Warning($"[PVP/决斗] 节点请求失败 类型={error.GetType().Name}");
            response.ErrorCode = 33;
            response.Reason = "决斗节点暂时不可用。";
        }
        finally
        {
            if (room.RuntimeId == roomId)
            {
                room.DuelRequestsInFlight[seat]--;
                if (creating)
                {
                    room.DuelOpeningInFlight = false;
                }
            }
            // 关房可能发生在创建回包之前。用原始路由补发幂等清理，避免晚到的创建留下原生实例。
            if (creating && !lobby.Closing && (room.RuntimeId != roomId || room.ClosedAt != 0))
            {
                lobby.Scene.Send(worker, new G2B_CloseGoRoom { RoomId = roomKey });
            }
        }
    }

    // 外网响应已由框架分配。复制到它的池化列表，归还内网回包后不会引用旧消息。
    private static void CopyReply(G2C_GoDuelResponse state, G2C_GoDuelResponse response)
    {
        response.ErrorCode = state.ErrorCode;
        response.Reason = state.Reason;
        response.DuelId = state.DuelId;
        response.ReadyMask = state.ReadyMask;
        response.AfterFrame = state.AfterFrame;
        response.Frames.AddRange(state.Frames);
        response.Result = state.Result;
        response.FinalHash = state.FinalHash;
        response.HashAckFrame = state.HashAckFrame;
        response.HashMatchedFrame = state.HashMatchedFrame;
        response.HashMismatchFrame = state.HashMismatchFrame;
        response.InputAckFrame = state.InputAckFrame;
        response.InputAckSequence = state.InputAckSequence;
        response.PendingActionSequence = state.PendingActionSequence;
        response.PendingActionUntil = state.PendingActionUntil;
        response.PrepRevision = state.PrepRevision;
        response.PrepPhase = state.PrepPhase;
        response.BlackRole = state.BlackRole;
        response.WhiteRole = state.WhiteRole;
        response.PrepLocked = state.PrepLocked;
        response.BlackLoad = state.BlackLoad;
        response.WhiteLoad = state.WhiteLoad;
        response.PrepRemainingMs = state.PrepRemainingMs;
    }
}

/// <summary>Gate 只读取认证与连接状态，连接变更以有序内网消息通知房间所有者。</summary>
internal static class GoDuelRoomRules
{
    /// <summary>检查双方当前连接是否都支持这版决斗协议。</summary>
    public static bool HasDuelPeers(this GoRoomEntity room)
    {
        return room.Black?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelAuthority.NetworkFingerprint &&
            room.White?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelAuthority.NetworkFingerprint;
    }

    /// <summary>推进已占座玩家的连接代际，并通知战斗节点清理上一条连接的待封输入。</summary>
    public static void InvalidateDuelSeat(this GoPlayerEntity player)
    {
        var room = player.Room;
        if (room == null || room.ClosedAt != 0 || (!room.DuelOpening && room.DuelId.Length == 0))
        {
            return;
        }
        var message = G2B_GoDuelSeatChanged.Create();
        message.RoomId = room.RoomId;
        message.Side = player.Side;
        message.SeatEpoch = ++room.DuelSeatEpochs[player.Side - 1];
        room.Scene.Send(room.WorkerAddress, message);
    }
}

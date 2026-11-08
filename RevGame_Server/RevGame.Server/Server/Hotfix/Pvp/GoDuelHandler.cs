using System.Diagnostics;
using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;
using RevGame.Pvp;
using RevGame.Pvp.Duel;

namespace Fantasy;

/// <summary>Gate 校验座位、棋盘资格与版本，再把输入交给每局权威封帧器。Battle 线程的棋盘保持冻结。</summary>
public sealed class C2G_GoDuelRequestHandler : MessageRPC<C2G_GoDuelRequest, G2C_GoDuelResponse>
{
    // 在 Gate 线程验证 Session、房间与配置，再按已认证座位处理决斗请求。错误通过响应返回。
    protected override async FTask Run(Session session, C2G_GoDuelRequest request, G2C_GoDuelResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        var player = lobby.Player(session);
        var room = player?.Room;
        if (player == null || room == null || room.RoomId != request.RoomId || room.ClosedAt != 0 ||
            request.Fingerprint != DuelConfig.Fingerprint || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 30;
            response.Reason = "决斗房间、会话或规则版本无效。";
            return;
        }
        try
        {
            bool test = request.Operation == DuelAuthority.TestChallenge;
#if !DEBUG
            if (test)
            {
                response.ErrorCode = 31;
                response.Reason = "此服务器未开启决斗测试。";
                return;
            }
#endif
            bool opening = request.Operation == DuelAuthority.Challenge || test;
            if (opening && room.Duel == null)
            {
                // 开发测试只跳过劣势门槛，仍绑定认证座位和当前已确认的棋局。
                bool seated = (player.Side == 1 && ReferenceEquals(room.Black, player)) ||
                    (player.Side == 2 && ReferenceEquals(room.White, player));
                if (!seated || room.Finished || room.StepPending || !room.HasDuelPeers() ||
                    !CanChallenge(room, player, test))
                {
                    response.ErrorCode = 31;
                    response.Reason = "当前局面或连接不满足决斗条件。";
                    return;
                }
                room.Duel = new DuelAuthority(room.RoomId);
                lobby.Broadcast(room);
            }
            if (room.Duel == null)
            {
                response.ErrorCode = 31;
                response.Reason = "决斗尚未发起。";
                return;
            }
            // 对局结束后，保留期间仍接收读取历史和终局样本，供两席完成对账。
            var state = room.Duel.Exchange(player.Side, new DuelRequest
            {
                operation = opening ? DuelAuthority.Read : request.Operation,
                fingerprint = request.Fingerprint, afterFrame = request.AfterFrame, inputFrame = request.InputFrame,
                input = request.Input, hashFrame = request.HashFrame, hash = request.Hash
            }, Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000));
            if (!string.IsNullOrEmpty(state.error))
            {
                response.ErrorCode = 32;
                response.Reason = state.error;
                return;
            }
            response.DuelId = state.id;
            response.ReadyMask = state.readyMask;
            response.AfterFrame = state.afterFrame;
            response.Frames.AddRange(state.frames);
            response.Result = state.result;
            response.FinalHash = state.finalHash;
            response.HashAckFrame = state.hashAckFrame;
            response.HashMatchedFrame = state.hashMatchedFrame;
            response.HashMismatchFrame = state.hashMismatchFrame;
            if (state.result != 0)
            {
                room.Finished = true;
            }
        }
        catch (Exception error)
        {
            Log.Error($"[PVP/决斗] 房间={room.RoomId} {error.GetType().Name}: {error.Message}");
            response.ErrorCode = 33;
            response.Reason = "决斗内核不可用。";
        }
        await FTask.CompletedTask;
    }

    // 用已通过 Battle 校验的棋谱重放围棋终态，再判发起座位的分差，不跨场景读活棋盘。
    private static bool CanChallenge(GoRoomEntity room, GoPlayerEntity player, bool test)
    {
        // 发起频率低，复用已通过 Battle 验证的棋谱计算资格，不跨线程读取工作节点棋盘。
        if (!test && room.Commands.Count < DuelConfig.MinimumMove)
        {
            return false;
        }
        var board = new GoCore(player.Komi);
        foreach (var command in room.Commands)
        {
            if (!board.TryApply((GoAction)command.Kind, (GoSide)command.Side, command.X, command.Y, out _))
            {
                return false;
            }
        }
        return test ? board.Phase == GoPhase.Playing :
            DuelEligibility.CanChallenge(board, (GoSide)player.Side, new GoBoardAnalysis());
    }
}

// 服务器房间的决斗兼容性查询，复用两席现有 Session 握手结果。
internal static class GoDuelRoomRules
{
    // 两席连接都声明同一决斗配置指纹才允许开启入口。
    public static bool HasDuelPeers(this GoRoomEntity room)
    {
        return room.Black?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelConfig.Fingerprint &&
            room.White?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelConfig.Fingerprint;
    }
}

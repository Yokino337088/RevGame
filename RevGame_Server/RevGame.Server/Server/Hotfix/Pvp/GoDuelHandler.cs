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
    /// <summary>在 Gate 线程按当前连接和座位校验决斗请求，创建或交换本房间的权威帧状态。错误通过响应返回。</summary>
    protected override async FTask Run(Session session, C2G_GoDuelRequest request, G2C_GoDuelResponse response, Action reply)
    {
        var lobby = session.Scene.GetComponent<GoLobbyComponent>();
        // 身份来自服务端绑定的 Session。请求中的房间编号仅用来核对当前房间。
        var player = lobby.Player(session);
        var room = player?.Room;
        // 房间、规则指纹与限流都通过后，才能进入权威决斗处理。
        if (player == null || room == null || room.RoomId != request.RoomId || room.ClosedAt != 0 ||
            request.Fingerprint != DuelConfig.Fingerprint || !GoLobbyComponentSystem.RateAllowed(player))
        {
            response.ErrorCode = 30;
            response.Reason = "决斗房间、会话或规则版本无效。";
            return;
        }
        try
        {
            // 测试操作在发布构建中直接拒绝。
            bool test = request.Operation == DuelAuthority.TestChallenge;
#if !DEBUG
            if (test)
            {
                response.ErrorCode = 31;
                response.Reason = "此服务器未开启决斗测试。";
                return;
            }
#endif
            // 只有挑战操作可首次创建决斗。已创建时沿用同一份权威状态。
            bool opening = request.Operation == DuelAuthority.Challenge || test;
            if (opening && room.Duel == null)
            {
                // 开发测试只跳过劣势门槛，仍绑定认证座位和当前已确认的棋局。
                bool seated = (player.Side == 1 && ReferenceEquals(room.Black, player)) ||
                    (player.Side == 2 && ReferenceEquals(room.White, player));
                // 等围棋这一手确认完再切入决斗，双方还需声明相同的决斗版本。
                if (!seated || room.Finished || room.StepPending || !room.HasDuelPeers() ||
                    !CanChallenge(room, player, test))
                {
                    response.ErrorCode = 31;
                    response.Reason = "当前局面或连接不满足决斗条件。";
                    return;
                }
                // 权威实例跟随房间存活。围棋入口看到此标记后停止接收新的围棋操作。
                room.Duel = new DuelAuthority(room.RoomId);
                // 把决斗状态通知两席，让客户端进入同一房间的决斗界面。
                lobby.Broadcast(room);
            }
            if (room.Duel == null)
            {
                response.ErrorCode = 31;
                response.Reason = "决斗尚未发起。";
                return;
            }
            // 对局结束后，保留期间仍接收读取历史和终局样本，供两席完成对账。
            // 使用已认证的座位提交输入和校验样本。时间来自单调 Stopwatch 并换算为毫秒。
            var state = room.Duel.Exchange(player.Side, new DuelRequest
            {
                // 挑战已在上方处理，这一次交换只读取新建或已有的状态。
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
            // 回包带决斗编号、就绪掩码和从指定帧开始的记录，供双方对齐进度。
            response.DuelId = state.id;
            response.ReadyMask = state.readyMask;
            response.AfterFrame = state.afterFrame;
            response.Frames.AddRange(state.frames);
            response.Result = state.result;
            response.FinalHash = state.finalHash;
            // 确认已收到的样本帧，以及双方指纹首次不一致和已匹配的帧进度。
            response.HashAckFrame = state.hashAckFrame;
            response.HashMatchedFrame = state.hashMatchedFrame;
            response.HashMismatchFrame = state.hashMismatchFrame;
            if (state.result != 0)
            {
                // 权威结果非零代表已有终局，房间继续保留结果供客户端读取。
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

    /// <summary>用已确认棋谱重放当前围棋局面，再判断发起方是否符合决斗资格。</summary>
    private static bool CanChallenge(GoRoomEntity room, GoPlayerEntity player, bool test)
    {
        // 发起频率低，复用已通过 Battle 验证的棋谱计算资格，不跨线程读取工作节点棋盘。
        if (!test && room.Commands.Count < DuelConfig.MinimumMove)
        {
            return false;
        }
        // 重放用独立棋盘，操作失败即拒绝挑战，避免用不完整的局面判断。
        var board = new GoCore(player.Komi);
        foreach (var command in room.Commands)
        {
            if (!board.TryApply((GoAction)command.Kind, (GoSide)command.Side, command.X, command.Y, out _))
            {
                return false;
            }
        }
        // 测试跳过普通资格算法，但仍要求围棋正在进行。
        return test ? board.Phase == GoPhase.Playing :
            DuelEligibility.CanChallenge(board, (GoSide)player.Side, new GoBoardAnalysis());
    }
}

/// <summary>从双方现有的会话握手信息判断决斗功能是否兼容。</summary>
internal static class GoDuelRoomRules
{
    /// <summary>两席都连接且都声明当前规则指纹时返回真。</summary>
    public static bool HasDuelPeers(this GoRoomEntity room)
    {
        return room.Black?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelConfig.Fingerprint &&
            room.White?.Connection?.GetComponent<GoSessionComponent>()?.DuelFingerprint == DuelConfig.Fingerprint;
    }
}

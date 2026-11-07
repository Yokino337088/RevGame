using System.Globalization;
using Fantasy.Async;
using Fantasy.Entitas.Interface;
using Fantasy.Network.Interface;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>对局线程独立验证落子、停手及数子，不接受客户端自报的棋盘或胜负。</summary>
public sealed class G2B_GoBatchRequestHandler : AddressRPC<Scene, G2B_GoBatchRequest, B2G_GoBatchResponse>
{
    protected override async FTask Run(Scene scene, G2B_GoBatchRequest request, B2G_GoBatchResponse response, Action reply)
    {
        await FTask.CompletedTask;
        var battle = scene.GetComponent<GoBattleComponent>();
        if (battle == null || battle.IsDisposed || request.Steps.Count > GoBattleBatch.MaxSteps)
        {
            response.ErrorCode = 1;
            return;
        }
        foreach (var input in request.Steps)
        {
            var result = GoBattleStepResult.Create();
            response.Results.Add(result);
            ApplyStep(battle, input, result);
        }
    }

    private static void ApplyStep(GoBattleComponent battle, GoBattleStepInput request, GoBattleStepResult response)
    {
        var step = new GoBattleStep(request.Sequence, request.Side, request.Kind, request.X, request.Y,
            request.ElapsedMs, request.Payload ?? "", request.PreviousHash ?? "", request.ResultHash ?? "");
        battle.Rooms.TryGetValue(request.RoomId, out var room);
        if (room != null && room.LastStep == step)
        {
            // Gate 可能错过了上次响应，返回已提交结果，避免重复改棋盘。
            WriteResult(room.Board, response);
            return;
        }
        if (request.Sequence != (room?.Board.Sequence ?? 0) + 1)
        {
            response.Reason = "对局节点上的棋谱序号不一致，请重新同步。";
            return;
        }
        // 先在副本验证结果哈希；无效的操作不能污染已确认棋局。
        var next = room?.Board.Clone() ?? new GoCore(request.Komi);
        if (next.StateHash.ToString(CultureInfo.InvariantCulture) != step.PreviousHash ||
            !next.TryApply((GoAction)step.Kind, (GoSide)step.Side, step.X, step.Y, out _) ||
            next.StateHash.ToString(CultureInfo.InvariantCulture) != step.ResultHash)
        {
            response.Reason = "落子不符合棋规或局面指纹不一致，请重新同步。";
            return;
        }
        battle.Rooms[request.RoomId] = new GoBattleRoom(next, step);
        WriteResult(next, response);
    }

    private static void WriteResult(GoCore board, GoBattleStepResult response)
    {
        response.Reason = "";
        response.Finished = board.Phase == GoPhase.Finished;
        response.Winner = (int)board.Winner;
        response.EndReason = (int)board.EndReason;
        response.DifferenceHalfPoints = board.Score.DifferenceHalfPoints;
    }
}

/// <summary>大厅终局、离席或过期时通知固定的 Battle 场景，释放该房间的规则状态。</summary>
public sealed class G2B_CloseGoRoomHandler : Address<Scene, G2B_CloseGoRoom>
{
    protected override async FTask Run(Scene scene, G2B_CloseGoRoom message)
    {
        scene.GetComponent<GoBattleComponent>()?.Rooms.Remove(message.RoomId);
        await FTask.CompletedTask;
    }
}

public sealed class GoBattleComponentDestroySystem : DestroySystem<GoBattleComponent>
{
    protected override void Destroy(GoBattleComponent self)
    {
        self.Rooms.Clear();
    }
}

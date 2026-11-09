using System.Globalization;
using Fantasy.Async;
using Fantasy.Entitas.Interface;
using Fantasy.Network.Interface;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>对局线程独立验证落子、停手及数子，不接受客户端自报的棋盘或胜负。</summary>
public sealed class G2B_GoBatchRequestHandler : AddressRPC<Scene, G2B_GoBatchRequest, B2G_GoBatchResponse>
{
    /// <summary>在目标 Battle 的 Scene 线程逐手验证，按输入顺序写结果供 Gate 对号入座。</summary>
    protected override async FTask Run(Scene scene, G2B_GoBatchRequest request, B2G_GoBatchResponse response, Action reply)
    {
        await FTask.CompletedTask;
        var battle = scene.GetComponent<GoBattleComponent>();
        // 场景组件不可用或批次超过约定容量时整批失败。
        if (battle == null || battle.IsDisposed || request.Steps.Count > GoBattleBatch.MaxSteps)
        {
            response.ErrorCode = 1;
            return;
        }
        foreach (var input in request.Steps)
        {
            // 每个输入预留一个结果，包括被棋规拒绝的操作。
            var result = GoBattleStepResult.Create();
            response.Results.Add(result);
            ApplyStep(battle, input, result);
        }
    }

    /// <summary>校验并提交单手操作。相同重发复用结果，序号或局面不一致时保留原棋盘。</summary>
    private static void ApplyStep(GoBattleComponent battle, GoBattleStepInput request, GoBattleStepResult response)
    {
        // 复制请求中的操作值，默认空文本统一为空字符串，便于重发比较。
        var step = new GoBattleStep(request.Sequence, request.Side, request.Kind, request.X, request.Y,
            request.ElapsedMs, request.Payload ?? "", request.PreviousHash ?? "", request.ResultHash ?? "");
        battle.Rooms.TryGetValue(request.RoomId, out var room);
        if (room != null && room.LastStep == step)
        {
            // Gate 可能错过了上次响应，返回已提交结果，避免重复改棋盘。
            WriteResult(room.Board, response);
            return;
        }
        // 新房第一手从 1 开始。已有棋盘只接受紧接已确认序号的操作。
        if (request.Sequence != (room?.Board.Sequence ?? 0) + 1)
        {
            response.Reason = "对局节点上的棋谱序号不一致，请重新同步。";
            return;
        }
        // 先在副本验证结果哈希；无效的操作不能污染已确认棋局。
        var next = room?.Board.Clone() ?? GoCore.CreateMatch(request.Komi);
        // 依次检查旧指纹、棋规和执行后指纹，任一失败都不会替换权威棋盘。
        if (next.StateHash.ToString(CultureInfo.InvariantCulture) != step.PreviousHash ||
            !next.TryApply((GoAction)step.Kind, (GoSide)step.Side, step.X, step.Y, out _) ||
            next.StateHash.ToString(CultureInfo.InvariantCulture) != step.ResultHash)
        {
            response.Reason = "落子不符合棋规或局面指纹不一致，请重新同步。";
            return;
        }
        // 所有校验成功后才替换保存的棋盘，并记录这一手以支持幂等重试。
        battle.Rooms[request.RoomId] = new GoBattleRoom(next, step);
        WriteResult(next, response);
    }

    /// <summary>从权威棋盘填写结果，空拒绝原因表示校验成功，分差使用半目。</summary>
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
    /// <summary>在房间所属的 Battle 线程删除棋盘，重复关房时字典找不到条目即可结束。</summary>
    protected override async FTask Run(Scene scene, G2B_CloseGoRoom message)
    {
        scene.GetComponent<GoBattleComponent>()?.Rooms.Remove(message.RoomId);
        await FTask.CompletedTask;
    }
}

/// <summary>对局 Scene 回收时清除组件持有的全部棋盘引用。</summary>
public sealed class GoBattleComponentDestroySystem : DestroySystem<GoBattleComponent>
{
    /// <summary>组件销毁回调，避免复用组件时带入上一场景的房间。</summary>
    protected override void Destroy(GoBattleComponent self)
    {
        self.Rooms.Clear();
    }
}

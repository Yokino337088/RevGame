using Fantasy.Async;

namespace Fantasy;

/// <summary>Gate 每个 Battle 合并同一更新周期内的请求，按下标唤醒各局的等待者。</summary>
internal static class GoBattleBatch
{
    // 小批次达到上限立即发出，让 Battle 可在 Gate 继续接收后续房间时并行验棋。
    public const int MaxSteps = 32;

    /// <summary>把单手输入加入目标 Battle 的批次，返回只属于这一手的等待信号。</summary>
    public static FTask<GoBattleOutcome?> Submit(GoLobbyComponent lobby, long address, GoBattleStepInput input)
    {
        // 同一个 Battle 地址共享当前待发送批次，不同 Battle 分开投递。
        if (!lobby.PendingBattleSteps.TryGetValue(address, out var pending))
        {
            pending = new List<GoPendingBattleStep>();
            lobby.PendingBattleSteps.Add(address, pending);
            // 用 Fantasy 的 1 毫秒单次定时器给同轮进入 Gate 的少量请求合并机会。
            FTask.OnceTimer(lobby.Scene, 1, () => Flush(lobby, address, pending).Coroutine());
        }
        // 每手各有一个完成对象，返回时用批次下标把结果交给原等待者。
        var completion = FTask<GoBattleOutcome?>.Create();
        pending.Add(new GoPendingBattleStep(input, completion));
        // 凑满就提前发送，限制单批大小并减少等待。
        if (pending.Count == MaxSteps)
        {
            Flush(lobby, address, pending).Coroutine();
        }
        return completion;
    }

    /// <summary>摘出当前批次并调用目标 Scene。成功按顺序分发结果，失败则完成整批等待者并返回空结果。</summary>
    private static async FTask Flush(GoLobbyComponent lobby, long address, List<GoPendingBattleStep> pending)
    {
        // 满批立即发出时旧定时器仍会触发，不能摘掉后来创建的新批次。
        if (!lobby.PendingBattleSteps.TryGetValue(address, out var current) || current != pending)
        {
            return;
        }
        // 先移出队列再等待 RPC，新到达的操作可以另建批次。
        lobby.PendingBattleSteps.Remove(address);
        GoMoveTiming.CountBatch(pending.Count);
        // 按入队顺序装入框架池化请求，响应约定保持这个顺序。
        var request = G2B_GoBatchRequest.Create();
        foreach (var item in pending)
        {
            request.Steps.Add(item.Input);
        }
        try
        {
            // Call 将请求发给指定 Battle 的 Scene，using 管理回包生命周期。
            using var response = await lobby.Scene.Call(address, request);
            // 只有回包类型、错误码和条数都一致，才能安全按下标匹配。
            if (response is B2G_GoBatchResponse result && result.ErrorCode == 0 && result.Results.Count == pending.Count)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var item = result.Results[i];
                    // 将数据取成值对象，Gate 的等待者无需持有即将回池的响应。
                    pending[i].Completion.SetResult(new GoBattleOutcome(item.Reason, item.Finished, item.Winner, item.EndReason, item.DifferenceHalfPoints));
                }
                return;
            }
        }
        catch (Exception error)
        {
            Log.Warning($"[PVP/节点] 节点={address} 批量校验失败 类型={error.GetType().Name}");
        }
        foreach (var item in pending)
        {
            // 超时或异常时也必须完成每个等待信号，避免房间永远卡在校验中。
            item.Completion.SetResult(null);
        }
    }
}

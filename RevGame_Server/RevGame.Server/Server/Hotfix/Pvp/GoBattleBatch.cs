using Fantasy.Async;

namespace Fantasy;

/// <summary>Gate 每个 Battle 合并同一更新周期内的请求，按下标唤醒各局的等待者。</summary>
internal static class GoBattleBatch
{
    // 小批次达到上限立即发出，让 Battle 可在 Gate 继续接收后续房间时并行验棋。
    public const int MaxSteps = 32;

    public static FTask<GoBattleOutcome?> Submit(GoLobbyComponent lobby, long address, GoBattleStepInput input)
    {
        if (!lobby.PendingBattleSteps.TryGetValue(address, out var pending))
        {
            pending = new List<GoPendingBattleStep>();
            lobby.PendingBattleSteps.Add(address, pending);
            // 用 Fantasy 的 1 毫秒单次定时器给同轮进入 Gate 的少量请求合并机会。
            FTask.OnceTimer(lobby.Scene, 1, () => Flush(lobby, address, pending).Coroutine());
        }
        var completion = FTask<GoBattleOutcome?>.Create();
        pending.Add(new GoPendingBattleStep(input, completion));
        if (pending.Count == MaxSteps)
        {
            Flush(lobby, address, pending).Coroutine();
        }
        return completion;
    }

    private static async FTask Flush(GoLobbyComponent lobby, long address, List<GoPendingBattleStep> pending)
    {
        // 满批立即发出时旧定时器仍会触发，不能摘掉后来创建的新批次。
        if (!lobby.PendingBattleSteps.TryGetValue(address, out var current) || current != pending)
        {
            return;
        }
        lobby.PendingBattleSteps.Remove(address);
        GoMoveTiming.CountBatch(pending.Count);
        var request = G2B_GoBatchRequest.Create();
        foreach (var item in pending)
        {
            request.Steps.Add(item.Input);
        }
        try
        {
            using var response = await lobby.Scene.Call(address, request);
            if (response is B2G_GoBatchResponse result && result.ErrorCode == 0 && result.Results.Count == pending.Count)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var item = result.Results[i];
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
            item.Completion.SetResult(null);
        }
    }
}

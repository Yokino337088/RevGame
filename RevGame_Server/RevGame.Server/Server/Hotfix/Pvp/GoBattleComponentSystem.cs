using System.Diagnostics;
using Fantasy.Async;
using Fantasy.Entitas;
using Fantasy.Entitas.Interface;
using Fantasy.Network.Interface;
using RevGame.Pvp;
using RevGame.Pvp.Duel;

namespace Fantasy;

/// <summary>Battle 从自己的权威棋盘检查资格，并交还一次决斗交换结果。</summary>
public sealed class G2B_GoDuelRequestHandler : AddressRPC<Scene, G2B_GoDuelRequest, B2G_GoDuelResponse>
{
    /// <summary>在房间所属线程操作原生实例，将完整帧结果交给 Gate 回复。</summary>
    protected override async FTask Run(Scene scene, G2B_GoDuelRequest request, B2G_GoDuelResponse response, Action reply)
    {
        await FTask.CompletedTask;
        var battle = scene.GetComponent<GoBattleComponent>();
        // 生成的内网回包只清除 State 引用。嵌套对象跟随回包回收，避免从消息池借出却没有归还入口。
        var state = new G2C_GoDuelResponse();
        response.State = state;
        if (battle == null || battle.IsDisposed || request.Side is < 1 or > 2)
        {
            state.ErrorCode = 30;
            state.Reason = "决斗节点或席位无效。";
            return;
        }
        battle.ExchangeDuel(request, state);
    }
}

/// <summary>接收 Gate 转发的连接变更，旧连接的输入不能跨越就绪屏障。</summary>
public sealed class G2B_GoDuelSeatChangedHandler : Address<Scene, G2B_GoDuelSeatChanged>
{
    /// <summary>仅接纳更新的座位代际，同一代的重复通知不再清理已就绪的输入。</summary>
    protected override async FTask Run(Scene scene, G2B_GoDuelSeatChanged message)
    {
        await FTask.CompletedTask;
        var battle = scene.GetComponent<GoBattleComponent>();
        if (battle != null && message.Side is 1 or 2 && battle.Duels.TryGetValue(message.RoomId, out var duel) &&
            message.SeatEpoch > duel.SeatEpochs[message.Side - 1])
        {
            duel.AcceptEpoch(message.Side, message.SeatEpoch);
            battle.RemoveActiveDuel(duel);
        }
    }
}

/// <summary>同 World 的 Gate 在分配房间前检查 Battle 的业务组件和内网通信。</summary>
public sealed class G2B_GoBattleReadyRequestHandler : AddressRPC<Scene, G2B_GoBattleReadyRequest, B2G_GoBattleReadyResponse>
{
    /// <summary>只有权威组件已装配且存活时，Gate 才能分配新房间。</summary>
    protected override async FTask Run(Scene scene, G2B_GoBattleReadyRequest request, B2G_GoBattleReadyResponse response, Action reply)
    {
        await FTask.CompletedTask;
        if (scene.GetComponent<GoBattleComponent>() is not { IsDisposed: false })
        {
            response.ErrorCode = 1;
        }
    }
}

/// <summary>房间删除和 Scene 销毁都通过同一个场景归还原生决斗实例。</summary>
public sealed class GoBattleDuelDestroySystem : DestroySystem<GoBattleDuel>
{
    /// <summary>从所属 Battle 释放原生实例，历史随本局一起销毁。</summary>
    protected override void Destroy(GoBattleDuel self)
    {
        self.Authority?.Dispose();
        self.Authority = null!;
    }
}

/// <summary>在 Battle 线程执行挑战、输入与补帧。单 Scene 只在存在可封帧房间时持有一个计时器。</summary>
internal static class GoBattleComponentSystem
{
    /// <summary>创建或读取房间的决斗，只有当前连接的输入可改变权威状态。</summary>
    public static void ExchangeDuel(this GoBattleComponent battle, G2B_GoDuelRequest request, G2C_GoDuelResponse response)
    {
        bool test = request.Operation == DuelAuthority.TestChallenge;
        bool opening = request.Operation == DuelAuthority.Challenge || test;
        battle.Duels.TryGetValue(request.RoomId, out var duel);
        long now = NowMilliseconds();
        try
        {
            if (duel == null)
            {
                if (!opening || !battle.CanChallenge(request, test))
                {
                    response.ErrorCode = 31;
                    response.Reason = "当前局面不满足决斗条件。";
                    return;
                }
                // 先初始化原生实例，成功后才登记 Entity；挑战重发查到的是同一权威。
                var authority = new DuelAuthority(request.RoomId, now: now);
                duel = Entity.Create<GoBattleDuel>(battle.Scene, false, false);
                duel.Authority = authority;
                battle.Duels.Add(request.RoomId, duel);
            }
            response.DuelId = duel.Authority.Id;
            if (duel.Failed || !duel.AcceptEpoch(request.Side, request.SeatEpoch))
            {
                response.ErrorCode = 30;
                response.Reason = "决斗已停止或连接已被替换。";
                return;
            }
            var result = duel.Authority.Exchange(request.Side, new DuelRequest
            {
                operation = opening ? DuelAuthority.Read : request.Operation, fingerprint = DuelAuthority.NetworkFingerprint,
                afterFrame = request.AfterFrame, inputFrame = request.InputFrame, input = request.Input,
                hashFrame = request.HashFrame, hash = request.Hash
            }, now);
            if (!string.IsNullOrEmpty(result.error))
            {
                response.ErrorCode = 32;
                response.Reason = result.error;
                return;
            }
            if (duel.Authority.Stopped || result.readyMask != 3)
            {
                battle.RemoveActiveDuel(duel);
            }
            else if (request.Operation == DuelAuthority.Ready && !battle.ActiveDuels.Contains(duel))
            {
                battle.ActiveDuels.Add(duel);
                if (battle.DuelTimer == 0)
                {
                    battle.DuelTimer = FTask.RepeatedTimer(battle.Scene, 10, battle.AdvanceDuels);
                }
            }
            WriteState(response, result);
        }
        catch (Exception error)
        {
            if (duel != null)
            {
                duel.Failed = true;
                battle.RemoveActiveDuel(duel);
            }
            Log.Error($"[PVP/决斗] 房间={request.RoomId} 权威处理失败：{error.Message}");
            response.ErrorCode = 33;
            response.Reason = "决斗内核不可用。";
        }
    }

    /// <summary>发起挑战必须匹配 Gate 已确认的围棋序号，并通过当前权威棋盘的资格判断。</summary>
    private static bool CanChallenge(this GoBattleComponent battle, G2B_GoDuelRequest request, bool test)
    {
#if DEBUG
        if (test && request.BoardSequence == 0 && !battle.Rooms.ContainsKey(request.RoomId))
        {
            return true;
        }
#else
        if (test)
        {
            return false;
        }
#endif
        return battle.Rooms.TryGetValue(request.RoomId, out var room) && room.Board.Sequence == request.BoardSequence &&
            (test ? room.Board.Phase == GoPhase.Playing : DuelEligibility.CanChallenge(room.Board, (GoSide)request.Side, new GoBoardAnalysis()));
    }

    /// <summary>发现更换连接时清空权威邮箱。小于该席当前代际的请求由调用方拒绝。</summary>
    public static bool AcceptEpoch(this GoBattleDuel duel, int side, long epoch)
    {
        ref long current = ref duel.SeatEpochs[side - 1];
        if (epoch < current)
        {
            return false;
        }
        if (epoch > current)
        {
            current = epoch;
            duel.Authority.DisconnectNetworkSeat(side);
        }
        return true;
    }

    /// <summary>每轮共用一次单调时钟。50 毫秒逻辑步长与补帧上限由 DuelAuthority 维护。</summary>
    private static void AdvanceDuels(this GoBattleComponent battle)
    {
        long now = NowMilliseconds();
        for (int index = battle.ActiveDuels.Count - 1; index >= 0; index--)
        {
            var duel = battle.ActiveDuels[index];
            try
            {
                duel.Authority.AdvanceNetwork(now);
            }
            catch (Exception error)
            {
                duel.Failed = true;
                Log.Error($"[PVP/决斗] 房间={duel.Authority.Id} 权威推进失败：{error.Message}");
            }
            if (duel.Failed || duel.Authority.Stopped)
            {
                battle.ActiveDuels.RemoveAt(index);
            }
        }
        battle.StopIdleTimer();
    }

    /// <summary>结束本局的定时推进，房间仍保留连续帧供两席查询。</summary>
    public static void RemoveActiveDuel(this GoBattleComponent battle, GoBattleDuel duel)
    {
        battle.ActiveDuels.Remove(duel);
        battle.StopIdleTimer();
    }

    /// <summary>没有双席就绪的活跃房间时停止 Battle 的推进计时器。</summary>
    private static void StopIdleTimer(this GoBattleComponent battle)
    {
        if (battle.ActiveDuels.Count == 0 && battle.DuelTimer != 0)
        {
            FTask.RemoveTimer(battle.Scene, ref battle.DuelTimer);
        }
    }

    /// <summary>将单调时钟换成毫秒，供封帧计算使用，独立于机器日历时间。</summary>
    private static long NowMilliseconds()
    {
        return (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;
    }

    /// <summary>复制当前游标后的帧、输入回执和对账进度，选角信息仅随准备状态返回。</summary>
    private static void WriteState(G2C_GoDuelResponse response, DuelReply state)
    {
        response.ReadyMask = state.readyMask;
        response.AfterFrame = state.afterFrame;
        response.Frames.AddRange(state.frames);
        response.Result = state.result;
        response.FinalHash = state.finalHash;
        response.HashAckFrame = state.hashAckFrame;
        response.HashMatchedFrame = state.hashMatchedFrame;
        response.HashMismatchFrame = state.hashMismatchFrame;
        response.InputAckFrame = state.inputAckFrame;
        response.InputAckSequence = state.inputAckSequence;
        response.PendingActionSequence = state.pendingActionSequence;
        response.PendingActionUntil = state.pendingActionUntil;
        if (state.preparation is { } prep)
        {
            response.PrepRevision = prep.revision;
            response.PrepPhase = (int)prep.phase;
            response.BlackRole = (int)prep.black;
            response.WhiteRole = (int)prep.white;
            response.PrepLocked = prep.locked;
            response.BlackLoad = prep.blackLoad;
            response.WhiteLoad = prep.whiteLoad;
            response.PrepRemainingMs = prep.remainingMs;
        }
    }
}

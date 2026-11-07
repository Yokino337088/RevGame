using Fantasy.Entitas;
using Fantasy.Async;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>一个 Battle 根场景的规则状态。只有该场景线程读写，Gate 通过 Address 提交操作。</summary>
public sealed class GoBattleComponent : Entity
{
    public readonly Dictionary<string, GoBattleRoom> Rooms = new(StringComparer.Ordinal);
}

/// <summary>房间最近一次通过校验的棋盘和操作，用于在 RPC 超时后安全重试。</summary>
public sealed record GoBattleRoom(GoCore Board, GoBattleStep LastStep);

/// <summary>网络对象可能回池，工作场景只保留操作值，不能持有 RPC 请求。</summary>
public readonly record struct GoBattleStep(int Sequence, int Side, int Kind, int X, int Y, long ElapsedMs, string Payload, string PreviousHash, string ResultHash);

/// <summary>Gate 等待中的单手。批量 RPC 回包会回池，FTask 只交还结果值。</summary>
public readonly record struct GoPendingBattleStep(GoBattleStepInput Input, FTask<GoBattleOutcome?> Completion);

public readonly record struct GoBattleOutcome(string? Reason, bool Finished, int Winner, int EndReason, int DifferenceHalfPoints);

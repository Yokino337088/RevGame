using Fantasy.Entitas;
using Fantasy.Async;
using RevGame.Pvp;

namespace Fantasy;

/// <summary>一个 Battle 根场景的规则状态。只有该场景线程读写，Gate 通过 Address 提交操作。</summary>
public sealed class GoBattleComponent : Entity
{
    /// <summary>按大厅分配的房间编号保存权威棋局，收到关房通知或场景销毁时删除。</summary>
    public readonly Dictionary<string, GoBattleRoom> Rooms = new(StringComparer.Ordinal);
}

/// <summary>房间最近一次通过校验的棋盘和操作，用于在 RPC 超时后安全重试。</summary>
/// <param name="Board">当前确认的棋盘，下一手会在它的副本上验证。</param>
/// <param name="LastStep">刚刚提交成功的操作，用于识别超时后的同一手重发。</param>
public sealed record GoBattleRoom(GoCore Board, GoBattleStep LastStep);

/// <summary>网络对象可能回池，工作场景只保留操作值，不能持有 RPC 请求。</summary>
/// <param name="Sequence">从 1 开始递增的棋谱序号。</param>
/// <param name="Side">执子方，1 黑方、2 白方。</param>
/// <param name="Kind">GoAction 的数值，用于确定具体棋规操作。</param>
/// <param name="X">横向交叉点坐标，九路棋盘范围为 0 到 8。</param>
/// <param name="Y">纵向交叉点坐标，九路棋盘范围为 0 到 8。</param>
/// <param name="ElapsedMs">客户端随操作报告的用时，单位毫秒。</param>
/// <param name="Payload">请求附带的扩展文本，参与重发内容比较。</param>
/// <param name="PreviousHash">执行前局面指纹。</param>
/// <param name="ResultHash">执行后预期的局面指纹。</param>
public readonly record struct GoBattleStep(int Sequence, int Side, int Kind, int X, int Y, long ElapsedMs, string Payload, string PreviousHash, string ResultHash);

/// <summary>Gate 等待中的单手。批量 RPC 回包会回池，FTask 只交还结果值。</summary>
/// <param name="Input">本手的跨场景校验输入，提交时加入对应 Battle 的批次。</param>
/// <param name="Completion">等待者使用的完成信号，空结果代表节点未返回有效响应。</param>
public readonly record struct GoPendingBattleStep(GoBattleStepInput Input, FTask<GoBattleOutcome?> Completion);

/// <summary>从回包提取的结果值，回包归池后 Gate 仍可用它更新状态。</summary>
/// <param name="Reason">非空内容表示棋规校验拒绝的原因。</param>
/// <param name="Finished">棋盘是否到达终局阶段。</param>
/// <param name="Winner">胜方的 GoSide 编号，0 表示无胜方。</param>
/// <param name="EndReason">GoEndReason 编号，表示终局原因。</param>
/// <param name="DifferenceHalfPoints">黑白计分差，单位半目。</param>
public readonly record struct GoBattleOutcome(string? Reason, bool Finished, int Winner, int EndReason, int DifferenceHalfPoints);

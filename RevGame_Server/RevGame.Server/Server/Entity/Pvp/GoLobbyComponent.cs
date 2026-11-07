using Fantasy.Entitas;
using Fantasy.Network;
using RevGame.Pvp.Duel;

namespace Fantasy;

/// <summary>
/// 单 Gate 的休闲围棋大厅数据。所有访问都发生在所属 Scene 线程，
/// 不需要给 Dictionary 加锁；离开该 Scene 时由 Fantasy 统一销毁。
/// 棋局规则由 GoBattle 场景校验；大厅只保留连接和给客户端补发用的历史。
/// </summary>
public sealed class GoLobbyComponent : Entity
{
    public readonly Dictionary<string, GoPlayerEntity> Players = new(StringComparer.Ordinal);
    public readonly Dictionary<string, GoRoomEntity> Rooms = new(StringComparer.Ordinal);
    // 按场次与完整规则隔离。队列保留入队时间顺序，玩家节点用于常数时间退队。
    public readonly Dictionary<(string Kind, string Rules), LinkedList<GoPlayerEntity>> Waiting = new();
    // 键是 Battle 根场景 Address，值是本大厅已分配且尚未释放的房间数。
    public readonly Dictionary<long, int> WorkerLoads = new();
    // 同一个 Gate 更新周期内，每个 Battle 收一批来自不同房间的操作。
    public readonly Dictionary<long, List<GoPendingBattleStep>> PendingBattleSteps = new();
    public long SweepTimer;
    public bool Closing;
}

/// <summary>匿名玩家身份归大厅所有，Session 只是可替换的连接；令牌不可公开或写日志。</summary>
public sealed class GoPlayerEntity : Entity
{
    public string Token = "";
    public string Nickname = "";
    public string LogNickname = "";
    public string RulesJson = "";
    public string MatchKind = "";
    public Session? Connection;
    public GoRoomEntity? Room;
    public long DisconnectedAt;
    public long QueuedAt;
    public LinkedListNode<GoPlayerEntity>? WaitingNode;
    public long LastRequestAt;
    public int RequestCount;
    public int Side;
    public long ReconnectWindowMs;
    public long MatchWindowMs;
    // 半目贴目从握手规则读取。本机棋力只在排队期间参与匹配，离队即清除。
    public int Komi;
    public int QueuedRating;
}

/// <summary>
/// 房间只保存排序后的操作副本。历史中的消息均为深复制，
/// 不能直接保存网络回调中的对象，因为 Fantasy 会在回调结束后回收消息池对象。
/// </summary>
public sealed class GoRoomEntity : Entity
{
    public string RoomId = "";
    // 关房后玩家可能已经回池。展示名和保留窗口是开房时的值，不再从回池对象取。
    public string BlackName = "";
    public string WhiteName = "";
    public long RetentionWindowMs;
    public GoPlayerEntity? Black;
    public GoPlayerEntity? White;
    public readonly List<GoRelayCommand> Commands = new();
    public long StartedAt;
    public long ClosedAt;
    public string CloseReason = "";
    public int NextSide = 1;
    // 房间整个生命周期使用同一地址。终局释放计算席位后，仍可通过大厅读取历史。
    public long WorkerAddress;
    public bool WorkerReleased;
    public bool Finished;
    public bool StepPending;
    // 入房时冻结黑方预期胜率，房间只保留结算参数与本局分差供重连补发。
    public double BlackExpectedResult;
    public bool RatingFinalized;
    public int BlackRatingDelta;
    public DuelAuthority? Duel;
}

/// <summary>挂在 Session 上的身份索引；断开时其 DestroySystem 通知大厅保留重连窗口。</summary>
public sealed class GoSessionComponent : Entity
{
    public GoPlayerEntity? Player;
    public bool SupportsDelta;
    public int DuelFingerprint;
}

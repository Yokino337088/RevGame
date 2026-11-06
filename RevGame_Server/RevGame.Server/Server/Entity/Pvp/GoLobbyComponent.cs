using Fantasy.Entitas;
using Fantasy.Network;

namespace Fantasy;

/// <summary>
/// 单 Gate 的休闲围棋大厅数据。所有访问都发生在所属 Scene 线程，
/// 不需要给 Dictionary 加锁；离开该 Scene 时由 Fantasy 统一销毁。
/// 大厅数据不依赖 GoCore；Hotfix仅为终局日志审计链接同源规则，不维护第二套规则。
/// </summary>
public sealed class GoLobbyComponent : Entity
{
    public readonly Dictionary<string, GoPlayerEntity> Players = new(StringComparer.Ordinal);
    public readonly Dictionary<string, GoRoomEntity> Rooms = new(StringComparer.Ordinal);
    public readonly List<GoPlayerEntity> Waiting = new();
    public long SweepTimer;
    public bool Closing;
}

/// <summary>匿名玩家身份归大厅所有，Session 只是可替换的连接；令牌不可公开或写日志。</summary>
public sealed class GoPlayerEntity : Entity
{
    public string Token = "";
    public string Nickname = "";
    public string RulesJson = "";
    public string MatchKind = "";
    public Session? Connection;
    public GoRoomEntity? Room;
    public long DisconnectedAt;
    public long QueuedAt;
    public long LastRequestAt;
    public int RequestCount;
    public int Side;
    public long ReconnectWindowMs;
    public long MatchWindowMs;
}

/// <summary>
/// 房间只保存排序后的操作副本。历史中的消息均为深复制，
/// 不能直接保存网络回调中的对象，因为 Fantasy 会在回调结束后回收消息池对象。
/// </summary>
public sealed class GoRoomEntity : Entity
{
    public string RoomId = "";
    public GoPlayerEntity? Black;
    public GoPlayerEntity? White;
    public readonly List<GoRelayCommand> Commands = new();
    public long StartedAt;
    public long ClosedAt;
    public string CloseReason = "";
    public int NextSide = 1;
    // 只去重终局日志，不改变房间接收/广播的协议行为。
    public bool ResultLogged;
}

/// <summary>挂在 Session 上的身份索引；断开时其 DestroySystem 通知大厅保留重连窗口。</summary>
public sealed class GoSessionComponent : Entity
{
    public GoPlayerEntity? Player;
}

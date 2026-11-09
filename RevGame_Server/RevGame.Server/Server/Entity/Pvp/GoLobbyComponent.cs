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
    /// <summary>按恢复令牌查找匿名玩家，连接断开后仍保留到重连期限。</summary>
    public readonly Dictionary<string, GoPlayerEntity> Players = new(StringComparer.Ordinal);
    /// <summary>按房间编号保存大厅侧的席位和棋谱，房间释放时移出索引。</summary>
    public readonly Dictionary<string, GoRoomEntity> Rooms = new(StringComparer.Ordinal);
    // 按场次与完整规则隔离。队列保留入队时间顺序，玩家节点用于常数时间退队。
    public readonly Dictionary<(string Kind, string Rules), LinkedList<GoPlayerEntity>> Waiting = new();
    // 键是 Battle 根场景 Address，值是本大厅已分配且尚未释放的房间数。
    public readonly Dictionary<long, int> WorkerLoads = new();
    // 同一个 Gate 更新周期内，每个 Battle 收一批来自不同房间的操作。
    public readonly Dictionary<long, List<GoPendingBattleStep>> PendingBattleSteps = new();
    /// <summary>大厅定期清理与重试匹配的 Fantasy 计时器句柄，销毁时取消。</summary>
    public long SweepTimer;
    /// <summary>大厅销毁开始时置为真，让握手和异步收尾识别停机状态。</summary>
    public bool Closing;
}

/// <summary>匿名玩家身份归大厅所有，Session 只是可替换的连接；令牌不可公开或写日志。</summary>
public sealed class GoPlayerEntity : Entity
{
    /// <summary>恢复身份用的随机令牌，同时是大厅索引的键，属于敏感信息。</summary>
    public string Token = "";
    /// <summary>给客户端展示的昵称，创建身份时去掉首尾空白。</summary>
    public string Nickname = "";
    /// <summary>去掉控制字符后的日志用昵称，避免玩家输入插入换行或颜色指令。</summary>
    public string LogNickname = "";
    /// <summary>握手时确认的原始规则文本，匹配与重连都按完整文本比较。</summary>
    public string RulesJson = "";
    /// <summary>客户端选择的场次，取值为 rookie 或 advanced。</summary>
    public string MatchKind = "";
    /// <summary>当前绑定的连接，断线时为空，重连会绑定另一条 Session。</summary>
    public Session? Connection;
    /// <summary>玩家所在的大厅房间，未入房或已离席时为空。</summary>
    public GoRoomEntity? Room;
    /// <summary>断线的 UTC Unix 毫秒，0 表示当前无待恢复的断线。</summary>
    public long DisconnectedAt;
    /// <summary>入队的 UTC Unix 毫秒，0 表示未排队。</summary>
    public long QueuedAt;
    /// <summary>在匹配链表中的节点，退队可直接摘除，避免遍历队列。</summary>
    public LinkedListNode<GoPlayerEntity>? WaitingNode;
    /// <summary>当前限流窗口开始时的 UTC Unix 毫秒。</summary>
    public long LastRequestAt;
    /// <summary>当前限流窗口已计入的请求数，进入下一窗口时归零。</summary>
    public int RequestCount;
    /// <summary>已认证的执子方，0 未入座、1 黑方、2 白方。</summary>
    public int Side;
    /// <summary>断线后保留身份的时长，单位毫秒。</summary>
    public long ReconnectWindowMs;
    /// <summary>一次排队可等待的最长时长，单位毫秒。</summary>
    public long MatchWindowMs;
    /// <summary>握手规则中的贴目值，单位为半目。</summary>
    public int Komi;
    /// <summary>此次排队使用的本地棋力分，退队时清除。</summary>
    public int QueuedRating;
}

/// <summary>
/// 房间只保存排序后的操作副本。历史中的消息均为深复制，
/// 不能直接保存网络回调中的对象，因为 Fantasy 会在回调结束后回收消息池对象。
/// </summary>
public sealed class GoRoomEntity : Entity
{
    /// <summary>房间创建时生成的唯一编号，用于大厅索引和 Battle 路由消息。</summary>
    public string RoomId = "";
    // 关房后玩家可能已经回池。展示名和保留窗口是开房时的值，不再从回池对象取。
    /// <summary>开房时记录的黑方昵称，黑方 Entity 离席回池后仍可展示。</summary>
    public string BlackName = "";
    /// <summary>开房时记录的白方昵称，白方 Entity 离席回池后仍可展示。</summary>
    public string WhiteName = "";
    /// <summary>关房后可保留快照的时长，取两位玩家的重连时长上限，单位毫秒。</summary>
    public long RetentionWindowMs;
    /// <summary>黑方座位的玩家引用，离席时清空。</summary>
    public GoPlayerEntity? Black;
    /// <summary>白方座位的玩家引用，离席时清空。</summary>
    public GoPlayerEntity? White;
    /// <summary>已获 Battle 确认的操作副本，按序号追加。房间销毁时归还池对象。</summary>
    public readonly List<GoRelayCommand> Commands = new();
    /// <summary>开房的 UTC Unix 毫秒，用于客户端同步。</summary>
    public long StartedAt;
    /// <summary>关闭的 UTC Unix 毫秒。0 表示大厅仍保留开放状态，规则终局由 Finished 单独表示。</summary>
    public long ClosedAt;
    /// <summary>关房原因，随状态快照告知客户端。</summary>
    public string CloseReason = "";
    /// <summary>大厅预检使用的下一手执子方，黑方先行。</summary>
    public int NextSide = 1;
    /// <summary>房间分配到的 Battle 场景地址，整个房间生命周期内保持固定。</summary>
    public long WorkerAddress;
    /// <summary>计算席位是否已归还，防止终局和关房各扣一次节点负载。</summary>
    public bool WorkerReleased;
    /// <summary>围棋规则或决斗已得出终局结果，此后不再提交新的围棋操作。</summary>
    public bool Finished;
    /// <summary>是否有一手操作等待 Battle 回包。每房最多一手在途。</summary>
    public bool StepPending;
    /// <summary>开房时冻结的黑方预期得分，范围 0 到 1，计分时用于计算分差。</summary>
    public double BlackExpectedResult;
    /// <summary>本局是否已得出可发送给客户端的棋力分差。</summary>
    public bool RatingFinalized;
    /// <summary>黑方本局棋力增减分，白方取其相反值。</summary>
    public int BlackRatingDelta;
    /// <summary>本房间的决斗权威实例，发起前为空，房间销毁时释放原生资源。</summary>
    public DuelAuthority? Duel;
}

/// <summary>挂在 Session 上的身份索引；断开时其 DestroySystem 通知大厅保留重连窗口。</summary>
public sealed class GoSessionComponent : Entity
{
    /// <summary>Session 绑定的匿名身份，连接被替换或销毁时清空。</summary>
    public GoPlayerEntity? Player;
    /// <summary>本连接是否声明支持按棋谱基序号接收增量快照。</summary>
    public bool SupportsDelta;
    /// <summary>本次握手声明的决斗配置指纹，供两席兼容性检查。旧客户端默认发 0。</summary>
    public int DuelFingerprint;
}

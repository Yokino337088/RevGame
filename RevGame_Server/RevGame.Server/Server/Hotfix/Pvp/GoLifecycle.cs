using Fantasy.Async;
using Fantasy.Entitas.Interface;
using Fantasy.Event;
using Fantasy.Platform.Net;

namespace Fantasy;

/// <summary>Gate 根场景管理大厅，GoBattle 根场景独立执行棋局规则。</summary>
public sealed class OnCreateScene_InitializeGoLobby : AsyncEventSystem<OnCreateScene>
{
    /// <summary>框架创建 Scene 时按根场景类型挂载业务组件，并建立 Gate 可分配的节点索引。</summary>
    protected override async FTask Handler(OnCreateScene value)
    {
        var scene = value.Scene;
        // 只在根场景装配大厅或对局组件，子场景不重复接管该职责。
        if (scene.SceneRuntimeType == SceneRuntimeType.Root)
        {
            if (scene.SceneType == SceneType.Gate)
            {
                var lobby = scene.AddComponent<GoLobbyComponent>(false);
                // 取配置中的 GoBattle 节点，仅把同一 World 和进程的节点加入当前大厅。
                var workers = SceneConfigData.Instance.GetSceneBySceneType(SceneType.GoBattle);
                foreach (var worker in workers.Where(worker => worker.WorldConfigId == scene.SceneConfig.WorldConfigId
                    && worker.ProcessConfigId == scene.SceneConfig.ProcessConfigId).OrderBy(worker => worker.Id))
                {
                    // 以场景地址作为路由键，初始化已分配的房间数为 0。
                    lobby.WorkerLoads.Add(worker.Address, 0);
                }
                Log.Info($"[PVP/大厅] 围棋大厅已就绪 Battle节点={lobby.WorkerLoads.Count}");
            }
            else if (scene.SceneType == SceneType.GoBattle)
            {
                // 规则棋盘归 GoBattle 自己的场景组件所有，Gate 通过消息访问。
                scene.AddComponent<GoBattleComponent>(false);
                Log.Info($"[PVP/节点] 围棋对局节点已就绪 地址={scene.Address}");
            }
        }
        await FTask.CompletedTask;
    }
}

/// <summary>大厅组件创建时注册周期任务。由 Fantasy 生命周期自动调用。</summary>
public sealed class GoLobbyComponentAwakeSystem : AwakeSystem<GoLobbyComponent>
{
    /// <summary>在组件所属 Scene 注册每秒扫尾一次的计时器。</summary>
    protected override void Awake(GoLobbyComponent self)
    {
        // 定时清理过期连接，并复查等待已放宽的匹配；落子只在收到操作时计算。
        self.SweepTimer = FTask.RepeatedTimer(self.Scene, 1000, self.Sweep);
    }
}

/// <summary>大厅组件销毁时停止接收与排队，释放尚未发送的批次。</summary>
public sealed class GoLobbyComponentDestroySystem : DestroySystem<GoLobbyComponent>
{
    /// <summary>先停止计时，再唤醒等待发送的操作，最后清空索引。</summary>
    protected override void Destroy(GoLobbyComponent self)
    {
        // 先标记停机，防止后续回调继续把大厅当成可写状态。
        self.Closing = true;
        // 计时器归所属 Scene 管理，销毁时用保存的句柄取消。
        FTask.RemoveTimer(self.Scene, ref self.SweepTimer);
        self.Waiting.Clear();
        self.WorkerLoads.Clear();
        foreach (var batch in self.PendingBattleSteps.Values)
        {
            foreach (var item in batch)
            {
                // 这些输入还在待发队列中，取消时需归还输入并用空结果唤醒等待者。
                item.Input.Dispose();
                item.Completion.SetResult(null);
            }
        }
        self.PendingBattleSteps.Clear();
        // 房间与玩家的 Entity 生命周期由框架管理，这里清掉组件自己的查找索引。
        self.Rooms.Clear();
        self.Players.Clear();
    }
}

/// <summary>Session 解绑时只把仍由该连接持有的玩家标为断线。</summary>
public sealed class GoSessionComponentDestroySystem : DestroySystem<GoSessionComponent>
{
    /// <summary>清空会话绑定与协商能力，必要时通知大厅开始重连保留。</summary>
    protected override void Destroy(GoSessionComponent self)
    {
        var player = self.Player;
        self.Player = null;
        self.SupportsDelta = false;
        self.DuelFingerprint = 0;
        // 重连可能已把玩家绑定到新 Session。旧连接的销毁回调不能覆盖新状态。
        if (player == null || player.IsDisposed || player.Connection != self.Parent)
        {
            return;
        }
        var lobby = self.Scene.GetComponent<GoLobbyComponent>();
        if (lobby != null && !lobby.IsDisposed)
        {
            lobby.Disconnected(player);
        }
    }
}

/// <summary>匿名玩家回收时重置其会话、匹配和棋力数据。</summary>
public sealed class GoPlayerEntityDestroySystem : DestroySystem<GoPlayerEntity>
{
    /// <summary>清除本次身份留下的引用、令牌和计数，让池化对象从干净状态复用。</summary>
    protected override void Destroy(GoPlayerEntity self)
    {
        self.Connection = null;
        self.Room = null;
        // 身份字段只属于本次连接生命周期，尤其是恢复令牌不能带到下一位玩家。
        self.Token = "";
        self.Nickname = "";
        self.LogNickname = "";
        self.RulesJson = "";
        self.MatchKind = "";
        // 时间窗、队列节点和限流计数从默认状态开始，避免复用后提前过期。
        self.DisconnectedAt = 0;
        self.QueuedAt = 0;
        self.WaitingNode = null;
        self.LastRequestAt = 0;
        self.RequestCount = 0;
        self.Side = 0;
        self.ReconnectWindowMs = 0;
        self.MatchWindowMs = 0;
        self.Komi = 0;
        self.QueuedRating = 0;
    }
}

/// <summary>房间释放后归还棋谱消息和决斗资源，并清空座位与节点状态。</summary>
public sealed class GoRoomEntityDestroySystem : DestroySystem<GoRoomEntity>
{
    /// <summary>按资源所有权归还命令对象与决斗实例，再重置房间字段供复用。</summary>
    protected override void Destroy(GoRoomEntity self)
    {
        // 棋谱保存了池化消息的深复制，每条都要归还后再清空列表。
        foreach (var command in self.Commands)
        {
            command.Return();
        }
        self.Commands.Clear();
        self.Black = null;
        self.White = null;
        self.RoomId = "";
        self.BlackName = "";
        self.WhiteName = "";
        self.RetentionWindowMs = 0;
        // 复用时必须重新分配节点、计算负载和结算，不能继承上一局的标志。
        self.WorkerAddress = 0;
        self.WorkerReleased = false;
        self.Finished = false;
        self.BlackExpectedResult = 0;
        self.RatingFinalized = false;
        self.BlackRatingDelta = 0;
        // 原生权威实例属于房间，不随单席断线立刻销毁；房间回收时成对释放。
        FTask.RemoveTimer(self.Scene, ref self.DuelTimer);
        self.Duel?.Dispose();
        self.Duel = null;
        self.StepPending = false;
        self.StartedAt = 0;
        self.ClosedAt = 0;
        self.CloseReason = "";
        // 下一次开房仍从黑方先行开始。
        self.NextSide = 1;
    }
}

using Fantasy.Async;
using Fantasy.Entitas.Interface;
using Fantasy.Event;

namespace Fantasy;

/// <summary>只在配置里的 Gate 根场景建立大厅；房间是该 Scene 拥有的轻量 Entity。</summary>
public sealed class OnCreateScene_InitializeGoLobby : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene value)
    {
        if (value.Scene.SceneType == SceneType.Gate && value.Scene.SceneRuntimeType == SceneRuntimeType.Root)
        {
            value.Scene.AddComponent<GoLobbyComponent>(false);
            Log.Info("九路围棋中转大厅已就绪：TCP 20000，规则由双方同源客户端校验。");
        }
        await FTask.CompletedTask;
    }
}

public sealed class GoLobbyComponentAwakeSystem : AwakeSystem<GoLobbyComponent>
{
    protected override void Awake(GoLobbyComponent self)
    {
        // 每秒清理过期连接即可；围棋没有每帧服务端规则计算。
        self.SweepTimer = FTask.RepeatedTimer(self.Scene, 1000, self.Sweep);
    }
}

public sealed class GoLobbyComponentDestroySystem : DestroySystem<GoLobbyComponent>
{
    protected override void Destroy(GoLobbyComponent self)
    {
        self.Closing = true;
        FTask.RemoveTimer(self.Scene, ref self.SweepTimer);
        self.Waiting.Clear();
        self.Rooms.Clear();
        self.Players.Clear();
    }
}

public sealed class GoSessionComponentDestroySystem : DestroySystem<GoSessionComponent>
{
    protected override void Destroy(GoSessionComponent self)
    {
        var player = self.Player;
        self.Player = null;
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

public sealed class GoPlayerEntityDestroySystem : DestroySystem<GoPlayerEntity>
{
    protected override void Destroy(GoPlayerEntity self)
    {
        self.Connection = null;
        self.Room = null;
        self.Token = "";
        self.Nickname = "";
        self.RulesJson = "";
        self.MatchKind = "";
        self.DisconnectedAt = 0;
        self.QueuedAt = 0;
        self.LastRequestAt = 0;
        self.RequestCount = 0;
        self.Side = 0;
        self.ReconnectWindowMs = 0;
        self.MatchWindowMs = 0;
    }
}

public sealed class GoRoomEntityDestroySystem : DestroySystem<GoRoomEntity>
{
    protected override void Destroy(GoRoomEntity self)
    {
        foreach (var command in self.Commands)
        {
            command.Return();
        }
        self.Commands.Clear();
        self.Black = null;
        self.White = null;
        self.RoomId = "";
        self.StartedAt = 0;
        self.ClosedAt = 0;
        self.CloseReason = "";
        self.NextSide = 1;
    }
}

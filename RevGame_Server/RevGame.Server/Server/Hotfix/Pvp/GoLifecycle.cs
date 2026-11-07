using Fantasy.Async;
using Fantasy.Entitas.Interface;
using Fantasy.Event;
using Fantasy.Platform.Net;

namespace Fantasy;

/// <summary>Gate 根场景管理大厅，GoBattle 根场景独立执行棋局规则。</summary>
public sealed class OnCreateScene_InitializeGoLobby : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene value)
    {
        var scene = value.Scene;
        if (scene.SceneRuntimeType == SceneRuntimeType.Root)
        {
            if (scene.SceneType == SceneType.Gate)
            {
                var lobby = scene.AddComponent<GoLobbyComponent>(false);
                var workers = SceneConfigData.Instance.GetSceneBySceneType(SceneType.GoBattle);
                foreach (var worker in workers.Where(worker => worker.WorldConfigId == scene.SceneConfig.WorldConfigId
                    && worker.ProcessConfigId == scene.SceneConfig.ProcessConfigId).OrderBy(worker => worker.Id))
                {
                    lobby.WorkerLoads.Add(worker.Address, 0);
                }
                Log.Info($"[PVP/大厅] 围棋大厅已就绪 Battle节点={lobby.WorkerLoads.Count}");
            }
            else if (scene.SceneType == SceneType.GoBattle)
            {
                scene.AddComponent<GoBattleComponent>(false);
                Log.Info($"[PVP/节点] 围棋对局节点已就绪 地址={scene.Address}");
            }
        }
        await FTask.CompletedTask;
    }
}

public sealed class GoLobbyComponentAwakeSystem : AwakeSystem<GoLobbyComponent>
{
    protected override void Awake(GoLobbyComponent self)
    {
        // 定时清理过期连接，并复查等待已放宽的匹配；落子只在收到操作时计算。
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
        self.WorkerLoads.Clear();
        foreach (var batch in self.PendingBattleSteps.Values)
        {
            foreach (var item in batch)
            {
                item.Input.Dispose();
                item.Completion.SetResult(null);
            }
        }
        self.PendingBattleSteps.Clear();
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
        self.SupportsDelta = false;
        self.DuelFingerprint = 0;
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
        self.LogNickname = "";
        self.RulesJson = "";
        self.MatchKind = "";
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
        self.BlackName = "";
        self.WhiteName = "";
        self.RetentionWindowMs = 0;
        self.WorkerAddress = 0;
        self.WorkerReleased = false;
        self.Finished = false;
        self.BlackExpectedResult = 0;
        self.RatingFinalized = false;
        self.BlackRatingDelta = 0;
        self.Duel?.Dispose();
        self.Duel = null;
        self.StepPending = false;
        self.StartedAt = 0;
        self.ClosedAt = 0;
        self.CloseReason = "";
        self.NextSide = 1;
    }
}

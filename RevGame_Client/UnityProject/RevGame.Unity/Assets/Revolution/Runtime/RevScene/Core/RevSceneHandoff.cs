using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一次交接的工作阶段，按执行顺序排列。业务可跳过不需要的阶段。</summary>
    public enum RevSceneHandoffStage
    {
        /// <summary>请求已接受，正在建立防止玩家操作半成品的遮挡或门禁。</summary>
        Covering,
        /// <summary>正在准备场景和玩法所需的资源。</summary>
        PreparingResources,
        /// <summary>正在加载目标 Unity 场景。</summary>
        LoadingScene,
        /// <summary>目标场景已确认，正在准备业务实例和交接状态。</summary>
        PreparingGameplay,
        /// <summary>业务状态已切换，正在等待首屏或玩法就绪。</summary>
        WaitingForGameplay,
        /// <summary>正在归还交接的临时资源并执行退场清理。</summary>
        CleaningUp,
        /// <summary>所有就绪和清理条件已满足，正在恢复玩家可见画面。</summary>
        Revealing
    }

    /// <summary>交接完成时的结果，由掌握玩法就绪条件的发起方报告。</summary>
    public enum RevSceneHandoffStatus
    {
        /// <summary>玩法已交付，可以从终态继续操作。</summary>
        Succeeded,
        /// <summary>交接失败，调用方可沿业务提供的入口恢复。</summary>
        Failed,
        /// <summary>发起方退出或中止了交接。</summary>
        Cancelled
    }

    /// <summary>一次已接单的交接。观察者可随时读阶段，或让多个调用者等待同一个终态。</summary>
    public sealed class RevSceneHandoff
    {
        /// <summary>本次交接唯一的完成源，发布结果后唤醒全部等待方。</summary>
        private readonly RevTaskCompletionSource<RevSceneHandoffResult> _completion = RevTask<RevSceneHandoffResult>.CreateSource();

        /// <summary>本次请求的唯一标识，用于查询最近的交接。</summary>
        public Guid RequestId { get; } = Guid.NewGuid();
        /// <summary>接单时的 Unity 活动场景。</summary>
        public string SourceScene { get; }
        /// <summary>请求要到达的 Unity 场景，由发起方指定。</summary>
        public string TargetScene { get; }
        /// <summary>接单时的业务状态，由发起方提供。</summary>
        public string SourceState { get; }
        /// <summary>请求的业务状态，可与同一场景内的其他状态区分。</summary>
        public string TargetState { get; }
        /// <summary>本次请求的资源来源和生命周期快照，随交接 ID 查询。</summary>
        public RevSceneHandoffResources Resources { get; } = new RevSceneHandoffResources();
        /// <summary>最近进入的工作阶段。交接结束后固定在最后执行到的阶段。</summary>
        public RevSceneHandoffStage Stage { get; internal set; }
        /// <summary>当前是否已有终态。终态发布后立即可读。</summary>
        public bool IsCompleted => Result != null;
        /// <summary>不可变的终态。交接进行中为空。</summary>
        public RevSceneHandoffResult Result { get; private set; }

        /// <summary>由追踪器接单时创建，保存来源和目标，避免依赖之后会变化的全局状态。</summary>
        internal RevSceneHandoff(string sourceScene, string targetScene, string sourceState, string targetState)
        {
            SourceScene = sourceScene;
            TargetScene = targetScene;
            SourceState = sourceState;
            TargetState = targetState;
        }

        /// <summary>在 Unity 主线程等待终态。业务失败或取消也以结果返回，完成后可重复等待。</summary>
        public RevTask<RevSceneHandoffResult> WaitAsync()
        {
            return _completion.Task;
        }

        /// <summary>追踪器已释放活动请求名额，此时先保存终态，再允许等待方同步继续执行。</summary>
        internal void Publish(RevSceneHandoffResult result)
        {
            Result = result;
            _completion.SetResult(result);
        }
    }

    /// <summary>终态快照。恢复或下一次切场不会改写已经交付的结果。</summary>
    public sealed class RevSceneHandoffResult
    {
        /// <summary>对应的已接单请求标识。</summary>
        public Guid RequestId { get; }
        /// <summary>该请求唯一的最终结果。</summary>
        public RevSceneHandoffStatus Status { get; }
        /// <summary>完成时最后执行到的阶段，可定位失败发生的位置。</summary>
        public RevSceneHandoffStage Stage { get; }
        /// <summary>完成时实际活动的 Unity 场景，失败时可能已切到目标场景。</summary>
        public string ActiveScene { get; }
        /// <summary>完成时实际业务状态，可能与活动场景的目标状态不同。</summary>
        public string ActualState { get; }
        /// <summary>发起方报告本次负责的清理是否完成；失败后的业务恢复由单独请求记录。</summary>
        public bool CleanupSucceeded { get; }
        /// <summary>供程序判断的稳定原因码，由发起方定义，成功时为空串。</summary>
        public string ReasonCode { get; }
        /// <summary>供人排查的说明，由发起方提供，缺省为空串。</summary>
        public string Detail { get; }

        /// <summary>追踪器在业务收尾后冻结结果，并读取当时实际活动的 Unity 场景。</summary>
        internal RevSceneHandoffResult(RevSceneHandoff handoff, RevSceneHandoffStatus status, string actualState,
            bool cleanupSucceeded, string reasonCode, string detail)
        {
            RequestId = handoff.RequestId;
            Status = status;
            Stage = handoff.Stage;
            ActiveScene = RevScene.CurrentName;
            ActualState = actualState;
            CleanupSucceeded = cleanupSucceeded;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>由业务流程持有的主线程交接追踪器。每个实例只允许一条在途请求，保留有限的历史。</summary>
    public sealed class RevSceneHandoffTracker
    {
        /// <summary>最多保留的最近请求数，含当前请求。淘汰只影响按 ID 查询，已有句柄继续有效。</summary>
        private const int HistoryCapacity = 16;
        /// <summary>按接单顺序保存交接，无引用业务对象或 Unity 资源。</summary>
        private readonly Queue<RevSceneHandoff> _recent = new Queue<RevSceneHandoff>(HistoryCapacity);
        /// <summary>唯一在途请求，终态发布前清除，以便等待方可以立即申请下一次交接。</summary>
        private RevSceneHandoff _active;

        /// <summary>最近一次接单的请求。拒绝重入时保持原值，尚未接单时为空。</summary>
        public RevSceneHandoff Latest { get; private set; }

        /// <summary>接收一个交接请求。已有在途请求时返回空，不创建新 ID，也不占历史名额。</summary>
        public RevSceneHandoff TryBegin(string targetScene, string sourceState, string targetState)
        {
            if (_active != null)
            {
                return null;
            }

            // 只有前一次已收尾时才能接单，所以队首必然可安全从查询历史淘汰。
            if (_recent.Count == HistoryCapacity)
            {
                _recent.Dequeue();
            }
            _active = new RevSceneHandoff(RevScene.CurrentName, targetScene, sourceState, targetState);
            Latest = _active;
            _recent.Enqueue(_active);
            return _active;
        }

        /// <summary>按 ID 查找在途或近期完成的请求。未知或已淘汰时返回 false。</summary>
        public bool TryGet(Guid requestId, out RevSceneHandoff handoff)
        {
            foreach (RevSceneHandoff recent in _recent)
            {
                if (recent.RequestId == requestId)
                {
                    handoff = recent;
                    return true;
                }
            }
            handoff = null;
            return false;
        }

        /// <summary>更新当前请求的阶段。已结案、其它请求或倒退的阶段会被拒绝。</summary>
        public bool TryAdvance(RevSceneHandoff handoff, RevSceneHandoffStage stage)
        {
            if (handoff == null || _active != handoff || stage < handoff.Stage)
            {
                return false;
            }
            handoff.Stage = stage;
            return true;
        }

        /// <summary>业务清理并解除自己的互斥后调用。只有当前请求可发布一次终态，旧回调无法覆盖结果。</summary>
        public bool TryComplete(RevSceneHandoff handoff, RevSceneHandoffStatus status, string actualState,
            bool cleanupSucceeded, string reasonCode = null, string detail = null)
        {
            if (handoff == null || _active != handoff)
            {
                return false;
            }

            var result = new RevSceneHandoffResult(handoff, status, actualState, cleanupSucceeded, reasonCode, detail);
            _active = null;
            handoff.Publish(result);
            return true;
        }
    }
}

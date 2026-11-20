// RevSceneHandoffExecution.cs — 交接发起方声明阶段回调，执行器统一接单、清理与结案。
// 业务提交前后的恢复权交给宿主。执行器保留真实场景、失败阶段和提交状态。
using System;
using UnityEngine.SceneManagement;

namespace Revolution
{
    /// <summary>一次场景交接的业务接线，在接单时复制，回调都在主线程开始执行。</summary>
    public sealed class RevSceneHandoffPlan
    {
        /// <summary>目标场景名或 Build Settings 路径。</summary>
        public string TargetScene;
        /// <summary>发起时的业务状态，用于诊断。</summary>
        public string SourceState;
        /// <summary>目标业务状态，用于诊断。</summary>
        public string TargetState;
        /// <summary>屏幕或应用宿主的令牌，须覆盖新旧业务域，阶段之间自动检查。</summary>
        public RevCancellationToken Cancellation;
        /// <summary>显示遮挡并等待真实落屏。跨场景遮挡可由宿主保持。</summary>
        public Func<RevSceneHandoff, RevTask> Cover;
        /// <summary>声明并准备首屏资源。交接资源租约由追踪器归还。</summary>
        public Func<RevSceneHandoff, RevTask> Preload;
        /// <summary>场景加载适配器。为空时执行 RevScene 的标准加载。</summary>
        public Func<RevSceneHandoff, RevTask> Load;
        /// <summary>标准加载的可选放行门禁。已有场景保持活动直到放行。</summary>
        public Func<RevTask<bool>> CanActivate;
        /// <summary>放行等待上限，单位为秒。只用于标准加载加门禁的路径。</summary>
        public float ActivationTimeoutSeconds = 60;
        /// <summary>准备目标业务，之后再调用独立的 Commit。</summary>
        public Func<RevSceneHandoff, RevTask> Prepare;
        /// <summary>提交目标业务状态。开始执行后失败也属于提交后恢复边界。</summary>
        public Func<RevSceneHandoff, RevTask> Commit;
        /// <summary>等待首屏与玩家操作条件，成功返回才允许揭幕。</summary>
        public Func<RevSceneHandoff, RevTask> WaitReady;
        /// <summary>归还临时业务对象。接单后成功与失败路径均调用一次。</summary>
        public Func<RevSceneHandoff, RevTask> Cleanup;
        /// <summary>成功路径恢复展示与输入。</summary>
        public Func<RevSceneHandoff, RevTask> Reveal;
        /// <summary>失败或取消后选择恢复策略。调用时本次临时业务清理已尝试。</summary>
        public Func<RevSceneHandoffFailure, RevTask> Recover;
        /// <summary>发布终态前解除业务门禁或冻结额外快照。</summary>
        public Action<RevSceneHandoff, RevSceneHandoffStatus> BeforePublish;
        /// <summary>终态发布前读取真实业务状态。</summary>
        public Func<string> ReadActualState;
        /// <summary>报告工作阶段和对应阶段内的进度。进度单位为 0～1。</summary>
        public Action<RevSceneHandoffStage, float> Progress;

        /// <summary>冻结本次回调与目标值，之后的配置更改仅影响新请求。</summary>
        internal RevSceneHandoffPlan Snapshot()
        {
            return (RevSceneHandoffPlan)MemberwiseClone();
        }
    }

    /// <summary>交给业务恢复回调的只读信息，保留原失败和提交边界。</summary>
    public sealed class RevSceneHandoffFailure
    {
        /// <summary>发生失败的交接，仍具有原来的请求 ID。</summary>
        public RevSceneHandoff Handoff { get; }
        /// <summary>当前失败是否属于取消。</summary>
        public RevSceneHandoffStatus Status { get; }
        /// <summary>业务提交回调是否已经开始。</summary>
        public bool CommitStarted { get; }
        /// <summary>失败原因，供统一错误界面和日志使用。</summary>
        public Exception Error { get; }

        /// <summary>由执行器在调用恢复策略之前冻结故障信息。</summary>
        internal RevSceneHandoffFailure(RevSceneHandoff handoff, RevSceneHandoffStatus status, bool commitStarted, Exception error)
        {
            Handoff = handoff;
            Status = status;
            CommitStarted = commitStarted;
            Error = error;
        }
    }

    /// <summary>实现标准阶段顺序、默认场景加载和唯一终态。宿主持有追踪器和业务策略。</summary>
    public static class RevSceneHandoffExecution
    {
        /// <summary>尝试执行交接并返回可等待句柄。已有在途请求时返回空且不调用接线。</summary>
        public static RevSceneHandoff Start(RevSceneHandoffTracker tracker, RevSceneHandoffPlan plan)
        {
            if (tracker == null)
            {
                throw new ArgumentNullException(nameof(tracker));
            }
            if (plan == null || string.IsNullOrWhiteSpace(plan.TargetScene) || plan.ReadActualState == null)
            {
                throw new ArgumentException("交接计划必须声明目标场景和实际状态读取。", nameof(plan));
            }
            RevSceneHandoffPlan snapshot = plan.Snapshot();
            RevSceneHandoff handoff = tracker.TryBegin(snapshot.TargetScene, snapshot.SourceState, snapshot.TargetState);
            if (handoff != null)
            {
                RunAsync(tracker, snapshot, handoff).Forget();
            }
            return handoff;
        }

        /// <summary>依序执行阶段。失败后先尝试清理、再恢复、最后冻结终态并唤醒等待方。</summary>
        private static async RevTask RunAsync(RevSceneHandoffTracker tracker, RevSceneHandoffPlan plan, RevSceneHandoff handoff)
        {
            bool commitStarted = false;
            bool cleanupAttempted = false;
            bool cleanupSucceeded = false;
            var status = RevSceneHandoffStatus.Succeeded;
            string reason = null;
            string detail = null;
            try
            {
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.Covering, plan.Cover);
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.PreparingResources, plan.Preload);
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.LoadingScene, plan.Load ?? (item => LoadAsync(plan)));
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.PreparingGameplay, plan.Prepare);
                if (plan.Commit != null)
                {
                    plan.Cancellation?.ThrowIfCancelled();
                    commitStarted = true;
                    await plan.Commit(handoff);
                }
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.WaitingForGameplay, plan.WaitReady);
                Advance(tracker, plan, handoff, RevSceneHandoffStage.CleaningUp);
                cleanupAttempted = true;
                if (plan.Cleanup != null)
                {
                    await plan.Cleanup(handoff);
                }
                cleanupSucceeded = true;
                await StepAsync(tracker, plan, handoff, RevSceneHandoffStage.Revealing, plan.Reveal);
                handoff.Resources.CaptureReady();
            }
            catch (Exception error)
            {
                status = error is RevOperationCanceledException || plan.Cancellation?.IsCancelled == true
                    ? RevSceneHandoffStatus.Cancelled : RevSceneHandoffStatus.Failed;
                reason = status == RevSceneHandoffStatus.Cancelled ? "flow-disposed" : FailureReason(handoff.Stage);
                detail = error.Message;
                if (!cleanupAttempted)
                {
                    try
                    {
                        if (plan.Cleanup != null)
                        {
                            await plan.Cleanup(handoff);
                        }
                        cleanupSucceeded = true;
                    }
                    catch (Exception cleanupError)
                    {
                        detail += "；清理失败：" + cleanupError.Message;
                        RevLog.Exception(cleanupError, "场景交接失败后的清理异常", "RevScene");
                    }
                }

                // 恢复适配器可能载入其它场景，预热引用应在本次所有权结束后归还。
                handoff.Resources.ReleaseWarmup();
                var failure = new RevSceneHandoffFailure(handoff, status, commitStarted, error);
                try
                {
                    if (plan.Recover != null)
                    {
                        await plan.Recover(failure);
                    }
                }
                catch (Exception recoveryError)
                {
                    detail += "；恢复失败：" + recoveryError.Message;
                    RevLog.Exception(recoveryError, "场景交接恢复异常", "RevScene");
                }
            }
            finally
            {
                string actual = handoff.SourceState;
                try
                {
                    // 调用顺序保证下一次交接等待者取得终态时，业务已解除本次互斥。
                    plan.BeforePublish?.Invoke(handoff, status);
                    actual = plan.ReadActualState();
                }
                catch (Exception error)
                {
                    status = RevSceneHandoffStatus.Failed;
                    cleanupSucceeded = false;
                    reason = "publish-failed";
                    detail = string.IsNullOrEmpty(detail) ? error.Message : detail + "；结案失败：" + error.Message;
                    RevLog.Exception(error, "场景交接结案异常", "RevScene");
                }
                tracker.TryComplete(handoff, status, actual, cleanupSucceeded, reason, detail);
            }
        }

        /// <summary>推进阶段，在回调开始与返回时核对宿主仍有效。</summary>
        private static async RevTask StepAsync(RevSceneHandoffTracker tracker, RevSceneHandoffPlan plan, RevSceneHandoff handoff,
            RevSceneHandoffStage stage, Func<RevSceneHandoff, RevTask> callback)
        {
            plan.Cancellation?.ThrowIfCancelled();
            Advance(tracker, plan, handoff, stage);
            if (callback != null)
            {
                await callback(handoff);
            }
            plan.Cancellation?.ThrowIfCancelled();
            plan.Progress?.Invoke(stage, 1);
        }

        /// <summary>沿现有追踪器记录阶段并公布本阶段的初始进度。</summary>
        private static void Advance(RevSceneHandoffTracker tracker, RevSceneHandoffPlan plan, RevSceneHandoff handoff, RevSceneHandoffStage stage)
        {
            if (!tracker.TryAdvance(handoff, stage))
            {
                throw new InvalidOperationException("当前交接不能进入阶段：" + stage);
            }
            plan.Progress?.Invoke(stage, 0);
        }

        /// <summary>按目标加载场景，使用真实激活结果判断成功。存在放行门禁时沿用标准回退。</summary>
        private static async RevTask LoadAsync(RevSceneHandoffPlan plan)
        {
            if (IsTarget(plan.TargetScene))
            {
                return;
            }
            Action<float> progress = value => plan.Progress?.Invoke(RevSceneHandoffStage.LoadingScene, value);
            if (plan.CanActivate == null)
            {
                await RevScene.LoadAsync(plan.TargetScene, progress);
            }
            else
            {
                await RevScene.LoadWhenReadyAsync(plan.TargetScene, progress, async () =>
                {
                    plan.Cancellation?.ThrowIfCancelled();
                    bool allowed = await plan.CanActivate();
                    return allowed && plan.Cancellation?.IsCancelled != true;
                }, -1, plan.ActivationTimeoutSeconds);
            }
            plan.Cancellation?.ThrowIfCancelled();
            if (!IsTarget(plan.TargetScene))
            {
                throw new InvalidOperationException("活动场景未交接到：" + plan.TargetScene);
            }
        }

        /// <summary>同时支持清单路径与场景名。路径声明按实际活动场景的完整路径验证。</summary>
        private static bool IsTarget(string target)
        {
            Scene active = SceneManager.GetActiveScene();
            return target.IndexOf('/') >= 0 ? active.path == target : active.name == target;
        }

        /// <summary>把阶段映射到稳定错误码，兼容现有场景验收。</summary>
        private static string FailureReason(RevSceneHandoffStage stage)
        {
            return stage switch
            {
                RevSceneHandoffStage.LoadingScene => "scene-load-failed",
                RevSceneHandoffStage.PreparingGameplay => "gameplay-prepare-failed",
                RevSceneHandoffStage.WaitingForGameplay => "gameplay-readiness-failed",
                RevSceneHandoffStage.CleaningUp => "cleanup-failed",
                _ => "handoff-failed"
            };
        }
    }
}

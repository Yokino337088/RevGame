// 通过真实 AB 检查 Revolution 域内所有权。只在开发态从空闲大厅执行。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.Core;
using RevGame.Flow;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    /// <summary>验证域退出后的资源、池实例和回调收尾。使用隔离域，不更改玩家存档或联机状态。</summary>
    internal static class GameDomainChecks
    {
        /// <summary>测试只会处理指定的沙箱域。</summary>
        private const string ProbeDomain = "RevGame.DomainProbe";
        /// <summary>实际 PvP 资源位于独立菜单 AB。</summary>
        private const string MenuRoot = "UI/Panel/Pvp/Menu";
        /// <summary>用于在途合并和父子域共享的真实 prefab。</summary>
        private const string SharedMenu = "PvpServerPanel";
        /// <summary>独立 AB 上的真实音效，未从大厅预加载。</summary>
        private const string SoundName = "Boss/Chapter01/Boss_Cleave";

        /// <summary>用于验证容器和业务扩展都能在退域自动调用 Dispose。</summary>
        private sealed class DisposalProbe : IDisposable
        {
            /// <summary>容器启用 eager 时创建本次独立释放样本。</summary>
            public DisposalProbe()
            {
            }

            /// <summary>已执行的释放次数，用于测试重复退出幂等。</summary>
            internal int Released { get; private set; }

            /// <summary>本夹具只记录实际进入 Dispose 的次数。</summary>
            public void Dispose()
            {
                Released++;
            }
        }

        /// <summary>由 RevMCP 主线程创建的单次跨帧验证，停止时通知等待方结束。</summary>
        private sealed class Operation : RevRuntimeOperation
        {
            /// <summary>工单取消令牌与被测域独立，收尾仍能运行。</summary>
            private readonly RevCancellationTokenSource _cancel = new RevCancellationTokenSource();
            /// <summary>成功或失败清理结束后置位，外层即可取回诊断。</summary>
            private bool _done;

            /// <summary>创建并开始运行一次验证。</summary>
            internal Operation()
            {
                Execute().Forget();
            }

            /// <summary>执行并把断言失败或取消返回给 RevMCP。</summary>
            private async RevTask Execute()
            {
                try
                {
                    Result = await Run(_cancel.Token);
                }
                catch (Exception error)
                {
                    Success = false;
                    Message = error.ToString();
                }
                finally
                {
                    _done = true;
                }
            }

            /// <summary>宿主轮询工单是否已结束。</summary>
            public override bool Tick()
            {
                return _done;
            }

            /// <summary>取消等待并保留 finally 中的资源收尾。</summary>
            public override void Cancel()
            {
                _cancel.Cancel();
                base.Cancel();
            }
        }

        /// <summary>挂载开发能力，生命周期跟随注册者。</summary>
        internal static void Register(object owner)
        {
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.scope-check", "用真实 AB 验证跨域共享、退域清理和交接预加载",
                empty, _ => new Operation(), owner: owner);
        }

        /// <summary>分三段验证退域、同名重入和失败结案。即使断言失败，也会归还测试自己的 preload。</summary>
        private static async RevTask<object> Run(RevCancellationToken token)
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            Check(flow.Screen == GameScreen.Home && !flow.IsTransitioning, "验收须由空闲大厅开始");
#if UNITY_EDITOR
            Check(RevResBootstrap.UseABInEditor, "验收须启用真实 AB");
#endif
            Check(!RevResManager.Get(MenuRoot, SharedMenu).IsLoaded, "共享菜单须从冷资源开始");

            RevResGroup group = RevResGroupUtil.Custom(227);
            RevScopeHandle lobby = RevScope.Current;
            RevResPreloadTask preload = null;
            RevSceneHandoffTracker tracker = RevScene.CreateHandoffTracker();
            RevSceneHandoff ticket = null;
            RevScopeHandle oldScope = RevScope.Enter(ProbeDomain, group);
            int callbacks = 0;
            int blocks = RevInput.BlockCount;
            var extension = oldScope.Own(new DisposalProbe());
            RevTimerHandle timer = RevTimer.After(60f, () => callbacks++);
            const string signal = "RevGame.DomainProbe.Event";
            RevEvent.AddEventListener(signal, () => callbacks++);
            RevInputBlock input = RevInput.Block();
            RevServices.Register<DisposalProbe, DisposalProbe>(eager: true);
            DisposalProbe service = RevServices.GetRequired<DisposalProbe>();
            try
            {
                RevSoundHandle sound = RevSound.Play(SoundName);
                Check(!sound.IsEmpty, "域内音效请求应被接受");
                await WaitFor(() => RevResManager.Get("Audio/Sfx", SoundName).IsLoaded, token);
                Check(BundleLoaded("audio_boss_chapter01"), "须实际读取测试音效的 AB");

                int staleCallbacks = 0;
                RevResHandle shared = RevResManager.LoadAsync(MenuRoot, SharedMenu, typeof(GameObject), _ => staleCallbacks++, group);
                preload = RevResPreloader.Preload(MenuRoot, SharedMenu, group);
                Check(shared.IsLoading && shared.RefCount == 2 && !preload.IsDone, "普通引用和预加载应合并到底层在途资源");

                oldScope.Close();
                Check(oldScope.Cancellation.IsCancelled && !timer.IsAlive && RevInput.BlockCount == blocks && !RevInput.Unblock(input) &&
                    !RevServices.IsRegistered<DisposalProbe>() && extension.Released == 1 && service.Released == 1 &&
                    RevEvent.DispatchEvent(signal) == 0 && callbacks == 0 && shared.RefCount == 1,
                    "退域须收掉计时器、输入、事件、服务和自有对象，保留正在预加载的一份引用");
                Check(RevResManager.GetRefCount("Audio/Sfx", SoundName) == 0, "域内音效的引用应在退域同步归零");

                double warmLoopMs;
                long warmLoopBytes;
                using (RevScopeHandle next = RevScope.Enter(ProbeDomain, group))
                {
                    await WaitFor(() => preload.IsDone && !RevResManager.Contains("Audio/Sfx", SoundName), token);
                    Check(preload.Failed == 0 && staleCallbacks == 0 && !BundleLoaded("audio_boss_chapter01") &&
                        BundleLoaded("ui_pvp") && shared.IsLoaded && next != oldScope,
                        "同名重入须隔离迟到回调，预加载继续完成，旧音效 AB 实际卸载");

                    Check(RevResManager.Load<GameObject>(MenuRoot, SharedMenu, group) == shared.Content as GameObject,
                        "预加载后的具体类型加载应复用同一资源");
                    GameObject parent = RevPool.Get(MenuRoot, SharedMenu);
                    Check(parent != null && shared.RefCount == 3, "父域资源、父域池、预加载各持有一份");
                    GameObject child;
                    using (RevScope.Enter("RevGame.DomainProbe.Child", group))
                    {
                        RevResManager.Load<GameObject>(MenuRoot, SharedMenu, group);
                        child = RevPool.Get(MenuRoot, SharedMenu);
                        Check(child != null && child != parent && shared.RefCount == 5, "相同分组和 prefab 应建立独立池和引用");
                    }
                    Check(parent.activeSelf && shared.RefCount == 3, "关子域应保留父域实际使用的对象和资源");
                    await WaitFor(() => child == null, token);

                    // 稳态路径始终保留原本已登记的一份引用。反复取还用相同路径，计量资源/域账本自身开销。
                    RevResManager.Load<GameObject>(MenuRoot, SharedMenu);
                    RevResManager.Release(MenuRoot, SharedMenu);
                    long startBytes = GC.GetAllocatedBytesForCurrentThread();
                    double startTime = Time.realtimeSinceStartupAsDouble;
                    for (int i = 0; i < 512; i++)
                    {
                        RevResManager.Load<GameObject>(MenuRoot, SharedMenu);
                        RevResManager.Release(MenuRoot, SharedMenu);
                    }
                    warmLoopMs = (Time.realtimeSinceStartupAsDouble - startTime) * 1000d;
                    warmLoopBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
                    Check(shared.RefCount == 3, "提前归还不能在退域时再重复扣减");
                }
                Check(shared.RefCount == 1 && BundleLoaded("ui_pvp"), "最后一个业务域退出后仍需保留独立预加载");

                ticket = tracker.TryBegin(RevScene.CurrentName, "ScopeProbe", "ScopeProbe");
                Check(ticket != null, "须创建验收独立交接");
                using (RevScope.Enter("RevGame.DomainProbe.Rollback", group))
                {
                    RevResHandle warmed = await ticket.Resources.PreloadAsync<GameObject>(MenuRoot, SharedMenu, "scope-check", true, group);
                    Check(warmed.IsLoaded && shared.RefCount == 2, "交接应独立持有自己的预热引用");
                }
                Check(shared.RefCount == 2, "发起域退出期间交接预热引用应保留");
                Check(tracker.TryComplete(ticket, RevSceneHandoffStatus.Failed, "ScopeProbe", true, "probe-failure") &&
                    shared.RefCount == 1, "失败结案应准确归还交接的一份引用");
                Check(RevScope.Current == lobby && !RevScope.LastCloseReport.HasFailure, "应回到原大厅域且回收无错误");

                return new Dictionary<string, object>
                {
                    ["pendingCallbackSuppressed"] = staleCallbacks == 0,
                    ["sharedPreloadSurvived"] = shared.RefCount == 1,
                    ["scopePoolsIsolated"] = true,
                    ["scopeControlsReleased"] = true,
                    ["scopedAudioBundleUnloaded"] = !BundleLoaded("audio_boss_chapter01"),
                    ["failedHandoffReleased"] = true,
                    ["warmLoadReleaseCount"] = 512,
                    ["warmLoadReleaseMs"] = warmLoopMs,
                    ["warmLoadReleaseBytes"] = warmLoopBytes
                };
            }
            finally
            {
                oldScope.Close();
                if (ticket != null && !ticket.IsCompleted)
                {
                    tracker.TryComplete(ticket, RevSceneHandoffStatus.Cancelled, "ScopeProbe", true, "probe-cancelled");
                }
                preload?.Cancel();
                RevResPreloader.Release(group);
            }
        }

        /// <summary>按可观测条件推进，不用固定睡眠猜测异步完成或帧末销毁。</summary>
        private static async RevTask WaitFor(Func<bool> condition, RevCancellationToken token)
        {
            double timeout = Time.realtimeSinceStartupAsDouble + 10d;
            while (!condition())
            {
                token.ThrowIfCancelled();
                Check(Time.realtimeSinceStartupAsDouble < timeout, "等待真实 AB 或实例收尾超时");
                await RevTaskScheduler.NextFrame();
            }
            token.ThrowIfCancelled();
        }

        /// <summary>读取 Unity 实际加载的 AssetBundle 名。</summary>
        private static bool BundleLoaded(string name)
        {
            return AssetBundle.GetAllLoadedAssetBundles().Any(bundle => bundle.name == name);
        }

        /// <summary>将业务后置条件失败写入自动化结果，并继续执行资源收尾。</summary>
        private static void Check(bool ok, string message)
        {
            if (!ok)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif

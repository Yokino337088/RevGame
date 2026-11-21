using System;
using RevGame.Core;
using RevGame.Endless;
using RevGame.Flow;
using RevGame.Pvp;
using Revolution;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.UI
{
    /// <summary>普通浏览器的大厅入口资源准备。由首页打开时登记，租约归本次大厅域。</summary>
    public sealed partial class HomePanel
    {
        /// <summary>大厅静置时优先准备的三个入口，按轻量设置、对战、无尽的成本顺序执行。</summary>
        private static readonly Type[] BrowserEntryPanels =
        {
            typeof(AudioSettingsPanel),
            typeof(PvpEntryPanel),
            typeof(EndlessEntryPanel)
        };

        /// <summary>同一个大厅域已经发过的预热任务，面板重开时只启动下一代域的任务。</summary>
        private RevScopeHandle _browserWarmupDomain;

        /// <summary>大厅装配时登记浏览器入口准备。前台点击仍沿用面板原有的加载和就绪条件。</summary>
        private void QueueBrowserEntryWarmup()
        {
#if !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            if (Application.platform != RuntimePlatform.WebGLPlayer)
            {
                return;
            }

            RevScopeHandle domain = RevScope.Get(GameDomain.Lobby);
            if (domain == null || !domain.IsOpen || ReferenceEquals(domain, _browserWarmupDomain))
            {
                return;
            }

            _browserWarmupDomain = domain;
            WarmBrowserEntriesAsync(domain, RevServices.GetRequired<GameFlow>()).Forget();
#endif
        }

        /// <summary>等大厅揭幕并获得可交互的一段空闲时间，再逐项读取资源。离开大厅后停止继续排队。</summary>
        private async RevTask WarmBrowserEntriesAsync(RevScopeHandle domain, GameFlow flow)
        {
            // OnOpen 发生在开场动画内。每次稍晚检查，给首帧、加载遮罩和玩家操作先让路。
            double readyDeadline = Time.realtimeSinceStartupAsDouble + 6;
            do
            {
                await RevTask.Delay(250);
                if (!domain.IsOpen || this == null || flow.Screen != GameScreen.Home)
                {
                    return;
                }
                if (Time.realtimeSinceStartupAsDouble >= readyDeadline)
                {
                    return;
                }
            }
            while (!CanWarmBrowserEntry(domain, flow));

            // 资源路径取自同一个 RevUI 元数据，用真实 AB 依赖和缓存合并前台打开与后台预热。
            foreach (Type panel in BrowserEntryPanels)
            {
                if (!CanWarmBrowserEntry(domain, flow))
                {
                    return;
                }
                if (RevUIPanelMeta.TryResolve(panel, out RevUIPanelMeta meta, out _))
                {
                    await HoldBrowserEntryAsync(domain, meta.Root, meta.Name, typeof(GameObject), RevResGroup.UI);
                    await RevTask.Yield();
                }
            }

            // 无尽入口还会显式等待图集对象。先借一份普通资源租约，真正打开时再由图集服务注册像素。
            if (CanWarmBrowserEntry(domain, flow))
            {
                await HoldBrowserEntryAsync(domain, RevResPath.Atlases, "EndlessMenu", typeof(SpriteAtlas), RevResGroup.Scene);
            }
        }

        /// <summary>只有首页可交互且没有覆盖或前台流程时，才允许发出下一项可选请求。</summary>
        private bool CanWarmBrowserEntry(RevScopeHandle domain, GameFlow flow)
        {
            return domain.IsOpen && this != null && State == RevUIPanelState.Opened
                && !IsCovered && flow.Screen == GameScreen.Home && !flow.IsGameplayBlocked;
        }

        /// <summary>为本代大厅保留一份资源。成功留到退域，失败提前归还，供正式入口再次尝试。</summary>
        private static async RevTask HoldBrowserEntryAsync(RevScopeHandle domain, string root, string name, Type type, RevResGroup group)
        {
            // 先登记在途租约再让帧。离开时由原域归还，前台若已合并同一请求仍持有自己的引用。
            RevResLease lease = domain.Own(RevResManager.LoadOwnedAsync(root, name, type, nameof(HomePanel), group: group));
            while (domain.IsOpen && lease.Handle.IsLoading)
            {
                await RevTask.Yield();
            }

            if (domain.IsOpen && !lease.Handle.IsLoaded && domain.Disown(lease))
            {
                lease.Dispose();
            }
        }
    }
}

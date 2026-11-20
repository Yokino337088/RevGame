#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.Core;
using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>通过正式大厅和 PvP 菜单验证面板缓存。仅开发态注册，不触发房间联机。</summary>
    public static partial class PvpAutomation
    {
        /// <summary>三个实际 PvP 子菜单共同的预制体逻辑目录。</summary>
        private const string CacheMenuRoot = "UI/Panel/Pvp/Menu";

        /// <summary>自动化单次工单的批次参数。整组回归可连续执行多个批次。</summary>
        [Serializable]
        private sealed class UiCacheArgs
        {
            /// <summary>本批执行的大厅进出轮数，取 1 到 5。</summary>
            public int cycles;
            /// <summary>在本批第一轮增加异域、转场中退域与低内存专项。</summary>
            public bool boundaries;
        }

        /// <summary>注册真实 AB 菜单循环，在 Unity 主线程执行，结果保留在工单回执。</summary>
        private static void RegisterUiCacheCheck()
        {
            const string schema = "{\"type\":\"object\",\"properties\":{\"cycles\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5},\"boundaries\":{\"type\":\"boolean\"}},\"required\":[\"cycles\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.ui-cache-check", "循环真实 AB 大厅和 PvP，验证同域复用、LRU、退域和低内存",
                schema, json => new UiScaleOperation(token => RunUiCacheCheck(JsonUtility.FromJson<UiCacheArgs>(json), token)),
                owner: Owner);
        }

        /// <summary>从空闲大厅接手，保存原全局额度；成功、失败和取消都恢复面板额度并返回大厅。</summary>
        private static async RevTask<object> RunUiCacheCheck(UiCacheArgs args, RevCancellationToken token)
        {
            RequireUi(args.cycles >= 1 && args.cycles <= 5, "一次缓存回归需要 1 至 5 轮");
#if UNITY_EDITOR
            RequireUi(RevResBootstrap.UseABInEditor, "缓存回归需要启用真实 AB");
#endif
            RequireUi(Flow.Screen == GameScreen.Home && !Flow.IsTransitioning && RevUI.IsOpen<HomePanel>(),
                "请从空闲且已显示的大厅开始缓存回归");
            RequireNoPvpMenuRefs();
            int originalLimit = RevUISetting.MaxIdlePanels;
            var rounds = new List<Dictionary<string, object>>(args.cycles);
            try
            {
                // 一个空闲额度即可用三个正式菜单制造稳定的 LRU 压力，期间共享图集始终由宿主持有。
                RevUISetting.MaxIdlePanels = 1;
                for (int i = 0; i < args.cycles; i++)
                {
                    token.ThrowIfCancelled();
                    rounds.Add(await RunUiCacheRound(i + 1, args.boundaries && i == 0, token));
                }
            }
            finally
            {
                RevUISetting.MaxIdlePanels = originalLimit;
                await WaitCacheFlowIdle(default);
                if (Flow.Screen == GameScreen.Pvp)
                {
                    RequireUi(Flow.ReturnHome(), "缓存回归清理应接受返回大厅请求");
                    await WaitCacheFlowIdle(default);
                }
                RequireUi(Flow.Screen == GameScreen.Home && !RevScope.IsActive(GameDomain.Pvp), "缓存回归应收尾到大厅");
            }

            return new Dictionary<string, object>
            {
                ["cycles"] = rounds.Count,
                ["realAb"] = true,
                ["boundaries"] = args.boundaries,
                ["restoredLimit"] = RevUISetting.MaxIdlePanels,
                ["rounds"] = rounds
            };
        }

        /// <summary>同一轮中比较 Server 冷开和同域热开，再检查按顺序裁剪和跨域清理。</summary>
        private static async RevTask<Dictionary<string, object>> RunUiCacheRound(int round, bool boundaries, RevCancellationToken token)
        {
            RequireUi(Flow.EnterPvp(), "大厅应接受正式 PvP 入场请求");
            await WaitCacheFlowIdle(token);
            PvpPanel host = RevUI.Get<PvpPanel>();
            PvpEntryPanel entry = RevUI.Get<PvpEntryPanel>();
            RevScopeHandle scope = RevScope.Current;
            RequireUi(Flow.Screen == GameScreen.Pvp && host != null && host.Menu == "Entry" && entry != null &&
                scope?.Name == GameDomain.Pvp && IsUiBundleLoaded("ui_pvp") && Service.Core == null && !Service.Connected,
                "本轮应装配真实 PvP 域及 AB 入口，并保持未开局未联网");

            int entryId = entry.GetInstanceID();
            SpriteAtlas menus = RequireSharedAtlases();
            RevUICacheSnapshot start = RevUI.GetCacheSnapshot();
            RequireUi(!RevResManager.Get(CacheMenuRoot, nameof(PvpServerPanel)).IsLoaded, "Server 预制体应冷读 AB");
            double cold = await OpenCacheMenu(host, "Server", token);
            PvpServerPanel server = RevUI.Get<PvpServerPanel>();
            int serverId = server.GetInstanceID();
            RequireUi(IdleCachePanel(entryId, scope.Name), "离开入口页后实例和租约应进入原域缓存");

            await OpenCacheMenu(host, "Entry", token);
            RequireUi(RevUI.Get<PvpEntryPanel>() == entry && IdleCachePanel(serverId, scope.Name), "同域重开入口应取回原实例");
            double warm = await OpenCacheMenu(host, "Server", token);
            RequireUi(RevUI.Get<PvpServerPanel>() == server && RevUI.GetCacheSnapshot().Hits - start.Hits == 2,
                "连续同域重开应命中 Entry 和 Server 两份原实例");

            await OpenCacheMenu(host, "Lan", token);
            PvpLanPanel lan = RevUI.Get<PvpLanPanel>();
            int lanId = lan.GetInstanceID();
            RequireUi(IdleCachePanel(serverId, scope.Name) && CacheReleased(entryId, "capacity", true) &&
                RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpEntryPanel)) == 0,
                "满额时应淘汰最久未用的 Entry，并保留刚关闭的 Server");
            await WaitCachePanelDestroyed(entry, token);

            await OpenCacheMenu(host, "Entry", token);
            RequireUi(RevUI.Get<PvpEntryPanel>().GetInstanceID() != entryId &&
                IdleCachePanel(lanId, scope.Name) && CacheReleased(serverId, "capacity", true),
                "淘汰后的 Entry 应重新装配，下一次裁剪应由 Server 让位给 Lan");

            if (boundaries)
            {
                await CheckUiCacheBoundaries(host, lan, menus, token);
            }

            // 退出前需要有一份空闲面板。正式切场负责归还租约和调用 FlushUnused。
            RevUICacheEntry aboutToRetire = RevUI.GetCacheSnapshot().Idle.Single(x => x.Scope == scope.Name);
            RequireUi(Flow.ReturnHome(), "PvP 应接受正式返回大厅请求");
            await WaitCacheFlowIdle(token);
            RequireUi(Flow.Screen == GameScreen.Home && !scope.IsOpen && host == null &&
                CacheReleased(aboutToRetire.InstanceId, "scope-exit", true) &&
                !RevUI.GetCacheSnapshot().Idle.Any(x => x.Scope == scope.Name), "退域应销毁在用面板及空闲实例");
            RequireNoPvpMenuRefs();
            RequireUi(!IsUiBundleLoaded("ui_pvp"), "返回大厅后专用菜单 AB 应卸载");
            RequireUi(RequireSharedAtlases() == menus && HomeHasPackedImage(), "大厅应继续正常使用原共享图集");

            return new Dictionary<string, object>
            {
                ["round"] = round,
                ["coldMs"] = Math.Round(cold, 3),
                ["warmMs"] = Math.Round(warm, 3),
                ["scopeReleased"] = true,
                ["pvpBundleUnloaded"] = true,
                ["sharedPacked"] = true
            };
        }

        /// <summary>在真实菜单上核对异域销毁、动画中退域、无域关闭和共享资源低内存保护。</summary>
        private static async RevTask CheckUiCacheBoundaries(PvpPanel host, PvpLanPanel previousLan, SpriteAtlas menus,
            RevCancellationToken token)
        {
            int previousLanId = previousLan.GetInstanceID();
            int closingId;
            int nestedLanId;
            HomePanel closing;
            using (RevScopeHandle nested = RevScope.Enter("PvpUiCacheCheck"))
            {
                RequireUi(nested.IsOpen, "专项嵌套域应成功开启");
                await OpenCacheMenu(host, "Lan", token);
                nestedLanId = RevUI.Get<PvpLanPanel>().GetInstanceID();
                RequireUi(nestedLanId != previousLanId && CacheReleased(previousLanId, "scope-mismatch", true),
                    "异域请求应销毁旧实例并取得独立实例");

                // 大厅面板具有真实的关闭动画。用同一帧退域确定资源回收不需要等待动画回调。
                closing = await RevUI.OpenAsync<HomePanel>();
                token.ThrowIfCancelled();
                RequireUi(closing != null, "关闭动画专项应打开真实面板");
                closingId = closing.GetInstanceID();
                RequireUi(RevUI.Close(closing) && closing.State == RevUIPanelState.Closing, "退域前面板确实正在播关闭动画");
            }
            RequireUi(CacheReleased(closingId, "scope-exit", false) && CacheReleased(nestedLanId, "scope-exit", false) &&
                RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpLanPanel)) == 0 && RevUI.Get<PvpPanel>() == host,
                "嵌套域退出应同步处理关闭中和活动面板，外层宿主仍可用");
            await WaitCachePanelDestroyed(closing, token);
            await WaitCachePanelDestroyed(previousLan, token);

            await OpenCacheMenu(host, "Server", token);
            PvpServerPanel detached = RevUI.Get<PvpServerPanel>();
            int detachedId = detached.GetInstanceID();
            // 与跨场景遮挡同样解绑。它可以继续显示，但明确关闭时必须归还自己的租约。
            RevScope.Detach(detached);
            RequireUi(RevUI.Close(detached) && CacheReleased(detachedId, "unscoped", false) &&
                RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpServerPanel)) == 0, "无归属面板关闭后不能缓存");

            await OpenCacheMenu(host, "Entry", token);
            PvpEntryPanel active = RevUI.Get<PvpEntryPanel>();
            await OpenCacheMenu(host, "Lan", token);
            await OpenCacheMenu(host, "Entry", token);
            int idleId = RevUI.GetCacheSnapshot().Idle.Single().InstanceId;
            // 手动走 Unity lowMemory 同一入口，同时保留真实活动页面的 AB。
            RevResAutoUnloader.RunOnce(force: true);
            RequireUi(RevUI.GetCacheSnapshot().Idle.Count == 0 && CacheReleased(idleId, "low-memory", true) &&
                RevUI.Get<PvpEntryPanel>() == active && active.isActiveAndEnabled && RequireSharedAtlases() == menus,
                "低内存应归还所有空闲实例，保留活动页和共享图集");
            await OpenCacheMenu(host, "Server", token); // 留一份真实空闲实例给正式退场验收
        }

        /// <summary>调用业务菜单切换直至首屏交付，返回该操作端到端壁钟毫秒数。</summary>
        private static async RevTask<double> OpenCacheMenu(PvpPanel host, string destination, RevCancellationToken token)
        {
            token.ThrowIfCancelled();
            double startedAt = Time.realtimeSinceStartupAsDouble;
            bool opened = await host.ShowDestinationAsync(destination);
            double elapsedMs = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000d;
            token.ThrowIfCancelled();
            RequireUi(opened && host.Menu == destination && host.MenuTicket?.FirstScreenReady == true, "未交付目标菜单 " + destination);
            return elapsedMs;
        }

        /// <summary>按实际流程屏障等状态稳定。清理用默认令牌仍保留 30 秒上限。</summary>
        private static async RevTask WaitCacheFlowIdle(RevCancellationToken token)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 30d;
            while (Flow.IsTransitioning)
            {
                token?.ThrowIfCancelled();
                RequireUi(Time.realtimeSinceStartupAsDouble < deadline, "场景交接未在期限内结束");
                await RevTaskScheduler.NextFrame();
            }
            token?.ThrowIfCancelled();
        }

        /// <summary>等待 Unity 帧末确认销毁。调用发生在 Update 前半段时，一次续体可能仍在同一帧。</summary>
        private static async RevTask WaitCachePanelDestroyed(RevUIPanel panel, RevCancellationToken token)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 2d;
            while (panel != null)
            {
                token.ThrowIfCancelled();
                RequireUi(!panel.gameObject.activeSelf && Time.realtimeSinceStartupAsDouble < deadline,
                    "处置面板应立即停用并在期限内实际销毁");
                await RevTaskScheduler.NextFrame();
            }
        }

        /// <summary>从当前只读快照确认缓存上限、所属域和实例租约。</summary>
        private static bool IdleCachePanel(int id, string scope)
        {
            RevUICacheSnapshot cache = RevUI.GetCacheSnapshot();
            return cache.Idle.Count == 1 && cache.Idle.Any(x => x.InstanceId == id && x.Scope == scope &&
                x.Reason == "keep-alive" && x.PrefabRefCount > 0 && !x.LeaseReleased && x.OpenId != Guid.Empty);
        }

        /// <summary>按实例、原因和入池状态核对框架已归还租约。</summary>
        private static bool CacheReleased(int id, string reason, bool wasIdle)
        {
            return RevUI.GetCacheSnapshot().RecentReleases.Any(x =>
                x.InstanceId == id && x.Reason == reason && x.WasIdle == wasIdle && x.LeaseReleased);
        }

        /// <summary>确认三个 PvP 专用菜单的实际引用都已归还。</summary>
        private static void RequireNoPvpMenuRefs()
        {
            RequireUi(RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpEntryPanel)) == 0 &&
                RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpLanPanel)) == 0 &&
                RevResManager.GetRefCount(CacheMenuRoot, nameof(PvpServerPanel)) == 0, "离开 PvP 后专用菜单仍持有资源");
        }

        /// <summary>按资源清单的包名核对 Unity 加载器中的真实 AB。</summary>
        private static bool IsUiBundleLoaded(string name)
        {
            return AssetBundle.GetAllLoadedAssetBundles().Any(bundle => bundle.name == name);
        }

        /// <summary>校验大厅和 PvP 共用的两张图集仍被持有，并返回菜单图集作实例身份对比。</summary>
        private static SpriteAtlas RequireSharedAtlases()
        {
            RevResHandle menus = RevResManager.Get(RevResPath.Atlases, "UIMenus");
            RevResHandle battle = RevResManager.Get(RevResPath.Atlases, "UIBattle");
            RequireUi(menus.IsLoaded && menus.Content is SpriteAtlas && menus.RefCount > 0 && IsUiBundleLoaded("atlas_uimenus") &&
                battle.IsLoaded && battle.Content is SpriteAtlas && battle.RefCount > 0 && IsUiBundleLoaded("atlas_uibattle"),
                "共享菜单和战斗图集应保留真实 AB 与持有引用");
            return (SpriteAtlas)menus.Content;
        }

        /// <summary>回到大厅后核对实际显示的 Image 可用且使用打包的 Sprite。</summary>
        private static bool HomeHasPackedImage()
        {
            HomePanel home = RevUI.Get<HomePanel>();
            return home != null && home.isActiveAndEnabled && home.GetComponentsInChildren<Image>().Any(image =>
                image.sprite != null && image.sprite.packed && image.sprite.texture != null);
        }
    }
}
#endif

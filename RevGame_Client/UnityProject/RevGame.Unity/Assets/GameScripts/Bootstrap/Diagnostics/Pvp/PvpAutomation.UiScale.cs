#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>在真实 PvP 界面上验证开窗、列表和弹窗调度。合成数据不发出网络请求。</summary>
    public static partial class PvpAutomation
    {
        /// <summary>只用于真实资源查找失败的候选，沿用现有 PvP 互斥组。</summary>
        [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Popup, "RevUIMissingMenuForCheck", ExclusiveGroup = "PvpMenu")]
        private sealed class MissingMenuForCheck : RevUIPanel
        {
            /// <summary>如果工程误导入了同名 Prefab，也不执行业务。</summary>
            protected override void OnBindView()
            {
            }
        }

        /// <summary>用一个普通 RevTask 驱动跨帧工单，Cancel 使等待立即收尾。</summary>
        private sealed class UiScaleOperation : RevRuntimeOperation
        {
            /// <summary>本轮验收自己的取消源。</summary>
            private readonly RevCancellationTokenSource _cancel = new RevCancellationTokenSource();
            /// <summary>已完成正常结果或异常清理。</summary>
            private bool _done;

            /// <summary>主线程开始一次确定的验收或列表采样。</summary>
            internal UiScaleOperation(Func<RevCancellationToken, RevTask<object>> run)
            {
                RunAsync(run).Forget();
            }

            /// <summary>把开发态断言异常转成失败回执，避免返回虚假的成功。</summary>
            private async RevTask RunAsync(Func<RevCancellationToken, RevTask<object>> run)
            {
                try
                {
                    Result = await run(_cancel.Token);
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

            /// <summary>当具体验收完成后让宿主发布回执。</summary>
            public override bool Tick()
            {
                return _done;
            }

            /// <summary>联动操作自己的等待边界。</summary>
            public override void Cancel()
            {
                _cancel.Cancel();
                base.Cancel();
            }
        }

        /// <summary>用于验证行已解绑的原始绑定，不会用于业务选择。</summary>
        private static RevUIVirtualRow _initialListRow;
        /// <summary>原始行开始验证时的绑定代次。</summary>
        private static int _initialListVersion;
        /// <summary>原始行离开视口后应被取消的令牌。</summary>
        private static RevCancellationToken _initialListToken;
        /// <summary>这一轮弹窗验收的终态。</summary>
        private static readonly Dictionary<string, object> PopupResults = new Dictionary<string, object>();
        /// <summary>只有实际点击确认才能增加，验证拒绝和取消无副作用。</summary>
        private static int _popupConfirmed;

        /// <summary>限定可控制的夹具阶段，并提供独立的只读查询。</summary>
        private static void RegisterUiScale()
        {
            RevRuntimeAutomation.Register("pvp.ui-handoff-check", "真实 AB 检查开窗交割、失败、快切及关闭后回包", Empty,
                _ => new UiScaleOperation(RunHandoffCheck), owner: Owner);
            const string listSchema = "{\"type\":\"object\",\"properties\":{\"step\":{\"type\":\"string\",\"enum\":[\"load\",\"scroll\",\"reorder\"]}},\"required\":[\"step\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.ui-list-fixture", "同一局域网列表验证固定千行、滚动、重排与解绑", listSchema,
                json => RunListStep(JsonUtility.FromJson<UiScaleArgs>(json).step), owner: Owner);
            RevRuntimeAutomation.Register("pvp.ui-list-compare", "真实视口对比全量行和虚拟列表的固定滚动负载", Empty,
                _ => new UiScaleOperation(token => RequireLan().CompareListForCheck(token)), owner: Owner);
            const string popupSchema = "{\"type\":\"object\",\"properties\":{\"step\":{\"type\":\"string\",\"enum\":[\"start\",\"exit\"]}},\"required\":[\"step\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.ui-popup-fixture", "驱动当前真实确认框，验收优先级、阶段、去重与回收", popupSchema,
                json => RunPopupStep(JsonUtility.FromJson<UiScaleArgs>(json).step), owner: Owner);
            RevRuntimeAutomation.Register("pvp.ui-popup-state", "读当前实际确认窗口及已完成结果", Empty,
                _ => PopupState(), readOnly: true, owner: Owner);
        }

        /// <summary>只允许调用方选择一个已定义的阶段。</summary>
        [Serializable]
        private sealed class UiScaleArgs
        {
            /// <summary>夹具推进到的固定阶段名。</summary>
            public string step;
        }

        /// <summary>从已有真实菜单出发验证失败、撤销、冷开和快速切换。</summary>
        private static async RevTask<object> RunHandoffCheck(RevCancellationToken token)
        {
            PvpPanel panel = RevUI.Get<PvpPanel>();
            PvpEntryPanel entry = RevUI.Get<PvpEntryPanel>();
            bool abMode = true;
#if UNITY_EDITOR
            abMode = RevResBootstrap.UseABInEditor;
#endif
            RequireUi(abMode && panel != null && entry != null && panel.Menu == "Entry", "需要真实 AB 下已就绪的入口页");
            Button enterServer = entry.transform.Find("server").GetComponent<Button>();
            const string menuRoot = "UI/Panel/Pvp/Menu";
            try
            {
                RequireUi(!RevResManager.Get(menuRoot, nameof(PvpServerPanel)).IsLoaded, "服务器菜单以冷资源开始");
                RevUIOpenTicket missing = RevUI.OpenTracked<MissingMenuForCheck>();
                RequireUi(RevUI.Get<PvpEntryPanel>() == entry && enterServer.IsInteractable(), "等待失败资源时旧页还能点击");
                await AwaitTicket(missing, token);
                RequireUi(missing.Status == RevUIOpenStatus.Failed && missing.Reason == "prefab-unavailable"
                    && RevUI.Get<PvpEntryPanel>() == entry && enterServer.IsInteractable(), "缺资源后保留原页和交互");

                int baseline = RevResManager.GetRefCount(menuRoot, nameof(PvpLanPanel));
                RevUIOpenTicket cancelled = RevUI.OpenTracked<PvpLanPanel>(deferAssembly: true);
                RequireUi(cancelled.Status == RevUIOpenStatus.Pending && RevUI.Close<PvpLanPanel>(), "用真实关闭入口取消冷加载");
                RequireUi(cancelled.Status == RevUIOpenStatus.Cancelled && cancelled.ResourceDisposition == "Released", "取消即归还本次引用");
                double deadline = Time.realtimeSinceStartupAsDouble + 12;
                while (RevResManager.Get(menuRoot, nameof(PvpLanPanel)).IsLoading)
                {
                    token.ThrowIfCancelled();
                    RequireUi(Time.realtimeSinceStartupAsDouble < deadline, "等待真实资源任务结束");
                    await RevTaskScheduler.NextFrame();
                }
                await RevTaskScheduler.NextFrame();
                RequireUi(!RevUI.IsOpen<PvpLanPanel>() && RevResManager.GetRefCount(menuRoot, nameof(PvpLanPanel)) == baseline
                    && RevUI.Get<PvpEntryPanel>() == entry, "关闭后完成的资源任务没有打开候选或增加引用");

                panel.ShowDestination("Server");
                RevUIOpenTicket cold = panel.MenuTicket;
                RequireUi(cold != null && cold.Status == RevUIOpenStatus.Pending && enterServer.IsInteractable(), "冷资源接单时旧输入仍有效");
                RequireUi(RevUI.OpenTracked<PvpServerPanel>().Id == cold.Id, "同键在途调用合并到同一个票据");
                await AwaitTicket(cold, token);
                RequireUi(cold.Status == RevUIOpenStatus.Succeeded && cold.FirstScreenReady && cold.PreviousPanelKey.EndsWith("PvpEntryPanel")
                    && cold.PreviousDisposition == "Pooled", "服务器页就绪后才回收旧页");
                RequireUi(panel.Menu == "Server" && RevUI.IsOpen<PvpServerPanel>() && !entry.gameObject.activeInHierarchy,
                    "公开状态和实际面板已切换");

                RevUIOpenTicket stale = RevUI.OpenTracked<PvpLanPanel>(deferAssembly: true);
                panel.ShowDestination("Entry");
                RevUIOpenTicket latest = panel.MenuTicket;
                await AwaitTicket(latest, token);
                RequireUi(stale.Status == RevUIOpenStatus.Superseded && stale.ResourceDisposition == "Released"
                    && latest.Status == RevUIOpenStatus.Succeeded && RevUI.Get<PvpEntryPanel>() == entry
                    && !RevUI.IsOpen<PvpLanPanel>(), "快速切页只有最后请求得到交付");

                PvpLanPanel.RejectNextFirstScreenForCheck();
                RevUIOpenTicket rejected = RevUI.OpenTracked<PvpLanPanel>(deferAssembly: true);
                await AwaitTicket(rejected, token);
                RequireUi(rejected.Status == RevUIOpenStatus.Failed && rejected.Reason == "first-screen-rejected"
                    && rejected.ResourceDisposition == "Released" && RevUI.Get<PvpEntryPanel>() == entry && enterServer.IsInteractable(),
                    "已装配候选拒绝首屏后归还资源，旧页仍可操作");

                RevUIOpenTicket retired;
                using (RevScopeHandle testScope = RevScope.Enter("PvpUiHandoffCheck"))
                {
                    RequireUi(testScope.IsOpen, "作用域样本已打开");
                    retired = RevUI.OpenTracked<PvpLanPanel>(deferAssembly: true);
                    RequireUi(retired.Status == RevUIOpenStatus.Pending, "候选属于提交时所在的域");
                }
                RequireUi(retired.Status == RevUIOpenStatus.Cancelled && retired.Reason == "scope-closed",
                    "域退出后取消原请求");
                RequireUi(RevUI.TryGetOpen(cold.Id, out RevUIOpenTicket recorded) && ReferenceEquals(recorded, cold), "按 ID 查询原始开窗");
                return new Dictionary<string, object>
                {
                    ["passed"] = true, ["coldId"] = cold.Id.ToString(), ["failedId"] = missing.Id.ToString(),
                    ["cancelledId"] = cancelled.Id.ToString(), ["supersededId"] = stale.Id.ToString(), ["scopeId"] = retired.Id.ToString(),
                    ["rejectedId"] = rejected.Id.ToString()
                };
            }
            finally
            {
                PvpLanPanel.ClearFirstScreenCheck();
                RevUI.Close<MissingMenuForCheck>();
                if (Flow.Screen == RevGame.Core.GameScreen.Pvp && panel != null && panel.IsOpened)
                {
                    panel.ShowDestination("Entry");
                }
            }
        }

        /// <summary>在测试工单期限内等待交付，取消时停止观察。</summary>
        private static async RevTask AwaitTicket(RevUIOpenTicket ticket, RevCancellationToken token)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (ticket.Status == RevUIOpenStatus.Pending)
            {
                token.ThrowIfCancelled();
                RequireUi(Time.realtimeSinceStartupAsDouble < deadline, "开窗任务在期限内交付");
                await RevTaskScheduler.NextFrame();
            }
            await RevTaskScheduler.NextFrame();
        }

        /// <summary>保证测试对象来自当前 PvP 菜单，避免在真实搜索或对局期间注入显示数据。</summary>
        private static PvpLanPanel RequireLan()
        {
            PvpLanPanel lan = RevUI.Get<PvpLanPanel>();
            RequireUi(lan != null && lan.IsOpened && Service.Lan == null && Service.Core == null, "需要空闲的局域网菜单");
            return lan;
        }

        /// <summary>使用实际列表绑定和滚动链路验证行身份、容量与取消。</summary>
        private static object RunListStep(string step)
        {
            PvpLanPanel lan = RequireLan();
            if (step == "load")
            {
                lan.LoadListFixture();
                _initialListRow = lan.GetFirstFixtureRow();
                _initialListVersion = _initialListRow.BindingVersion;
                _initialListToken = _initialListRow.GetCancellationToken();
            }
            else if (step == "scroll")
            {
                lan.ScrollForCheck();
                lan.MoveListFixture(.25f);
                RequireUi(_initialListRow != null && !_initialListRow.IsCurrent(_initialListVersion) && _initialListToken.IsCancelled,
                    "旧行的事件与异步绑定在滚出视口时已失效");
            }
            else
            {
                lan.ReorderListFixture();
            }
            return lan.ScrollStateForCheck();
        }

        /// <summary>向真实活动棋盘提交固定优先级与阶段的确认项，无棋谱副作用。</summary>
        private static object RunPopupStep(string step)
        {
            PvpPanel panel = RevUI.Get<PvpPanel>();
            RequireUi(panel != null && panel.PopupCheckReady && Service.Mode == PvpConnectionMode.Local, "确认测试需要本地棋盘且当前无窗口");
            if (step == "start")
            {
                PopupResults.Clear();
                _popupConfirmed = 0;
                ObservePopup(panel, "low", "low", 1, "Battle", 12).Forget();
                ObservePopup(panel, "high", "high", 9, "Battle", 12).Forget();
                ObservePopup(panel, "duplicate", "high", 0, "Battle", 12).Forget();
                ObservePopup(panel, "expired", "for-other-phase", 99, "InactivePhase", .1).Forget();
            }
            else
            {
                ObservePopup(panel, "exit-active", "exit-active", 9, "Battle", 12).Forget();
                ObservePopup(panel, "exit-waiting", "exit-waiting", 0, "InactivePhase", 12).Forget();
            }
            return PopupState();
        }

        /// <summary>记录真实控件完成的结果和确认次数，保留到返回大厅后才做清理断言。</summary>
        private static async RevTask ObservePopup(PvpPanel panel, string resultKey, string key, int priority, string phase, double timeout)
        {
            PopupResults[resultKey] = "Pending";
            RevPopupStatus status = await panel.ShowPopupForCheck(key, priority, phase, timeout, () => _popupConfirmed++);
            PopupResults[resultKey] = status.ToString();
        }

        /// <summary>只读窗口身份、文字、已完成票据与确认次数。</summary>
        private static object PopupState()
        {
            return new Dictionary<string, object>
            {
                ["view"] = RevUI.Get<PvpPanel>()?.PopupViewForCheck(),
                ["confirmed"] = _popupConfirmed,
                ["results"] = PopupResults
            };
        }

        /// <summary>关闭本轮夹具持有的对象引用和结果记录。</summary>
        private static void ClearUiScaleChecks()
        {
            _initialListRow = null;
            _initialListToken = null;
            PopupResults.Clear();
            _popupConfirmed = 0;
        }

        /// <summary>验收只在业务可观测值成立时继续。</summary>
        private static void RequireUi(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif

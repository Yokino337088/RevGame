// ============================================================
// RevUIManager.cs —— UI 管理器（打开/关闭/层级/池化/返回栈）
//
// 位置：Runtime\RevUISystem\Implementation\
//
// 【业务不要直接用这个类】用 Facade 里的 RevUI（同目录上一级）——
//   那是唯一入口，门面只做转发，将来换实现不用改业务代码。
//
// 【找实例的顺序：从快到慢】
//     ① 已在打开中  → 把请求并进在途那次（**同一面板并发打开只会加载/创建一次**）
//     ② 已打开      → 直接复用（置顶 + 按最新数据重画）
//     ③ 实例池里有  → 取出复用（不加载、不实例化、不重新绑定 —— 最快的一条路）
//     ④ 都没有      → 异步加载预制体 → 实例化 → 装配 → 打开
//   两次同时打开 BagPanel 共用同一个在途请求，
//   只会产生一个实例，两份回调都会在打开完成时收到同一个面板。
//
// 【关闭只做三件事】立刻从索引摘掉（后续查询/返回栈不再看到它）→ 播放关闭转场 →
//   回池（KeepAlive）或延迟一帧销毁（DestroyOnClose / 池满）。转场是异步的，
//   所以"关闭中"这个状态必须存在：这期间再点开同一个面板，会走"新建/取池"而不是复用那个正在关闭的实例。
//
// 【它不管的事】
//   · 不写任何文件、不生成任何代码（没有代码生成流程）；
//   · 不做动画（转场由面板的 PlayOpenTransition/PlayCloseTransition 决定，框架不依赖缓动库）；
//   · 不做"UI 数据"（数据从哪来是业务的事：面板自己去取，或调用方 SetData）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Revolution
{
    /// <summary>UI 管理器：面板的打开 / 关闭 / 层级 / 池化 / 返回栈 / 诊断</summary>
    public sealed partial class RevUIManager : RevSingleton<RevUIManager>
    {
        // 单例由 RevSingleton 负责创建（不需要外部 new）
        private RevUIManager() { }

        // ============================================================
        // 索引
        // ============================================================

        /// <summary>面板键 → 已打开的面板</summary>
        private readonly Dictionary<string, RevUIPanel> _opened = new Dictionary<string, RevUIPanel>(StringComparer.Ordinal);

        /// <summary>面板键 → 还在等这次加载结果的回调（在途合并）</summary>
        private readonly Dictionary<string, OpenRequest> _loading =
            new Dictionary<string, OpenRequest>(StringComparer.Ordinal);

        /// <summary>请求身份覆盖资源等待、首屏准备和入场转场。旧回调只操作这次租约。</summary>
        private sealed class OpenRequest
        {
            /// <summary>目标类型、资源键和互斥组。</summary>
            internal RevUIPanelMeta Meta;
            /// <summary>同键合并请求最近一次有效数据。</summary>
            internal object Data;
            /// <summary>等待本次打开结果的普通 API 调用方。</summary>
            internal readonly List<Action<RevUIPanel>> Callbacks = new List<Action<RevUIPanel>>();
            /// <summary>由调用时的域持有，异步完成时不改挂到当时顶层。</summary>
            internal RevScopeHandle Scope;
            /// <summary>公开的开窗时间线和终态。</summary>
            internal RevUIOpenTicket Ticket;
            /// <summary>预备中或已交割但仍在播放入场的实例。</summary>
            internal RevUIPanel Panel;
            /// <summary>尚未交给面板的预制体引用。</summary>
            internal RevResLease Resource;
            /// <summary>首屏预备期间的透明、不响应输入的容器。</summary>
            internal RectTransform Staging;
            /// <summary>候选页或实时打开的面板是否已加入公开索引。</summary>
            internal bool Published;
            /// <summary>首屏等待和 Part 加载用来识别过期请求。</summary>
            internal readonly RevCancellationTokenSource Cancellation = new RevCancellationTokenSource();
        }

        /// <summary>主线程待装配的请求；每帧按数量和时间预算消费。</summary>
        private readonly Queue<OpenRequest> _assemblies = new Queue<OpenRequest>();
        /// <summary>按 ID 读到正在打开及最近完成的值类型证据。</summary>
        private readonly Dictionary<Guid, RevUIOpenTicket> _openTickets = new Dictionary<Guid, RevUIOpenTicket>();
        /// <summary>仅保留最近完成的 32 个请求，未完成请求不参与淘汰。</summary>
        private readonly Queue<Guid> _finishedTickets = new Queue<Guid>();

        // 面板实例各持有一份 prefab 资源引用；只保存路径与 instance ID，
        // 即使 Unity 对象在新会话已变成假 null，也能准确归还旧引用。
        // 关闭转场另用同一租约跟踪所有权转移，避免 Shutdown 与晚到回调重复释放。

        /// <summary>跟随面板实例的具名 prefab 租约，关闭转场与 Shutdown 共用同一次归还。</summary>
        private sealed class ClosingResourceLease
        {
            /// <summary>面板实例标识，用来关联关闭回调与原租约。</summary>
            internal readonly int InstanceId;
            /// <summary>面板成功接管的 prefab 引用，由面板资源收尾归还。</summary>
            internal readonly RevResLease Resource;
            /// <summary>持有该引用的实际面板实例。</summary>
            internal readonly RevUIPanel Panel;
            /// <summary>转场或 Shutdown 是否已归还这份引用。</summary>
            internal bool Released;
            /// <summary>直接回收已开始，转场或禁用回调不得重新将本实例归池。</summary>
            internal bool Reclaiming;
            /// <summary>取得本实例的最近一次开窗 ID，关联空闲记录和开窗时间线。</summary>
            internal Guid OpenId;
            /// <summary>等待该面板回池、销毁或归还资源的替换记录。</summary>
            internal RevUIOpenTicket Replacement;
            /// <summary>交割时冻结旧页输入用的组件。</summary>
            internal CanvasGroup InputGate;
            /// <summary>关闭前的交互设置，回池前还原。</summary>
            internal bool WasInteractable;
            /// <summary>关闭前的射线设置，回池前还原。</summary>
            internal bool WasBlocking;

            /// <summary>实例创建后接管原加载引用，无需额外增加计数。</summary>
            internal ClosingResourceLease(RevUIPanel panel, RevResLease resource)
            {
                Panel = panel;
                InstanceId = panel != null ? panel.GetInstanceID() : 0;
                Resource = resource;
            }
        }

        private bool _shuttingDown;

        /// <summary>打开顺序（层内排序 + 返回栈都读它）</summary>
        private readonly List<RevUIPanel> _openOrder = new List<RevUIPanel>();

        /// <summary>所有尚未归还的面板 prefab 引用，包含打开中、打开态、池中及关闭转场中实例。</summary>
        private readonly Dictionary<int, ClosingResourceLease> _panelResourceLeases =
            new Dictionary<int, ClosingResourceLease>(16);

        /// <summary>已从打开索引摘除、但关闭转场尚未完成的资源租约。</summary>
        private readonly List<ClosingResourceLease> _closingResources = new List<ClosingResourceLease>(4);

        /// <summary>互斥组 → 当前占着这个组的面板</summary>
        private readonly Dictionary<string, RevUIPanel> _exclusive = new Dictionary<string, RevUIPanel>(StringComparer.Ordinal);

        /// <summary>关闭后留着的实例（复用）</summary>
        private readonly RevUIPanelPool _pool = new RevUIPanelPool();

        /// <summary>层内排序用的复用缓冲（避免每次重排都分配一个 List）</summary>
        private readonly List<RevUIPanel> _layerBuffer = new List<RevUIPanel>();

        /// <summary>已经告过"画布类型被放回常用"的面板类型（每类只告一次）</summary>
        private readonly HashSet<Type> _canvasTypeWarned = new HashSet<Type>();

        private RevUIRoot _root;
        private bool _layoutDirty;

        /// <summary>当前打开中的面板数</summary>
        public int OpenedCount => _opened.Count;

        /// <summary>正在加载中的面板数</summary>
        public int LoadingCount => _loading.Count;

        /// <summary>池里留着的实例数</summary>
        public int CachedCount => _pool.Count;

        /// <summary>UI 根节点（框架内部用；业务要挂自己的 UI 请用 RevUI 的公开接口，不要依赖内部类型）</summary>
        internal RevUIRoot Root => _root;

        // ============================================================
        // 打开
        // ============================================================

        /// <summary>打开面板并通知等待者。普通 API 与可追踪入口共用同一条交接链。</summary>
        internal RevUIPanel OpenCore(Type panelType, object data, List<Action<RevUIPanel>> callbacks)
        {
            return OpenInternal(panelType, data, callbacks, true, false, out _);
        }

        /// <summary>尝试立即使用已有资源；需要读资源时保留当前页并返回空。</summary>
        internal RevUIPanel OpenImmediate(Type panelType)
        {
            return OpenInternal(panelType, null, null, false, false, out _);
        }

        /// <summary>接收可追踪打开请求。调用方可要求已缓存预制体也走分帧装配。</summary>
        internal RevUIOpenTicket OpenTracked(Type panelType, object data, bool deferAssembly)
        {
            OpenInternal(panelType, data, null, true, deferAssembly, out RevUIOpenTicket ticket);
            return ticket;
        }

        /// <summary>同键合并；互斥组只取消未交付的旧候选。已显示页保留到新页通过首屏。</summary>
        private RevUIPanel OpenInternal(Type panelType, object data, List<Action<RevUIPanel>> callbacks,
            bool allowLoad, bool deferAssembly, out RevUIOpenTicket ticket)
        {
            if (!RevUIPanelMeta.TryResolve(panelType, out RevUIPanelMeta meta, out string error))
            {
                ticket = NewTicket(panelType.Name);
                FinishTicket(ticket, RevUIOpenStatus.Failed, "invalid-panel");
                RevUILog.Error(error);
                InvokeAll(callbacks, null);
                return null;
            }
            if (_shuttingDown)
            {
                ticket = NewTicket(meta.Key);
                FinishTicket(ticket, RevUIOpenStatus.Cancelled, "ui-shutdown");
                InvokeAll(callbacks, null);
                return null;
            }
            EnsureReady();
            string key = meta.Key;
            if (_loading.TryGetValue(key, out OpenRequest waiting))
            {
                ticket = waiting.Ticket;
                if (callbacks != null)
                {
                    waiting.Callbacks.AddRange(callbacks);
                }
                if (data != null)
                {
                    waiting.Data = data;
                    waiting.Panel?.SetData(data);
                }
                if (waiting.Published)
                {
                    CancelCandidates(meta.ExclusiveGroup, waiting);
                }
                return null;
            }

            if (_opened.TryGetValue(key, out RevUIPanel opened))
            {
                ticket = NewTicket(meta.Key);
                CancelCandidates(meta.ExclusiveGroup);
                if (opened == null || !_opened.TryGetValue(key, out RevUIPanel current) || current != opened)
                {
                    FinishTicket(ticket, RevUIOpenStatus.Cancelled, "panel-closed");
                    InvokeAll(callbacks, null);
                    return null;
                }
                BringToTop(opened);
                if (data != null)
                {
                    opened.SetData(data);
                }
                else
                {
                    opened.RefreshView();
                }
                ticket.FirstScreenReady = true;
                ticket.ResourceDisposition = "Panel";
                FinishTicket(ticket, RevUIOpenStatus.Succeeded, "already-open");
                InvokeAll(callbacks, opened);
                return opened;
            }

            OpenRequest request = BeginOpen(meta, data, callbacks);
            ticket = request.Ticket;
            // 先登记新请求，再撤销旧候选。旧候选的完成回调可同步发起更新的请求。
            CancelCandidates(meta.ExclusiveGroup, request);
            if (!IsCurrent(key, request))
            {
                return null;
            }

            RevUIPanel pooled = TakeCachedPanel(request);
            if (!IsCurrent(key, request))
            {
                return null;
            }
            if (pooled != null)
            {
                ticket.Advance(RevUIOpenStage.Instance);
                return BuildPanel(request);
            }

            RevResHandle cached = RevResManager.Get(meta.Root, meta.Name);
            if (cached.IsLoaded)
            {
                RevResHandle handle = RevResManager.LoadHandle<GameObject>(meta.Root, meta.Name, RevResGroup.UI);
                request.Resource = RevResManager.OwnReference(handle, "RevUI/" + meta.PanelType.Name);
                ticket.ResourceDisposition = "Pending";
                return ResourceReady(request, deferAssembly);
            }
            if (!allowLoad)
            {
                AbortOpen(request, RevUIOpenStatus.Failed, "requires-async");
                return null;
            }

            // LoadOwnedAsync 在缓存命中时可能同步回调。先认领回调给出的租约，再转交给装配或释放。
            RevResLease started = RevResManager.LoadOwnedAsync(meta.Root, meta.Name, typeof(GameObject),
                "RevUI/" + meta.PanelType.Name, resource =>
                {
                    if (!IsCurrent(key, request))
                    {
                        resource.Dispose();
                        return;
                    }
                    request.Resource = resource;
                    request.Ticket.ResourceDisposition = "Pending";
                    ResourceReady(request, deferAssembly || meta.ExclusiveGroup != null);
                }, RevResGroup.UI);
            if (IsCurrent(key, request) && ticket.Stage == RevUIOpenStage.Resource)
            {
                request.Resource = started;
                ticket.ResourceDisposition = "Pending";
            }
            return null;
        }

        /// <summary>校验已取得的引用；冷互斥页由队列在 UI 根节点的帧预算内装配。</summary>
        private RevUIPanel ResourceReady(OpenRequest request, bool deferAssembly)
        {
            if (!IsCurrent(request.Meta.Key, request))
            {
                return null;
            }
            if (!(request.Resource?.Handle.Content is GameObject))
            {
                RevUILog.Warning($"打开 {request.Meta.Key} 失败：预制体未就绪（{request.Resource?.Handle.ErrorReason}）。");
                AbortOpen(request, RevUIOpenStatus.Failed, "prefab-unavailable");
                return null;
            }
            if (deferAssembly)
            {
                request.Ticket.Advance(RevUIOpenStage.Queued);
                _assemblies.Enqueue(request);
                return null;
            }
            return BuildPanel(request);
        }

        /// <summary>逐帧装配候选，单次实例化不可拆分，超时则把其余任务留给下一帧。</summary>
        private void PumpAssemblies()
        {
            double started = Time.realtimeSinceStartupAsDouble;
            int left = _assemblies.Count;
            int assembled = 0;
            int limit = Math.Max(1, RevUISetting.MaxAssembliesPerFrame);
            while (left-- > 0 && assembled < limit && _assemblies.Count != 0)
            {
                OpenRequest request = _assemblies.Dequeue();
                if (IsCurrent(request.Meta.Key, request))
                {
                    assembled++;
                    BuildPanel(request);
                }
                if ((Time.realtimeSinceStartupAsDouble - started) * 1000d >= RevUISetting.AssemblyBudgetMs)
                {
                    break;
                }
            }
        }

        /// <summary>实例化或复用，再按请求策略进入首屏预备或普通开窗。</summary>
        private RevUIPanel BuildPanel(OpenRequest request)
        {
            RevUIPanelMeta meta = request.Meta;
            RevUIOpenTicket ticket = request.Ticket;
            bool handoff = meta.ExclusiveGroup != null;
            RevUIPanel panel = request.Panel;
            GameObject instance = null;
            try
            {
                ticket.Advance(RevUIOpenStage.Instance);
                RevUICanvasType canvas = ResolveCanvasType(meta);
                Transform parent = _root.GetPanelParent(meta.Layer, canvas);
                if (handoff)
                {
                    // 独立预备容器不占公开层级、遮罩或返回栈。激活和布局可在这里准备，旧页仍接受输入。
                    request.Staging = CreateStaging(parent);
                    parent = request.Staging;
                }
                if (panel == null)
                {
                    if (!(request.Resource?.Handle.Content is GameObject prefab) || prefab == null)
                    {
                        AbortOpen(request, RevUIOpenStatus.Failed, "prefab-expired");
                        return null;
                    }
                    instance = UnityEngine.Object.Instantiate(prefab, parent, false);
                    if (!IsCurrent(meta.Key, request))
                    {
                        RevUIRoot.DestroyObject(instance);
                        return null;
                    }
                    instance.name = meta.Name;
                    RevUIRoot.ApplyUILayer(instance);
                    panel = instance.GetComponent(meta.PanelType) as RevUIPanel;
                    if (panel == null)
                    {
                        RevUIRoot.DestroyObject(instance);
                        AbortOpen(request, RevUIOpenStatus.Failed, "missing-panel-component");
                        return null;
                    }
                    request.Panel = panel;
                    TrackPanelResource(panel, request.Resource).OpenId = ticket.Id;
                    request.Resource = null;
                    ticket.ResourceDisposition = "Panel";
                    panel.CanvasType = canvas;
                    ticket.Advance(RevUIOpenStage.Binding);
                    panel.InternalSetup(meta, handoff);
                }
                else
                {
                    panel.transform.SetParent(parent, false);
                    panel.CanvasType = canvas;
                    ticket.Advance(RevUIOpenStage.Binding);
                    panel.InternalReuse();
                }
                if (!IsCurrent(meta.Key, request))
                {
                    return null;
                }
                RevScope.Detach(panel);
                request.Scope?.Attach(panel);
                if (!handoff)
                {
                    Register(panel);
                    request.Published = true;
                }
                if (request.Data != null)
                {
                    panel.SetData(request.Data);
                }
                if (!IsCurrent(meta.Key, request))
                {
                    return null;
                }

                ticket.Advance(RevUIOpenStage.FirstScreen);
                if (handoff)
                {
                    PrepareAndCommitAsync(request).Forget();
                }
                else
                {
                    panel.InternalOpen(() => OnPanelOpened(request), error => AbortOpen(request, RevUIOpenStatus.Failed, "open-transition-failed"));
                }
                return ticket.Status == RevUIOpenStatus.Pending || ticket.Status == RevUIOpenStatus.Succeeded ? panel : null;
            }
            catch (Exception error)
            {
                if (instance != null && request.Panel == null)
                {
                    RevUIRoot.DestroyObject(instance);
                }
                RevLog.Exception(error, $"面板 {meta.Key} 装配失败", "UI");
                AbortOpen(request, RevUIOpenStatus.Failed, "assembly-failed");
                return null;
            }
        }

        /// <summary>新窗的所有内容在隐藏容器中准备；失败只回收这一候选。</summary>
        private async RevTask PrepareAndCommitAsync(OpenRequest request)
        {
            RevUIPanel panel = request.Panel;
            try
            {
                bool ready = await panel.InternalPrepareFirstScreenAsync(request.Cancellation.Token);
                if (!IsCurrent(request.Meta.Key, request))
                {
                    return;
                }
                if (!ready || panel == null)
                {
                    AbortOpen(request, RevUIOpenStatus.Failed, "first-screen-rejected");
                    return;
                }

                RevUIOpenTicket ticket = request.Ticket;
                ticket.FirstScreenReady = true;
                ticket.Advance(RevUIOpenStage.Commit);
                _exclusive.TryGetValue(request.Meta.ExclusiveGroup, out RevUIPanel previous);
                panel.transform.SetParent(request.Staging.parent, false);
                // 变更父节点可能同步触发业务关闭，不能再发布一个已经失效的候选。
                if (panel == null || !IsCurrent(request.Meta.Key, request))
                {
                    return;
                }
                panel.transform.SetAsLastSibling();
                ReleaseStaging(request);
                Register(panel);
                request.Published = true;

                if (previous != null)
                {
                    RetirePrevious(previous, ticket);
                }
                if (!IsCurrent(request.Meta.Key, request))
                {
                    return;
                }

                // 首屏已交割，入场动效是表现增强。启动失败时保留已就绪的新窗，并记录回退原因。
                ticket.Advance(RevUIOpenStage.Reveal);
                panel.InternalRevealPrepared(() => OnPanelOpened(request), error =>
                {
                    if (IsCurrent(request.Meta.Key, request))
                    {
                        RevUIAnim.StopAllOf(panel);
                        RevUIAnim.RestoreAllBasesIn(panel);
                        CompleteOpen(request, panel, RevUIOpenStatus.Succeeded, "reveal-fallback");
                    }
                });
            }
            catch (Exception error)
            {
                if (IsCurrent(request.Meta.Key, request))
                {
                    RevLog.Exception(error, $"面板 {request.Meta.Key} 首屏准备失败", "UI");
                    AbortOpen(request, RevUIOpenStatus.Failed, "first-screen-failed");
                }
            }
        }

        /// <summary>在原层创建等尺寸预备容器；内容可布局，透明且禁止指针命中。</summary>
        private static RectTransform CreateStaging(Transform parent)
        {
            var go = new GameObject("[RevUIPreparing]", typeof(RectTransform), typeof(CanvasGroup));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            CanvasGroup group = go.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
            return rect;
        }

        /// <summary>从请求撤销容器；失败时也立即停止树上的输入和更新，再延迟销毁。</summary>
        private static void ReleaseStaging(OpenRequest request)
        {
            if (request.Staging != null)
            {
                request.Staging.gameObject.SetActive(false);
                RevUIRoot.DestroyObject(request.Staging.gameObject);
                request.Staging = null;
            }
        }

        /// <summary>安装旧页的退场跟踪并关闭交互；旧页回池或释放时完成原票据的退场部分。</summary>
        private void RetirePrevious(RevUIPanel previous, RevUIOpenTicket ticket)
        {
            ClosingResourceLease lease = GetPanelResourceLease(previous) ?? TrackPanelResource(previous, null);
            lease.Replacement = ticket;
            ticket.PreviousPanelKey = previous.PanelKey;
            ticket.PreviousDisposition = "Closing";

            CanvasGroup gate = previous.GetComponent<CanvasGroup>();
            if (gate == null)
            {
                gate = previous.gameObject.AddComponent<CanvasGroup>();
            }
            lease.InputGate = gate;
            lease.WasInteractable = gate.interactable;
            lease.WasBlocking = gate.blocksRaycasts;
            gate.interactable = false;
            gate.blocksRaycasts = false;
            Close(previous);
        }

        /// <summary>打开动画正常完成后发布一次结果。</summary>
        private void OnPanelOpened(OpenRequest request)
        {
            if (IsCurrent(request.Meta.Key, request))
            {
                request.Ticket.FirstScreenReady = true;
                CompleteOpen(request, request.Panel, RevUIOpenStatus.Succeeded, "opened");
            }
        }

        /// <summary>使候选失效后释放资源，再通知所有等待者。正在显示的旧页不在候选清单内。</summary>
        private void AbortOpen(OpenRequest request, RevUIOpenStatus status, string reason)
        {
            if (!IsCurrent(request.Meta.Key, request))
            {
                return;
            }
            _loading.Remove(request.Meta.Key);
            RevTimer.CancelAllOf(request);
            request.Cancellation.Cancel();

            RevUIPanel panel = request.Panel;
            if (panel != null)
            {
                if (request.Published)
                {
                    Unregister(panel);
                }
                panel.InternalAbortHandoff();
                RevScope.Detach(panel);
                ReleasePanelResource(panel);
                panel.gameObject.SetActive(false);
                if (request.Staging == null || !panel.transform.IsChildOf(request.Staging))
                {
                    RevUIRoot.DestroyObject(panel.gameObject);
                }
            }
            request.Resource?.Dispose();
            request.Resource = null;
            ReleaseStaging(request);
            request.Panel = null;
            request.Data = null;
            if (request.Ticket.ResourceDisposition != "None")
            {
                request.Ticket.ResourceDisposition = "Released";
            }
            FinishTicket(request.Ticket, status, reason);
            InvokeAll(request.Callbacks, null);
        }

        /// <summary>
        /// 面板最终进哪个画布（规则见 <see cref="RevUILayerUtil.ResolveCanvasType"/>）。
        /// 声明了静态 / 动态却不在 Scene 层 → 放回常用画布，并**每个面板类型只告警一次**（否则会被 Loading 条这类场景刷屏）。
        /// </summary>
        private RevUICanvasType ResolveCanvasType(RevUIPanelMeta meta)
        {
            RevUICanvasArchitecture architecture = _root.Architecture;

            if (RevUILayerUtil.IsCanvasTypeDemoted(architecture, meta.CanvasType, meta.Layer) &&
                _canvasTypeWarned.Add(meta.PanelType))
                RevUILog.Warning(
                    $"{meta.PanelType.Name} 声明了 CanvasType = {meta.CanvasType}，但它在 {meta.Layer} 层 → 已放进常用画布。\n" +
                    "  静态 / 动态画布整体排在常用画布之下，只收 Scene 层（主界面、HUD）；" +
                    "其它层放进去会被常用画布里的界面盖住。要么把层级改成 Scene，要么去掉 CanvasType。");

            return RevUILayerUtil.ResolveCanvasType(architecture, meta.CanvasType, meta.Layer);
        }

        /// <summary>把已准备的面板放进公开索引，作用域在装配时已绑定。</summary>
        private void Register(RevUIPanel panel)
        {
            _opened[panel.PanelKey] = panel;
            _openOrder.Add(panel);

            if (panel.Meta.ExclusiveGroup != null)
                _exclusive[panel.Meta.ExclusiveGroup] = panel;

            _layoutDirty = true;
        }

        /// <summary>撤销同组未发布候选，当前显示的面板等到新页首屏通过才退场。</summary>
        private void CancelCandidates(string group, OpenRequest except = null)
        {
            if (group == null)
            {
                return;
            }
            var pending = new List<OpenRequest>();
            foreach (OpenRequest request in _loading.Values)
            {
                if (request != except && !request.Published && request.Meta.ExclusiveGroup == group)
                {
                    pending.Add(request);
                }
            }
            foreach (OpenRequest request in pending)
            {
                AbortOpen(request, RevUIOpenStatus.Superseded, "superseded");
            }
        }

        /// <summary>把面板挪到"它所在层的最上面"（层内顺序由打开顺序决定，这里只改全局顺序）</summary>
        private void BringToTop(RevUIPanel panel)
        {
            _openOrder.Remove(panel);
            _openOrder.Add(panel);
            _layoutDirty = true;
        }

        // ============================================================
        // 关闭
        // ============================================================

        /// <summary>取消候选或关闭已发布面板，关闭完成后按归属和缓存策略处置。</summary>
        public bool Close(RevUIPanel panel)
        {
            if (panel == null || panel.PanelKey == null)
            {
                return false;
            }

            if (panel.PanelKey != null && _loading.TryGetValue(panel.PanelKey, out OpenRequest candidate)
                && candidate.Panel == panel && !candidate.Published)
            {
                AbortOpen(candidate, RevUIOpenStatus.Cancelled, "closed");
                return true;
            }

            // 防重入：正在关 / 已经关的，再来一次直接忽略（业务不必自己防抖）
            if (panel.State == RevUIPanelState.Closing || panel.State == RevUIPanelState.Closed) return false;

            OpenRequest opening = UnpublishPanel(panel);
            RevUIRoot closingRoot = _root;
            ClosingResourceLease lease = GetPanelResourceLease(panel) ?? TrackPanelResource(panel, null);
            RevScopeHandle scope = RevScope.Core.FindOwnerOf(panel);
            _closingResources.Add(lease);

            panel.InternalClose(() => CompletePanelClose(lease, scope, closingRoot));

            if (opening != null)
            {
                CompleteOpen(opening, null, RevUIOpenStatus.Cancelled, "closed");
            }
            return true;
        }

        /// <summary>立即从公开索引摘除面板，返回仍在等待打开完成的同一实例请求。</summary>
        private OpenRequest UnpublishPanel(RevUIPanel panel)
        {
            Unregister(panel);
            if (_loading.TryGetValue(panel.PanelKey, out OpenRequest opening) && ReferenceEquals(opening.Panel, panel))
            {
                _loading.Remove(panel.PanelKey);
                return opening;
            }
            return null;
        }

        /// <summary>按类型关闭；尚无可见实例时也能取消在途开窗。</summary>
        public bool Close(Type panelType)
        {
            RevUIPanel panel = Get(panelType);
            if (panel != null)
            {
                return Close(panel);
            }
            if (RevUIPanelMeta.TryResolve(panelType, out RevUIPanelMeta meta, out _) && _loading.TryGetValue(meta.Key, out OpenRequest request))
            {
                AbortOpen(request, RevUIOpenStatus.Cancelled, "closed");
                return true;
            }
            return false;
        }

        /// <summary>关闭指定层并取消该层候选，返回处理的请求和面板数。</summary>
        public int CloseAll(RevUILayer? layer = null)
        {
            // 先保存面板快照，关闭会修改打开索引。
            RevUIPanel[] snapshot = _openOrder.ToArray();

            int closed = CancelUnpublished(request => !layer.HasValue || request.Meta.Layer == layer.Value, "layer-closed");
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (layer.HasValue && snapshot[i].Layer != layer.Value) continue;
                if (Close(snapshot[i])) closed++;
            }

            return closed;
        }

        /// <summary>关闭互斥组当前页及在途候选，防止离开后回调重新开页。</summary>
        public int CloseGroup(string group)
        {
            if (string.IsNullOrEmpty(group)) return 0;
            _exclusive.TryGetValue(group, out RevUIPanel panel);
            int cancelled = CancelUnpublished(request => request.Meta.ExclusiveGroup == group, "group-closed");
            return panel != null && Close(panel) ? cancelled + 1 : cancelled;
        }

        /// <summary>返回：关掉"最晚打开、且参与返回栈"的那个面板</summary>
        public bool Back()
        {
            for (int i = _openOrder.Count - 1; i >= 0; i--)
            {
                RevUIPanel panel = _openOrder[i];
                if (panel.Meta == null || !panel.Meta.InBackStack) continue;
                if (panel.Layer == RevUILayer.Toast) continue;         // 提示层不参与返回
                return Close(panel);
            }

            return false;
        }

        /// <summary>关掉某层最上面那个"带遮罩的"面板（点遮罩时用）</summary>
        internal bool CloseTopOf(RevUILayer layer)
        {
            // "最上面" = 视觉上最上面：先比画布（三 Canvas 下静态 < 动态 < 常用），再比打开顺序
            RevUIPanel top = null;
            int topCanvas = -1;
            for (int i = _openOrder.Count - 1; i >= 0; i--)
            {
                RevUIPanel panel = _openOrder[i];
                if (panel.Layer != layer) continue;
                if (panel.Meta == null || panel.Meta.MaskResolved != RevUIMaskMode.ClickBlock) continue;

                int canvas = RevUILayerUtil.CanvasStackIndex(panel.CanvasType);
                if (canvas > topCanvas) { top = panel; topCanvas = canvas; }
            }

            return top != null && Close(top);
        }

        private void Unregister(RevUIPanel panel)
        {
            if (ReferenceEquals(panel, null)) return;
            string key = panel.PanelKey;
            if (key != null && _opened.TryGetValue(key, out RevUIPanel currentOpen) && ReferenceEquals(currentOpen, panel))
                _opened.Remove(key);
            _openOrder.Remove(panel);

            if (panel.Meta != null && panel.Meta.ExclusiveGroup != null &&
                _exclusive.TryGetValue(panel.Meta.ExclusiveGroup, out RevUIPanel current) && ReferenceEquals(current, panel))
                _exclusive.Remove(panel.Meta.ExclusiveGroup);

            _layoutDirty = true;
        }

        private static RevUICacheMode ResolveCacheMode(RevUIPanel panel)
            => panel.Meta == null || panel.Meta.CacheMode == RevUICacheMode.Unspecified
                ? RevUISetting.DefaultCacheMode
                : panel.Meta.CacheMode;

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>取已打开的面板（没开着返回 null）</summary>
        public RevUIPanel Get(Type panelType)
        {
            if (!RevUIPanelMeta.TryResolve(panelType, out RevUIPanelMeta meta, out _)) return null;
            return _opened.TryGetValue(meta.Key, out RevUIPanel panel) ? panel : null;
        }

        /// <summary>取已打开的面板（强类型）</summary>
        public T Get<T>() where T : RevUIPanel => Get(typeof(T)) as T;

        /// <summary>这个面板现在开着吗</summary>
        public bool IsOpen(Type panelType)
        {
            RevUIPanel panel = Get(panelType);
            return panel != null && panel.State != RevUIPanelState.Closed && panel.State != RevUIPanelState.Closing;
        }

        /// <summary>这个面板现在开着吗（强类型）</summary>
        public bool IsOpen<T>() where T : RevUIPanel => IsOpen(typeof(T));

        /// <summary>某层最上面那个面板（做"当前界面是哪个"的判断用）</summary>
        public RevUIPanel TopOf(RevUILayer layer)
        {
            // "最上面的面板"应当指玩家眼睛看到的最前面，而不只是最近打开的那个。
            // Split 模式会把界面放到多个 Canvas；Canvas 自身有固定前后层级，较早打开的高层 Canvas 面板，
            // 仍可能盖住较晚打开的低层 Canvas 面板。只按打开时间排序会返回错误面板，让"当前界面是谁"的判断出错。
            // 因此先比较 Canvas 层级；在同一个 Canvas 内，才用打开顺序判断前后。
            RevUIPanel top = null;
            int topCanvas = -1;

            for (int i = _openOrder.Count - 1; i >= 0; i--)
            {
                RevUIPanel panel = _openOrder[i];
                if (panel.Layer != layer) continue;

                int canvas = RevUILayerUtil.CanvasStackIndex(panel.CanvasType);
                if (canvas > topCanvas) { top = panel; topCanvas = canvas; }   // 同画布取更晚打开的
            }

            return top;
        }

        /// <summary>当前所有已打开的面板（只读遍历用；外部不要改这个顺序）</summary>
        public IReadOnlyList<RevUIPanel> OpenedPanels => _openOrder;

        // ============================================================
        // 预热
        // ============================================================

        /// <summary>
        /// 预热：只把预制体加载进资源缓存，不创建实例。
        /// ★ 进战斗前把要用的界面预热一遍，战斗里就能"零加载"地打开它（避免战斗中途卡一下）。
        /// </summary>
        public void Preload(Type panelType, Action onLoaded = null)
        {
            if (!RevUIPanelMeta.TryResolve(panelType, out RevUIPanelMeta meta, out string error))
            {
                RevUILog.Error(error);
                onLoaded?.Invoke();
                return;
            }

            EnsureReady();

            RevResPreloader.Preload(meta.Root, new[] { meta.Name }, RevResGroup.UI,
                onCompleted: _ => onLoaded?.Invoke());
        }

        // ============================================================
        // 每帧维护（由 RevUIRoot 驱动）
        // ============================================================

        /// <summary>先按帧预算装配，然后在渲染前更新脏层级。</summary>
        internal void OnRootTick()
        {
            if (_root != null && _assemblies.Count != 0)
            {
                PumpAssemblies();
            }
            if (!_layoutDirty || _root == null) return;

            _layoutDirty = false;
            ApplyLayerLayout();
        }

        /// <summary>
        /// 重排：层内的兄弟顺序（决定谁在上面）、遮罩位置、以及"被盖住"通知。
        /// ★ 从上往下扫：高层只要有带遮罩的面板，下面所有层的面板都算被盖住。
        /// ★ 三 Canvas 架构下，同一层（只可能是 Scene 层）的面板分在不同画布里：
        ///   视觉上"静态画布 < 动态画布 < 常用画布"，画布内才按打开顺序 —— 所以先按画布、再按打开顺序排。
        ///   单 Canvas 架构下所有面板都在常用画布，结果与"只按打开顺序"完全一致。
        /// </summary>
        private void ApplyLayerLayout()
        {
            bool coveredFromAbove = false;

            for (int li = RevUILayerUtil.All.Length - 1; li >= 0; li--)
            {
                RevUILayer layer = RevUILayerUtil.All[li];

                // ① 收集本层已打开的面板，排成"视觉上从下到上"：先按画布（静态 → 动态 → 常用），画布内按打开顺序
                _layerBuffer.Clear();
                for (int ci = 0; ci < RevUILayerUtil.CanvasTypesBottomUp.Length; ci++)
                {
                    RevUICanvasType canvasType = RevUILayerUtil.CanvasTypesBottomUp[ci];
                    for (int i = 0; i < _openOrder.Count; i++)
                        if (_openOrder[i].Layer == layer && _openOrder[i].CanvasType == canvasType)
                            _layerBuffer.Add(_openOrder[i]);
                }

                if (_layerBuffer.Count == 0)
                {
                    _root.Mask.Apply(layer, null);                     // 本层空了，遮罩收掉
                    continue;
                }

                // ② 谁提供遮罩：本层最上面那个"要挡点击"的面板
                int maskIndex = -1;
                for (int i = _layerBuffer.Count - 1; i >= 0; i--)
                {
                    RevUIPanelMeta meta = _layerBuffer[i].Meta;
                    if (meta != null && meta.MaskResolved == RevUIMaskMode.ClickBlock) { maskIndex = i; break; }
                }

                // ③ 排兄弟顺序：每个父节点（层挂点 / 静态画布 / 动态画布）里各自从 0 排起；
                //    遮罩插到"提供遮罩那个面板"的正下方（遮罩会跟着它换父节点）
                Transform currentParent = null;
                int sibling = 0;
                for (int i = 0; i < _layerBuffer.Count; i++)
                {
                    Transform t = _layerBuffer[i].transform;
                    if (t.parent != currentParent) { currentParent = t.parent; sibling = 0; }
                    t.SetSiblingIndex(sibling++);
                }

                _root.Mask.Apply(layer, maskIndex < 0 ? null : _layerBuffer[maskIndex]);

                // ④ 被盖住通知：本层里位于遮罩提供者之下的面板 + （更高层有遮罩时）本层全部
                for (int i = 0; i < _layerBuffer.Count; i++)
                {
                    bool covered = coveredFromAbove || (maskIndex >= 0 && i < maskIndex);
                    _layerBuffer[i].InternalSetCovered(covered);
                }

                if (maskIndex >= 0) coveredFromAbove = true;           // 本层有遮罩 → 下面的层全被盖住
            }
        }

        // ============================================================
        // 会话维护（进 Play / 退出）
        // ============================================================

        /// <summary>新会话归还上一次的请求和面板租约，同时清空开窗历史。</summary>
        internal void ResetForNewSession()
        {
            // Domain Reload 关闭时面板已经随上一轮场景销毁，但这些 prefab 引用计数仍是静态状态。
            ReleaseAllPanelResourceLeases();
            _panelResourceLeases.Clear();
            _closingResources.Clear();
            _shuttingDown = true;
            CancelPendingOpens();
            _shuttingDown = false;
            _opened.Clear();
            _loading.Clear();
            _openOrder.Clear();
            _exclusive.Clear();
            _pool.Clear();
            ResetCacheDiagnostics();
            _openTickets.Clear();
            _finishedTickets.Clear();
            _root = null;
            _layoutDirty = false;
        }

        /// <summary>创建当前 UI 根节点；旧根丢失时先撤销那一代请求。</summary>
        private void EnsureReady()
        {
            if (_root != null) return;                     // Unity 的假 null：根节点被销毁后这里会走重建

            // 根节点可能已随场景卸载销毁；Unity 对象即使是假 null，保存的租约路径仍可用来归还。
            ReleaseAllPanelResourceLeases();
            _panelResourceLeases.Clear();
            _closingResources.Clear();

            // 根节点没了 = 换了一轮运行（或第一次用）：索引里那些实例已经不存在了，清掉
            _opened.Clear();
            _shuttingDown = true;
            CancelPendingOpens();
            _shuttingDown = false;
            _openOrder.Clear();
            _exclusive.Clear();
            _pool.Clear();
            _layoutDirty = false;

            _root = RevUIRoot.Create();
        }

        /// <summary>全部清空（回登录界面 / 切大版本）：所有面板实例 + 根节点一起销毁</summary>
        public int ShutdownAll()
        {
            var destroying = new List<RevUIPanel>(_openOrder);
            foreach (OpenRequest request in _loading.Values)
            {
                // OnBindView / OnInit 可重入 ShutdownAll；此时实例尚未 Register，必须纳入销毁快照。
                if (request.Panel != null && !destroying.Contains(request.Panel)) destroying.Add(request.Panel);
            }
            for (int i = 0; i < _closingResources.Count; i++)
            {
                RevUIPanel closing = _closingResources[i].Panel;
                if (closing != null && !destroying.Contains(closing)) destroying.Add(closing);
            }
            RevUIPanel[] snapshot = destroying.ToArray();
            List<RevUIPanelPool.IdlePanel> idle = _pool.RemoveOldestUntil(0);

            _opened.Clear();
            _shuttingDown = true;                          // 取消回调中不能立即重开，避免清理过程重入
            _openOrder.Clear();
            _exclusive.Clear();
            _layoutDirty = true;

            int idleCount = idle?.Count ?? 0;
            int count = snapshot.Length + idleCount;
            RevUILog.Info($"ShutdownAll：销毁 {snapshot.Length} 个打开中的 + {idleCount} 个池中实例");
            ReleaseClosingResources();
            CancelPendingOpens();

            if (_root != null)
            {
                for (int i = 0; i < snapshot.Length; i++) _root.AddPendingRelease(snapshot[i]);
                for (int i = 0; i < idleCount; i++) _root.AddPendingRelease(idle[i].Panel);

                _root.DestroySelf();                       // 内部会立刻处理完待销毁队列 + 销毁根节点
                _root = null;
            }
            else
            {
                // 根节点已随场景销毁，面板引用可能已是假 null；用创建时保存的租约路径归还。
                ReleaseAllPanelResourceLeases();
                _panelResourceLeases.Clear();
            }

            _shuttingDown = false;
            return count;
        }

        /// <summary>登记面板接管的资源租约；池中复用继续持有原引用。</summary>
        private ClosingResourceLease TrackPanelResource(RevUIPanel panel, RevResLease resource)
        {
            if (ReferenceEquals(panel, null)) return null;

            int instanceId = panel.GetInstanceID();
            if (_panelResourceLeases.TryGetValue(instanceId, out ClosingResourceLease existing)) return existing;

            var lease = new ClosingResourceLease(panel, resource);
            _panelResourceLeases.Add(instanceId, lease);
            return lease;
        }

        private ClosingResourceLease GetPanelResourceLease(RevUIPanel panel)
        {
            if (ReferenceEquals(panel, null)) return null;
            return _panelResourceLeases.TryGetValue(panel.GetInstanceID(), out ClosingResourceLease lease) ? lease : null;
        }

        /// <summary>根节点销毁面板前归还其租约；记录保留到 OnDestroy，防止重复回调重复 Release。</summary>
        internal void ReleasePanelResource(RevUIPanel panel)
        {
            ClosingResourceLease lease = GetPanelResourceLease(panel);
            if (lease != null) ReleasePanelResource(lease);
        }

        /// <summary>归还面板接管的租约，并更新等待这次退场的开窗历史。</summary>
        private void ReleasePanelResource(ClosingResourceLease lease)
        {
            if (lease == null || lease.Released)
            {
                return;
            }
            lease.Released = true;
            lease.Resource?.Dispose();
            if (lease.Replacement != null)
            {
                lease.Replacement.PreviousDisposition = "Released";
                lease.Replacement = null;
            }
        }

        private void ReleaseAllPanelResourceLeases()
        {
            foreach (ClosingResourceLease lease in _panelResourceLeases.Values)
                ReleasePanelResource(lease);
        }

        /// <summary>面板被业务直接 Destroy 时，从所有 UI 索引移除并归还其资源引用。</summary>
        internal void NotifyPanelDestroyed(RevUIPanel panel)
        {
            if (ReferenceEquals(panel, null)) return;
            panel.InternalRelease();

            string key = panel.PanelKey;
            if (key != null && _loading.TryGetValue(key, out OpenRequest request) && ReferenceEquals(request.Panel, panel))
            {
                ReleasePanelResource(panel);
                AbortOpen(request, RevUIOpenStatus.Cancelled, "panel-destroyed");
            }

            Unregister(panel);
            _pool.Forget(panel);

            // 面板彻底销毁：从它所在的域里摘掉（否则域会一直攥着一个已销毁的对象，直到域关闭才放手）
            RevScope.Detach(panel);

            if (_panelResourceLeases.TryGetValue(panel.GetInstanceID(), out ClosingResourceLease lease))
            {
                ReleasePanelResource(lease);
                _panelResourceLeases.Remove(lease.InstanceId);
                _closingResources.Remove(lease);
            }

            RevUIAnim.StopAllOf(panel);
        }

        private void ReleaseClosingResources()
        {
            for (int i = 0; i < _closingResources.Count; i++)
                ReleasePanelResource(_closingResources[i]);
            _closingResources.Clear();
        }

        // ============================================================
        // 诊断
        // ============================================================

        /// <summary>一句话快照 + 明细（排查"这个界面到底开没开、池里留了几个"时打一次就够）</summary>
        public string DumpStats()
        {
            var sb = new StringBuilder();
            bool split = _root != null && _root.Architecture == RevUICanvasArchitecture.Split;

            sb.Append("RevUI（").Append(split ? "三 Canvas 动静分离" : "单 Canvas").Append("）：打开中 ").Append(_opened.Count)
              .Append(" 个，加载中 ").Append(_loading.Count)
              .Append(" 个，池中 ").Append(_pool.Count).Append(" 个\n");

            if (_openOrder.Count > 0)
            {
                sb.Append("打开顺序（从下到上）：\n");
                for (int i = 0; i < _openOrder.Count; i++)
                {
                    RevUIPanel panel = _openOrder[i];
                    sb.Append("  [").Append(panel.Layer);
                    if (split) sb.Append('/').Append(panel.CanvasType);
                    sb.Append("] ").Append(panel.PanelKey).Append("  ").Append(panel.State);

                    if (panel.IsCovered) sb.Append("（被上层遮罩盖住）");
                    if (panel.Meta != null && panel.Meta.ExclusiveGroup != null)
                        sb.Append("（互斥组 ").Append(panel.Meta.ExclusiveGroup).Append("）");

                    sb.Append('\n');
                }
            }

            if (_loading.Count > 0)
            {
                sb.Append("在途加载：\n");
                foreach (KeyValuePair<string, OpenRequest> pair in _loading)
                    sb.Append("  ").Append(pair.Key).Append("（等待 ").Append(pair.Value.Callbacks.Count).Append(" 个回调）\n");
            }

            sb.Append(_pool.DumpStats());
            return sb.ToString();
        }

        // ============================================================
        // 小工具
        // ============================================================

        /// <summary>登记请求与所属域，互斥页有界等待首屏和转场结束。</summary>
        private OpenRequest BeginOpen(RevUIPanelMeta meta, object data, List<Action<RevUIPanel>> callbacks)
        {
            var request = new OpenRequest { Meta = meta, Data = data, Scope = RevScope.Current, Ticket = NewTicket(meta.Key) };
            if (callbacks != null)
            {
                request.Callbacks.AddRange(callbacks);
            }
            _loading[meta.Key] = request;
            if (meta.ExclusiveGroup != null)
            {
                RevTimer.After(RevUISetting.HandoffTimeoutSeconds, () => OnHandoffTimeout(request),
                    RevTimeDomain.Unscaled, owner: request);
            }
            return request;
        }

        /// <summary>仅在这一代请求仍登记时接受异步完成。</summary>
        private bool IsCurrent(string key, OpenRequest request)
            => _loading.TryGetValue(key, out OpenRequest current) && ReferenceEquals(current, request);

        /// <summary>首屏超时撤销候选；交割后超时则结束表现并保留新页。</summary>
        private void OnHandoffTimeout(OpenRequest request)
        {
            if (!IsCurrent(request.Meta.Key, request))
            {
                return;
            }
            if (request.Published && request.Panel != null)
            {
                RevUIAnim.StopAllOf(request.Panel);
                RevUIAnim.RestoreAllBasesIn(request.Panel);
                CompleteOpen(request, request.Panel, RevUIOpenStatus.Succeeded, "reveal-timeout");
            }
            else
            {
                AbortOpen(request, RevUIOpenStatus.Cancelled, "handoff-timeout");
            }
        }

        /// <summary>从在途索引摘除，清掉定时器，再通知票据及普通等待者。</summary>
        private void CompleteOpen(OpenRequest request, RevUIPanel panel, RevUIOpenStatus status, string reason)
        {
            if (IsCurrent(request.Meta.Key, request))
            {
                _loading.Remove(request.Meta.Key);
            }
            RevTimer.CancelAllOf(request);
            FinishTicket(request.Ticket, status, reason);
            InvokeAll(request.Callbacks, panel);
        }

        /// <summary>退出、根节点更换等全局清理，释放在途候选的引用和回调。</summary>
        private void CancelPendingOpens()
        {
            var requests = new List<OpenRequest>(_loading.Values);
            foreach (OpenRequest request in requests)
            {
                AbortOpen(request, RevUIOpenStatus.Cancelled, "ui-shutdown");
            }
            _assemblies.Clear();
        }

        /// <summary>取消匹配的候选，迭代快照以容许取消回调同步提交新请求。</summary>
        private int CancelUnpublished(Predicate<OpenRequest> matches, string reason)
        {
            var requests = new List<OpenRequest>();
            foreach (OpenRequest request in _loading.Values)
            {
                if (!request.Published && matches(request))
                {
                    requests.Add(request);
                }
            }
            foreach (OpenRequest request in requests)
            {
                AbortOpen(request, RevUIOpenStatus.Cancelled, reason);
            }
            return requests.Count;
        }

        /// <summary>作用域退场时，清掉尚未出现在可见面板索引中的候选。</summary>
        internal int CancelScopeOpens(RevScopeHandle scope)
        {
            return CancelUnpublished(request => request.Scope == scope, "scope-closed");
        }

        /// <summary>按 ID 查询当前和最近的请求，结果不持有面板实例。</summary>
        internal bool TryGetOpen(Guid id, out RevUIOpenTicket ticket)
        {
            return _openTickets.TryGetValue(id, out ticket);
        }

        /// <summary>取消指定的未交付请求。完成交割后的关闭继续使用面板生命周期入口。</summary>
        internal bool CancelOpen(Guid id)
        {
            OpenRequest found = null;
            foreach (OpenRequest request in _loading.Values)
            {
                if (request.Ticket.Id == id && !request.Published)
                {
                    found = request;
                    break;
                }
            }
            if (found == null)
            {
                return false;
            }
            AbortOpen(found, RevUIOpenStatus.Cancelled, "caller-cancelled");
            return true;
        }

        /// <summary>在可能同步完成时，也先为新票据登记唯一标识。</summary>
        private RevUIOpenTicket NewTicket(string panelKey)
        {
            var ticket = new RevUIOpenTicket(panelKey);
            _openTickets.Add(ticket.Id, ticket);
            return ticket;
        }

        /// <summary>只淘汰已经归档的请求，最后唤醒票据等待者。</summary>
        private void FinishTicket(RevUIOpenTicket ticket, RevUIOpenStatus status, string reason)
        {
            if (ticket.Status != RevUIOpenStatus.Pending)
            {
                return;
            }
            _finishedTickets.Enqueue(ticket.Id);
            if (_finishedTickets.Count > 32)
            {
                _openTickets.Remove(_finishedTickets.Dequeue());
            }
            ticket.Finish(status, reason);
        }

        /// <summary>隔离每个旧 API 等待者的回调错误，并清掉引用。</summary>
        private static void InvokeAll(List<Action<RevUIPanel>> callbacks, RevUIPanel panel)
        {
            if (callbacks == null) return;

            for (int i = 0; i < callbacks.Count; i++)
            {
                if (callbacks[i] == null) continue;

                // 隔离：一个业务回调抛异常，不该影响其它等待者
                try { callbacks[i](panel); }
                catch (Exception e) { RevUILog.Error($"打开回调抛异常（已隔离）：{e}"); }
            }

            callbacks.Clear();
        }
    }
}

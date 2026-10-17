// ============================================================
// RevUIManager.cs —— UI 管理器（打开/关闭/层级/池化/返回栈）
//
// 位置：Runtime\RevUISystem\Implementation\
//
// 【业务不要直接用这个类】用 Facade 里的 RevUI（同目录上一级）——
//   那是唯一入口，门面只做转发，将来换实现不用改业务代码。
//
// 【找实例的顺序：从快到慢（采纳王者的三层查找）】
//     ① 已在打开中  → 把请求并进在途那次（**同一面板并发打开只会加载/创建一次**）
//     ② 已打开      → 直接复用（置顶 + 按最新数据重画）
//     ③ 实例池里有  → 取出复用（不加载、不实例化、不重新绑定 —— 最快的一条路）
//     ④ 都没有      → 异步加载预制体 → 实例化 → 装配 → 打开
//   注意 ① 是"真正合并"，不像王者那样只是"靠 Singleton 近似"：两个业务同时 Open<BagPanel>()，
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
    public sealed class RevUIManager : RevSingleton<RevUIManager>
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

        // 请求对象身份覆盖"加载 + 打开动画"整个阶段，旧回调不能消费同键的新请求。
        private sealed class OpenRequest
        {
            internal RevUIPanelMeta Meta;
            internal object Data;
            internal readonly List<Action<RevUIPanel>> Callbacks = new List<Action<RevUIPanel>>();
            internal RevUIPanel Panel;
        }

        // 面板实例各持有一份 prefab 资源引用；只保存路径与 instance ID，
        // 即使 Unity 对象在新会话已变成假 null，也能准确归还旧引用。
        // 关闭转场另用同一租约跟踪所有权转移，避免 Shutdown 与晚到回调重复释放。

        private sealed class ClosingResourceLease
        {
            internal readonly int InstanceId;
            internal readonly RevResHandle Handle;
            internal readonly RevUIPanel Panel;
            internal bool Released;

            internal ClosingResourceLease(RevUIPanel panel, RevResHandle handle)
            {
                Panel = panel;
                InstanceId = panel != null ? panel.GetInstanceID() : 0;
                Handle = handle;
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

        /// <summary>打开面板（回调式；<paramref name="callbacks"/> 里的每个回调都会在"完全打开"后收到面板）</summary>
        internal RevUIPanel OpenCore(Type panelType, object data, List<Action<RevUIPanel>> callbacks)
            => OpenInternal(panelType, data, callbacks, allowLoad: true);

        /// <summary>只走"内存里已有"的路子（已打开 / 池里命中）；需要加载时返回 null，不去加载</summary>
        internal RevUIPanel OpenImmediate(Type panelType)
            => OpenInternal(panelType, null, null, allowLoad: false);

        private RevUIPanel OpenInternal(Type panelType, object data, List<Action<RevUIPanel>> callbacks, bool allowLoad)
        {
            if (!RevUIPanelMeta.TryResolve(panelType, out RevUIPanelMeta meta, out string error))
            {
                RevUILog.Error(error);
                InvokeAll(callbacks, null);
                return null;
            }

            if (!typeof(RevUIPanel).IsAssignableFrom(panelType))
            {
                RevUILog.Error($"{panelType.Name} 不是面板：面板必须继承 RevUIPanel（带数据的继承 RevUIPanel<TData>）。");
                InvokeAll(callbacks, null);
                return null;
            }

            if (_shuttingDown) { InvokeAll(callbacks, null); return null; }
            EnsureReady();

            string key = meta.Key;

            // 合并到完全打开为止；最后一次非空数据优先，与已打开面板更新数据的规则一致。
            if (_loading.TryGetValue(key, out OpenRequest waiting))
            {
                if (callbacks != null) waiting.Callbacks.AddRange(callbacks);
                if (data != null)
                {
                    waiting.Data = data;
                    if (waiting.Panel != null) waiting.Panel.SetData(data);
                }
                RevUILog.Info($"{key} 正在加载中，本次请求并入在途的那一次");
                return null;
            }

            // ② 已经打开 → 置顶 + 按最新数据重画
            if (_opened.TryGetValue(key, out RevUIPanel opened))
            {
                BringToTop(opened);
                if (data != null) opened.SetData(data);
                else opened.RefreshView();

                InvokeAll(callbacks, opened);
                return opened;
            }

            // ③ 互斥组：开新的之前，先把同组里那个关掉
            CloseExclusive(meta);

            // ④ 池里有 → 取出复用（最快路径）
            RevUIPanel pooled = _pool.Take(key);
            if (pooled != null)
            {
                RevUILog.Info($"{key} 命中实例池，直接复用");
                OpenRequest request = BeginOpen(meta, data, callbacks);
                request.Panel = pooled;
                pooled.InternalReuse();
                if (!IsCurrent(meta.Key, request) || _root == null) return null;
                Register(pooled);
                if (request.Data != null) pooled.SetData(request.Data);
                if (!IsCurrent(meta.Key, request)) return null;
                bool failed = false;
                pooled.InternalOpen(() => CompleteOpen(meta.Key, request, pooled), error => { failed = true; FailOpen(meta.Key, request, pooled); });
                return failed ? null : pooled;
            }

            // ⑤ 预制体已经在**资源缓存**里且加载成功（Preload 过 / 编辑器直读 / 之前加载过被留着）
            //    → 同步实例化。失败句柄也会留在资源缓存里；它不能当作"有可用预制体"，
            //    否则 Load 会再加一份引用、返回 null，且错误会被误报为"不是 GameObject"。
            RevResHandle cachedHandle = RevResManager.Get(meta.Root, meta.Name);
            if (cachedHandle.IsLoaded)
            {
                // LoadHandle 会为本次面板持有增加一份引用；之后无论校验成功与否都必须归还或交给面板。
                RevResHandle acquired = RevResManager.LoadHandle<GameObject>(meta.Root, meta.Name, RevResGroup.UI);
                GameObject cached = acquired.Get<GameObject>();
                if (!acquired.IsLoaded || cached == null)
                {
                    RevUILog.Error(
                        $"{meta.Key} 的缓存资源不是可用的 GameObject 预制体（原因：{acquired.ErrorReason}）。" +
                        $"检查面板特性里的 root / name 是否指向了正确的预制体。");
                    RevResManager.DecRef(acquired);
                    InvokeAll(callbacks, null);
                    return null;
                }

                return InstantiatePanel(meta, data, cached, acquired, callbacks);
            }

            // ⑥ 同步调用不能等待；异步调用继续往下走。
            //    LoadAsync 遇到已完成且无人持有的失败句柄时会移除旧失败项并重新尝试，
            //    因此修正资源路径后可以直接再次打开，无须等 FlushUnused。
            if (!allowLoad)
            {
                if (cachedHandle.Key != 0)
                {
                    RevUILog.Warning(cachedHandle.IsLoading
                        ? $"{meta.Key} 的预制体仍在异步加载中；本次同步 Open 不会等待，请使用 OpenAsync。"
                        : $"{meta.Key} 的缓存资源加载失败（原因：{cachedHandle.ErrorReason}）；请改用 OpenAsync 重试，或先确认资源路径与资源系统已初始化。");
                    InvokeAll(callbacks, null);
                }
                return null;
            }

            OpenRequest pending = BeginOpen(meta, data, callbacks);
            RevUILog.Info($"开始加载面板 {key}");

            RevResManager.LoadAsync(meta.Root, meta.Name, typeof(GameObject), handle =>
            {
                // 用原始句柄释放，资源系统重置后不会误扣同路径新句柄的引用。
                if (!IsCurrent(key, pending) || _root == null)
                {
                    RevResManager.DecRef(handle);
                    CompleteOpen(key, pending, null);
                    return;
                }
                GameObject prefab = handle.Content as GameObject;
                if (prefab == null)
                {
                    RevUILog.Error(
                        $"打开 {meta.Key} 失败：预制体加载不到。\n" +
                        $"  逐条查：① 资源是否在「资源根目录」下、并设了 AB 名；" +
                        $"② 打包工具「检查」页签有没有报漏标；③ 路径 \"{meta.Root}\" + \"{meta.Name}\" 是否写对。" +
                        $"（默认资源名就是类名，不一致时用 [RevUIPanel(root, layer, \"名字\")] 写清楚）");
                    RevResManager.DecRef(handle);
                    CompleteOpen(key, pending, null);
                    return;
                }
                InstantiatePanel(meta, pending.Data, prefab, handle, pending.Callbacks, pending);
            }, RevResGroup.UI);

            return null;
        }

        /// <summary>
        /// 实例化 → 装配 → 登记 → 打开。
        /// ★ 同步路径（预制体已在缓存里）和异步路径（加载回来）共用这一份，避免两条路各写一遍走偏。
        /// </summary>
        private RevUIPanel InstantiatePanel(RevUIPanelMeta meta, object data, GameObject prefab, RevResHandle acquired,
            List<Action<RevUIPanel>> callbacks, OpenRequest request = null)
        {
            request = request ?? BeginOpen(meta, data, callbacks);
            GameObject go = null;
            RevUIPanel panel = null;
            bool tracked = false;
            try
            {
                // ★ 挂到哪：单 Canvas → 对应层挂点；三 Canvas → 静态 / 动态画布（仅 Scene 层）或常用画布的层挂点
                RevUICanvasType canvasType = ResolveCanvasType(meta);
                go = UnityEngine.Object.Instantiate(prefab, _root.GetPanelParent(meta.Layer, canvasType), false);
                go.name = meta.Name;

                // ★ 统一 UI 图层：Camera 模式下相机的 Culling Mask 只认这个图层，漏了就是"面板开了但看不见"
                RevUIRoot.ApplyUILayer(go);

                panel = go.GetComponent(meta.PanelType) as RevUIPanel;
                if (panel == null)
                {
                    RevUILog.Error(
                        $"面板预制体 {meta.Key} 的根节点上没有 {meta.PanelType.Name} 组件 —— 预制体与脚本对不上。\n" +
                        $"  默认约定：预制体名 = 面板类名；不一致就在特性里写第三个参数指定资源名。");
                    RevResManager.DecRef(acquired);      // 只归还本次取得的原始句柄
                    RevUIRoot.DestroyObject(go);
                    CompleteOpen(meta.Key, request, null);
                    return null;
                }

                request.Panel = panel;
                TrackPanelResource(panel, acquired);
                tracked = true;
                panel.CanvasType = canvasType;
                panel.InternalSetup(meta);
                if (!IsCurrent(meta.Key, request) || _root == null) return null;
                Register(panel);
                if (request.Data != null) panel.SetData(request.Data);
                if (!IsCurrent(meta.Key, request)) return null;

                bool failed = false;
                panel.InternalOpen(() => CompleteOpen(meta.Key, request, panel), error => { failed = true; FailOpen(meta.Key, request, panel); });
                return failed ? null : panel;
            }
            catch (Exception e)
            {
                // 实例化、装配也可能失败。请求必须结束，且未交给面板的引用由这里归还。
                RevLog.Exception(e, $"面板 {meta.Key} 实例化或装配失败", "UI");
                if (tracked) { FailOpen(meta.Key, request, panel); }
                else
                {
                    RevResManager.DecRef(acquired);
                    if (go != null) { RevUIRoot.DestroyObject(go); }
                    CompleteOpen(meta.Key, request, null);
                }
                return null;
            }
        }

        private void FailOpen(string key, OpenRequest request, RevUIPanel panel)
        {
            if (!IsCurrent(key, request)) { return; }
            Unregister(panel);
            try { panel.InternalRelease(); }
            catch (Exception e) { RevLog.Exception(e, "打开失败后的面板清理异常", "UI"); }
            finally
            {
                ReleasePanelResource(panel);
                if (panel != null)
                {
                    if (_root != null) { _root.AddPendingRelease(panel); }
                    else { RevUIRoot.DestroyObject(panel.gameObject); }
                }
                CompleteOpen(key, request, null);
            }
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

        private void Register(RevUIPanel panel)
        {
            // ★ 作用域：有域开着，就把面板登记到域上 —— 域关闭时框架会替你把它关掉（RevScope 第 2 步）。
            //   没有域时什么也不做（行为与没有作用域系统时一致）。池里复用的实例每次打开都会重新登记（"最近一次的域说了算"）。
            RevScope.AttachSilently(panel);

            _opened[panel.PanelKey] = panel;
            _openOrder.Add(panel);

            if (panel.Meta.ExclusiveGroup != null)
                _exclusive[panel.Meta.ExclusiveGroup] = panel;

            _layoutDirty = true;
        }

        private void CloseExclusive(RevUIPanelMeta meta)
        {
            if (meta.ExclusiveGroup == null) return;

            if (_exclusive.TryGetValue(meta.ExclusiveGroup, out RevUIPanel current) &&
                current != null && current.State != RevUIPanelState.Closed)
                Close(current);

            // _exclusive 只记录已经打开的面板；正在异步加载的面板还没登记在那里，所以单纯关闭当前面板找不到它。
            // 若此时又打开同组面板，旧加载完成后也会打开，结果同一互斥组同时出现两个界面。先取消旧请求并用 null 通知等待者，
            // 让最新打开请求成为该组唯一有效的面板；旧资源加载即使晚到也不能再显示旧面板。
            var pending = new List<KeyValuePair<string, OpenRequest>>();
            foreach (KeyValuePair<string, OpenRequest> pair in _loading)
            {
                if (pair.Key == meta.Key || pair.Value.Meta == null ||
                    pair.Value.Meta.ExclusiveGroup != meta.ExclusiveGroup) continue;
                pending.Add(pair);
            }

            for (int i = 0; i < pending.Count; i++)
            {
                KeyValuePair<string, OpenRequest> pair = pending[i];
                OpenRequest request = pair.Value;
                if (!IsCurrent(pair.Key, request)) continue;

                _loading.Remove(pair.Key);
                InvokeAll(request.Callbacks, null);
                request.Callbacks.Clear();

                // OnBindView 可能在 Register（登记到打开表）之前调用 ShutdownAll 或打开同组新面板。
                // 这时实例还不在普通打开列表里，常规清理找不到它；但创建时已经增加了 prefab 资源引用，必须单独归还并销毁。
                if (request.Panel != null && !_openOrder.Contains(request.Panel))
                {
                    if (_root != null) _root.AddPendingRelease(request.Panel);
                    else
                    {
                        ReleasePanelResource(request.Panel);
                        RevUIRoot.DestroyObject(request.Panel.gameObject);
                    }
                }
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

        /// <summary>关闭一个面板（回池还是销毁由它的 CacheMode 决定）</summary>
        public bool Close(RevUIPanel panel)
        {
            if (panel == null) return false;

            // 防重入：正在关 / 已经关的，再来一次直接忽略（业务不必自己防抖）
            if (panel.State == RevUIPanelState.Closing || panel.State == RevUIPanelState.Closed) return false;

            Unregister(panel);

            _loading.TryGetValue(panel.PanelKey, out OpenRequest opening);
            if (opening != null && opening.Panel == panel) _loading.Remove(panel.PanelKey);
            else opening = null;
            RevUIRoot closingRoot = _root;
            ClosingResourceLease lease = GetPanelResourceLease(panel) ?? TrackPanelResource(panel, null);
            _closingResources.Add(lease);

            bool keepAlive = ResolveCacheMode(panel) == RevUICacheMode.KeepAlive;
            bool canPool = keepAlive && !_pool.IsFull(panel.PanelKey);

            panel.InternalClose(() =>
            {
                // ShutdownAll / 场景重建已经归还了这份引用。异步转场晚到的回调只能退出，
                // 不能再读取已销毁面板的假 null 属性或重复 Release 同一路径。
                if (lease.Released) return;
                _closingResources.Remove(lease);

                // 根节点已被销毁：用关闭时快照的句柄归还，panel 此时可能已经是 Unity 假 null。
                if (closingRoot == null || _root != closingRoot)
                {
                    ReleasePanelResource(lease);
                    return;
                }

                if (canPool)
                {
                    _pool.Put(panel);
                    RevUILog.Info($"{panel.PanelKey} 已回实例池（下次打开不再加载）");
                    return;
                }

                _root.AddPendingRelease(panel);        // 延迟一帧销毁：避免在事件/遍历里删对象
            });

            if (opening != null)
            {
                InvokeAll(opening.Callbacks, null);
                opening.Callbacks.Clear();
            }
            return true;
        }

        /// <summary>按类型关闭（没开着返回 false）</summary>
        public bool Close(Type panelType)
        {
            RevUIPanel panel = Get(panelType);
            return panel != null && Close(panel);
        }

        /// <summary>关闭所有（可按层级）；返回关掉的数量</summary>
        public int CloseAll(RevUILayer? layer = null)
        {
            // ★ 先快照再遍历：Close 会改 _openOrder（"遍历中删除"是王者文档点名的坑）
            RevUIPanel[] snapshot = _openOrder.ToArray();

            int closed = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (layer.HasValue && snapshot[i].Layer != layer.Value) continue;
                if (Close(snapshot[i])) closed++;
            }

            return closed;
        }

        /// <summary>关闭某个互斥组当前占用的面板</summary>
        public int CloseGroup(string group)
        {
            if (string.IsNullOrEmpty(group)) return 0;
            if (!_exclusive.TryGetValue(group, out RevUIPanel panel) || panel == null) return 0;

            return Close(panel) ? 1 : 0;
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

        /// <summary>根节点每帧调一次：只在"打开/关闭/置顶"之后才真的重排一次（脏标记）</summary>
        internal void OnRootTick()
        {
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

        /// <summary>
        /// 新一轮运行开始（进 Play 前）时清空索引。
        /// ★ 关掉"域重载"反复进 Play 时，静态实例还在，但场景里的面板早就没了 —— 必须清干净。
        /// </summary>
        internal void ResetForNewSession()
        {
            // Domain Reload 关闭时面板已经随上一轮场景销毁，但这些 prefab 引用计数仍是静态状态。
            ReleaseAllPanelResourceLeases();
            _panelResourceLeases.Clear();
            _closingResources.Clear();
            foreach (OpenRequest request in _loading.Values) request.Callbacks.Clear();
            _opened.Clear();
            _loading.Clear();
            _openOrder.Clear();
            _exclusive.Clear();
            _pool.Clear();
            _root = null;
            _layoutDirty = false;
        }

        private void EnsureReady()
        {
            if (_root != null) return;                     // Unity 的假 null：根节点被销毁后这里会走重建

            // 根节点可能已随场景卸载销毁；Unity 对象即使是假 null，保存的租约路径仍可用来归还。
            ReleaseAllPanelResourceLeases();
            _panelResourceLeases.Clear();
            _closingResources.Clear();

            // 根节点没了 = 换了一轮运行（或第一次用）：索引里那些实例已经不存在了，清掉
            _opened.Clear();
            _loading.Clear();
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
            List<RevUIPanel> idle = _pool.Drain();

            _opened.Clear();
            _shuttingDown = true;                          // 取消回调中不能立即重开，避免清理过程重入
            _openOrder.Clear();
            _exclusive.Clear();
            _layoutDirty = true;

            int count = snapshot.Length + idle.Count;
            RevUILog.Info($"ShutdownAll：销毁 {snapshot.Length} 个打开中的 + {idle.Count} 个池中实例");
            ReleaseClosingResources();
            CancelPendingOpens();

            if (_root != null)
            {
                for (int i = 0; i < snapshot.Length; i++) _root.AddPendingRelease(snapshot[i]);
                for (int i = 0; i < idle.Count; i++) _root.AddPendingRelease(idle[i]);

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

        private ClosingResourceLease TrackPanelResource(RevUIPanel panel, RevResHandle handle)
        {
            if (ReferenceEquals(panel, null)) return null;

            int instanceId = panel.GetInstanceID();
            if (_panelResourceLeases.TryGetValue(instanceId, out ClosingResourceLease existing)) return existing;

            var lease = new ClosingResourceLease(panel, handle);
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

        private void ReleasePanelResource(ClosingResourceLease lease)
        {
            if (lease == null || lease.Released) return;
            lease.Released = true;
            RevResManager.DecRef(lease.Handle);
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
            if (key != null && _loading.TryGetValue(key, out OpenRequest request) && request.Panel == panel)
            {
                _loading.Remove(key);
                InvokeAll(request.Callbacks, null);
                request.Callbacks.Clear();
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

        private OpenRequest BeginOpen(RevUIPanelMeta meta, object data, List<Action<RevUIPanel>> callbacks)
        {
            var request = new OpenRequest { Meta = meta, Data = data };
            if (callbacks != null) request.Callbacks.AddRange(callbacks);
            _loading[meta.Key] = request;
            return request;
        }

        private bool IsCurrent(string key, OpenRequest request)
            => _loading.TryGetValue(key, out OpenRequest current) && ReferenceEquals(current, request);

        private void CompleteOpen(string key, OpenRequest request, RevUIPanel panel)
        {
            if (IsCurrent(key, request)) _loading.Remove(key);
            InvokeAll(request.Callbacks, panel);
            request.Callbacks.Clear();
        }

        private void CancelPendingOpens()
        {
            var requests = new List<OpenRequest>(_loading.Values);
            _loading.Clear();
            foreach (OpenRequest request in requests)
            {
                InvokeAll(request.Callbacks, null);
                request.Callbacks.Clear();
            }
        }

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

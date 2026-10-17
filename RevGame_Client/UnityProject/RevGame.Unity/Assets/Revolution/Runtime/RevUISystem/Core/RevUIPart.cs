// ============================================================
// RevUIPart.cs —— Part 基类（依附于面板的可复用单元）
//
// 位置：Runtime\RevUISystem\Core\
//
// 【Part 与 Panel 的区别（采纳王者 Form / Part 的划分）】
//     Panel = 一个**独立界面**：有自己的层级、能独立打开关闭、进返回栈、独立资源加载。
//     Part  = 一个**依附的单元**：不能独立存在，生命周期完全跟随宿主（顶部栏、页签组、通用列表项…）。
//
//   Part 的价值在"复用"：同一个"商品格"给背包、商城、活动三个面板用 ——
//   写一次，三处引用，改一次三处生效。这正是王者把界面拆成 Form + Part 的原因。
//
// 【两种 Part，按"要不要独立预制体"来选】
//     · 节点级 Part（**不加特性**）：就摆在宿主面板的预制体里。
//        面板用 `[RevBind] private RevShopTab _tab;` 直接拿到，零加载成本 —— 绝大多数 Part 属于这种。
//     · 预制体 Part（`[RevUIPart("UI/Part")]`）：自己是独立预制体，**可跨面板复用**，
//        由 `RevUIPart.Create<T>(host, parent, onCreated)` 挂进宿主指定的插槽里。
//
// 【与宿主 / 与别的 Part 怎么通信】
//     · Part → 宿主：只认 IRevUIPartHost（PartRoot / IsHostOpened / RequestClose / NotifyPartChanged），
//       所以**同一个 Part 才能被多个面板复用**；
//     · Part ↔ Part：不直接耦合，经宿主中转（NotifyPartChanged → 宿主的 OnPartChanged）；
//     · 宿主 → Part：宿主直接调 Part 的公开方法（宿主当然知道自己有哪些 Part）。
//
// 【关闭时同样一行防泄漏】Part 关闭时框架自动执行 RevEvent.RemoveAllByOwner(this)。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>Part 基类：可复用的 UI 单元（生命周期完全跟随宿主）</summary>
    [DisallowMultipleComponent]
    public abstract class RevUIPart : MonoBehaviour, IRevUIUserEvents
    {
        // ============================================================
        // 元数据
        // ============================================================

        /// <summary>Part 元数据（是否独立预制体、资源路径…）</summary>
        public RevUIPartMeta Meta { get; private set; }

        /// <summary>宿主（面板）。Part 只认这个接口，所以能跨面板复用</summary>
        public IRevUIPartHost Host { get; private set; }

        /// <summary>宿主是不是面板（是的话能拿到面板本体；不是则为 null）</summary>
        public RevUIPanel HostPanel => Host as RevUIPanel;

        /// <summary>当前是否处于打开状态（Host 还在用我）</summary>
        public bool IsOpened { get; private set; }

        /// <summary>Part 身份键（节点级 Part 的根目录为空，键就是类名）</summary>
        public string PartKey => Meta == null ? GetType().Name : Meta.Key;

        /// <summary>这个 Part 是不是独立预制体（可跨面板复用）</summary>
        public bool IsPrefabPart => Meta != null && Meta.IsPrefab;

        // 预制体 Part 会占用一份资源引用；销毁时还掉（和 RevPool 持有句柄的思路一致）
        private bool _holdsResourceRef;
        private RevResHandle _resourceHandle;

        private sealed class CreateRequest
        {
            internal IRevUIPartHost Host;
            internal Transform Slot;
            internal Type Type;
            internal int Version;
            internal readonly List<Action<RevUIPart>> Callbacks = new List<Action<RevUIPart>>();
        }

        private static readonly List<CreateRequest> PendingCreates = new List<CreateRequest>();

        internal static void CancelCreates(IRevUIPartHost host)
        {
            var cancelled = PendingCreates.FindAll(r => ReferenceEquals(r.Host, host));
            foreach (CreateRequest request in cancelled) PendingCreates.Remove(request);
            foreach (CreateRequest request in cancelled) FinishCreate(request, null);
        }

        private static void FinishCreate(CreateRequest request, RevUIPart part)
        {
            PendingCreates.Remove(request);
            var callbacks = request.Callbacks.ToArray();
            request.Callbacks.Clear();
            foreach (Action<RevUIPart> callback in callbacks)
                RevUILog.Guard("Part 创建回调", () => callback(part));
        }

        // ============================================================
        // 业务要重写的钩子（和面板同一套纪律）
        // ============================================================

        /// <summary>首次装配完成时调用一次</summary>
        protected virtual void OnPartInit() { }

        /// <summary>★ 表现装配：拿控件、挂交互（[RevBind] 字段此时已有值）</summary>
        protected abstract void OnBindView();

        /// <summary>宿主打开时调用（含宿主复用后再打开）</summary>
        protected virtual void OnPartOpen() { }

        /// <summary>★ 数据落屏：只有"需要被宿主刷新"的 Part 才重写它</summary>
        protected virtual void OnPartRefresh() { }

        /// <summary>宿主关闭 / 自己被回收时调用</summary>
        protected virtual void OnPartClose() { }

        /// <summary>通知宿主"我这儿变了"（宿主在 OnPartChanged 里决定怎么协调）</summary>
        protected void NotifyHost() => Host?.NotifyPartChanged(this);

        // ============================================================
        // 交互回调（与面板同名同款：不想写绑定字段就用节点名分发）
        // ============================================================

        protected virtual void OnClick(string nodeName) { }

        /// <summary>某个控件被长按（按住超过 <c>RevUISetting.ButtonLongPressSeconds</c> 后松开）</summary>
        protected virtual void OnLongPress(string nodeName) { }

        /// <summary>指针在某个控件上松开（无论按了多久）</summary>
        protected virtual void OnLoosen(string nodeName) { }

        /// <summary>
        /// 显示动画：宿主打开本 Part 时自动播（一行加动效）。
        /// <code>protected override RevUIAnimPreset ShowAnimation => RevUIAnimPreset.SlideInFromBottom;</code>
        /// </summary>
        protected virtual RevUIAnimPreset ShowAnimation => RevUIAnimPreset.None;

        /// <summary>
        /// 隐藏动画：**注意 Part 的关闭是同步的**（宿主关掉 → 立刻禁用），
        /// 所以这里只在"宿主自己决定延迟禁用"时才有意义；
        /// 需要"收起动画播完再走"就把 Part 做成独立开关（在宿主里配合 <c>RevUIAnim</c> 播完再关）。
        /// </summary>
        protected virtual RevUIAnimPreset HideAnimation => RevUIAnimPreset.None;

        protected virtual void OnToggleChanged(string nodeName, bool value) { }
        protected virtual void OnSliderChanged(string nodeName, float value) { }
        protected virtual void OnInputChanged(string nodeName, string value) { }
        protected virtual void OnInputEndEdit(string nodeName, string value) { }
        protected virtual void OnDropdownChanged(string nodeName, int index) { }
        protected virtual void OnScrollChanged(string nodeName, float x, float y) { }

        // ============================================================
        // 创建 / 销毁（预制体 Part 用）
        // ============================================================

        /// <summary>
        /// 在宿主里创建（或复用）一个**预制体 Part**。
        /// ★ 异步：真机上预制体在 AB 包里，首次必须异步加载（和资源系统一致）。
        ///   同一个宿主下已有同类型 Part 时，不重复创建，直接刷新后回调 —— 避免"开着开着多出来一个"。
        /// </summary>
        /// <param name="host">宿主（面板）</param>
        /// <param name="parent">挂在哪个节点下；传 null = 挂在 host.PartRoot</param>
        /// <param name="onCreated">创建完成回调（失败时收到 null）</param>
        public static void Create<T>(IRevUIPartHost host, Transform parent, Action<T> onCreated)
            where T : RevUIPart
        {
            if (host == null)
            {
                RevUILog.Error($"创建 Part {typeof(T).Name} 失败：宿主为 null。");
                onCreated?.Invoke(null);
                return;
            }

            RevUIPartMeta meta = RevUIPartMeta.Resolve(typeof(T));
            if (!meta.IsPrefab)
            {
                RevUILog.Error(
                    $"创建 Part {typeof(T).Name} 失败：它是**节点级 Part**（没写 [RevUIPart(root)]），" +
                    $"应该直接用面板上的 [RevBind] 字段拿到，不需要 Create。\n" +
                    $"要让它能跨面板复用，就在类上写：[RevUIPart(\"UI/Part\")]");
                onCreated?.Invoke(null);
                return;
            }

            Transform slot = parent != null ? parent : host.PartRoot;
            if (slot == null)
            {
                RevUILog.Error($"创建 Part {typeof(T).Name} 失败：宿主没有可用的挂点（PartRoot 为 null）。");
                onCreated?.Invoke(null);
                return;
            }

            RevUIPanel hostPanel = host as RevUIPanel;
            if (!host.IsHostOpened || (hostPanel != null && hostPanel.State != RevUIPanelState.Opened))
            {
                onCreated?.Invoke(null);
                return;
            }
            int version = hostPanel == null ? 0 : hostPanel.LifetimeVersion;
            CreateRequest pending = PendingCreates.Find(r => ReferenceEquals(r.Host, host) &&
                r.Slot == slot && r.Type == typeof(T) && r.Version == version);
            if (pending != null)
            {
                if (onCreated != null) pending.Callbacks.Add(part => onCreated(part as T));
                return;
            }

            // 已经有同类型实例 → 复用（刷新一次就够）
            T exist = slot.GetComponentInChildren<T>(true);
            if (exist != null)
            {
                // ★ Bug 修复（2026-09-30）：复用前必须检查它还开着没有 ——
                //   ClosePart(false) 关掉的 Part 是"失活保留"（IsOpened=false + SetActive(false)），
                //   原复用路径只调 InternalRefresh，而它对 !IsOpened 直接 return：
                //   不会 SetActive(true)、不会触发 OnPartOpen —— 业务拿到的是一个失活的 Part，
                //   表现为"第二次打开这个 Part 不出现"。
                if (exist.IsOpened) exist.InternalRefresh();
                else exist.InternalOpen();

                onCreated?.Invoke(exist);
                return;
            }

            var request = new CreateRequest { Host = host, Slot = slot, Type = typeof(T), Version = version };
            if (onCreated != null) request.Callbacks.Add(part => onCreated(part as T));
            PendingCreates.Add(request);
            RevResManager.LoadAsync(meta.Root, meta.Name, typeof(GameObject), handle =>
            {
                GameObject prefab = handle.Content as GameObject;
                if (!PendingCreates.Contains(request) || !host.IsHostOpened ||
                    (hostPanel != null && hostPanel.LifetimeVersion != version))
                {
                    RevResManager.DecRef(handle);
                    FinishCreate(request, null);
                    return;
                }
                // ★ Bug 修复（2026-09-30）：异步加载回来时宿主可能已经被关闭回池 / 销毁
                //   （加载需要时间，这期间面板被关是常态）——此时 slot 已是假 null：
                //   还往下走就会把 Part 实例化到已销毁的父节点上（抛异常）或场景根上（脱离宿主），
                //   并且"Part 生命周期完全跟随宿主"的约定被破坏。失败时按约定回调 null。
                if (slot == null)
                {
                    RevUILog.Warning($"创建 Part {typeof(T).Name} 中止：宿主在预制体加载完成前已被关闭/销毁。");
                    RevResManager.DecRef(handle);
                    FinishCreate(request, null);
                    return;
                }

                if (prefab == null)
                {
                    RevUILog.Error($"创建 Part {typeof(T).Name} 失败：加载不到预制体 {meta.Key}（可能是没打包 / 路径写错）。");
                    RevResManager.DecRef(handle);
                    FinishCreate(request, null);
                    return;
                }

                GameObject go = Instantiate(prefab, slot, false);
                go.name = typeof(T).Name;                       // 名字可读，方便看层级时辨认

                RevUIRoot.ApplyUILayer(go);                      // ★ 与面板同一套：Camera 模式靠图层被相机认到

                T part = go.GetComponent<T>();
                if (part == null)
                {
                    RevUILog.Error(
                        $"Part 预制体 {meta.Key} 的根节点上没有 {typeof(T).Name} 组件 —— " +
                        $"预制体与脚本对不上（类名和预制体名默认应该一致）。");
                    Destroy(go);
                    RevResManager.DecRef(handle);
                    FinishCreate(request, null);
                    return;
                }

                part._holdsResourceRef = true;
                part._resourceHandle = handle;
                part.InternalInit(host);
                if (!PendingCreates.Contains(request) || !host.IsHostOpened ||
                    (hostPanel != null && hostPanel.LifetimeVersion != version))
                {
                    part.ClosePart();
                    FinishCreate(request, null);
                    return;
                }
                part.InternalOpen();

                FinishCreate(request, part.IsOpened ? part : null);
            }, RevResGroup.UI);
        }

        /// <summary>在宿主的默认挂点下创建</summary>
        public static void Create<T>(IRevUIPartHost host, Action<T> onCreated) where T : RevUIPart
            => Create(host, null, onCreated);

        /// <summary>
        /// 关闭自己。destroy = true 时连 GameObject 一起销毁，并还掉资源引用；
        /// destroy = false 只失活（适合"偶尔出现"的 Part，留着省一次加载）。
        /// </summary>
        public void ClosePart(bool destroy = true)
        {
            InternalClose();

            if (!destroy)
            {
                gameObject.SetActive(false);
                return;
            }

            ReleaseHeldResource();
            Destroy(gameObject);
        }

        // ============================================================
        // 框架内部
        // ============================================================

        internal void InternalInit(IRevUIPartHost host)
        {
            if (Host == host) return;                     // 已经初始化过（嵌套绑定时会重复进来）

            Host = host;
            Meta = RevUIPartMeta.Resolve(GetType());

            RevUIBindPlan plan = RevUIBindPlan.Get(GetType(), typeof(RevUIPart));
            RevUILog.Guard($"{GetType().Name}.绑定控件(Part)", () => RevUIBinder.Bind(this, transform, plan, host));

            RevUILog.Guard($"{GetType().Name}.OnPartInit", OnPartInit);
            RevUILog.Guard($"{GetType().Name}.OnBindView(Part)", OnBindView);
        }

        internal void InternalOpen()
        {
            if (IsOpened || (Host != null && !Host.IsHostOpened)) return;
            IsOpened = true;
            gameObject.SetActive(true);

            RevUILog.Guard($"{GetType().Name}.OnPartOpen", OnPartOpen);
            if (this == null || !IsOpened) return;
            InternalRefresh();
            if (this == null || !IsOpened) return;                            // 打开即画一次，免得"宿主忘了刷"

            // ★ 显示动画（一行预设；None = 不做，行为与之前完全一致）
            if (ShowAnimation != RevUIAnimPreset.None)
                RevUIAnim.Play(this, ShowAnimation, 0f, null, owner: this);
        }

        internal void InternalRefresh()
        {
            if (!IsOpened) return;                        // 没开着就别刷（浪费 + 可能碰到未激活节点）
            RevUILog.Guard($"{GetType().Name}.OnPartRefresh", OnPartRefresh);
        }

        internal void InternalClose()
        {
            if (!IsOpened) return;

            IsOpened = false;
            RevUIAnim.StopAllOf(this);                     // 与面板一致：关闭时不留以 Part 为 owner 的残余动画
            RevUILog.Guard($"{GetType().Name}.OnPartClose", OnPartClose);
            RevEvent.RemoveAllByOwner(this);               // 一行防泄漏（同面板）
        }

        private void ReleaseHeldResource()
        {
            if (!_holdsResourceRef) return;

            _holdsResourceRef = false;
            // 优先用加载时拿到的原始句柄归还：资源系统若已重置，也不会误扣同路径新句柄的引用。
            if (_resourceHandle != null)
            {
                RevResManager.DecRef(_resourceHandle);
                _resourceHandle = null;
            }
            else if (Meta != null) RevResManager.Release(Meta.Root, Meta.Name);
        }

        // ============================================================
        // IRevUIUserEvents（显式实现）
        // ============================================================

        void IRevUIUserEvents.DispatchClick(string nodeName) => RevUILog.Guard($"OnClick({nodeName})", () =>
        {
            OnClick(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.Click);        // 方法特性那条路
        });

        void IRevUIUserEvents.DispatchLongPress(string nodeName) => RevUILog.Guard($"OnLongPress({nodeName})", () =>
        {
            OnLongPress(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.LongPress);
        });

        void IRevUIUserEvents.DispatchLoosen(string nodeName) => RevUILog.Guard($"OnLoosen({nodeName})", () =>
        {
            OnLoosen(nodeName);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.Loosen);
        });

        void IRevUIUserEvents.DispatchToggleChanged(string nodeName, bool value) => RevUILog.Guard($"OnToggleChanged({nodeName})", () =>
        {
            OnToggleChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.ToggleChanged, value);
        });

        void IRevUIUserEvents.DispatchSliderChanged(string nodeName, float value) => RevUILog.Guard($"OnSliderChanged({nodeName})", () =>
        {
            OnSliderChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.SliderChanged, value);
        });

        void IRevUIUserEvents.DispatchInputChanged(string nodeName, string value) => RevUILog.Guard($"OnInputChanged({nodeName})", () =>
        {
            OnInputChanged(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.InputChanged, value);
        });

        void IRevUIUserEvents.DispatchInputEndEdit(string nodeName, string value) => RevUILog.Guard($"OnInputEndEdit({nodeName})", () =>
        {
            OnInputEndEdit(nodeName, value);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.InputEndEdit, value);
        });

        void IRevUIUserEvents.DispatchDropdownChanged(string nodeName, int index) => RevUILog.Guard($"OnDropdownChanged({nodeName})", () =>
        {
            OnDropdownChanged(nodeName, index);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.DropdownChanged, index);
        });

        void IRevUIUserEvents.DispatchScrollChanged(string nodeName, float x, float y) => RevUILog.Guard($"OnScrollChanged({nodeName})", () =>
        {
            OnScrollChanged(nodeName, x, y);
            RevUIWidgetEvents.Invoke(this, nodeName, RevUIWidgetEventKind.ScrollChanged, x, y);
        });

        // ============================================================
        // Unity 生命周期
        // ============================================================

        protected virtual void OnDestroy()
        {
            RevUIAnim.StopAllOf(this);
            ReleaseHeldResource();       // 兜底：被外部销毁时也要把资源引用还掉，否则会漏一份引用
            RevEvent.RemoveAllByOwner(this);
        }

        public override string ToString() => $"{GetType().Name}{(IsPrefabPart ? $"[{Meta.Key}]" : "（节点级）")}";
    }
}

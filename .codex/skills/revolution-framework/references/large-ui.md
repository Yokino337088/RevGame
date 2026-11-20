# 大型 UI 实现与验收

## 页面交割

同组页面声明 `ExclusiveGroup` 进入交割流程。旧页在资源准备、实例化、绑定与首屏等待期间保持可见可操作。候选借用发起时的 `RevScope`，在隐藏容器内准备。框架确认请求仍有效后换入新页、关闭旧页输入，再退场旧页。点击回调和整屏跳转应以交割结果更新当前页标记。

- 用 `RevUI.OpenTracked<T>(deferAssembly: true)` 取得可等待票据。需要数据时用 `OpenTracked<T, TData>(data, ...)`。同键在途请求合并并采用最近一次非空数据。
- 重写 `PrepareFirstScreenAsync(RevCancellationToken)` 仅等待首屏必需工作。跨帧后核对令牌。装配、布局和组件准备限制在当前候选子树；预备子树的 CanvasGroup 沿用父组的可见性和射线规则。
- 冷互斥页使用主线程装配队列。可以为已缓存的重 Prefab 选择分帧装配。每次 Unity 实例化不可抢占，仍需按需用 `RevUIPart` 创建次要子树。`MaxAssembliesPerFrame` 和 `AssemblyBudgetMs` 控制每帧的后续调度。
- 用 `TryGetOpen(id, out ticket)` 或开发态 RevMCP 的 `ui.open-state` 查阶段和结果。旧页的 `PreviousDisposition` 按退场异步推进到 `Pooled` 或 `Released`。候选失败或取消时检查 `ResourceDisposition` 与资源实际引用。调用者可用 `CancelOpen(id)` 撤销未交付的合并请求。

验收覆盖真实 AB 冷开、同键合并、同组快切、加载或首屏失败、关闭后回包、退域。检查旧页的真实射线、新页首次可操作、晚到的请求结果和资源归还。用开窗 ID 对齐各阶段，区分等待时间和实例化峰值。

## 域内面板缓存

- `KeepAlive` 仅适用于仍绑定有效域的面板。普通关窗走关闭转场，回池后再次打开需要同一个域句柄。异域请求命中同键的空闲旧实例时，框架销毁它并归还原租约。同名域重新进入也会取得新句柄。
- `RevScope.Exit` 撤销该域在途开窗，直接收尾活动、退场中及空闲面板。在资源步骤前执行 `OnClose`（每次关闭最多一次）、`OnRelease` 和租约归还。实际 GameObject 在 Unity 帧末销毁。跨场景遮挡可显式 `Detach` 并由流程负责关窗，关窗后会销毁。
- `RevUISetting.MaxCachedPanels` 默认每种一份，`MaxIdlePanels` 默认全局 32 份。超额时从最久未使用的空闲实例开始裁剪，降低全局额度立即生效，设为 0 可禁用空闲缓存。两项配置都在主线程调整。
- Unity `lowMemory` 和 `RevResAutoUnloader.RunOnce(force: true)` 走同一路径，先释放空闲 UI 的租约，再由资源门卫处理已归零的资源。活动页继续持有原租约，共享图集在有效引用期间保留。
- `RevUI.GetCacheSnapshot()` 与开发态 `ui.cache-state` 显示命中次数、实例 ID、域名及其进入时刻 `scopeEnteredTicks`、开窗 ID、缓存理由、最近 32 条处置和预制体剩余引用数。空闲理由为 `keep-alive`。处置原因含 `capacity`、`per-panel-limit`、`scope-exit`、`scope-mismatch`、`unscoped`、`low-memory`。`leaseReleased` 表示该实例已归还引用，同一预制体仍可有其他持有者。

在真实 AB 下重复进入、换页和返回。用实例 ID 区分命中与重新装配，比较同一个菜单的冷开和域内热开。压低额度验证 LRU，核对异域请求、关闭动画中退域、解绑面板关闭及低内存。返回后检查域内专用 Prefab 与 AB，确认共享图集仍可渲染。帧末销毁用下一帧断言。

## 固定行高虚拟列表

用 `RevUIVirtualList<T>` 接管已有竖向 ScrollRect 和顶部锚定的行模板。内容根用顶部拉伸布局，行距包含间隔。业务提交不可原地更改的只读快照，键在一次列表中稳定且唯一。布局或视口变化后调用 `RefreshViewport()`，离开界面调用 `Dispose()`。

绑定函数接收行和当前数据，返回本次事件或租约的清理函数。跨帧时可按需取得行取消令牌，也可保存 `BindingVersion` 并用 `row.IsCurrent(version)` 判定结果。选择和导航使用数据键；索引用来定位可见窗口。替换数据时框架尽量保留顶部键及行内偏移，已删除的键回退到裁剪后的原索引。

验收同视口、同模板、同一份数据滚动与刷新。记录实体行峰值、帧间隔 P95 和 GC 有效样本。覆盖大跨度滚动、排序、顶部删除、真实点击和旧绑定回包。

## 弹窗调度

一个 `RevPopupQueue` 负责一个展示通道，捕获当前或显式的 `RevScopeHandle`。业务通过 `SetPhase` 宣告准入，用 `Enqueue(key, phase, priority, waitSeconds, show, hide)` 受理窗口。空阶段可随时加入候选，数值较大的优先，同值按入队顺序。活动窗口占用通道直至结束，阶段变化时撤销失去准入的活动窗口。

展示函数用现有面板或 Part。对应窗口的按钮用 `TryComplete(ticket, confirmed)`，外部关闭用 `Cancel(ticket)`。检查 `WaitAsync()` 终态为 `Confirmed` 再执行业务。隐藏回调应幂等。宿主退出时 `Dispose()`，域退出也会撤销在途窗口。

验收优先级、同键去重、错阶段过期、确认与拒绝、活动和等待请求随域退出而回收。Toast 保持替换语义，使用独立通道。

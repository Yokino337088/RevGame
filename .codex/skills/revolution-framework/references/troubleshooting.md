# 排错速查（Troubleshooting）：症状 → 最可能原因 → 先查什么

> **用法**：先在 §1 按"你看到的现象"找行（按模块分组）；找不到再走 §2 的通用诊断阶梯。
> 来源：`Revolution.Document/bug修复日志/` 6 份日志 + 各《使用说明》的 FAQ。
> **重要前提**：下表里"现行做法"的缺陷**多数已在框架里修好**——如果你在**最新代码**上仍看到这个现象，更可能是**业务侧绕过了契约**（例如绕过管理器销毁面板、跨 Play 会话保存旧句柄）；如果你的框架版本较旧，先看 `CHANGELOG.md` 是否已修。

## 0. 先别猜：一分钟诊断阶梯（§2 的精简版）

1. `RevLog` 按 tag 过滤（`Res` / `Scene` / `ActionSequence` / `HotUpdate` / `UI` / `Strip` …），或 `RevLog.Dump(200)` 取出事前现场。
2. 句柄 / 返回值原因：`handle.ErrorReason`（资源）· `RevGMResult.Message`（GM）· `RevHotUpdate.LastError` + `Dump()`（热更）· `RevLog.SinkErrors`（日志通道坏了）。
3. 自检计数：`RevTimer.Count` / `RevMono.Count` / `RevEvent.GetListenerCount(名)` / `RevResManager.CachedCount` + `GetRefCount` / `RevPool.DumpStats()` / `RevUI.DumpStats()` / `RevAppLifecycle.PauseCount` / `RevStripGuard.Dump()`。
4. 区分"编辑器 vs 真机"：`RevResBootstrap.UseABInEditor = true` 在编辑器里走一遍真链路。
5. 复现：开对应 Demo 场景点按钮；或写一条 GM 命令在面板里跑。

---

## 1. 症状速查表

### 1.1 通用高频（先看这几条，覆盖大半"莫名其妙"）

| 症状 | 最可能原因 | 先查 / 做法 |
|---|---|---|
| 编辑器好好的，真机大面积 `FileNotExist` | 编辑器直读不走 AB；资源**漏标 AB / ResMap 没重生成** | RevAB「检查」页签：漏标 + **ResMap 一致性检查**；`UseABInEditor = true` 复验 |
| 热更成功了但内容还是旧的 | ① 编辑器还在直读；② 更新后没 `Release` 就 `Load`（命中旧缓存） | `UseABInEditor = true`；先 `RevResManager.Release(根, 名)` 再 `LoadAsync` |
| 对象销毁后回调 / 计时器 / 每帧逻辑仍在跑 | 没传 `owner`；匿名 lambda 注册事件无法按委托移除 | `RevTimer.CancelAllOf(this)` / `RevMono.RemoveAllOf(this)` / `RevEvent.RemoveAllByOwner(this)` |
| 事件"没人听" | 事件名拼错；或同一事件名注册了两种签名 | `DispatchEvent` 返回值 `0` = 没人听；`RevEvent.LogNoListener = true`；`GetListenerCount(名)` |
| 同步 `Load` 返回 null / `RevUI.Open<T>()` 返回 null | 真机首次是异步（AB 要加载） | `LoadAsync` / `OpenAsync` / 先 `Preload` |
| 暂停游戏后倒计时不动 | 默认 `Scaled` 域被 `timeScale = 0` 冻结 | UI 倒计时 / 超时用 `RevTimeDomain.Unscaled` |
| 第二次 Play 时重复注册报错 / 监听翻倍 / 状态错乱 | **关闭 Domain Reload**，静态字段没清 | 框架各模块已带复位钩子；你自己的静态容器照 `Support/*UnityHooks.cs` 写 |
| 编辑器正常，真机刚体不动 / 射线恒 false / 面板空白 / 没声音 | **IL2CPP 代码裁剪**删了 native wrapper | `RevStripGuard.Dump()`；主推 `Managed Stripping = Disabled`；否则生成 `link.xml` |
| 数据改了界面没变 | 框架不监听数据对象内容 | 原地改后手动 `RefreshView()`；或换新对象走 `SetData` |
| 切后台回来连接断了 / 声音没了 | 没订阅生命周期 | `RevAppLifecycle.Resumed` + `LastBackgroundSeconds`；`PauseCount` |
| 小游戏上一启动就崩 | 用了 `Thread` / `Task` / 文件日志 | 异步用 `RevTask`；`RevLog.SinkErrors` |
| 编辑器里热更 404 / AB 读不到 | 编辑器平台名恒为 `PC`，与打包产物目录名不一致 | 编辑器调试把 Build Target 切回 Standalone，或配 `PlatformOverride` |
| GM 命令注册报"已经注册过" | 命令名归一化后变成空名（`/` / `///` / `" / "`） | 命令名不能为空 / 含空白 / 只有分隔符 |
| Unity 版本 / 项目设置 guard 报错，或本地 `ProjectVersion.txt` 与推荐版本不同 | 本地编辑器记录与 guard 契约未一致，不一定是本次改动引入 | 推荐 Unity 2022.3 LTS；`ProjectVersion.txt` 已忽略，不用 `git checkout` 恢复；按 `verify-and-test.md` 的版本 / guard 专题定位现有冲突 |

### 1.2 UI

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| 面板复用后残留上次数据 / 状态 | 没走 `OnReuse` 重置自己缓存的字段；`Data` 框架会清但你的字段不会 | `OnReuse` 里清；`OnRefreshView` 里先判 `Data == null` |
| 泛型面板 `Data` 恒为 null / 旧值 | （旧版缺陷，**已修**：`OnDataSet` 统一出口）；仍出现 → 版本太旧 | 看 `CHANGELOG`「UI 系统」；确认走的是 `SetData` / `Open<T,TData>` |
| 同互斥组 A 还在加载时开 B，加载完 A、B 同时出现 | （已修）加载中的请求没登记进互斥表 | 现行：开同组新面板会取消其它未完成的同组请求；查 `ExclusiveGroup` |
| 只配了 `HideAnimation` 的 KeepAlive 面板重开后透明 / 缩小 / 在屏外 | 复用时没恢复动画基准态 | 现行 `InternalReuse` 会恢复；**建议同时配 `ShowAnimation`** |
| `ShutdownAll()` 后动画回调仍在跑 / 清理中报错 | Shutdown 没先停动画（已修） | 现行 `InternalRelease` 幂等、先取消打开请求 |
| 在 `OnBindView` / `OnInit` 里调 `ShutdownAll()`，新实例残留 | 实例在钩子执行前尚未进索引（已修） | 现行绑定后会检查"已释放"；别在这些钩子里做全局清理 |
| 反复进 Play 后面板 prefab 引用计数不归零 | 关闭 Domain Reload 后对象成假 null，找不回资源路径（已修） | 现行用"instance ID + 资源路径"租约，`ShutdownAll` 与新会话按路径快照释放 |
| 业务 `Destroy(panel.gameObject)` 后再开同类面板仍命中旧实例 | 绕过管理器销毁 | 现行 `OnDestroy` 会通知管理器；**别绕过**，关自己用 `CloseSelf()` |
| Part 里按钮长按 / 松开被父面板收到，或同一事件触发两次 | 父面板扫整棵后代树，继电器只有一个 receiver（已修） | 现行取最近的 `IRevUIUserEvents` 所有者 |
| 场景里有未激活 EventSystem，运行时又多一个，输入重复处理 | `EnsureEventSystem` 只找 active（已修） | 现行含未激活对象；`RevUISetting.AutoCreateEventSystem` |
| 自定义动画 `Wrap=Once` 但 `Loops=-1` 不结束 | 引擎对所有 Wrap 都检查 `Loops`（已修） | 现行 `Once` 忽略 `Loops` |
| 二次 Play 后 UI 动画不推进 / 旧句柄误操作新会话动画 | 驱动被复位清空；动画 ID 计数重置（已修） | **不要跨 Play 会话保存旧动画句柄** |
| 重写 `PlayOpenTransition` 后面板永远停在 Opening，或 `PlayCloseTransition` 后一直 Closing | 对应钩子漏调 `onDone` | 开、关动画都必须在完成时调用 `onDone?.Invoke()`；自定义转场优先成对实现 |
| 自定义钩子和 `ShowAnimation` / `HideAnimation` 播了两段动画 | 两套生命周期动画串行执行 | 自定义转场优先只用 `PlayOpenTransition` / `PlayCloseTransition`；标准预设才单独用 `ShowAnimation` / `HideAnimation` |
| RevGM 面板点命令/“填入命令”后输入框仍是旧内容 | IMGUI TextField 保留焦点时内部 TextEditor 缓存旧文本，下一次 OnGUI 又覆盖程序设置的新命令 | 填入前先释放 `GUIUtility.keyboardControl`，更新输入状态后在下一次 OnGUI 请求搜索框焦点；不要只改 `_input` |
| 一直 `RefreshView` 死循环 / 每次刷新发一次请求 | 在 `OnRefreshView` 里发请求或改数据 | 请求放 `OnOpen` / 交互回调 |
| 竖屏下 UI 仍按横屏缩放，或旋转后 Match 不符合预期 | 方向设置过晚、`UseDesign` 保留原设计，或 Scaler 不是框架支持的模式 | 首次 `Open` / `Preload` 前选 `CanvasOrientation`（默认 `Auto`）；横屏基准转竖屏换位为 `1080×1920`、Match 默认 `0`，转回横屏恢复设计 Match；见 `ui-and-layering.md` §1.5 |
| Boot 已设 `Portrait`，Prefab 编辑预览仍横屏，或手机没锁竖屏 | Prefab Mode 不走 `RevUIRoot` 运行时链路；UI 设计方向不控制设备方向 | 手动配预览 Scaler / Game 竖屏，或运行 `RevUI.OpenAsync` 真链路；设备方向在 Player Settings / 平台配置，安全区和布局重排另做 |

### 1.3 输入

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| 按键没反应 | 动作没 `Bind`（**不报错，永远返回 false**） | `RevInput.Bind(...)`；`FindConflicts()` |
| 弹窗期间 ESC / 返回键也失效 | `Block(World)` 覆盖所有动作相位，**无系统动作例外** | 别依赖 ESC 例外；弹窗用 `OpenScope().Block()`，不要停整个模块 |
| `ResetAll()` / 切后台后没有 `OnReleased` | 静默复位，不合成释放事件 | 失焦收尾订阅 `RevAppLifecycle` |
| 回调里退订其它处理器，同帧后面订阅者漏收 | 派发缓冲，增删**下一帧生效** | 别期待当帧增删立刻生效 |
| 改键存档读回后连发 / 死区变了或加载失败 | （已修）旧版小数位截断、非法值被吞 | 现行 `SaveBindings ⇄ LoadBindings` 无损；`LoadText` 严格校验；看报错行号，整次拒绝并保留旧绑定 |
| `timeScale = 0` 时连发仍触发 | （已修）旧版连发用 realtime | 现行按 `snapshot.DeltaTime`；`SetRepeat(delay ≤ 0)` = 关连发 |
| 屏蔽某根手指后仍触发 Tap/Swipe/Pinch | （已修）手势识别读了未过滤快照 | 查 `Block(Pointer, pointerId)` 与屏蔽谓词 |
| 双指缩放出现约 180° 虚假旋转 | （已修）取前两根指针，顺序变连接向量反向 | 现行稳定选 ID 最小的两根；换指重建基线 |
| 删除动作再建同名动作，旧回调还在触发 | （已修）`RemoveAction` 不清订阅 | 现行同步清 Pressed / Released / Repeat / Axis 订阅 |
| `OffAxis(axis, null)` / `OffRepeat(action, null)` 后回调仍在 | （已修）旧版只删一个 | 现行 null 退订删光所有匹配项 |
| `OpenScope` 的 `Dispose()` 后输入仍被屏蔽 | Dispose 后仍注册（已修） | 现行已 Dispose 的 Scope 拒绝 `Block` 与订阅；别在 Dispose 后异步注册 |
| 在 Poll / 输入回调里调 `RevInput.Tick` 抛"重入" | 禁止 Tick 重入（设计） | 检查采集委托与回调里有没有调 Tick |

### 1.4 计时器 / 公共 Mono / 异步

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| `SyncServerTime` 后 `At` 计时器第一帧就提前触发 / `Left` 偏大 | （已修）校准锚点取了 Tick 的 realtime | 现行用 `RealtimeProvider`（`Time.realtimeSinceStartupAsDouble`） |
| `At` 返回空句柄 | 没校准：原因 `NoServerTime` | 先 `SyncServerTime`；`At` / `SyncServerTime` 统一转 UTC（别传 `DateTime.Now`） |
| 回调里 `Restart()` 想"重新计时"被同帧回收 | （已修）Restart 现撤销待回收标记 | 现行重试类写法可用 |
| 回调 / 订阅者抛异常后一次性计时器回调丢失、后续域不推进 | （已修）上报环节自己抛错，`_inTick` 卡 true | 现行 try/finally 复位，三个域各自保护；看 `RevTimer.Failed` / `OnException` |
| 传 NaN / Infinity 增量后所有计时器永不触发 | （已修）只拦 `< 0` | 现行把非有限值当 0；查调用 Tick 传入的 dt |
| 对已关闭的 Scope 调 `Every` 后计时器常驻直到 `Overflow` | （已修）已关闭 Scope 仍可创建 | 现行返回空句柄；**检查返回句柄是否为空** |
| `times = -5` 变无限循环 | （已修）负数当无限 | 现行只接受 `-1` 表示无限，其它负数报 `InvalidDuration` |
| 只手动调 `TickFixed` 后，UI 倒计时 / After / At 静默不走 | （已修）Update 与 FixedUpdate 共用一个手动驱动标记 | 现行两路各自独立（`ManualRenderDriven` / `ManualFixedDriven`） |
| 一个 `Failed` / `OnException` 处理器抛错，同相位其它监听者不执行 | （已修）报告器自己抛错中断相位 | 现行逐处理器隔离；非法 `RevMonoPhase` 报 `InvalidPhase` |
| Dispose 后的 Scope 仍有监听 / 协程在跑 | Scope 释放后仍可添加（已修） | `RevMono.RemoveAllOf(scope)`（同时移监听并停协程） |
| 任务早已成功，之后 await 又抛异常 / 丢了结果 | （已修）迟到的 `SetException` 改写异常字段 | 现行只接受第一次完成 |
| `await WhenAll(...)` 走了成功分支，但有子任务失败 / 被取消 | （已修）旧版只计数不查异常 | 现行等全部结束后有异常就整批失败，空列表立即成功 |
| 异步流程"没跑完就切场景了" | 续体每帧限量（128）；长流程被切场景踩空 | `RevCancellationTokenSource` + `ThrowIfCancelled()`；宿主销毁时取消 |

### 1.5 资源 / 对象池 / 场景

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| 同路径换一种类型加载返回 null 且没原因 | 命中缓存但类型不兼容 | 失败原因 `TypeMismatch`；用 `LoadHandle<T>` 看 `ErrorReason` |
| 同步加载失败并 `Release` 后，再 `Load` 仍一直失败 | （已修）归零的失败句柄仍留缓存 | 现行 `RefCount ≤ 0` 的失败句柄会摘除并重试；仍被持有的不会被替换 |
| 取消加载 / 卸载资源组后，其它资源共享的 AB 依赖被提前卸载 | （已修）取消路径与句柄各释放一次依赖 | 现行句柄依赖快照所有权；`UnloadGroup` 会取消在途任务 |
| 引用计数不归零 / 资源没被域回收 | `Load` 没 `Release`，或没有活动域 / `TakeOverOwner` 关闭 / `Reclaim` 未配置而仍为 `Unknown` | `GetRefCount` / `CachedCount` / `handle.Group`；仅当前域启用 `TakeOverOwner`（默认 true）且 `Reclaim` 非 `Unknown` 才自动补分组；`Reclaim` 默认 `Unknown`，关域不按组回收；跨域共享审查 `Resident` / owner 与首次分组归属，慎用 `force` |
| Android 打 AB 被 Shader 校验拦下 | 显式材质 Shader 引用缺失，或无可打包资产路径且不在 Always Included | 查看 `ABValidator.ValidateAndroidShaders` 错误的材质 / 来源；修实际 Shader 或所需的 Always Included 项，不把全部 Shader 强加，也不改默认 UGUI 无显式材质；见 `editor-tools.md` §2 |
| Android Prefab 已加载却变紫 | Shader 缺失 / `Hidden/InternalErrorShader` / `isSupported == false`，或关键词变体 / 渲染管线 / 图形 API 问题 | 先查 tag `Res` 材质 Shader 诊断；确认 Player / AB 平台与管线一致，再查 Shader 包依赖和变体。前置校验不证明真机兼容；Shader 问题不等于包没加载 |
| Android 主包 / 依赖包 / 目标包加载失败 | 产物平台、热更版本或解析后的来源路径错误 | `handle.ErrorReason` + tag `Res` 的主包 / 依赖包 / 目标包路径；先核对包文件与来源，不将 `BundleLoadFail` 当成 Shader 变体问题 |
| `RevPool.GetAsync` 的句柄释放后 prefab 提前卸载 / 销毁池时多减引用 | 调用方与池共用一份引用（已修） | 用完 `RevResManager.DecRef(handle)`；现行新建池会额外 `AddRef` 独立持有 |
| `Shutdown(group)` 后同路径取对象仍命中旧池 | `ClearGroup` 只清空闲实例，池与引用仍保留 | 现行顺序：先 `RevPool.DestroyGroup(group)`，再 `Shutdown(group)` |
| prefab 被替换 / 卸载后，`Get` 到旧 prefab 的实例 | （已修）`Rebind` 后旧代借出实例又被归还 | 现行 `Rebind` / `Dispose` 强制销毁旧代活动对象 |
| 复用对象的 `OnEnable` 读到上次残留；`ActiveCount` 偏大 | （已修）`OnPoolGet` 晚于 `OnEnable`；延迟回收对象被算活动 | 现行先 `OnPoolGet` 再 `SetActive(true)`；延迟对象只计入 `Recycling` |
| 关闭 Domain Reload 二次 Play 后资源请求一直不启动 | 加载泵 `_pumping` / `_loading` 状态残留（已修） | 现行代际复位；同步 Loader 抛异常转 `BundleLoadFail` 并记日志 |
| 同步切场景后在 `OnLoaded` 里读 `CurrentName` 是旧值 | （已修）旧版刚调 `LoadScene` 就广播 | 现行等 `sceneLoaded` 匹配目标场景后才 `Done` 并广播 |
| 切场景后 `IsLoading` 一直 true，之后请求全被拦 | （已修）发起加载抛异常（如 buildIndex 越界）无收尾 | 现行转统一失败收尾：`State = Failed` + `OnLoadFailed` |
| 一个 `OnProgress` / `OnLoaded` 订阅者抛异常，其它 UI 不再更新 | （已修）一次性调用所有订阅者 | 现行 `Guard` 逐个调用并分别捕获；看日志里被记录的异常 |

### 1.6 音效 / 服务定位器 / 状态机 / 动作序列

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| 请求 BGM 却播出同名 SFX / 误判 BGM 加载失败 | （已修）缓存键只用资源名 | 现行键为"资源根目录 + 资源名" |
| 音频加载中 `Unload`，资源一直留在缓存 | （已修）缓存项被删但引用没归还 | 现行 Entry 保存 `RevResHandle`，`Unload` / `UnloadAll` 逐条归还 |
| `PlayOn` 的目标在加载期间被销毁，声音从世界原点响起 | （已修）目标变假 null 被误当 `PlayAt` | 现行回收槽位并报 `NoTarget`、返回空句柄；**检查返回句柄是否为空** |
| BGM 歌单一首结束后停住 | `VoiceFinished` / `Failed` 订阅者抛异常（已修） | 现行逐订阅者捕获 |
| 销毁容器后文件 / 连接泄漏 | 某个服务 `Dispose` 抛异常中断后续释放（已修） | 现行逐个捕获，最后汇总 `AggregateException`；查该异常内容 |
| 异步切换 B 途中请求切回当前 A，A 的 `OnExit` / `OnEnter` 被重复执行 | （已修）取消 B 后仍把 A 当一次真实切换 | 现行只取消旧切换并保持当前状态 |
| 栈状态机 `Change(A)` 后栈里同一对象出现两份，Pop 回调错乱 | （已修）把栈下层同一实例再压栈顶 | 现行 `Change` 只替换栈顶，目标在下层会报错；需要多个同类状态用工厂创建不同实例 |
| 序列里 `StopAll` / `Clear` / `Dispose` 导致 Tick 越界 / 已归池实例被推进 | （已修）Tick 按下标遍历 | 现行按快照遍历，期间取消在本次 Tick 末尾收尾 |
| `PlayAsync` 永不完成 | `RunFinished` 订阅者抛异常（已修） | 现行各环节独立 try/catch，等待者必被唤醒 |
| 序列等待超时 / 异步任务失败 / 收尾异常像"被吞了" | 旧版静默 | 现行经 `RevLog` 输出（tag = `ActionSequence`，写明序列名与步骤） |
| 嵌套序列的 `ctx.Source` 不对 | 嵌套未沿用父上下文 | 用 `Play(definition, context)` 重载 |

### 1.7 热更

| 症状 | 最可能原因 | 先查 / 现行做法 |
|---|---|---|
| Android 真机上未热更过的内置包加载失败 | 首包没落地 `persistentDataPath/RevHotUpdate/_builtin/Android/`（已修） | 看该目录有没有包；现行在 `RunCheckAsync` 落地 |
| 第二轮热更后，未变包加载失败 | 未变包未按目标 SHA 补入完整候选，或版本上下文不一致 | 查 `ValidateSnapshotAsync` 与四钩子；有效清单缺包必须拒绝激活，不任意回读旧同名包；见 `hot-update.md` §3/§5.1 |
| `CheckAsync` → `UpdateAsync` 后加载仍是旧内容 | 钩子/映射/依赖/缓存键未一致更新，或编辑器仍直读 | 查 `RevHotResBridge.Install()` 四项：路径、缓存键、依赖、ResMap；同时核对 `UseABInEditor` |
| 同版本重启不能续传，或换源后续拼坏内容 | `.part.meta` 文件身份/完整 URL 不一致 | 只有版本、包名、大小、SHA 与 URL 一致才复用；200/416 从零请求，取消保留同身份分片；见 `hot-update.md` §5.1 |
| 校验通过但业务资源仍不可用 | SHA + 内置主包校验不等于业务资源加载 | 当前无 `ProbeBundles` 代表资源探针，必须做 Unity/真机实际加载验收；不把 Init 当成业务可用 |
| Reset 后旧钩子仍挂着 | 当前桥 Reset 只复位安装标志 | 没有解除四委托的完整卸载入口，不把 Reset 当作卸载；见 `hot-update.md` §2 |
| 玩家取消下载后却提示更新成功，资源半新半旧 | （已修）`WhenAll` 吞掉取消异常 | 现行取消时不打 `.stamp` 不切版本；查 `DownloadSession.Cancelled` |
| 新增 / 变更的包资源 missing，时好时坏 | （已修）清单依赖图没被消费 | 查 `RevABLoader.DependenciesOverride` / `ResolveDependencies` |
| WebGL 重启 / 同会话二次热更后新增资源加载不到 | 内存映射表为空或仍是旧表（已修） | 查 `HasMemoryResMap` / `EnsureWebGLResMapAsync` 的 `force` |
| 下载进度条几乎不动、跳档，速度恒 0 或抖动 | （已修）并发共用一个进度字段 | 现行 `RevHotProgressAggregator` 按文件名分账 |
| 弱网下热更永久卡死 | 超时 / 退避配置不合法 | `TimeoutSeconds ≥ 1`；`RetryBackoffMs` 在 0..60000（配置期 `Validate()` 校验） |
| 首启且大版本不匹配（强更）提示可用但资源加载失败 | （已修）强更分支没有 `ReinitResourceSystem` | 查 `ForceUpdateRequired` 分支；`check.Config` 为空报 `ConfigInvalid` |
| 清单被 CDN 截断仍打完成标记 | （已修）未校验 `@bundleCount` | 现行数量不符整体失败并保持旧版本 |
| 小游戏上配置报错 | 小游戏禁明文 http、并发 > 4 | `RevHotConfig.Validate()` 在配置期拦；用 https，并发 ≤ 4（默认 2） |

---

## 2. 通用诊断阶梯（找不到对应行时）

```text
Step 1  现象在编辑器 / 真机都有，还是只在真机？
        └─ 只在真机 → 先查：资源漏标 / ResMap 不一致 / 代码裁剪 / 平台名 / 同步 API 假设（见 §1.1）
Step 2  日志里有没有人话？
        └─ RevLog.Dump(200)；按 tag 过滤；RevLog.SinkErrors > 0 说明某个日志通道在坏
Step 3  有没有"空句柄 / null / false"被忽略？
        └─ RevTimer / RevSound / RevPool 的失败都表现为"空句柄 / null"，原因在句柄字段或 Failed 事件里
Step 4  生命周期：对象销毁 / 面板关闭 / 切场景时，有没有清理？
        └─ owner + RemoveAllOf / CancelAllOf / RemoveAllByOwner；资源成对 Release
Step 5  是不是触发了"框架明确不做"的事？
        └─ decision-guide.md §2（例如：以为有 AddOnce / 以为 Block 会放行 ESC / 以为 Timer 会自动补偿卡顿）
Step 6  复现并缩小：开对应 Demo 场景，找最接近的那个按钮；再不行写一条 GM 命令复现
```

## 3. 用这张表给别人（或 AI）报 bug 时要带什么

- 现象（能观察到的）+ 编辑器 / 真机 + 是否开了 Domain Reload / `UseABInEditor` / 代码裁剪；
- 相关 tag 的日志、`handle.ErrorReason` / `LastError`；
- 相关自检计数（`CachedCount` / `GetRefCount` / `DumpStats` …）；
- 最小复现：基于哪个 Demo 场景改的。

# 高频踩坑集（现象 → 原因 → 正确做法）

> 全部来自框架的代码注释 / 文档 / 真实修过的 bug。写代码前扫一遍，能省掉大半返工。

## 1. "编辑器里一切正常，真机大面积失败"

- **现象**：资源/配置表在编辑器读得到，出包后 `FileNotExist`。
- **原因**：编辑器默认走 `RevEditorResPolicy`（AssetDatabase 直读），**AB / Resources 根本不参与**；资源没打 AB 标记（漏标）或 ResMap 没重新生成，编辑器里照样"能用"。
- **做法**：新增资源 → 放进 resRoot（默认 `Assets/GameRes`）→ 设 `assetBundleName` → 跑打包 / "仅生成映射"；打包前看「检查」页签的漏标报告；联调时把 `RevResBootstrap.UseABInEditor = true` 走一遍真链路。

## 2. "热更成功了，但内容还是旧的"

- **原因**：编辑器还开着直读模式；或者资源有缓存 + 引用计数没释放。
- **做法**：`RevResBootstrap.UseABInEditor = true`（菜单 `Revolution.Tools/资源/AB 加载模式（编辑器）`）；加载前先 `RevResManager.Release(根, 名)` 再 `LoadAsync`；必要时 `RevResManager.UnloadAll()` 模拟重启。

## 3. 事件/计时器/每帧回调泄漏（对象已销毁还在被调）

- **原因**：匿名 lambda 注册后**无法按委托移除**；循环计时器没留"能停"的路。
- **做法**：先定归属：对象级 owner + 对应清理、模块 OpenScope，或启用 TakeOverOwner 的活动域兜底 + Exit。显式 owner 优先，需跟域退出的对象用 Attach；匿名 lambda 可按 owner/域清理，按委托移除则保留同一引用。

## 4. 事件"没人听"或"签名不匹配"

- **原因**：事件名拼错（框架不会替你检查）；或同一个事件名被注册成了两种签名。
- **做法**：事件名常量集中一个类；`DispatchEvent` 的返回值 **0 = 没人听**，直接 `RevLog.Warn` 出来（`RevEvent.LogNoListener = true` 也能自动提示）；**同一事件名只允许一种签名**。

## 5. 关闭 Domain Reload（Enter Play Mode Options）导致的跨 Play 残留

- **现象**：第二次 Play 时命令重复注册报错 / 事件监听翻倍 / 热更状态错乱。
- **原因**：静态字段不清空。
- **做法**：框架各模块都带了 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 复位钩子（RevEvent / RevGM / RevHotUpdate…）；**你自己写的静态容器也要照做**（`Assets/Revolution/Runtime/*/Support/*UnityHooks.cs` 是范例）。

## 6. 资源引用计数不归零 → 永远不卸

- **原因**：取用未释放，或域外/关闭兜底/未配回收组时仍传 Unknown。
- **做法**：引用按契约配对；活动域 `TakeOverOwner=true` 且 `Reclaim` 非 Unknown 才把 Unknown 加载补到域回收组。关域按既有顺序清池再清资源，不把全局共享组作为战斗回收组；跨域共享明确 Resident/生命周期，慎用 force。

## 7. 同步 `Load` 拿不到资源 / `RevUI.Open<T>()` 返回 null

- **原因**：真机首次是异步的（AB 在包外/包内需要加载）；同步 API 只在"已缓存 / 编辑器直读"时有效。
- **做法**：真机首次一律 `LoadAsync` / `RevUI.OpenAsync`；想秒开先 `Preload`。

## 8. `RevLog.Debug` 在正式包里"丢了日志"

- **原因**：`Debug` 是**编译期删除**（连参数表达式都不求值）。
- **做法**：要留就定义 `REVLOG_DEBUG`；热循环里先 `RevLog.IsEnabled(...)` 再拼字符串。

## 9. 时间域选错 → 暂停游戏后倒计时不动（或该冻结的没冻结）

- **原因**：默认 `RevTimeDomain.Scaled` 受 `timeScale` 影响。
- **做法**：UI 倒计时 / 超时 / 暂停菜单用 `Unscaled`；`RevTimer.At(...)` 前必须先 `SyncServerTime`；`RevTimer.Paused` 是**整模块**开关。

## 10. 对象池"上次的数据还在"

- **原因**：池对象既不销毁也不重置；`OnPoolReturn` 没清字段。
- **做法**：`IRevPoolable.OnPoolReturn` 里把字段清干净；`Return` 后不要再访问该对象（可能已被复用）；WebGL 一律 `RevPool.GetAsync`。

## 11. ResMap 读到空表 / 改了路径读不到表

- **原因**：表是**引导链起点**，运行时路径（`"ResourceSystem/ResMap"`）与编辑器路径（`ABBuildSetting.MapAssetPath`）是**两处平行硬编码**，改一个忘一个就落到"空表 → 全部 Resources 兜底"（编辑器里看不出问题）。
- **做法**：只改一处时同步另一处；表缺失时框架**不崩**但语义变了，排查时先确认表在不在。

## 12. 文件夹 `.meta` 位置写错

- **现象**：Unity 里文件夹没有 meta、或每个人生成一套不同 GUID 导致仓库天天冲突。
- **做法**：**必须是同级 `<文件夹名>.meta`**（写在文件夹内部的是废文件，Unity 不认）。

## 13. 热更包被实现成"只从持久化读" → 新装机必崩

- **原因**：新装机 / 清数据时 `{persistentDataPath}` 是空的，所有"热更包"全部 `BundleLoadFail`。
- **做法**：坚持**覆盖式语义**（持久化优先 → 回退 StreamingAssets 首包）；把"标记"理解成"允许被覆盖"而不是"必须从某处读"。

## 14. 替换/更新包之后"没生效"或替换失败

- **原因**：Windows 上文件被 `LoadFromFile` 映射后会**锁文件**；即使替换成功，**已加载的包仍在内存里**，本次进程不会生效。
- **做法**：`RevResBootstrap.Instance.ShutdownAll()` → 换文件 → `Init()`；开发期手工验证就"搬完包重进一次 Play"。**绝不在包被使用时覆盖文件**。

## 15. GM 命令注册报"已经注册过了"（但你没注册过同名命令）

- **原因**：名字归一化（`Trim().Trim('/')`）后变成空名（`/`、`///`、`" / "`），第一次注册出一个空名命令，第二次才报重名 —— 真正原因（名字不合法）被盖掉。
- **做法**：命令名不能为空、**不能含空白字符**、**不能只有分隔符**；分组用 `/`（如 `经济/加金币`）。

## 16. 服务定位器相关的坑

**`RevServices`（默认入口：一行注册 / 取用 / 注销）**

- **注册了却忘了注销** → 服务一直占着内存（连同它引用的所有对象）。跟着模块 / 场景走的服务带 `owner: this`，`OnDestroy` 里 `UnregisterAllOf(this)`；`Debug.Log(RevServices.Dump())` 查"谁还占着"。
- **重进场景 / 重开模块报"已经注册过了"** → 上一次没注销。补注销，**别用"先试着注销一下"掩盖**（会掩盖真正的泄漏）；想换实现先 `Unregister`。
- **把取到的服务长期存成字段** → 注销后字段指着一个已 `Dispose` 的对象。要用就现取（一次字典查找，0 分配）。
- **"取不到服务"但明明注册过** → 看错误信息里有没有"★ 它曾经被 Unregister 注销过"：有 = 别处注销了它（查 `Unregister<T>()` / `UnregisterAllOf(...)` 的调用点），不是没注册。
- **可 Tick 的服务不走 `OnTick`** → 服务是**第一次被取用时才创建**，创建后才被驱动；启动期就要激活，类型注册用 `eager: true`（或注册后立刻 `GetRequired` 一次）。
- **监听引擎首次事件的服务漏掉回调（例如 SpriteAtlas 的 `atlasRequested` 导致 UI 白图）** → `Register<I, T>()` 默认只登记、不会调用构造函数；必须在首次事件可能发生前 `Register<I, T>(owner: this, eager: true)`，或紧跟注册调用一次 `GetRequired<I>()`。`eager` 仅适用于类型对类型重载，创建失败会撤销本次注册并保留原始错误；默认懒创建失败仍可重试。
- **`owner` 传了值类型 / 临时对象** → `UnregisterAllOf` 返回 0。`owner` 传引用类型且前后是同一个对象（通常 `this`）。
- **`Register<I, T>()` 编译不过** → `T` 必须有**公开无参构造**；需要构造参数就用工厂重载 `Register<I>(s => new T(...))`。
- **想要"每局一份"** → `RevServices` 只有全局一份；改用 `RevServiceLocator` 的 `AddScoped` + `CreateScope()`。

**`RevServiceLocator`（Builder 写法：作用域 / 单测隔离）**

- 组装完成后还想去注册 / 注销服务 → `Build()` 之后**不可改**（想注销单个服务请用 `RevServices`）；
- 在根容器上取 `AddScoped` 的服务 → **直接报错**（必须放进 `CreateScope()` 的 `using`）；
- `AddSingleton(instance)` 的服务没人释放 → **容器不管你自己创建的实例**（"谁创建谁释放"）；
- 忘了缓存 `Build()` 的结果 → 重复 `Build` 报错 / 白付装配成本（放进 `static readonly`）。

## 17. 异步任务"没跑完就切场景了"

- **原因**：`RevTask` 的续体每帧**限量执行**（默认 128）；长流程里切场景/关面板会让后续步骤踩空。
- **做法**：长流程给"取消源"（`RevCancellationTokenSource`），关键节点 `ThrowIfCancelled()`；宿主销毁时取消；不要在不 await 的任务里捕获已销毁对象。

## 18. UI 面板复用后状态错乱

- **原因**：面板池取出的实例带着上一次的状态；没走 `OnReuse` 钩子；数据没清。
- **做法**：重置逻辑写在 `OnReuse`；关闭前清数据（框架会清 `Data`，但**你自己缓存的字段要清**）；弹窗/常驻 HUD 正确设置 `InBackStack` 与 `RevUICacheMode`。

## 19. 编辑器里平台/目录对不上（热更 404、AB 读不到）

- **原因**：编辑器里 `RevHotPlatform.Name` **恒为 `PC`**（带 `!UNITY_EDITOR`），而打包产物目录按 `BuildTarget` 命名（Android / iOS / WebGL）。
- **做法**：编辑器调试时把 Build Target 切回 Standalone；或在配置里用 `PlatformOverride` 对齐；demo 工具会打印警告。

## 20. 改完代码"文档没跟上"

- **现象**：文档与代码不一致 → 下一个人（或 AI）照着文档写出错的代码。
- **做法**：改行为 → 同步该模块《使用说明》；改设计/取舍 → 同步《架构解析》；**md 与 html 都要改**（在线站由 html 发布）；README 里的统计数字（模块数 / 行数）顺手核一遍。

## 21. 数据改了，界面没反应（UI 刷新时机）

- **现象**：`Data.Count++`（或 `Data.Items.Add(...)`）之后界面不变。
- **原因**：框架**不监听数据对象内容**，不是双向绑定；`OnRefreshView` 只在"打开 / 面板开着时 `SetData` / 手动 `RefreshView()`"三个时机被调。
- **做法**：原地改内容后手动 `RefreshView()`；或换新对象走 `SetData(new …)`（面板开着会自动刷）。`struct` 数据读 `Data` 是拷贝，只能走换新对象。**`OnDataChanged` 里别再手动 `RefreshView()`**（面板开着时框架随后会自动刷，手动再刷 = 每次换数据画两遍）。详见 `ui-and-layering.md` §1.1。

## 22. `OnRefreshView` 里 `Data` 为 null → NRE

- **原因**：`SetData(null)` 会把强类型 `Data` 清成 null；面板回池再取出时也会被清（防"上次的数据还在"）；打开面板**没传数据**时 `Data` 就是 null。
- **做法**：`OnRefreshView` 里用 `Data` 前先判空；"打开时必须有数据"的面板一律走 `RevUI.Open<T, TData>(data)`。

## 23. 切后台后"连接断了 / 声音没了 / 活动时间错了"

- **原因**：移动端 / 小游戏切后台会同时带走音频通道、长连接、一大段时间；业务却没人知道发生过。
- **做法**：订阅 `RevAppLifecycle.Resumed`（`owner: this`），用 `LastBackgroundSeconds` 决定是否重连 / 重算 / 重播 BGM / 重新校准服务器时间。**别自己写 `OnApplicationPause` / `OnApplicationFocus`**（Pause 与 Focus 会前后脚到达，框架已去重，自己写会重复触发重连）。`Quitting` 移动端不保证触发，数据要随存随写。

## 24. 编辑器好好的，真机"静默失效"（刚体不动 / 射线恒 false / 面板空白 / 没声音）

- **原因**：**IL2CPP 代码裁剪**删掉了"编译期看起来没人用"的类；原生模块（物理 / UI / 动画 / 音频 / 粒子）的托管 wrapper 被裁，而场景里挂的原生组件还在 → 不报错只是不工作。编辑器不裁剪，所以看不出。
- **做法**：**主推 `Managed Stripping Level = Disabled`**（代价只有包体，出两包比差值 3 分钟测准）；必须开裁剪时生成 `Assets/link.xml`（菜单 `Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)`），真机看 `RevStripGuard` 的启动自检输出。详见 `startup-and-platform.md` §3。

## 25. 小游戏 / WebGL 上"一启动就崩"或"文件日志不工作"

- **原因**：小游戏**单线程**，`new Thread` 直接抛 `PlatformNotSupportedException`；`persistentDataPath` 是虚拟文件系统，"目录可写探测"会误判可用。
- **做法**：框架已处理文件日志（`RevFileSink.SupportsBackgroundWriter`，WebGL 默认 false；原因报到 `RevLog.SinkErrors`）；**自己的代码别用 `Thread` / `Task.Run` / `System.Threading.Tasks.Task`**，异步一律 `RevTask`；同步加载在 WebGL 上不可用，一律 `LoadAsync` / `GetAsync` / `OpenAsync`。清单见 `startup-and-platform.md` §4。

## 26. 对象池按类型取时"取不出来 / 取了别的池"

- **原因**：类上没写 `[RevPoolPrefab]`（报错并返回 null）；特性**不继承**，子类要自己写；同一预制体被两个类声明会各取各的池。
- **做法**：预制体根节点主脚本上写一次 `[RevPoolPrefab(root, name, Group = …)]`；`Get<T>` / `Clear<T>` / `DestroyPool<T>` 三个入口解析的是同一条池。**`Clear*` 只清空闲实例（池保留）；`DestroyPool*` / `DestroyGroup` / `DestroyAll` 才连借出中的实例与 prefab 引用一起收**——退出业务域先 `DestroyGroup`，再 `RevResBootstrap.Instance.Shutdown(group)`。

## 27. 启动时"手动初始化了一圈模块"反而出问题

- **原因**：`RevEvent` / `RevInput` / `RevTimer` / `RevUI` / `RevPool` / `RevAppLifecycle` 等已由各自的 `RuntimeInitializeOnLoadMethod` 钩子或首次使用自动就位；重复手动启动可能清掉监听、取消加载、重复驱动。
- **做法**：启动入口**只调一次 `RevInitialize.Initialize()`**（只装配资源系统策略，幂等）；全局服务在启动入口一行一个 `RevServices.Register<…>()`；别在前置回调里递归调 `Initialize()`。骨架 `templates/RevBoot.cs.txt`。

## 28. `async void` 入口里异常"静默消失"

- **原因**：`async void` 的异常不会抛给任何人（按钮 / 事件入口常见）；`RevTask` 不 `await` 也不 `Forget()` 会触发编译器警告。
- **做法**：入口 `async void` 整体 `try/catch` + `RevLog.Exception(e, …)`；不 await 的任务显式 `.Forget()`；关键节点 `token.ThrowIfCancelled()`。

## 29. 关域后回调/服务还在，或全局心跳被收走

- 显式 owner 优先于当前域，服务不自动归域；需要随域的对象用 `RevScope.Attach(owner)`，域服务显式 `owner: RevScope.Current`。默认兜底需 `TakeOverOwner=true`；跨域心跳使用显式全局 owner。资源回收还需非 `Unknown` 的 `Reclaim`，不能只 Enter 就假设全部自动清理。
- FSM 只开关自己的域；`RevScope` 不依赖 FSM。`SetTickEnabled(false)` 只停域兜底 Mono 与 Logic Tick，Timer 照走；暂停不是回收。自定义域用业务 `const string`，不要修改预置 `RevDomain` 才能加域。见 `playbooks.md` R19。

## 30. 选竖屏后 Prefab 预览没变，或页面仍是横屏布局

- `CanvasOrientation` 只处理运行时根 Canvas 实例缩放；固定 Portrait/Landscape 不会锁设备方向或重排面板。首次 Open/Preload 前设置；Prefab 编辑预览要手动 Scaler + Game 竖屏分辨率，安全区与排版由业务处理。见 `ui-and-layering.md` §1.5。

## 31. Android AB 打包被拦或材质变紫

- `ValidateAndroidShaders` 在窗口与 CI 入口预检显式 Shader 缺失/无法随包加载且不在 Always Included 的风险，不全量保留所有 Shader、不误报默认 UGUI 无显式材质。
- 查看 `Res` 标签，先区分主包/依赖包/目标包来源失败与 Shader 缺失/不支持；变体裁剪、管线或图形 API 不匹配仍需真机验收。预检通过不能证明设备兼容。见 `resource-and-ab.md`。

## 32. 热更校验过了，却把它当作全部业务资源可用

- 文件平台完整快照尺寸/SHA 与内置主包检查不等于业务资源加载；当前没有 `ProbeBundles` 代表资源探针。WebGL 走 Unity Hash128/CRC，映射表另校验；有效清单中的缺包不能任意混读旧同名包。
- `.part.meta` 身份不一致不得续拼；发布 `SelfCheck` 已重算真 SHA，但不是客户端运行回归。`Reset()` 只复位桥安装标志，不能声称四委托已解除。见 `hot-update.md` §2、§5.1。

## 33. 策划数值写死在 C#（“占位，等 Excel 接入”永远等不到）

- **现象**：`PieceCatalog` 里 `AddTemplate(Shield, "盾战", 120, 8, …)`、`case Attack: _atk += 0.25f`、`Consts.SupportDamage = 25`、说明文案写 `"本轮攻击+25%"`；策划想调数得让程序改代码重编译；文案与逻辑各写一份，改一处另一处不动（“写 25%、实际 30%”）。
- **原因**：先用常量跑通玩法，注释写“占位”，之后没人回头迁移；导表工具已有、`RevDataTableManager` 也会读，但**没有人规定“什么必须进表”**。
- **做法**：策划会调的数一律进 Excel（铁律 21、`playbooks.md` R22）：实体表 + 通用参数表 + 规则表；文案用 `{Amount%}` 占位符与同一行数值同源；启动时 `GameConfig.Initialize` 一次性校验并抛出中文问题清单；业务读强类型只读入口。迁移用“新旧共用转储程序 + 固定种子整局模拟逐字节比对”证明行为不变。
- **例外要写明理由**：结构常量（和预制体/场景绑定）、纯表现时长、算法内部评分权重可留在代码，但必须在注释里写“为什么不进表”，并在汇报里列出，不要悄悄留下。
- 迁完后在 `Consts` 里**删掉**旧副本，让编译器替你找出遗漏的引用；顺带检查 HUD/日志里手写的 `×2`、`* 10`、`每三秒` 这类“文案里的数字”。

---

# 已修复缺陷的"现行契约速查"

> 用途：这些缺陷都**已经在框架里修好**。写业务代码时**不要再给它们写绕过代码**，也不要把"旧行为"当成现状。
> 明细与复现断言：`Assets/Revolution/CHANGELOG.md` 的"未发布"段 · `Revolution.Document/bug修复日志/`（6 份：UI / 输入 / 计时器 / 热更 / 对象池-公共Mono-资源 / 场景-服务定位器-音效-状态机-RevTask）。

| 模块 | 现行契约（修复后） |
|---|---|
| **RevUI** | 泛型面板 `Data` 与 `OnRefreshView` 的"只画 Data"契约成立（`OnDataSet` 统一出口）；打开 / 关闭转场完成回调有**状态守卫**（打开途中被关闭不会再收到"打开完成"）；`ClosePart(false)` 关掉的 Part 再打开会重新出现；`ShutdownAll` / 场景卸载后到达的关闭回调不会往空池塞死实例；`TopOf` 与 `CloseTopOf` 规则一致（`Split` 三画布下返回视觉最上层）；`RevUIAnim.RestoreBase` 能停掉正在播的动画 |
| **RevInput** | `SetRepeat(delay ≤ 0)` = **关连发**；`SaveBindings ⇄ LoadBindings` 无损往返连发配置（`repeat:延迟/间隔`）；屏蔽 World 时**轴读数归零**但按下时间仍记录；`BindAxis` 键与键位 / 轴键**双向冲突检测**；监听者回调里注销自己（`OffAllOf`）安全；`Failed` 订阅不跨局残留 |
| **RevTimer** | 到期回调里调 `Restart()` **有效**（撤销待回收标记，重试类写法可用）；全局暂停期间 `SyncServerTime` 不丢暂停时长（`At` 不再整体错位） |
| **RevServiceLocator** | 作用域 `Dispose` 后自摘；根容器释放先摘列表再倒序逐个释放；`OnInit` 失败回滚时对容器拥有的实例补 `Dispose`；**同一实例注册到多个服务类型只挂载一次**（`OnTick` 不会跑两遍、`Dispose` 不会放两遍） |
| **RevServices** | （新增入口，工程外 55 条断言通过）重复注册当场报错；注销 = 摘表 + 停驱动 + 容器创建的实例 `Dispose`（恰好 1 次，重复注销安全）；你传进来的实例**不 `Dispose`**；`UnregisterAllOf(owner)` 逆创建序、个别 `Dispose` 抛错不影响其余（最后统一 `AggregateException`）；同一实例注册成多个类型：每帧只驱动 1 次、最后一个持有者注销才 `Dispose`；`OnInit` 失败回滚并 `Dispose`、注册保留可重试；注销后取服务报错且提示"曾被注销"；进 Play 复位吞掉上一局 `Dispose` 异常；Tick 由框架自动驱动、单个服务 `OnTick` 抛错只记日志（tag = `Services`）不影响其余 |
| **RevMono** | `RemoveAllOf` 按相位内计数判脏（只动 Update 不会把别的相位标脏、破坏稳态零分配）；进 Play 复位一并放掉 `Failed` 订阅 |
| **RevScene** | 发起加载抛异常（如 `buildIndex` 越界）会转**统一失败收尾**（`State = Failed` + `OnLoadFailed`），`IsLoading` 不会永久卡 true；同步 `Load` 广播的是**目标场景名**；退出 Play 时的在途加载状态进 Play 会复位 |
| **RevSequence** | 步骤 / 回调 / await 续体里 `StopAll` / `Clear` / `Dispose` 安全（Tick 按快照遍历）；步骤里 `Stop` 自己后不再执行后续步骤；`RunFinished` 订阅者抛异常不会让 `PlayAsync` 永不完成；等待超时 / 异步任务失败 / 收尾异常**经 RevLog 输出**（tag = `ActionSequence`）；`OnCancel` / `OnCompleted` / `OnCancelled` 多次调用**叠加**；触发者被销毁时序列自动取消；`FinallySteps` 现在表示 `.Finally` 步骤、取消收尾改名 `CancelSteps` |
| **RevLog** | 文件大小按 **UTF-8 字节**统计（中文日志不会涨到上限约 3 倍才轮转）；主线程 Flush / Dispose 与后台线程写同一 `StreamWriter` 已互斥；WebGL 自动禁用文件通道 |
| **RevPool / 资源** | 失败句柄在引用归零后允许重新尝试加载；`Clear<T>()` / `DestroyPool<T>()` 按类型清理（见 §26） |
| **RevScope / RevLogic** | 自定义字符串域；默认域兜底但显式 owner 优先，服务显式归属；非 Unknown Reclaim 才按组收资源/池；停 Tick 不停 Timer；Logic Stop 先解绑自身受控注册，再业务收尾 |
| **FSM** | 类型名统一 FSM，既有文件路径保留旧名；宿主驱动，状态只开关自己的域，Scope 内核不依赖 FSM |
| **RevHotUpdate** | 候选清单门禁、完整快照 SHA、跨启动分片身份与发布 SelfCheck 真 SHA 已实现；有清单时不混读任意旧包，失败回退也可能失败需停在错误；当前未提供 ProbeBundles/完整卸载桥接口。端到端范围见 `hot-update.md` |
| **配置表 / 打包** | `DataLoad → RevDataLoad`、`LiteAB → RevAB` 已改名（旧名只会出现在历史文档 / 旧生成物里）；ResMap 与 AB 产物一致性检查在「检查」页签 |
